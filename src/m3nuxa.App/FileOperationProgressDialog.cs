using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace m3nuxa.App;
internal sealed class FileOperationProgressDialog : Window, IDisposable
{
    private readonly ProgressBar progress;
    private readonly TextBlock label = new() { TextWrapping = TextWrapping.Wrap };
    private bool closed;
    public event EventHandler? CancelRequested;
    public FileOperationProgressDialog(int count)
    {
        Title = "Файловая операция";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 220));
        progress = new() { Maximum = count };
        var cancel = new Button { Content = "Отмена" };
        cancel.Click += (_, _) => { cancel.IsEnabled = false; CancelRequested?.Invoke(this, EventArgs.Empty); };
        var panel = new StackPanel { Padding = new Thickness(20), Spacing = 12 };
        panel.Children.Add(progress); panel.Children.Add(label); panel.Children.Add(cancel); Content = panel;
        Closed += (_, _) => { closed = true; CancelRequested?.Invoke(this, EventArgs.Empty); };
    }
    public void Report(int completed, string path) { if (!closed) { progress.Value = completed; label.Text = path; } }
    public async Task<FileConflictChoice> AskConflict(string path)
    {
        if (closed) return FileConflictChoice.Cancel;
        var dialog = new ContentDialog { XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Конфликт имён", Content = $"Объект уже существует:\n{path}", PrimaryButtonText = "Заменить", SecondaryButtonText = "Пропустить", CloseButtonText = "Отмена" };
        return await dialog.ShowAsync() switch { ContentDialogResult.Primary => FileConflictChoice.Replace,
            ContentDialogResult.Secondary => FileConflictChoice.Skip, _ => FileConflictChoice.Cancel };
    }
    public void Dispose() { if (!closed) Close(); }
}
