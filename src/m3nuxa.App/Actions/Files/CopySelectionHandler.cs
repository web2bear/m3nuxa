namespace m3nuxa.App;

internal sealed class CopySelectionHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.CopySelection;

    public async Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        return await FileActionOperations.ExecuteFileOperationAsync(action, context, owner, cancellationToken);
    }
}
