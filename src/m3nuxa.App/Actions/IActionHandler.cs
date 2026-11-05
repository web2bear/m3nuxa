namespace m3nuxa.App;

internal interface IActionHandler
{
    ActionKind Kind { get; }
    Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken);
}
