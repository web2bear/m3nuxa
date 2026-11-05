namespace m3nuxa.App;

internal sealed class OpenItemHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.OpenItem;

    public Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        var target = ActionParameters.ExpandSingle(action.Path, context, encodeUrlComponents: action.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || action.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (!(Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") && !File.Exists(target))
            throw new FileNotFoundException(L.Format("ActionFileUnavailableTarget", target));
        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        return Task.FromResult(ActionExecutor.Ok());
    }
}
