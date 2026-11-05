using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

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

internal sealed class WorkflowRunsWindow : Window
{
    private readonly List<WorkflowRun> runs;
    private readonly StackPanel rows = new() { Spacing = 10 };
    public WorkflowRunsWindow(List<WorkflowRun> runs)
    {
        this.runs = runs;
        Title = L.Get("WorkflowRuns");
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(650, 520));
        var root = new StackPanel { Padding = new Thickness(20), Spacing = 12 };
        var clear = new Button { Content = L.Get("WorkflowClear") };
        clear.Click += (_, _) => { runs.RemoveAll(run => run.Finished); Refresh(); };
        root.Children.Add(clear);
        root.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Refresh();
    }

    public void Refresh()
    {
        rows.Children.Clear();
        foreach (var run in runs.AsEnumerable().Reverse())
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = run.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var state = !run.Finished ? L.Get("WorkflowRunning") : run.Cancelled ? L.Get("WorkflowCancelled") : run.Error is null ? L.Get("WorkflowSuccess") : L.Get("WorkflowFailed");
            var step = run.StepIndex < 0 ? "" : L.Format("WorkflowStep", run.StepIndex + 1, run.StepKind);
            panel.Children.Add(new TextBlock { Text = $"{state} {step}", TextWrapping = TextWrapping.Wrap });
            foreach (var summary in run.FileSteps)
                panel.Children.Add(new TextBlock { Text = $"{L.Format("WorkflowStep", summary.StepIndex + 1, "")}: {L.Format("ActionSummary", summary.Succeeded, summary.Skipped, summary.Failed)}", TextWrapping = TextWrapping.Wrap });
            if (run.Error is not null) panel.Children.Add(new TextBlock { Text = run.Error, TextWrapping = TextWrapping.Wrap });
            if (!run.Finished)
            {
                var cancel = new Button { Content = L.Get("WorkflowCancel") };
                cancel.Click += (_, _) => { run.Cancellation.Cancel(); cancel.IsEnabled = false; };
                panel.Children.Add(cancel);
            }
            rows.Children.Add(panel);
        }
    }
}
