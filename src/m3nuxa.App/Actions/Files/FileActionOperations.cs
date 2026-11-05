
namespace m3nuxa.App;

internal static class FileActionOperations
{
    internal static async Task<ActionResult> ExecuteFileOperationAsync(MenuAction action, InvocationContext context,
        nint owner, CancellationToken cancellationToken)
    {
        if (context.SelectedPaths.Count == 0)
            throw new InvalidOperationException(L.Get("ActionNoSelection"));
        var destination = ActionParameters.ExpandSingle(action.Path, context);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(L.Format("ActionDestinationUnavailable", destination));
        var move = action.Kind == ActionKind.MoveSelection;
        using var dialog = new FileOperationProgressDialog(context.SelectedPaths.Count);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        dialog.CancelRequested += (_, _) => linked.Cancel();
        dialog.Activate();
        var ui = SynchronizationContext.Current ?? throw new InvalidOperationException("UI thread required.");
        var token = linked.Token;
        var result = await Task.Run(() => CopyOrMove(context.SelectedPaths, destination, move, token,
            (index, item) => ui.Post(_ => dialog.Report(index, item), null),
            path =>
            {
                var completion = new TaskCompletionSource<FileConflictChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
                ui.Post(async _ =>
                {
                    try
                    {
                        if (token.IsCancellationRequested) { completion.TrySetCanceled(token); return; }
                        completion.TrySetResult(await dialog.AskConflict(ActionExecutor.UsesSensitiveValue(action) ? "…" : path, token));
                    }
                    catch (Exception ex) { completion.TrySetException(ex); }
                }, null);
                return completion.Task.WaitAsync(token).GetAwaiter().GetResult();
            }));
        return result;
    }

    private static ActionResult CopyOrMove(IReadOnlyList<string> sources, string destination, bool move,
        CancellationToken cancellationToken, Action<int, string> progress, Func<string, FileConflictChoice> conflict)
    {
        var succeeded = 0;
        var skipped = 0;
        var failures = new List<string>();
        var cancelled = false;
        for (var index = 0; index < sources.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            var source = sources[index];
            progress(index, source);
            try
            {
                var target = Path.Combine(destination, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
                if (Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar).Equals(
                    Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(L.Get("ActionSamePath"));
                var exists = File.Exists(target) || Directory.Exists(target);
                if (exists)
                {
                    switch (conflict(target))
                    {
                        case FileConflictChoice.Skip: skipped++; continue;
                        case FileConflictChoice.Cancel: cancelled = true; break;
                    }
                    if (cancelled) break;
                }
                if (Directory.Exists(source))
                {
                    var sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var targetFull = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (targetFull.StartsWith(sourceFull, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(L.Get("ActionNestedFolder"));
                    if (exists) ReplaceDirectory(source, target, cancellationToken);
                    else CopyDirectoryNew(source, target, cancellationToken);
                    if (move) Directory.Delete(source, true);
                }
                else if (File.Exists(source))
                {
                    if (exists) ReplaceFile(source, target, cancellationToken);
                    else if (move) File.Move(source, target);
                    else File.Copy(source, target);
                    if (exists && move) File.Delete(source);
                }
                else throw new FileNotFoundException(L.Get("ActionSourceUnavailable"), source);
                succeeded++;
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex) { failures.Add($"{source}: {ex.Message}"); }
        }
        progress(sources.Count, L.Get("ActionDone"));
        return new ActionResult(succeeded, skipped, failures, cancelled);
    }

    private static void CopyDirectory(string source, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.EnumerateDirectories(source))
            CopyDirectory(folder, Path.Combine(target, Path.GetFileName(folder)), cancellationToken);
    }

    private static void CopyDirectoryNew(string source, string target, CancellationToken cancellationToken)
    {
        var staged = target + ".m3nuxa-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            CopyDirectory(source, staged, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staged, target);
        }
        finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
    }

    private static void ReplaceFile(string source, string target, CancellationToken cancellationToken)
    {
        if (Directory.Exists(target)) throw new IOException(L.Get("ActionTargetIsFolder"));
        var staged = target + ".m3nuxa-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(source, staged);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, target, true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private static void ReplaceDirectory(string source, string target, CancellationToken cancellationToken)
    {
        if (File.Exists(target)) throw new IOException(L.Get("ActionTargetIsFile"));
        var suffix = ".m3nuxa-" + Guid.NewGuid().ToString("N");
        var staged = target + suffix + ".tmp";
        var backup = target + suffix + ".backup";
        var backedUp = false;
        try
        {
            CopyDirectory(source, staged, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(target, backup);
            backedUp = true;
            Directory.Move(staged, target);
            backedUp = false;
            try { Directory.Delete(backup, true); } catch { /* Replacement succeeded; preserve backup if cleanup fails. */ }
        }
        catch
        {
            if (backedUp && !Directory.Exists(target)) Directory.Move(backup, target);
            throw;
        }
        finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
    }


}

internal enum FileConflictChoice { Replace, Skip, Cancel }
