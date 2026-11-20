using System.Text.Json;
using System.Text.Json.Serialization;
using m3nuxa.App.Configuration;

namespace m3nuxa.Editor.Configuration;

// Owns the local draft and its history; the editor supplies user gestures.
internal sealed class EditorDraft
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions PipeConfigOptions = new(Options) { MaxDepth = 62 };
    private sealed record HistoryEntry(string Snapshot, Transfer? Transfer = null);
    private sealed record Transfer(EditorDraft Origin, EditorDraft Target, string OriginalId, string MovedId, HashSet<string> MovedIds)
    {
        public bool Detached { get; set; }
        public bool OriginSaved { get; set; }
        public bool TargetSaved { get; set; }
        public EditorDraft Other(EditorDraft draft) => ReferenceEquals(draft, Origin) ? Target : Origin;
    }

    private readonly Stack<HistoryEntry> undo = new();
    private readonly Stack<HistoryEntry> redo = new();
    private HashSet<string> baselineIds = new(StringComparer.OrdinalIgnoreCase);
    private string? baseline;
    private string? pending;

    public EditorDraft() => SetBaseline();
    public AppConfig Config { get; set; } = new();
    public bool IsDirty => baseline is not null && Snapshot() != baseline;
    public string? Baseline => baseline;
    internal int UndoCount { get { CommitPending(); return undo.Count; } }
    public string Snapshot() => JsonSerializer.Serialize(Config, Options);
    private IEnumerable<Transfer> UnsavedOutgoingTransfers() => undo.Select(entry => entry.Transfer).OfType<Transfer>()
        .Where(transfer => !transfer.TargetSaved && ReferenceEquals(transfer.Origin, this) &&
            baselineIds.Contains(transfer.OriginalId) && FindSiblings(Config.Items, transfer.OriginalId) is null);

    public bool HasOutgoingTransfer => UnsavedOutgoingTransfers().Any();
    public bool HasDiscardedOutgoingTransfer => UnsavedOutgoingTransfers().Any(transfer =>
        transfer.Detached || FindSiblings(transfer.Target.Config.Items, transfer.MovedId) is null);
    private IEnumerable<Transfer> TransfersWithoutSavedCopy() => undo.Select(entry => entry.Transfer).OfType<Transfer>()
        .Where(transfer => transfer.OriginSaved && !transfer.TargetSaved && ReferenceEquals(transfer.Target, this));

    public bool HasOnlyUnsavedCopy => TransfersWithoutSavedCopy().Any();
    public bool MustSaveBeforeMoving(MenuNode item) => TransfersWithoutSavedCopy().Any(transfer => transfer.MovedIds.Overlaps(NodeIds([item])));

    public void Reset(AppConfig config) { Config = config; ReplaceBaseline(saved: false); }
    public void SetBaseline() => ReplaceBaseline(saved: true);

    private void ReplaceBaseline(bool saved)
    {
        var previousIds = baselineIds;
        baselineIds = NodeIds(Config.Items).ToHashSet(StringComparer.OrdinalIgnoreCase);
        baseline = Snapshot();
        ClearRedo();
        foreach (var entry in undo)
        {
            if (entry.Transfer is not { } transfer) continue;
            if (saved && ReferenceEquals(transfer.Target, this) && baselineIds.Contains(transfer.MovedId))
                transfer.TargetSaved = true;
            if (saved && ReferenceEquals(transfer.Origin, this) && previousIds.Contains(transfer.OriginalId) && !baselineIds.Contains(transfer.OriginalId))
                transfer.OriginSaved = true;
            transfer.Detached = true;
        }
        undo.Clear();
        pending = null;
    }
    public void BeginChange() { CommitPending(); pending = Snapshot(); }
    public void CommitPending()
    {
        if (pending is null) return;
        if (pending != Snapshot()) { undo.Push(new(pending)); ClearRedo(); }
        pending = null;
    }
    public void RecordChange(string before) => RecordChange(before, null);

    private void RecordChange(string before, Transfer? transfer)
    {
        CommitPending();
        if (before != Snapshot()) { undo.Push(new(before, transfer)); ClearRedo(); }
    }

    private void ClearRedo() => DiscardRedoFrom(0);

    private void DiscardRedoFrom(int first)
    {
        var entries = redo.ToArray();
        if (first >= entries.Length) return;
        redo.Clear();
        for (var index = first - 1; index >= 0; index--) redo.Push(entries[index]);
        foreach (var transfer in entries.Skip(first).Select(entry => entry.Transfer).OfType<Transfer>())
        {
            var other = transfer.Other(this);
            var counterpart = Array.FindIndex(other.redo.ToArray(), entry => ReferenceEquals(entry.Transfer, transfer));
            if (counterpart >= 0) other.DiscardRedoFrom(counterpart);
        }
    }

    public bool Undo() => NavigateHistory(reapply: false, out _, out _);
    public bool Redo() => NavigateHistory(reapply: true, out _, out _);

    public bool NavigateHistory(bool reapply, out bool linked, out bool wouldLoseTransfer)
    {
        linked = false;
        wouldLoseTransfer = false;
        CommitPending();
        var history = reapply ? redo : undo;
        if (!history.TryPeek(out var entry)) return false;
        if (!reapply && entry.Transfer is { OriginSaved: true, TargetSaved: false } incoming && ReferenceEquals(incoming.Target, this))
        {
            wouldLoseTransfer = true;
            return false;
        }
        if (entry.Transfer is { Detached: false } transfer)
        {
            linked = true;
            var other = transfer.Other(this);
            other.CommitPending();
            var otherHistory = reapply ? other.redo : other.undo;
            if (!history.TryPeek(out entry) || !ReferenceEquals(entry.Transfer, transfer) ||
                !otherHistory.TryPeek(out var counterpart) || !ReferenceEquals(counterpart.Transfer, transfer)) return false;
            other.ApplyHistory(reapply);
        }
        ApplyHistory(reapply);
        return true;
    }

    private void ApplyHistory(bool reapply)
    {
        var entry = (reapply ? redo : undo).Pop();
        (reapply ? undo : redo).Push(entry with { Snapshot = Snapshot() });
        Config = Deserialize(entry.Snapshot);
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

    public MenuNode? TryInsertCopy(MenuNode source, List<MenuNode> destination, int index)
    {
        var copy = CloneWithNewIds(source);
        CommitPending();
        var before = Snapshot();
        if (!TryInsertValidated(copy, destination, index)) return null;
        RecordChange(before);
        return copy;
    }

    public MenuNode? TryTransfer(EditorDraft target, string id, List<MenuNode> destination, int index, out string? conflictingHotkey)
    {
        conflictingHotkey = null;
        if (ReferenceEquals(this, target)) return null;
        var siblings = FindSiblings(Config.Items, id);
        if (siblings?.FirstOrDefault(node => node.Id == id) is not { } item) return null;
        if (MustSaveBeforeMoving(item)) return null;
        conflictingHotkey = FindHotkeyConflict(item, target.Config);
        if (conflictingHotkey is not null) return null;
        var targetIds = NodeIds(target.Config.Items).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = NodeIds([item]).Any(targetIds.Contains) ? CloneWithNewIds(item) : item;
        CommitPending();
        target.CommitPending();
        var before = Snapshot();
        var targetBefore = target.Snapshot();
        if (!target.TryInsertValidated(moved, destination, index)) return null;
        siblings.Remove(item);
        var transfer = new Transfer(this, target, item.Id, moved.Id, NodeIds([moved]).ToHashSet(StringComparer.OrdinalIgnoreCase))
        {
            TargetSaved = target.IsStored(moved)
        };
        RecordChange(before, transfer);
        target.RecordChange(targetBefore, transfer);
        return moved;
    }

    private bool IsStored(MenuNode item)
    {
        if (!baselineIds.Contains(item.Id) || baseline is null) return false;
        var stored = EnumerateNodes(Deserialize(baseline).Items).FirstOrDefault(node => node.Id == item.Id);
        return stored is not null && JsonSerializer.Serialize(stored, Options) == JsonSerializer.Serialize(item, Options);
    }

    private bool TryInsertValidated(MenuNode item, List<MenuNode> destination, int index)
    {
        index = Math.Clamp(index, 0, destination.Count);
        destination.Insert(index, item);
        try { JsonSerializer.SerializeToUtf8Bytes(Config, PipeConfigOptions); }
        catch (JsonException)
        {
            destination.RemoveAt(index);
            return false;
        }
        return true;
    }

    private static string? FindHotkeyConflict(MenuNode item, AppConfig target)
    {
        var hotkeys = new HashSet<(uint Modifiers, uint Key)>();
        foreach (var hotkey in EnumerateNodes(target.Items).Select(node => node.Hotkey)
            .Append(target.Settings?.MainHotkey).Append(target.Settings?.PanelHotkey))
            if (hotkey is not null && HotkeyParser.TryParse(hotkey, out var modifiers, out var key)) hotkeys.Add((modifiers, key));

        foreach (var node in EnumerateNodes([item]))
            if (node.Hotkey is { } hotkey && HotkeyParser.TryParse(hotkey, out var modifiers, out var key) && !hotkeys.Add((modifiers, key)))
                return HotkeyParser.Format(modifiers, key);
        return null;
    }

    private static IEnumerable<MenuNode> EnumerateNodes(IEnumerable<MenuNode> nodes) =>
        nodes.SelectMany(node => EnumerateNodes(node.Children).Prepend(node));

    private static IEnumerable<string> NodeIds(IEnumerable<MenuNode> nodes) => EnumerateNodes(nodes).Select(node => node.Id);

    private static MenuNode CloneWithNewIds(MenuNode node)
    {
        var copy = JsonSerializer.Deserialize<MenuNode>(JsonSerializer.Serialize(node, Options), Options)!;
        AssignNewIds(copy);
        return copy;
    }

    internal static void AssignNewIds(MenuNode node)
    {
        node.Id = Guid.NewGuid().ToString("N");
        foreach (var child in node.Children) AssignNewIds(child);
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
