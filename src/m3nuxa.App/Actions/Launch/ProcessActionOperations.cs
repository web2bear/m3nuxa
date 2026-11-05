using System.Diagnostics;

namespace m3nuxa.App;

internal static class ProcessActionOperations
{
    internal static async Task StartProcessAsync(MenuAction action, InvocationContext context, bool script, CancellationToken cancellationToken)
    {
        var path = ActionParameters.ExpandSingle(action.Path, context);
        if (!File.Exists(path)) throw new FileNotFoundException(L.Format("ActionFileUnavailablePath", path));
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var interpreter = script ? (action.Interpreter ?? "") : "";
        string executable;
        var prefix = new List<string>();
        if (!script) executable = path;
        else if (extension is ".bat" or ".cmd")
        {
            executable = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            prefix.AddRange(["/d", "/s", "/c", path]);
        }
        else if (extension == ".ps1")
        {
            executable = string.IsNullOrWhiteSpace(interpreter) ? "powershell.exe" : interpreter;
            prefix.AddRange(["-NoProfile", "-File", path]);
        }
        else throw new InvalidOperationException(L.Get("ActionScriptType"));

        var workingDirectory = string.IsNullOrWhiteSpace(action.WorkingDirectory)
            ? Path.GetDirectoryName(path)!
            : ActionParameters.ExpandSingle(action.WorkingDirectory, context);
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException(L.Format("ActionWorkingFolderUnavailable", workingDirectory));
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = script && action.HideConsole,
            WindowStyle = script && action.HideConsole ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        foreach (var arg in prefix) info.ArgumentList.Add(arg);
        foreach (var arg in ActionParameters.ExpandArguments(action.Arguments, context)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException(L.Get("ActionStartFailed"));
        if (!action.WaitForExit) return;
        try
        {
            if (action.TimeoutMilliseconds is int timeout)
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(timeout), cancellationToken);
            else
                await process.WaitForExitAsync(cancellationToken);
        }
        catch (TimeoutException) { throw new WorkflowSafeException(L.Get("WorkflowProcessTimeout")); }
        if (process.ExitCode != 0) throw new WorkflowSafeException(L.Format("WorkflowProcessExitCode", process.ExitCode));
    }


}
