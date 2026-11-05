namespace m3nuxa.App;

internal sealed class OpenFolderHandler : IActionHandler
{
    public ActionKind Kind => ActionKind.OpenFolder;

    public Task<ActionResult> ExecuteAsync(MenuAction action, InvocationContext context, nint owner, CancellationToken cancellationToken)
    {
        var folder = ActionParameters.ExpandSingle(action.Path, context);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(L.Format("ActionFolderUnavailable", folder));
        // Explorer uses a comma to separate its switch from the quoted path.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { Arguments = $"/n,\"{folder}\"", UseShellExecute = false });
        return Task.FromResult(ActionExecutor.Ok());
    }
}
