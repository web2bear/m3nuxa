using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using m3nuxa.App;

namespace m3nuxa.Editor;

public sealed record EditorResponse(bool Success, string? Message, JsonNode? Data);

public sealed class EditorPipeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _session = Guid.NewGuid().ToString("N");

    public async Task<EditorResponse> BeginAsync()
    {
        var response = await SendAsync("begin");
        if (response.Success) return response;

        var processes = Process.GetProcessesByName("m3nuxa.App");
        try { if (processes.Length > 0) return response; }
        finally { foreach (var process in processes) process.Dispose(); }

        var appPath = Path.Combine(AppContext.BaseDirectory, "m3nuxa.App.exe");
        if (!File.Exists(appPath))
            return new(false, "m3nuxa.App.exe не найден рядом с редактором. Запустите приложение из общей папки публикации.", null);

        try
        {
            using var app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = true });
            if (app is null) return new(false, "Не удалось запустить m3nuxa.App.exe.", null);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(300);
                response = await SendAsync("begin");
                if (response.Success) return response;
                if (app.HasExited) break;
            }
        }
        catch (Exception ex) { return new(false, $"Не удалось запустить m3nuxa.App.exe: {ex.Message}", null); }
        return response;
    }
    public Task<EditorResponse> ReadAsync() => SendAsync("read");
    public Task<EditorResponse> ReplaceDraftAsync(AppConfig config) =>
        SendAsync("replaceDraft", new JsonObject { ["config"] = JsonSerializer.SerializeToNode(config, JsonOptions) });
    public Task<EditorResponse> ValidateAsync() => SendAsync("validate");
    public Task<EditorResponse> ApplyAsync() => SendAsync("apply");
    public Task<EditorResponse> ReloadAsync() => SendAsync("reload");

    public static ConfigDraft? ReadDraft(EditorResponse response)
    {
        if (!response.Success || response.Data is null) return null;
        var config = response.Data["config"]?.Deserialize<AppConfig>(JsonOptions);
        var version = response.Data["version"]?.GetValue<long>();
        return config is null || version is null ? null : new ConfigDraft { Config = config, BaseVersion = version.Value };
    }

    public async Task<EditorResponse> SendAsync(string operation, JsonNode? payload = null)
    {
        DevLog.Write("editor-pipe", $"Sending {operation}");
        var request = new JsonObject
        {
            ["session"] = _session,
            ["operation"] = operation,
            ["arguments"] = payload?.DeepClone() ?? new JsonObject()
        };

        var processes = Process.GetProcessesByName("m3nuxa.App");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".",
                        $"m3nuxa-{Environment.UserName}-{process.Id}", PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await pipe.ConnectAsync(timeout.Token);
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(request.ToJsonString());
                    var response = await reader.ReadLineAsync(timeout.Token);
                    if (response is null) return new(false, "Приложение в трее закрыло соединение.", null);
                    var data = JsonNode.Parse(response);
                    DevLog.Write("editor-pipe", $"Received {operation} response");
                    var error = data?["error"]?.GetValue<string>();
                    if (error is not null) return new(false, error, data);
                    var success = data?["Success"]?.GetValue<bool>() ?? true;
                    var message = data?["Error"]?.GetValue<string>();
                    return new(success, message, data);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
                {
                    DevLog.Write("editor-pipe", $"Connection to pid={process.Id} failed for {operation}", ex);
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        DevLog.Write("editor-pipe", $"No tray process answered {operation}");
        return new(false, "m3nuxa в трее не запущен или не отвечает.", null);
    }
}
