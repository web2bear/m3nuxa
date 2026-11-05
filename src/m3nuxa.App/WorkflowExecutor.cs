using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace m3nuxa.App;

internal sealed record FileStepSummary(int StepIndex, int Succeeded, int Skipped, int Failed, bool Cancelled);

internal sealed record WorkflowResult(bool Cancelled, int StepIndex, string? Error)
{
    public bool Success => !Cancelled && Error is null;
    // Counts only; file paths and expanded parameter values must never be stored here.
    public IReadOnlyList<FileStepSummary> FileSteps { get; init; } = [];
}

internal static class WorkflowExecutor
{
    // All clipboard and input operations share this gate, even between independent launches.
    private static readonly SemaphoreSlim InputGate = new(1, 1);

    public static async Task<WorkflowResult> RunAsync(IReadOnlyList<MenuAction> steps,
        InvocationContext context, CancellationToken cancellationToken, Action<int, MenuAction>? progress = null)
    {
        var index = -1;
        var fileSteps = new List<FileStepSummary>();
        WorkflowResult Finish(bool cancelled, int stepIndex, string? error) =>
            new(cancelled, stepIndex, error) { FileSteps = fileSteps.ToArray() };
        try
        {
            for (index = 0; index < steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = steps[index];
                progress?.Invoke(index, step);
                var locked = false;
                try
                {
                    if (ActionExecutor.RequiresInputGate(step))
                    {
                        await InputGate.WaitAsync(cancellationToken);
                        locked = true;
                    }
                    var stepContext = context;
                    // A process may run for hours; only hold the gate long enough to capture its input.
                    if (locked && ActionExecutor.UsesClipboardValue(step) &&
                        step.Kind is not (ActionKind.SendShortcut or ActionKind.InsertText or ActionKind.PasteText or ActionKind.SetClipboardText))
                    {
                        stepContext = context with { ClipboardText = await ClipboardOperations.ReadTextAsync(cancellationToken) };
                        InputGate.Release();
                        locked = false;
                    }
                    var result = await ActionExecutor.ExecuteAsync(step, stepContext, cancellationToken: cancellationToken);
                    var isFileStep = step.Kind is ActionKind.CopySelection or ActionKind.MoveSelection;
                    if (isFileStep)
                        fileSteps.Add(new FileStepSummary(index, result.Succeeded, result.Skipped, result.Failures.Count, result.Cancelled));
                    if (result.Cancelled) return Finish(true, index, null);
                    if (!result.Success)
                    {
                        var error = isFileStep && ActionExecutor.UsesSensitiveValue(step)
                            ? L.Get("WorkflowSensitiveError") : result.Error ?? L.Get("ActionFailed");
                        return Finish(false, index, error);
                    }
                }
                finally { if (locked) InputGate.Release(); }
            }
            return Finish(false, Math.Max(0, steps.Count - 1), null);
        }
        catch (OperationCanceledException) { return Finish(true, index, null); }
        catch (Exception ex)
        {
            var sensitive = index >= 0 && index < steps.Count && ActionExecutor.UsesSensitiveValue(steps[index]);
            return Finish(false, index,
                sensitive && ex is not WorkflowSafeException ? L.Get("WorkflowSensitiveError") : ex.Message);
        }
    }
}

internal static class ClipboardOperations
{
    private const int Attempts = 5;
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out nint dataObject);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(nint dataObject);

    internal static async Task<string> ReadTextAsync(CancellationToken token)
    {
        for (var i = 0; ; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var contents = Clipboard.GetContent();
                if (!contents.Contains(StandardDataFormats.Text))
                    throw new WorkflowSafeException(L.Get("WorkflowClipboardNoText"));
                return await contents.GetTextAsync();
            }
            catch (COMException) when (i < Attempts - 1) { await Task.Delay(100, token); }
        }
    }

    internal static async Task SetTextAsync(string text, CancellationToken token)
    {
        for (var i = 0; ; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var data = new DataPackage();
                data.SetText(text);
                Clipboard.SetContent(data);
                Clipboard.Flush();
                return;
            }
            catch (COMException) when (i < Attempts - 1) { await Task.Delay(100, token); }
        }
    }

    internal static async Task PasteTextAsync(string text, InvocationContext context, bool restore, CancellationToken token)
    {
        if (context.SourceWindow == IntPtr.Zero || !NativeMethods.IsWindow(context.SourceWindow))
            throw new WorkflowSafeException(L.Get("ActionSourceWindowClosed"));
        nint previous = 0;
        var captured = false;
        uint written = 0;
        try
        {
            if (restore)
            {
                Marshal.ThrowExceptionForHR(OleGetClipboard(out previous));
                captured = true;
            }
            await SetTextAsync(text, token);
            written = GetClipboardSequenceNumber();
            if (!NativeMethods.SetForegroundWindow(context.SourceWindow) || NativeMethods.GetForegroundWindow() != context.SourceWindow)
                throw new WorkflowSafeException(L.Get("ActionTextFocusFailed"));
            KeyboardSender.Send("Ctrl+V");
            // Once Ctrl+V is sent, cancellation must not restore the clipboard early.
            // Windows does not acknowledge when a target has consumed clipboard data;
            // the grace period is therefore best-effort, not a delivery guarantee.
            await Task.Delay(200);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            try
            {
                if (captured && written != 0 && GetClipboardSequenceNumber() == written)
                    Marshal.ThrowExceptionForHR(OleSetClipboard(previous));
            }
            finally { if (previous != 0) Marshal.Release(previous); }
        }
    }
}

internal static class KnownFolders
{
    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    internal static string Resolve(string name)
    {
        if (name == "Temp") return Path.GetTempPath();
        if (name == "Downloads")
        {
            Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(Downloads, 0, 0, out var ptr));
            try { return Marshal.PtrToStringUni(ptr) ?? throw new InvalidOperationException(L.Get("WorkflowFolderUnavailable")); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        var folder = name switch
        {
            "Desktop" => Environment.SpecialFolder.DesktopDirectory,
            "Documents" => Environment.SpecialFolder.MyDocuments,
            "Pictures" => Environment.SpecialFolder.MyPictures,
            "Music" => Environment.SpecialFolder.MyMusic,
            "Videos" => Environment.SpecialFolder.MyVideos,
            "Profile" => Environment.SpecialFolder.UserProfile,
            _ => throw new InvalidOperationException(L.Format("WorkflowFolderUnknown", name))
        };
        var result = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(result) ? throw new InvalidOperationException(L.Format("WorkflowFolderUnavailableNamed", name)) : result;
    }
}
