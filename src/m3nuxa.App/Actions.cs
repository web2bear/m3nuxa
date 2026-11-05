using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace m3nuxa.App;

internal sealed record InvocationContext(IntPtr SourceWindow, IReadOnlyList<string> SelectedPaths, string? CurrentFolder)
{
    public string? ClipboardText { get; init; }
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

// Messages in this exception contain no expanded parameter values and may be shown to the user.
internal sealed class WorkflowSafeException(string message) : InvalidOperationException(message) { }

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
            if (UsesClipboardValue(action) && context.ClipboardText is null)
                context = context with { ClipboardText = await ClipboardOperations.ReadTextAsync(cancellationToken) };
            switch (action.Kind)
            {
                case ActionKind.OpenFolder:
                    var folder = ExpandSingle(action.Path, context);
                    if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(L.Format("ActionFolderUnavailable", folder));
                    Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/n", folder }, UseShellExecute = false });
                    return Ok();
                case ActionKind.OpenItem:
                    var target = ExpandSingle(action.Path, context, encodeUrlComponents: action.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || action.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
                    if (!(Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") && !File.Exists(target))
                        throw new FileNotFoundException(L.Format("ActionFileUnavailableTarget", target));
                    _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    return Ok();
                case ActionKind.RunProgram:
                    await StartProcessAsync(action, context, false, cancellationToken);
                    return Ok();
                case ActionKind.RunScript:
                    await StartProcessAsync(action, context, true, cancellationToken);
                    return Ok();
                case ActionKind.Delay:
                    await Task.Delay(action.DelayMilliseconds, cancellationToken);
                    return Ok();
                case ActionKind.SetClipboardText:
                    await ClipboardOperations.SetTextAsync(TemplateExpander.Expand(action.Text, context), cancellationToken);
                    return Ok();
                case ActionKind.PasteText:
                    await ClipboardOperations.PasteTextAsync(TemplateExpander.Expand(action.Text, context), context, action.RestoreClipboard, cancellationToken);
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
            return new ActionResult(0, 0, [UsesSensitiveValue(action) && ex is not WorkflowSafeException ? L.Get("WorkflowSensitiveError") : ex.Message], false);
        }
    }

    private static ActionResult Ok() => new(1, 0, [], false);

    internal static bool UsesClipboardValue(MenuAction action) =>
        new[] { action.Path, action.Arguments, action.WorkingDirectory, action.Text }
            .Any(value => value?.Contains("{clipboardText}", StringComparison.Ordinal) == true);

    internal static bool UsesSensitiveValue(MenuAction action) =>
        action.Kind is ActionKind.SetClipboardText or ActionKind.PasteText || new[] { action.Path, action.Arguments, action.WorkingDirectory, action.Text }
            .Any(value => value?.Contains("{clipboardText}", StringComparison.Ordinal) == true ||
                          value?.Contains("{env:", StringComparison.Ordinal) == true);

    internal static bool RequiresInputGate(MenuAction action) => UsesClipboardValue(action) ||
        action.Kind is ActionKind.SendShortcut or ActionKind.InsertText or ActionKind.PasteText or ActionKind.SetClipboardText;

    private static async Task StartProcessAsync(MenuAction action, InvocationContext context, bool script, CancellationToken cancellationToken)
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

    private static void SendShortcut(string? shortcut, InvocationContext context)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) throw new InvalidOperationException(L.Get("ActionShortcutMissing"));
        if (context.SourceWindow == IntPtr.Zero || !NativeMethods.IsWindow(context.SourceWindow))
            throw new WorkflowSafeException(L.Get("ActionSourceWindowClosed"));
        if (!NativeMethods.SetForegroundWindow(context.SourceWindow) ||
            NativeMethods.GetForegroundWindow() != context.SourceWindow)
            throw new WorkflowSafeException(L.Get("ActionShortcutFocusFailed"));
        KeyboardSender.Send(shortcut);
    }

    private static void InsertText(string text, InvocationContext context)
    {
        if (string.IsNullOrEmpty(text)) throw new InvalidOperationException(L.Get("ActionSnippetEmpty"));
        if (context.SourceWindow == IntPtr.Zero || !NativeMethods.IsWindow(context.SourceWindow))
            throw new WorkflowSafeException(L.Get("ActionSourceWindowClosed"));
        if (!NativeMethods.SetForegroundWindow(context.SourceWindow) || NativeMethods.GetForegroundWindow() != context.SourceWindow)
            throw new WorkflowSafeException(L.Get("ActionTextFocusFailed"));
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
        var token = linked.Token;
        var result = await Task.Run(() => CopyOrMove(context.SelectedPaths, destination, move, token,
            (index, item) => ui.Post(_ => dialog.Report(index, item), null),
            path =>
            {
                var completion = new TaskCompletionSource<FileConflictChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
                ui.Post(async _ =>
                {
                    try
                    {
                        if (token.IsCancellationRequested) { completion.TrySetCanceled(token); return; }
                        completion.TrySetResult(await dialog.AskConflict(UsesSensitiveValue(action) ? "…" : path, token));
                    }
                    catch (Exception ex) { completion.TrySetException(ex); }
                }, null);
                return completion.Task.WaitAsync(token).GetAwaiter().GetResult();
            }));
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
                if (Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar).Equals(
                    Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(L.Get("ActionSamePath"));
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

    private static string ExpandSingle(string? template, InvocationContext context, bool encodeUrlComponents = false)
    {
        if (string.IsNullOrWhiteSpace(template)) throw new InvalidOperationException(L.Get("ActionPathRequired"));
        return TemplateExpander.Expand(template, context, encodeUrlComponents);
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
    private static readonly Regex Variable = new(@"\{([a-zA-Z]+(?::[^{}]+)?)\}", RegexOptions.Compiled);

    public static string Expand(string template, InvocationContext context, bool encodeUrlComponents = false) =>
        Variable.Replace(template, match =>
        {
            var value = ExpandVariable(match, context);
            return encodeUrlComponents ? Uri.EscapeDataString(value) : value;
        });

    private static string ExpandVariable(Match match, InvocationContext context)
    {
        var first = context.SelectedPaths.FirstOrDefault();
        var key = match.Groups[1].Value;
        if (key.StartsWith("env:", StringComparison.Ordinal))
            return Environment.GetEnvironmentVariable(key[4..]) ?? throw Missing(key);
        if (key.StartsWith("knownFolder:", StringComparison.Ordinal))
            return KnownFolders.Resolve(key[12..]);
        return key switch
        {
            "clipboardText" => context.ClipboardText ?? throw Missing("clipboard text"),
            "currentFolder" => context.CurrentFolder ?? throw Missing(L.Get("ContextCurrentFolder")),
            "selectedPath" => first ?? throw Missing(L.Get("ContextSelectedPath")),
            "fileName" => first is null ? throw Missing(L.Get("ContextSelectedPath")) : Path.GetFileName(first),
            "extension" => first is null ? throw Missing(L.Get("ContextSelectedPath")) : Path.GetExtension(first),
            "parentFolder" => first is null ? throw Missing(L.Get("ContextSelectedPath")) : Path.GetDirectoryName(first) ?? "",
            "urlEncodedText" or "selectedText" => throw Missing(L.Get("ContextSelectedText")),
            "selectedPaths" => throw new InvalidOperationException(L.Get("ActionSelectedPathsArgument")),
            _ => throw new InvalidOperationException(L.Format("ActionUnknownVariable", match.Value))
        };
    }

    private static WorkflowSafeException Missing(string name) => new(L.Format("ActionMissingContext", name));
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
