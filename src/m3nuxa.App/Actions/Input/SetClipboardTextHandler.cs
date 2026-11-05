namespace m3nuxa.App;

internal sealed class SetClipboardTextHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.SetClipboardText;

    public async Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        await ClipboardOperations.SetTextAsync(TemplateExpander.Expand(action.Text, context), cancellationToken);
        return ActionExecutor.Ok();
    }
}
