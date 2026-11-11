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
    public Action? OpenSettings { get; set; }
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, ConfigDraft> _drafts = [];
    private readonly Func<bool, IReadOnlyList<string>>? _captureHotkeys;
    private int _captureConnections;
    private readonly Func<IReadOnlyList<string>>? _hotkeyStatus;
    private readonly Func<AppConfig, string?>? _preflight;

    public AgentPipe(ConfigStore store, Action applied, Func<bool, IReadOnlyList<string>>? captureHotkeys = null, Func<IReadOnlyList<string>>? hotkeyStatus = null, Func<AppConfig, string?>? preflight = null)
    {
        _store = store;
        _applied = applied;
        _captureHotkeys = captureHotkeys;
        _hotkeyStatus = hotkeyStatus;
        _preflight = preflight;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("A UI synchronization context is required.");
        _ = Listen();
    }

    public static string PipeName => $"m3nuxa-{Environment.UserName}-{Environment.ProcessId}";

    public async Task<JsonNode> ExecuteAsync(string session, string operation, JsonObject? arguments = null)
    {
        var request = AgentPipeProtocol.CreateRequest(session, operation, arguments);
        var response = JsonNode.Parse(await Dispatch(request.ToJsonString()))!.AsObject();
        if (response["protocolVersion"]?.GetValue<int>() != AgentPipeProtocol.Version)
            throw new InvalidDataException("The agent pipe returned an unsupported protocol version.");
        if (response["success"]?.GetValue<bool>() != true)
        {
            var error = response["error"]?.AsObject();
            throw new ProtocolException(error?["code"]?.GetValue<string>() ?? "request_failed",
                error?["message"]?.GetValue<string>() ?? "Agent pipe request failed.");
        }
        return response["data"]?.DeepClone() ?? new JsonObject();
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
                    if (IsCaptureRequest(line))
                    {
                        capturing = await HandleCaptureRequest(line, reader, writer);
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

    private static bool IsCaptureRequest(string line) =>
        JsonNode.Parse(line)?["operation"]?.GetValue<string>() == "captureHotkeys";

    private async Task<bool> HandleCaptureRequest(string line, StreamReader reader, StreamWriter writer)
    {
        var request = JsonNode.Parse(line);
        if (request?["protocolVersion"]?.GetValue<int>() != AgentPipeProtocol.Version)
        {
            await writer.WriteLineAsync(ProtocolError("unsupported_protocol_version", "Unsupported protocol version."));
            return false;
        }
        try
        {
            await SetCaptureAsync(true);
            await writer.WriteLineAsync(ProtocolSuccess(new JsonObject()));
        }
        catch (Exception ex)
        {
            await writer.WriteLineAsync(ProtocolError("capture_unavailable", ex.Message));
            return false;
        }
        try
        {
            // The open pipe owns the pause; EOF also restores keys after an editor crash.
            var stopLine = await reader.ReadLineAsync(_stop.Token);
            var stopRequest = stopLine is null ? null : JsonNode.Parse(stopLine)?.AsObject();
            if (stopRequest?["protocolVersion"]?.GetValue<int>() != AgentPipeProtocol.Version ||
                stopRequest["operation"]?.GetValue<string>() != "stopCapture")
                throw new IOException("Invalid capture completion request.");
            var unavailable = await SetCaptureAsync(false);
            await writer.WriteLineAsync(ProtocolSuccess(JsonSerializer.SerializeToNode(new { unavailableHotkeys = unavailable }, JsonOptions)!));
            return false;
        }
        catch
        {
            await SetCaptureAsync(false);
            throw;
        }
    }

    private Task<IReadOnlyList<string>> SetCaptureAsync(bool start)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try
            {
                if (_captureHotkeys is null) throw new InvalidOperationException("Hotkey capture is not supported by the tray application.");
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
            try
            {
                var request = JsonNode.Parse(line)!.AsObject();
                if (request["protocolVersion"]?.GetValue<int>() != AgentPipeProtocol.Version)
                    throw new ProtocolException("unsupported_protocol_version", "Unsupported protocol version.");
                if (request["session"]?.GetValue<string>() is null || request["operation"]?.GetValue<string>() is null)
                    throw new ProtocolException("invalid_request", "Request must include session and operation.");
                var data = Handle(request);
                if (request["operation"]?.GetValue<string>() is "apply" or "saveAs" or "reload")
                {
                    var succeeded = data["Success"]?.GetValue<bool>() == true;
                    if (!succeeded)
                    {
                        var code = data["Validation"]?["IsValid"]?.GetValue<bool>() == false
                            ? "validation_failed" : "operation_failed";
                        var message = data["Error"]?.GetValue<string>() ?? "Operation failed.";
                        completion.SetResult(ProtocolError(code, message, data));
                        return;
                    }
                }
                completion.SetResult(ProtocolSuccess(data));
            }
            catch (Exception ex)
            {
                DevLog.Write("app-pipe", "Request failed", ex);
                var error = ex as ProtocolException;
                completion.SetResult(ProtocolError(error?.Code ?? (ex is ArgumentException ? "invalid_argument" : "request_failed"), ex.Message));
            }
        }, null);
        return completion.Task;
    }

    internal Task<string> DispatchForChecks(string line) => Dispatch(line);

    private static string ProtocolSuccess(JsonNode data) => new JsonObject
    {
        ["protocolVersion"] = AgentPipeProtocol.Version, ["success"] = true, ["data"] = data
    }.ToJsonString(JsonOptions);

    private static string ProtocolError(string code, string message, JsonNode? data = null) => new JsonObject
    {
        ["protocolVersion"] = AgentPipeProtocol.Version, ["success"] = false,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }, ["data"] = data
    }.ToJsonString(JsonOptions);

    internal static string SerializeSuccessForChecks(JsonNode data) => ProtocolSuccess(data);
    internal static string SerializeErrorForChecks(string code, string message) => ProtocolError(code, message);

    internal sealed class ProtocolException(string code, string message) : InvalidOperationException(message)
    {
        public string Code { get; } = code;
    }

    private JsonNode Handle(JsonObject request)
    {
        var session = request["session"]?.GetValue<string>() ?? throw new ArgumentException("The session field is required.");
        var operation = request["operation"]?.GetValue<string>() ?? throw new ArgumentException("The operation field is required.");
        DevLog.Write("app-pipe", $"Handling {operation}");
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        return operation switch
        {
            "openSettings" => OpenSettingsRequest(),
            "read" => ReadConfiguration(),
            "loadConfig" => LoadConfiguration(args),
            "begin" => BeginDraft(session),
            "reload" => ReloadConfiguration(),
            _ => HandleDraftOperation(session, operation, args)
        };
    }

    private JsonNode OpenSettingsRequest()
    {
        OpenSettings?.Invoke();
        return new JsonObject();
    }

    private JsonNode ReadConfiguration() =>
        JsonSerializer.SerializeToNode(new
        {
            version = _store.Version,
            config = _store.Current,
            configPath = File.Exists(_store.FilePath) ? Path.GetFullPath(_store.FilePath) : null,
            mainHotkey = _store.Current.Settings.MainHotkey,
            unavailableHotkeys = _hotkeyStatus?.Invoke()
        }, JsonOptions)!;

    private JsonNode LoadConfiguration(JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? throw new ArgumentException("The path field is required.");
        var loaded = _store.LoadDraft(path);
        return JsonSerializer.SerializeToNode(new { config = loaded.Config }, JsonOptions)!;
    }

    private JsonNode BeginDraft(string session)
    {
        var draft = _store.CreateDraft();
        _drafts[session] = draft;
        return JsonSerializer.SerializeToNode(new
        {
            version = draft.BaseVersion,
            config = draft.Config,
            configPath = File.Exists(_store.FilePath) ? Path.GetFullPath(_store.FilePath) : null
        }, JsonOptions)!;
    }

    private JsonNode ReloadConfiguration()
    {
        var result = _store.Reload();
        if (result.Success) { _drafts.Clear(); _applied(); }
        var response = JsonSerializer.SerializeToNode(result, JsonOptions)!;
        response["unavailableHotkeys"] = JsonSerializer.SerializeToNode(_hotkeyStatus?.Invoke(), JsonOptions);
        return response;
    }

    private JsonNode HandleDraftOperation(string session, string operation, JsonObject args)
    {
        if (!_drafts.TryGetValue(session, out var draft))
            throw new ProtocolException("draft_not_started", "Call begin before editing the draft.");

        return operation switch
        {
            "replaceDraft" => ReplaceDraft(draft, args),
            "validate" => ValidateDraft(draft),
            "apply" => ApplyDraft(session, draft),
            "saveAs" => SaveDraftAs(session, draft, args),
            "create" => CreateMenuItem(draft.Config.Items, args),
            "update" => UpdateMenuItem(draft.Config.Items, args),
            "setSteps" => SetActionSteps(draft.Config.Items, args),
            "delete" => DeleteMenuItem(draft.Config.Items, args),
            "move" => MoveMenuItem(draft.Config.Items, args),
            _ => throw new ProtocolException("unknown_operation", $"Unknown operation: {operation}")
        };
    }

    private static JsonNode ReplaceDraft(ConfigDraft draft, JsonObject args)
    {
        draft.Config = args["config"]?.Deserialize<AppConfig>(JsonOptions)
            ?? throw new ArgumentException("The config field is required.");
        return JsonSerializer.SerializeToNode(new { version = draft.BaseVersion, config = draft.Config }, JsonOptions)!;
    }

    private JsonNode ValidateDraft(ConfigDraft draft) =>
        JsonSerializer.SerializeToNode(_store.Validate(draft.Config), JsonOptions)!;

    private JsonNode ApplyDraft(string session, ConfigDraft draft) => SaveDraft(session, draft, null);

    private JsonNode SaveDraftAs(string session, ConfigDraft draft, JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? throw new ArgumentException("The path field is required.");
        return SaveDraft(session, draft, path);
    }

    private JsonNode SaveDraft(string session, ConfigDraft draft, string? destinationPath)
    {
        var validation = _store.Validate(draft.Config);
        if (validation.IsValid && draft.BaseVersion == _store.Version && _preflight?.Invoke(draft.Config) is { } error)
            return JsonSerializer.SerializeToNode(ApplyResult.Failed(error, validation), JsonOptions)!;

        var result = destinationPath is null ? _store.Apply(draft) : _store.SaveAs(draft, destinationPath);
        if (result.Success) { _drafts.Remove(session); _applied(); }
        var response = JsonSerializer.SerializeToNode(result, JsonOptions)!;
        response["unavailableHotkeys"] = JsonSerializer.SerializeToNode(_hotkeyStatus?.Invoke(), JsonOptions);
        return response;
    }

    private static JsonNode CreateMenuItem(List<MenuNode> items, JsonObject args)
    {
        var node = args["node"]?.Deserialize<MenuNode>(JsonOptions) ?? throw new ArgumentException("The node field is required.");
        Insert(items, args["parentId"]?.GetValue<string>(), args["index"]?.GetValue<int>() ?? -1, node);
        return JsonSerializer.SerializeToNode(node, JsonOptions)!;
    }

    private static JsonNode UpdateMenuItem(List<MenuNode> items, JsonObject args)
    {
        var id = RequiredId(args);
        var replacement = args["node"]?.Deserialize<MenuNode>(JsonOptions) ?? throw new ArgumentException("The node field is required.");
        var (siblings, index) = Find(items, id);
        var existing = siblings[index];

        EnsureSameKind(existing, replacement);
        EnsureWorkflowStepsUnchanged(existing, replacement);
        EnsureChildrenUnchanged(existing, replacement);

        // Omitted lists deserialize as empty: preserve existing data rather than losing it.
        replacement.Steps = existing.Steps;
        replacement.Children = existing.Children;
        replacement.Id = id;
        siblings[index] = replacement;
        return JsonSerializer.SerializeToNode(replacement, JsonOptions)!;
    }

    private static void EnsureSameKind(MenuNode existing, MenuNode replacement)
    {
        if (existing.Kind != replacement.Kind)
            throw new ProtocolException("item_kind_immutable", "Update cannot change the item kind; create a new item instead.");
    }

    private static void EnsureWorkflowStepsUnchanged(MenuNode existing, MenuNode replacement)
    {
        if (replacement.Steps is { Count: > 0 } &&
            JsonSerializer.Serialize(replacement.Steps, JsonOptions) != JsonSerializer.Serialize(existing.Steps, JsonOptions))
            throw new ProtocolException("steps_require_set_steps", "Update cannot change workflow steps; use setSteps explicitly.");
    }

    private static void EnsureChildrenUnchanged(MenuNode existing, MenuNode replacement)
    {
        if (replacement.Children is { Count: > 0 } &&
            JsonSerializer.Serialize(replacement.Children, JsonOptions) != JsonSerializer.Serialize(existing.Children, JsonOptions))
            throw new ProtocolException("children_require_tree_operations", "Update cannot change children; use create, move or delete explicitly.");
    }

    private static JsonNode SetActionSteps(List<MenuNode> items, JsonObject args)
    {
        var (siblings, index) = Find(items, RequiredId(args));
        var node = siblings[index];
        if (node.Kind != MenuNodeKind.Action) throw new ProtocolException("steps_require_action", "Steps can only be set on an action.");
        var steps = args["steps"]?.Deserialize<List<MenuAction>>(JsonOptions) ?? throw new ArgumentException("The steps field is required.");
        if (steps.Count == 0) throw new ProtocolException("steps_empty", "An action must contain at least one step.");
        if (steps.Any(step => step is null)) throw new ProtocolException("steps_contain_null", "Steps cannot contain null entries.");
        node.Steps = steps;
        return JsonSerializer.SerializeToNode(node, JsonOptions)!;
    }

    private static JsonNode DeleteMenuItem(List<MenuNode> items, JsonObject args)
    {
        var (siblings, index) = Find(items, RequiredId(args));
        siblings.RemoveAt(index);
        return new JsonObject { ["ok"] = true };
    }

    private static JsonNode MoveMenuItem(List<MenuNode> items, JsonObject args)
    {
        var (siblings, index) = Find(items, RequiredId(args));
        var node = siblings[index];
        var parentId = args["parentId"]?.GetValue<string>();
        if (parentId is not null && (parentId == node.Id || Contains(node.Children, parentId)))
            throw new ProtocolException("move_into_descendant", "A group cannot be moved into itself or one of its descendants.");

        var destination = Target(items, parentId);
        siblings.RemoveAt(index);
        var targetIndex = args["index"]?.GetValue<int>() ?? -1;
        if (ReferenceEquals(destination, siblings) && targetIndex > index) targetIndex--;
        if (targetIndex < 0 || targetIndex > destination.Count) targetIndex = destination.Count;
        destination.Insert(targetIndex, node);
        return new JsonObject { ["ok"] = true };
    }

    private static string RequiredId(JsonObject args) => args["id"]?.GetValue<string>() ?? throw new ArgumentException("The id field is required.");

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
        throw new KeyNotFoundException($"Menu item '{id}' was not found.");
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
            if (list[parentIndex].Kind != MenuNodeKind.Group) throw new ProtocolException("parent_must_be_group", "The parent item must be a group.");
            target = list[parentIndex].Children;
        }
        return target;
    }

    public void Dispose() => _stop.Cancel();
}
