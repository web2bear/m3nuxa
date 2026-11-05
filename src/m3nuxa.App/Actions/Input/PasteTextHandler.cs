namespace m3nuxa.App;

internal sealed class PasteTextHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.PasteText;

    public async Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        await ClipboardOperations.PasteTextAsync(TemplateExpander.Expand(action.Text, context), context, action.RestoreClipboard, cancellationToken);
        return ActionExecutor.Ok();
    }
}
