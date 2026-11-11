using m3nuxa.App;
using System.Diagnostics;
using System.IO.Pipes;

namespace m3nuxa.Editor;

// A dedicated connection keeps the tray pause scoped to this recording session.
internal sealed class HotkeyCaptureConnection : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private HotkeyCaptureConnection(NamedPipeClientStream pipe)
    {
        this.pipe = pipe;
        reader = new StreamReader(pipe, leaveOpen: true);
        writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    }
    public static async Task<HotkeyCaptureConnection> OpenAsync()
    {
        foreach (var process in Process.GetProcessesByName("m3nuxa.App"))
        {
            using (process)
            {
                var pipe = new NamedPipeClientStream(".", $"m3nuxa-{Environment.UserName}-{process.Id}",
                    PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                HotkeyCaptureConnection? connection = null;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await pipe.ConnectAsync(timeout.Token);
                    connection = new HotkeyCaptureConnection(pipe);
                    await connection.writer.WriteLineAsync(AgentPipeProtocol.CreateRequest("capture", "captureHotkeys").ToJsonString());
                    var response = await connection.reader.ReadLineAsync(timeout.Token);
                    var envelope = response is null ? null : System.Text.Json.Nodes.JsonNode.Parse(response)?.AsObject();
                    if (envelope?["protocolVersion"]?.GetValue<int>() != AgentPipeProtocol.Version ||
                        envelope["success"]?.GetValue<bool>() != true)
                        throw new IOException(envelope?["error"]?["message"]?.GetValue<string>() ?? L.Get("Editor_TheTrayApplicationDoesNot_3ebdc0"));
                    return connection;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                { connection?.Dispose(); pipe.Dispose(); }
            }
        }
        throw new IOException(L.Get("Editor_CouldNotSuspendHotkeysCheck_871b0b"));
    }
    public async Task CompleteAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await writer.WriteLineAsync(AgentPipeProtocol.CreateRequest("capture", "stopCapture").ToJsonString());
        var response = await reader.ReadLineAsync(timeout.Token);
        var envelope = response is null ? null : System.Text.Json.Nodes.JsonNode.Parse(response)?.AsObject();
        if (envelope?["protocolVersion"]?.GetValue<int>() != AgentPipeProtocol.Version ||
            envelope["success"]?.GetValue<bool>() != true)
            throw new IOException(envelope?["error"]?["message"]?.GetValue<string>() ?? L.Get("Editor_TheConnectionToTheTray_0e8bf1"));
    }
    public void Dispose()
    {
        try { writer.Dispose(); }
        catch (IOException) { }
        finally { reader.Dispose(); pipe.Dispose(); }
    }
}
