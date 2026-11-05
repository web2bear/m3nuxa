namespace m3nuxa.App;

internal sealed class DelayHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.Delay;

    public async Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        await Task.Delay(action.DelayMilliseconds, cancellationToken);
        return ActionExecutor.Ok();
    }
}
