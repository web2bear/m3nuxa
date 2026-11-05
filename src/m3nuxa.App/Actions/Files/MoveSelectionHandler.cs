namespace m3nuxa.App;

internal sealed class MoveSelectionHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.MoveSelection;

    public async Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        return await FileActionOperations.ExecuteFileOperationAsync(action, context, owner, cancellationToken);
    }
}
