using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace m3nuxa.App;

internal sealed record InvocationContext(IntPtr SourceWindow, IReadOnlyList<string> SelectedPaths, string? CurrentFolder)
{
    public static InvocationContext Capture(IntPtr? sourceWindow = null)
    {
        var window = sourceWindow ?? NativeMethods.GetForegroundWindow();
        var paths = new List<string>();
        string? folder = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is not null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                foreach (dynamic explorer in shell.Windows())
                {
                    try
                    {
                        if ((nint)(int)explorer.HWND != window) continue;
                        folder = (string)explorer.Document.Folder.Self.Path;
                        foreach (dynamic item in explorer.Document.SelectedItems())
                            paths.Add((string)item.Path);
                        break;
                    }
                    catch (Exception ex) { DevLog.Write("context", "Could not capture Explorer window context", ex); }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            // The source may be a non-Explorer window. Preserve its handle for SendShortcut.
        }
        return new InvocationContext(window, paths, folder);
    }
}

internal sealed record ActionResult(int Succeeded, int Skipped, IReadOnlyList<string> Failures, bool Cancelled)
{
    public bool Success => !Cancelled && Failures.Count == 0;
    public string? Error => Failures.Count == 0 ? null : string.Join("\n", Failures);
}

internal static class ActionExecutor
{
    public static async Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context,
        nint owner = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            switch (action.Kind)
            {
                case ActionKind.OpenFolder:
                    var folder = ExpandSingle(action.Path, context);
                    if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(L.Format("ActionFolderUnavailable", folder));
                    Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/n", folder }, UseShellExecute = false });
                    return Ok();
                case ActionKind.OpenItem:
                    var target = ExpandSingle(action.Path, context);
                    if (!(Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") && !File.Exists(target))
                        throw new FileNotFoundException(L.Format("ActionFileUnavailableTarget", target));
                    _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    return Ok();
                case ActionKind.RunProgram:
                    StartProcess(action, context, false);
                    return Ok();
                case ActionKind.RunScript:
                    StartProcess(action, context, true);
                    return Ok();
                case ActionKind.SendShortcut:
                    SendShortcut(action.Shortcut, context);
                    return Ok();
                case ActionKind.InsertText:
                    InsertText(TemplateExpander.Expand(action.Text, context), context);
                    return Ok();
                case ActionKind.CopySelection:
                case ActionKind.MoveSelection:
                    return await ExecuteFileOperationAsync(action, context, owner, cancellationToken);
                default:
                    throw new InvalidOperationException(L.Get("ActionUnknown"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MessageBox.Show( ex.Message, L.Get("ActionFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return new ActionResult(0, 0, [ex.Message], false);
        }
    }

    private static ActionResult Ok() => new(1, 0, [], false);

    private static void StartProcess(MenuAction action, InvocationContext context, bool script)
    {
        var path = ExpandSingle(action.Path, context);
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
            : ExpandSingle(action.WorkingDirectory, context);
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
        foreach (var arg in ExpandArguments(action.Arguments, context)) info.ArgumentList.Add(arg);
        _ = Process.Start(info) ?? throw new InvalidOperationException(L.Get("ActionStartFailed"));
    }

    private static void SendShortcut(string? shortcut, InvocationContext context)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) throw new InvalidOperationException(L.Get("ActionShortcutMissing"));
        if (context.SourceWindow == IntPtr.Zero || !NativeMethods.IsWindow(context.SourceWindow))
            throw new InvalidOperationException(L.Get("ActionSourceWindowClosed"));
        if (!NativeMethods.SetForegroundWindow(context.SourceWindow) ||
            NativeMethods.GetForegroundWindow() != context.SourceWindow)
            throw new InvalidOperationException(L.Get("ActionShortcutFocusFailed"));
        KeyboardSender.Send(shortcut);
    }

    private static void InsertText(string text, InvocationContext context)
    {
        if (string.IsNullOrEmpty(text)) throw new InvalidOperationException(L.Get("ActionSnippetEmpty"));
        if (context.SourceWindow == IntPtr.Zero || !NativeMethods.IsWindow(context.SourceWindow))
            throw new InvalidOperationException(L.Get("ActionSourceWindowClosed"));
        if (!NativeMethods.SetForegroundWindow(context.SourceWindow) || NativeMethods.GetForegroundWindow() != context.SourceWindow)
            throw new InvalidOperationException(L.Get("ActionTextFocusFailed"));
        KeyboardSender.InsertText(text);
    }

    private static async Task<ActionResult> ExecuteFileOperationAsync(MenuAction action, InvocationContext context,
        nint owner, CancellationToken cancellationToken)
    {
        if (context.SelectedPaths.Count == 0)
            throw new InvalidOperationException(L.Get("ActionNoSelection"));
        var destination = ExpandSingle(action.Path, context);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(L.Format("ActionDestinationUnavailable", destination));
        var move = action.Kind == ActionKind.MoveSelection;
        using var dialog = new FileOperationProgressDialog(context.SelectedPaths.Count);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        dialog.CancelRequested += (_, _) => linked.Cancel();
        dialog.Activate();
        var ui = SynchronizationContext.Current ?? throw new InvalidOperationException("UI thread required.");
        var result = await Task.Run(() => CopyOrMove(context.SelectedPaths, destination, move, linked.Token,
            (index, item) => ui.Post(_ => dialog.Report(index, item), null),
            path =>
            {
                FileConflictChoice choice = FileConflictChoice.Cancel;
                var completion = new TaskCompletionSource<FileConflictChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
                ui.Post(async _ => { try { completion.SetResult(await dialog.AskConflict(path)); } catch (Exception ex) { completion.SetException(ex); } }, null);
                choice = completion.Task.GetAwaiter().GetResult();
                return choice;
            }));
        dialog.Dispose();
        var summary = L.Format("ActionSummary", result.Succeeded, result.Skipped, result.Failures.Count) +
            (result.Cancelled ? L.Get("ActionCancelled") : "") +
            (result.Failures.Count > 0 ? "\n\n" + string.Join("\n", result.Failures) : "");
        MessageBox.Show( summary, L.Get("ActionFileResult"), MessageBoxButtons.OK,
            result.Failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        return result;
    }

    private static ActionResult CopyOrMove(IReadOnlyList<string> sources, string destination, bool move,
        CancellationToken cancellationToken, Action<int, string> progress, Func<string, FileConflictChoice> conflict)
    {
        var succeeded = 0;
        var skipped = 0;
        var failures = new List<string>();
        var cancelled = false;
        for (var index = 0; index < sources.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            var source = sources[index];
            progress(index, source);
            try
            {
                var target = Path.Combine(destination, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
                var exists = File.Exists(target) || Directory.Exists(target);
                if (exists)
                {
                    switch (conflict(target))
                    {
                        case FileConflictChoice.Skip: skipped++; continue;
                        case FileConflictChoice.Cancel: cancelled = true; break;
                    }
                    if (cancelled) break;
                }
                if (Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar).Equals(
                    Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(L.Get("ActionSamePath"));
                if (Directory.Exists(source))
                {
                    var sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var targetFull = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (targetFull.StartsWith(sourceFull, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(L.Get("ActionNestedFolder"));
                    if (exists) ReplaceDirectory(source, target, cancellationToken);
                    else CopyDirectoryNew(source, target, cancellationToken);
                    if (move) Directory.Delete(source, true);
                }
                else if (File.Exists(source))
                {
                    if (exists) ReplaceFile(source, target, cancellationToken);
                    else if (move) File.Move(source, target);
                    else File.Copy(source, target);
                    if (exists && move) File.Delete(source);
                }
                else throw new FileNotFoundException(L.Get("ActionSourceUnavailable"), source);
                succeeded++;
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex) { failures.Add($"{source}: {ex.Message}"); }
        }
        progress(sources.Count, L.Get("ActionDone"));
        return new ActionResult(succeeded, skipped, failures, cancelled);
    }

    private static void CopyDirectory(string source, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.EnumerateDirectories(source))
            CopyDirectory(folder, Path.Combine(target, Path.GetFileName(folder)), cancellationToken);
    }

    private static void CopyDirectoryNew(string source, string target, CancellationToken cancellationToken)
    {
        var staged = target + ".m3nuxa-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            CopyDirectory(source, staged, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staged, target);
        }
        finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
    }

    private static void ReplaceFile(string source, string target, CancellationToken cancellationToken)
    {
        if (Directory.Exists(target)) throw new IOException(L.Get("ActionTargetIsFolder"));
        var staged = target + ".m3nuxa-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(source, staged);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, target, true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private static void ReplaceDirectory(string source, string target, CancellationToken cancellationToken)
    {
        if (File.Exists(target)) throw new IOException(L.Get("ActionTargetIsFile"));
        var suffix = ".m3nuxa-" + Guid.NewGuid().ToString("N");
        var staged = target + suffix + ".tmp";
        var backup = target + suffix + ".backup";
        var backedUp = false;
        try
        {
            CopyDirectory(source, staged, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(target, backup);
            backedUp = true;
            Directory.Move(staged, target);
            backedUp = false;
            try { Directory.Delete(backup, true); } catch { /* Replacement succeeded; preserve backup if cleanup fails. */ }
        }
        catch
        {
            if (backedUp && !Directory.Exists(target)) Directory.Move(backup, target);
            throw;
        }
        finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
    }

    private static string ExpandSingle(string? template, InvocationContext context)
    {
        if (string.IsNullOrWhiteSpace(template)) throw new InvalidOperationException(L.Get("ActionPathRequired"));
        return TemplateExpander.Expand(template, context);
    }

    private static IReadOnlyList<string> ExpandArguments(string? template, InvocationContext context)
    {
        if (string.IsNullOrWhiteSpace(template)) return [];
        var arguments = new List<string>();
        foreach (var token in CommandLineTokenizer.Split(template))
        {
            if (token == "{selectedPaths}")
            {
                if (context.SelectedPaths.Count == 0) throw new InvalidOperationException(L.Get("ActionPathsMissing"));
                arguments.AddRange(context.SelectedPaths);
            }
            else arguments.Add(TemplateExpander.Expand(token, context));
        }
        return arguments;
    }
}

internal static class TemplateExpander
{
    private static readonly Regex Variable = new(@"\{([a-zA-Z]+)\}", RegexOptions.Compiled);

    public static string Expand(string template, InvocationContext context) => Variable.Replace(template, match =>
    {
        var first = context.SelectedPaths.FirstOrDefault();
        return match.Groups[1].Value switch
        {
            "currentFolder" => context.CurrentFolder ?? throw Missing(L.Get("ContextCurrentFolder")),
            "selectedPath" => first ?? throw Missing(L.Get("ContextSelectedPath")),
            "fileName" => first is null ? throw Missing(L.Get("ContextSelectedPath")) : Path.GetFileName(first),
            "extension" => first is null ? throw Missing(L.Get("ContextSelectedPath")) : Path.GetExtension(first),
            "parentFolder" => first is null ? throw Missing(L.Get("ContextSelectedPath")) : Path.GetDirectoryName(first) ?? "",
            "urlEncodedText" or "selectedText" => throw Missing(L.Get("ContextSelectedText")),
            "selectedPaths" => throw new InvalidOperationException(L.Get("ActionSelectedPathsArgument")),
            _ => throw new InvalidOperationException(L.Format("ActionUnknownVariable", match.Value))
        };
    });

    private static InvalidOperationException Missing(string name) => new(L.Format("ActionMissingContext", name));
}

internal static class CommandLineTokenizer
{
    public static IReadOnlyList<string> Split(string value)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var c in value)
        {
            if (c == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
            }
            else { token.Append(c); started = true; }
        }
        if (quoted) throw new InvalidOperationException(L.Get("ActionUnclosedQuote"));
        if (started) result.Add(token.ToString());
        return result;
    }
}

internal enum FileConflictChoice { Replace, Skip, Cancel }

internal static partial class NativeMethods
{
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr handle);
}
