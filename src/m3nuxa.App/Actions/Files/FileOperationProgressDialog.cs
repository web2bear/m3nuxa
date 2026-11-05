using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace m3nuxa.App;
internal sealed class FileOperationProgressDialog : Window, IDisposable
{
    private readonly ProgressBar progress;
    private readonly TextBlock label = new() { TextWrapping = TextWrapping.Wrap };
    private bool closed;
    private readonly TaskCompletionSource<XamlRoot> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event EventHandler? CancelRequested;
    public FileOperationProgressDialog(int count)
    {
        Title = L.Get("FileOperationTitle");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 220));
        progress = new() { Maximum = count };
        var cancel = new Button { Content = L.Get("FileOperationCancel") };
        cancel.Click += (_, _) => { cancel.IsEnabled = false; CancelRequested?.Invoke(this, EventArgs.Empty); };
        var panel = new StackPanel { Padding = new Thickness(20), Spacing = 12 };
        panel.Children.Add(progress); panel.Children.Add(label); panel.Children.Add(cancel); Content = panel;
        panel.Loaded += (_, _) => { if (panel.XamlRoot is { } root) ready.TrySetResult(root); };
        Closed += (_, _) => { closed = true; ready.TrySetCanceled(); CancelRequested?.Invoke(this, EventArgs.Empty); };
    }
    public void Report(int completed, string path) { if (!closed) { progress.Value = completed; label.Text = path; } }
    public async Task<FileConflictChoice> AskConflict(string path, CancellationToken cancellationToken)
    {
        if (closed || cancellationToken.IsCancellationRequested) return FileConflictChoice.Cancel;
        XamlRoot root;
        try { root = ((FrameworkElement)Content).XamlRoot ?? await ready.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return FileConflictChoice.Cancel; }
        if (closed || cancellationToken.IsCancellationRequested) return FileConflictChoice.Cancel;
        var dialog = new ContentDialog { XamlRoot = root,
            Title = L.Get("FileConflictTitle"), Content = L.Format("FileConflictExists", path), PrimaryButtonText = L.Get("FileConflictReplace"), SecondaryButtonText = L.Get("FileConflictSkip"), CloseButtonText = L.Get("FileOperationCancel") };
        var operation = dialog.ShowAsync();
        using var registration = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            try { dialog.Hide(); } catch (InvalidOperationException) { /* The dialog has already closed. */ }
        }));
        return await operation switch { ContentDialogResult.Primary => FileConflictChoice.Replace,
            ContentDialogResult.Secondary => FileConflictChoice.Skip, _ => FileConflictChoice.Cancel };
    }
    public void Dispose() { if (!closed) Close(); }
}
