namespace m3nuxa.App;

internal sealed class RunScriptHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.RunScript;

    public async Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        await ProcessActionOperations.StartProcessAsync(action, context, true, cancellationToken);
        return ActionExecutor.Ok();
    }
}
