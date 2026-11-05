namespace m3nuxa.App;

internal sealed class SendShortcutHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.SendShortcut;

    public Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        InputActionOperations.SendShortcut(action.Shortcut, context);
        return Task.FromResult(ActionExecutor.Ok());
    }
}
