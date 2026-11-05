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
    private static readonly IReadOnlyDictionary<ActionKind, IActionHandler> Handlers = CreateHandlers();

    private static IReadOnlyDictionary<ActionKind, IActionHandler> CreateHandlers()
    {
        IActionHandler[] handlers = [new OpenFolderHandler(), new OpenItemHandler(), new RunProgramHandler(),
            new RunScriptHandler(), new SendShortcutHandler(), new CopySelectionHandler(), new MoveSelectionHandler(),
            new InsertTextHandler(), new DelayHandler(), new SetClipboardTextHandler(), new PasteTextHandler()];
        var byKind = handlers.ToDictionary(handler => handler.Kind);
        if (Enum.GetValues<ActionKind>().Any(kind => !byKind.ContainsKey(kind)))
            throw new InvalidOperationException("An action handler is missing.");
        return byKind;
    }

    public static async Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context,
        nint owner = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            if (UsesClipboardValue(action) && context.ClipboardText is null)
                context = context with { ClipboardText = await ClipboardOperations.ReadTextAsync(cancellationToken) };
            if (!Handlers.TryGetValue(action.Kind, out var handler))
                throw new InvalidOperationException(L.Get("ActionUnknown"));
            return await handler.ExecuteAsync(action, context, owner, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ActionResult(0, 0, [UsesSensitiveValue(action) && ex is not WorkflowSafeException ? L.Get("WorkflowSensitiveError") : ex.Message], false);
        }
    }

    internal static ActionResult Ok() => new(1, 0, [], false);

    internal static bool UsesClipboardValue(MenuAction action) =>
        new[] { action.Path, action.Arguments, action.WorkingDirectory, action.Text }
            .Any(value => value?.Contains("{clipboardText}", StringComparison.Ordinal) == true);

    internal static bool UsesSensitiveValue(MenuAction action) =>
        action.Kind is ActionKind.SetClipboardText or ActionKind.PasteText || new[] { action.Path, action.Arguments, action.WorkingDirectory, action.Text }
            .Any(value => value?.Contains("{clipboardText}", StringComparison.Ordinal) == true ||
                          value?.Contains("{env:", StringComparison.Ordinal) == true);

    internal static bool RequiresInputGate(MenuAction action) => UsesClipboardValue(action) ||
        action.Kind is ActionKind.SendShortcut or ActionKind.InsertText or ActionKind.PasteText or ActionKind.SetClipboardText;
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


internal static partial class NativeMethods
{
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr handle);
}
