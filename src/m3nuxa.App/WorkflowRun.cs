namespace m3nuxa.App;

internal sealed class WorkflowRun
{
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public CancellationTokenSource Cancellation { get; } = new();
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int StepIndex { get; set; } = -1;
    public string StepKind { get; set; } = "";
    public string? Error { get; set; }
    public IReadOnlyList<FileStepSummary> FileSteps { get; set; } = [];
    public bool Finished { get; set; }
    public bool Cancelled { get; set; }
}
