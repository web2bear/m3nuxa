
namespace m3nuxa.App;

internal static class InputActionOperations
{
    internal static void SendShortcut(string? shortcut, InvocationContext context)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) throw new InvalidOperationException(L.Get("ActionShortcutMissing"));
        if (context.SourceWindow == IntPtr.Zero || !NativeMethods.IsWindow(context.SourceWindow))
            throw new WorkflowSafeException(L.Get("ActionSourceWindowClosed"));
        if (!NativeMethods.SetForegroundWindow(context.SourceWindow) ||
            NativeMethods.GetForegroundWindow() != context.SourceWindow)
            throw new WorkflowSafeException(L.Get("ActionShortcutFocusFailed"));
        KeyboardSender.Send(shortcut);
    }

    internal static void InsertText(string text, InvocationContext context)
    {
        if (string.IsNullOrEmpty(text)) throw new InvalidOperationException(L.Get("ActionSnippetEmpty"));
        if (context.SourceWindow == IntPtr.Zero || !NativeMethods.IsWindow(context.SourceWindow))
            throw new WorkflowSafeException(L.Get("ActionSourceWindowClosed"));
        if (!NativeMethods.SetForegroundWindow(context.SourceWindow) || NativeMethods.GetForegroundWindow() != context.SourceWindow)
            throw new WorkflowSafeException(L.Get("ActionTextFocusFailed"));
        KeyboardSender.InsertText(text);
    }


}
