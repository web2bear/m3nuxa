using System.Diagnostics;
using System.Text;

namespace m3nuxa.App;

internal static class ApplicationLog
{
    private static readonly object Gate = new();
    private static readonly string Pathname = Path.Combine(AppContext.BaseDirectory, "logs", "m3nuxa-log.txt");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Pathname)!);
                File.AppendAllText(Pathname, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DevLog.Write("log", "Could not write application log", ex);
            }
        }
    }

    public static void Open()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Pathname)!);
            if (!File.Exists(Pathname)) File.WriteAllText(Pathname, "", new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo(Pathname) { UseShellExecute = true });
        }
    }
}
