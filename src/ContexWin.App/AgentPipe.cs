using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ContexWin.App;

internal sealed class AgentPipe : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly ConfigStore _store;
    private readonly Action _applied;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, ConfigDraft> _drafts = [];

    public AgentPipe(ConfigStore store, Action applied)
    {
        _store = store;
        _applied = applied;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Нужен UI поток.");
        _ = Listen();
    }

    public static string PipeName => $"ContexWin-{Environment.UserName}-{Environment.ProcessId}";

    private async Task Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                while (pipe.IsConnected && !_stop.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null) break;
                    var response = await Dispatch(line);
                    await writer.WriteLineAsync(response);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { await Task.Delay(250); }
        }
    }

    private Task<string> Dispatch(string line)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try { completion.SetResult(Handle(JsonNode.Parse(line)!.AsObject()).ToJsonString(JsonOptions)); }
            catch (Exception ex) { completion.SetResult(new JsonObject { ["error"] = ex.Message }.ToJsonString()); }
        }, null);
        return completion.Task;
    }

    private JsonNode Handle(JsonObject request)
    {
        var session = request["session"]?.GetValue<string>() ?? throw new ArgumentException("Требуется session.");
        var operation = request["operation"]?.GetValue<string>() ?? throw new ArgumentException("Требуется operation.");
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        if (operation == "read") return JsonSerializer.SerializeToNode(new { version = _store.Version, config = _store.Current }, JsonOptions)!;
        if (operation == "begin")
        {
            _drafts[session] = _store.CreateDraft();
            return JsonSerializer.SerializeToNode(new { version = _drafts[session].BaseVersion, config = _drafts[session].Config }, JsonOptions)!;
        }
        if (!_drafts.TryGetValue(session, out var draft)) throw new InvalidOperationException("Сначала вызовите begin.");
        var items = draft.Config.Items;
        if (operation == "validate") return JsonSerializer.SerializeToNode(_store.Validate(draft.Config), JsonOptions)!;
        if (operation == "apply")
        {
            var result = _store.Apply(draft);
            if (result.Success) { _drafts.Remove(session); _applied(); }
            return JsonSerializer.SerializeToNode(result, JsonOptions)!;
        }
        if (operation == "create")
        {
            var node = args["node"]?.Deserialize<MenuNode>(JsonOptions) ?? throw new ArgumentException("Требуется node.");
            Insert(items, args["parentId"]?.GetValue<string>(), args["index"]?.GetValue<int>() ?? -1, node);
            return JsonSerializer.SerializeToNode(node, JsonOptions)!;
        }
        if (operation == "update")
        {
            var id = RequiredId(args);
            var replacement = args["node"]?.Deserialize<MenuNode>(JsonOptions) ?? throw new ArgumentException("Требуется node.");
            var (list, index) = Find(items, id);
            replacement.Id = id;
            list[index] = replacement;
            return JsonSerializer.SerializeToNode(replacement, JsonOptions)!;
        }
        if (operation is "delete" or "move")
        {
            var (list, index) = Find(items, RequiredId(args));
            var node = list[index];
            var parentId = args["parentId"]?.GetValue<string>();
            if (operation == "move" && parentId is not null && (parentId == node.Id || Contains(node.Children, parentId)))
                throw new InvalidOperationException("Нельзя переместить группу внутрь себя.");
            List<MenuNode>? destination = null;
            if (operation == "move") destination = Target(items, parentId);
            list.RemoveAt(index);
            if (destination is not null)
            {
                var targetIndex = args["index"]?.GetValue<int>() ?? -1;
                if (ReferenceEquals(destination, list) && targetIndex > index) targetIndex--;
                if (targetIndex < 0 || targetIndex > destination.Count) targetIndex = destination.Count;
                destination.Insert(targetIndex, node);
            }
            return new JsonObject { ["ok"] = true };
        }
        throw new ArgumentException($"Неизвестная операция: {operation}");
    }

    private static string RequiredId(JsonObject args) => args["id"]?.GetValue<string>() ?? throw new ArgumentException("Требуется id.");

    private static bool Contains(IEnumerable<MenuNode> nodes, string id) =>
        nodes.Any(node => node.Id == id || Contains(node.Children, id));

    private static (List<MenuNode> List, int Index) Find(List<MenuNode> nodes, string id)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Id == id) return (nodes, i);
            if (nodes[i].Kind == MenuNodeKind.Group)
            {
                try { return Find(nodes[i].Children, id); } catch (KeyNotFoundException) { }
            }
        }
        throw new KeyNotFoundException($"Пункт {id} не найден.");
    }

    private static void Insert(List<MenuNode> roots, string? parentId, int index, MenuNode node)
    {
        var target = Target(roots, parentId);
        if (index < 0 || index > target.Count) index = target.Count;
        target.Insert(index, node);
    }

    private static List<MenuNode> Target(List<MenuNode> roots, string? parentId)
    {
        List<MenuNode> target;
        if (parentId is null) target = roots;
        else
        {
            var (list, parentIndex) = Find(roots, parentId);
            if (list[parentIndex].Kind != MenuNodeKind.Group) throw new InvalidOperationException("Родителем может быть только группа.");
            target = list[parentIndex].Children;
        }
        return target;
    }

    public void Dispose() => _stop.Cancel();
}
