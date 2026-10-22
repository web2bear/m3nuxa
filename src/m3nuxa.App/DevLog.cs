using System.Diagnostics;
using System.Text;

namespace m3nuxa.App;

internal static class DevLog
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "logs", "m3nuxa-dev.log");

    public static void Write(string component, string message, Exception? exception = null)
    {
        var line = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {component}: {message}";
        if (exception is not null) line += Environment.NewLine + exception;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) { Debug.WriteLine($"Logging failed: {ex}"); }
        }
    }
}
