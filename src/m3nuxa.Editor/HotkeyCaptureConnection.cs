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
                    await connection.writer.WriteLineAsync("{\"operation\":\"captureHotkeys\"}");
                    var response = await connection.reader.ReadLineAsync(timeout.Token);
                    if (response != "{}") throw new IOException("Приложение в трее не поддерживает запись сочетаний. Перезапустите обновлённую версию.");
                    return connection;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                { connection?.Dispose(); pipe.Dispose(); }
            }
        }
        throw new IOException("Не удалось приостановить горячие клавиши. Проверьте, что обновлённое приложение запущено в трее.");
    }
    public async Task CompleteAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await writer.WriteLineAsync("stop");
        if (await reader.ReadLineAsync(timeout.Token) is null) throw new IOException("Соединение с приложением в трее закрыто.");
    }
    public void Dispose()
    {
        try { writer.Dispose(); }
        catch (IOException) { }
        finally { reader.Dispose(); pipe.Dispose(); }
    }
}
