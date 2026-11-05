namespace m3nuxa.App;

internal sealed class RunProgramHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.RunProgram;

    public async Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        await ProcessActionOperations.StartProcessAsync(action, context, false, cancellationToken);
        return ActionExecutor.Ok();
    }
}
