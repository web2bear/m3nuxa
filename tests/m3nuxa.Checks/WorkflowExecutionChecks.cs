using m3nuxa.App;
using System.Diagnostics;

internal static class WorkflowExecutionChecks
{
    public static void Run(string root) => RunAsync(root).GetAwaiter().GetResult();

    private static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "workflow-execution");
        Directory.CreateDirectory(directory);
        var context = new InvocationContext(IntPtr.Zero, [], null);
        var empty = await WorkflowExecutor.RunAsync([], context, CancellationToken.None);
        Check(!empty.Success && empty.StepIndex == -1 && empty.Error == L.Get("WorkflowNoSteps"), "empty sequence reports an error on invocation");
        foreach (var invalid in new[] { new MenuAction { Kind = ActionKind.OpenFolder }, new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = -1 }, new MenuAction { Kind = ActionKind.SendShortcut }, new MenuAction { Kind = ActionKind.InsertText }, new MenuAction { Kind = ActionKind.RunProgram, Path = "x", TimeoutMilliseconds = 0 } })
        {
            var invalidResult = await WorkflowExecutor.RunAsync([invalid], context, CancellationToken.None);
            Check(!invalidResult.Success && !string.IsNullOrWhiteSpace(invalidResult.Error), "invalid step reports an error on invocation");
        }
        var unknown = await ActionExecutor.ExecuteAsync(new MenuAction { Kind = (ActionKind)999 }, context);
        Check(!unknown.Success && unknown.Error == L.Get("ActionUnknown"), "unknown action reports a safe error");
        var sensitivePath = Path.Combine(directory, "sensitive missing folder");
        var sensitive = await ActionExecutor.ExecuteAsync(
            new MenuAction { Kind = ActionKind.OpenFolder, Path = "{clipboardText}" },
            context with { ClipboardText = sensitivePath });
        Check(!sensitive.Success && sensitive.Error == L.Get("WorkflowSensitiveError") &&
            !sensitive.Error.Contains(sensitivePath, StringComparison.Ordinal), "handler errors do not expose sensitive values");
        var observed = new List<int>();
        var timer = Stopwatch.StartNew();
        var result = await WorkflowExecutor.RunAsync([
            new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 120 },
            new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 10 }
        ], context, CancellationToken.None, (index, _) => observed.Add(index));
        Check(result.Success && observed.SequenceEqual([0, 1]) && timer.ElapsedMilliseconds >= 100, "delays execute in order and wait");

        observed.Clear();
        result = await WorkflowExecutor.RunAsync([
            new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 0 },
            new MenuAction { Kind = ActionKind.OpenFolder, Path = Path.Combine(directory, "missing") },
            new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 0 }
        ], context, CancellationToken.None, (index, _) => observed.Add(index));
        Check(!result.Success && !result.Cancelled && result.StepIndex == 1 && observed.SequenceEqual([0, 1]), "failure stops following steps");

        using (var cancel = new CancellationTokenSource())
        {
            observed.Clear();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = WorkflowExecutor.RunAsync([
                new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 30000 },
                new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 0 }
            ], context, cancel.Token, (index, _) => { observed.Add(index); started.TrySetResult(); });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.Cancelled && observed.SequenceEqual([0]), "cancelling delay stops following steps");
        }

        var sourceDir = Path.Combine(directory, "source");
        var targetDir = Path.Combine(directory, "target");
        Directory.CreateDirectory(sourceDir); Directory.CreateDirectory(targetDir);
        var sourceFile = Path.Combine(sourceDir, "move.txt");
        var targetFile = Path.Combine(targetDir, "move.txt");
        var copyOrMove = typeof(FileActionOperations).GetMethod("CopyOrMove", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        ActionResult RunMove(Func<string, FileConflictChoice> conflict) => (ActionResult)copyOrMove.Invoke(null,
            [new[] { sourceFile }, targetDir, true, CancellationToken.None, (Action<int, string>)((_, _) => { }), conflict])!;
        File.WriteAllText(sourceFile, "new content");
        var moved = RunMove(_ => throw new Exception("No conflict expected"));
        Check(moved.Success && !File.Exists(sourceFile) && File.ReadAllText(targetFile) == "new content", "file moves to destination");
        File.WriteAllText(sourceFile, "replacement");
        var skipped = RunMove(_ => FileConflictChoice.Skip);
        Check(skipped.Skipped == 1 && File.Exists(sourceFile) && File.ReadAllText(targetFile) == "new content", "skip preserves both files");
        var replaced = RunMove(_ => FileConflictChoice.Replace);
        Check(replaced.Success && !File.Exists(sourceFile) && File.ReadAllText(targetFile) == "replacement", "replace moves file to existing destination");
        var samePath = (ActionResult)copyOrMove.Invoke(null,
            [new[] { targetFile }, targetDir, true, CancellationToken.None, (Action<int, string>)((_, _) => { }),
                (Func<string, FileConflictChoice>)(_ => throw new Exception("Same-path move must not show a conflict"))])!;
        Check(!samePath.Success && File.ReadAllText(targetFile) == "replacement", "same-path move fails safely without conflict prompt");

        var exitScript = Path.Combine(directory, "exit7.cmd");
        File.WriteAllText(exitScript, "@echo off\r\nexit /b 7\r\n");
        observed.Clear();
        result = await WorkflowExecutor.RunAsync([
            new MenuAction { Kind = ActionKind.RunScript, Path = exitScript, HideConsole = true, WaitForExit = true },
            new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 0 }
        ], context, CancellationToken.None, (index, _) => observed.Add(index));
        Check(!result.Success && result.StepIndex == 0 && observed.SequenceEqual([0]) && result.Error is not null && result.Error.Contains('7'), "nonzero exit stops subsequent steps");

        var done = Path.Combine(directory, "completed.txt");
        var slowScript = Path.Combine(directory, "slow.cmd");
        File.WriteAllText(slowScript, $"@echo off\r\nping -n 3 127.0.0.1 >nul\r\necho done>\"{done}\"\r\n");
        observed.Clear();
        result = await WorkflowExecutor.RunAsync([
            new MenuAction { Kind = ActionKind.RunScript, Path = slowScript, HideConsole = true, WaitForExit = true, TimeoutMilliseconds = 10000 },
            new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 0 }
        ], context, CancellationToken.None, (index, _) => observed.Add(index));
        Check(result.Success && File.Exists(done) && observed.SequenceEqual([0, 1]), "waiting for process finishes before next step");

        File.Delete(done);
        using (var cancel = new CancellationTokenSource())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = WorkflowExecutor.RunAsync([
                new MenuAction { Kind = ActionKind.RunScript, Path = slowScript, HideConsole = true, WaitForExit = true },
                new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 0 }
            ], context, cancel.Token, (index, _) => { if (index == 0) started.TrySetResult(); });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(300);
            cancel.Cancel();
            result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.Cancelled && result.StepIndex == 0, "process wait can be cancelled");
            await WaitForFile(done, TimeSpan.FromSeconds(10));
            Check(File.Exists(done), "cancelling wait does not terminate process");
        }
    }

    private static async Task WaitForFile(string path, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && timer.Elapsed < timeout) await Task.Delay(50);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
