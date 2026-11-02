using System.Text.Json;
using System.Text.Json.Serialization;
using m3nuxa.App;

namespace m3nuxa.Editor;

// Owns the local draft and its history; the editor supplies user gestures.
internal sealed class EditorDraft
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();
    private string? baseline;
    private string? pending;

    public EditorDraft() => SetBaseline();
    public AppConfig Config { get; set; } = new();
    public bool IsDirty => baseline is not null && Snapshot() != baseline;
    public string? Baseline => baseline;
    internal int UndoCount { get { CommitPending(); return undo.Count; } }
    public string Snapshot() => JsonSerializer.Serialize(Config, Options);
    public void Reset(AppConfig config) { Config = config; baseline = Snapshot(); undo.Clear(); redo.Clear(); pending = null; }
    public void SetBaseline() { baseline = Snapshot(); undo.Clear(); redo.Clear(); pending = null; }
    public void BeginChange() { CommitPending(); pending = Snapshot(); }
    public void CommitPending()
    {
        if (pending is null) return;
        if (pending != Snapshot()) { undo.Push(pending); redo.Clear(); }
        pending = null;
    }
    public void RecordChange(string before)
    {
        CommitPending();
        if (before != Snapshot()) { undo.Push(before); redo.Clear(); }
    }
    public bool Undo()
    {
        CommitPending();
        if (undo.Count == 0) return false;
        redo.Push(Snapshot()); Config = Deserialize(undo.Pop()); return true;
    }
    public bool Redo()
    {
        CommitPending();
        if (redo.Count == 0) return false;
        undo.Push(Snapshot()); Config = Deserialize(redo.Pop()); return true;
    }
    public void Restore(string snapshot) { pending = null; Config = Deserialize(snapshot); }
    public void Insert(List<MenuNode> destination, int index, IEnumerable<MenuNode> nodes)
    {
        var additions = nodes.ToList();
        if (additions.Count == 0) return;
        BeginChange();
        destination.InsertRange(Math.Clamp(index, 0, destination.Count), additions);
        CommitPending();
    }

    public bool Delete(string id)
    {
        var siblings = FindSiblings(Config.Items, id);
        var node = siblings?.FirstOrDefault(item => item.Id == id);
        if (node is null) return false;
        BeginChange(); siblings!.Remove(node); CommitPending(); return true;
    }

    public bool MoveToEnd(string id, List<MenuNode> destination) => Move(id, destination, destination.Count);

    public bool CanMove(string id, List<MenuNode> destination)
    {
        var siblings = FindSiblings(Config.Items, id);
        var node = siblings?.FirstOrDefault(item => item.Id == id);
        return node is not null && !ContainsList(node, destination);
    }

    public bool Move(string id, List<MenuNode> destination, int index)
    {
        var siblings = FindSiblings(Config.Items, id);
        var node = siblings?.FirstOrDefault(item => item.Id == id);
        if (node is null || ContainsList(node, destination)) return false;
        BeginChange();
        var oldIndex = siblings!.IndexOf(node);
        if (ReferenceEquals(siblings, destination) && index > oldIndex) index--;
        siblings.RemoveAt(oldIndex); destination.Insert(Math.Clamp(index, 0, destination.Count), node);
        CommitPending(); return true;
    }

    private static bool ContainsList(MenuNode node, List<MenuNode> list) =>
        ReferenceEquals(node.Children, list) || node.Children.Any(child => ContainsList(child, list));

    private static List<MenuNode>? FindSiblings(List<MenuNode> nodes, string id)
    {
        if (nodes.Any(node => node.Id == id)) return nodes;
        foreach (var node in nodes)
            if (FindSiblings(node.Children, id) is { } result) return result;
        return null;
    }

    private static AppConfig Deserialize(string snapshot) => JsonSerializer.Deserialize<AppConfig>(snapshot, Options)!;
}
