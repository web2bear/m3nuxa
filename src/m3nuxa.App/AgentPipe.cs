using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace m3nuxa.App;

internal sealed class AgentPipe : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly ConfigStore _store;
    private readonly Action _applied;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, ConfigDraft> _drafts = [];
    private readonly Func<bool, IReadOnlyList<string>>? _captureHotkeys;
    private int _captureConnections;
    private readonly Func<IReadOnlyList<string>>? _hotkeyStatus;

    public AgentPipe(ConfigStore store, Action applied, Func<bool, IReadOnlyList<string>>? captureHotkeys = null, Func<IReadOnlyList<string>>? hotkeyStatus = null)
    {
        _store = store;
        _applied = applied;
        _captureHotkeys = captureHotkeys;
        _hotkeyStatus = hotkeyStatus;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Нужен UI поток.");
        _ = Listen();
    }

    public static string PipeName => $"m3nuxa-{Environment.UserName}-{Environment.ProcessId}";

    public async Task<JsonNode> ExecuteAsync(string session, string operation, JsonObject? arguments = null)
    {
        var request = new JsonObject
        {
            ["session"] = session,
            ["operation"] = operation,
            ["arguments"] = arguments ?? new JsonObject()
        };
        var response = JsonNode.Parse(await Dispatch(request.ToJsonString()))!;
        if (response["error"] is JsonNode error) throw new InvalidOperationException(error.GetValue<string>());
        return response;
    }

    private async Task Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch { pipe.Dispose(); throw; }
                _ = ServeClient(pipe);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { DevLog.Write("app-pipe", "Listener error", ex); await Task.Delay(250); }
        }
    }

    private async Task ServeClient(NamedPipeServerStream pipe)
    {
        var capturing = false;
        using (pipe)
        using (var reader = new StreamReader(pipe, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true })
        {
            try
            {
                while (pipe.IsConnected && !_stop.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null) break;
                    if (JsonNode.Parse(line)?["operation"]?.GetValue<string>() == "captureHotkeys")
                    {
                        await SetCaptureAsync(true);
                        capturing = true;
                        await writer.WriteLineAsync("{}");
                        // The open pipe owns the pause; EOF also restores keys after an editor crash.
                        await reader.ReadLineAsync(_stop.Token);
                        var unavailable = await SetCaptureAsync(false);
                        capturing = false;
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new { unavailableHotkeys = unavailable }));
                        break;
                    }
                    var response = await Dispatch(line);
                    await writer.WriteLineAsync(response);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException ex) { DevLog.Write("app-pipe", "Client connection ended", ex); }
            catch (Exception ex) { DevLog.Write("app-pipe", "Capture failed", ex); }
            finally { if (capturing) await SetCaptureAsync(false); }
        }
    }

    private Task<IReadOnlyList<string>> SetCaptureAsync(bool start)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try
            {
                if (_captureHotkeys is null) throw new InvalidOperationException("Запись сочетаний не поддерживается приложением в трее.");
                _captureConnections += start ? 1 : -1;
                completion.SetResult(_captureHotkeys(_captureConnections > 0));
            }
            catch (Exception ex) { completion.SetException(ex); }
        }, null);
        return completion.Task;
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
        DevLog.Write("app-pipe", $"Handling {operation}");
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        if (operation == "read") return JsonSerializer.SerializeToNode(new { version = _store.Version, config = _store.Current, unavailableHotkeys = _hotkeyStatus?.Invoke() }, JsonOptions)!;
        if (operation == "begin")
        {
            _drafts[session] = _store.CreateDraft();
            return JsonSerializer.SerializeToNode(new { version = _drafts[session].BaseVersion, config = _drafts[session].Config }, JsonOptions)!;
        }
        if (operation == "reload")
        {
            var result = _store.Reload();
            if (result.Success) { _drafts.Clear(); _applied(); }
            var response = JsonSerializer.SerializeToNode(result, JsonOptions)!;
            response["unavailableHotkeys"] = JsonSerializer.SerializeToNode(_hotkeyStatus?.Invoke(), JsonOptions);
            return response;
        }
        if (!_drafts.TryGetValue(session, out var draft)) throw new InvalidOperationException("Сначала вызовите begin.");
        if (operation == "replaceDraft")
        {
            draft.Config = args["config"]?.Deserialize<AppConfig>(JsonOptions)
                ?? throw new ArgumentException("Требуется config.");
            return JsonSerializer.SerializeToNode(new { version = draft.BaseVersion, config = draft.Config }, JsonOptions)!;
        }
        var items = draft.Config.Items;
        if (operation == "validate") return JsonSerializer.SerializeToNode(_store.Validate(draft.Config), JsonOptions)!;
        if (operation == "apply")
        {
            var result = _store.Apply(draft);
            if (result.Success) { _drafts.Remove(session); _applied(); }
            var response = JsonSerializer.SerializeToNode(result, JsonOptions)!;
            response["unavailableHotkeys"] = JsonSerializer.SerializeToNode(_hotkeyStatus?.Invoke(), JsonOptions);
            return response;
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
