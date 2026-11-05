namespace m3nuxa.App;

internal sealed class InsertTextHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.InsertText;

    public Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        InputActionOperations.InsertText(TemplateExpander.Expand(action.Text, context), context);
        return Task.FromResult(ActionExecutor.Ok());
    }
}
