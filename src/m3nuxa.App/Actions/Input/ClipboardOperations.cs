using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace m3nuxa.App;

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

