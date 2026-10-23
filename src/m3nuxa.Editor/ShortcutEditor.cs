using m3nuxa.App;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace m3nuxa.Editor;

internal sealed class ShortcutEditor : StackPanel
{
    private readonly TextBlock valueText = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button edit = new();
    private readonly Button clear = new() { Content = "Убрать" };
    private readonly Button cancel = new() { Content = "Отмена", Visibility = Visibility.Collapsed };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly Func<string> getValue;
    private readonly Action<string> setValue;
    private readonly Func<string, string?> validate;
    private readonly Func<Task> beforeRecording;
    private KeyboardCapture? capture;
    private HotkeyCaptureConnection? connection;
    private bool recording;
    private int generation;

    public ShortcutEditor(string id, string label, string help, Func<string> getValue, Action<string> setValue,
        Func<string, string?> validate, Func<Task> beforeRecording)
    {
        this.getValue = getValue; this.setValue = setValue; this.validate = validate; this.beforeRecording = beforeRecording;
        Spacing = 5;
        Children.Add(new TextBlock { Text = label });
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(valueText);
        row.Children.Add(edit); Grid.SetColumn(edit, 1);
        row.Children.Add(clear); Grid.SetColumn(clear, 2);
        row.Children.Add(cancel); Grid.SetColumn(cancel, 3);
        Children.Add(row);
        Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 });
        Children.Add(status);
        AutomationProperties.SetAutomationId(edit, id + "Edit");
        AutomationProperties.SetName(edit, "Изменить: " + label);
        AutomationProperties.SetAutomationId(clear, id + "Clear");
        AutomationProperties.SetName(clear, "Убрать: " + label);
        AutomationProperties.SetAutomationId(cancel, id + "Cancel");
        AutomationProperties.SetName(cancel, "Отменить запись: " + label);
        AutomationProperties.SetAutomationId(valueText, id + "Value");
        AutomationProperties.SetAutomationId(status, id + "Status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        edit.Click += async (_, _) => await StartAsync();
        cancel.Click += async (_, _) => await StopAsync();
        clear.Click += (_, _) => { setValue(""); SetError(null); Refresh(); };
        Loaded += (_, _) => App.MainWindow.Activated += WindowActivated;
        Unloaded += async (_, _) => { App.MainWindow.Activated -= WindowActivated; await StopAsync(); };
        LostFocus += (_, _) => DispatcherQueue.TryEnqueue(async () =>
        {
            if (recording && XamlRoot is not null && !Contains(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject)) await StopAsync();
        });
        Refresh();
    }

    private bool Contains(DependencyObject? element)
    {
        while (element is not null) { if (element == this) return true; element = VisualTreeHelper.GetParent(element); }
        return false;
    }
    private async void WindowActivated(object sender, WindowActivatedEventArgs args)
    { if (args.WindowActivationState == WindowActivationState.Deactivated) await StopAsync(); }

    internal void Refresh()
    {
        var value = getValue();
        valueText.Text = string.IsNullOrWhiteSpace(value) ? "Не назначено" : value.Replace("+", " + ");
        edit.Content = string.IsNullOrWhiteSpace(value) ? "Назначить" : "Изменить";
        clear.Visibility = string.IsNullOrWhiteSpace(value) || recording ? Visibility.Collapsed : Visibility.Visible;
    }
    internal void SetError(string? message)
    {
        status.Text = message ?? "";
        status.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
    internal async Task StartAsync()
    {
        if (recording) return;
        await beforeRecording();
        var version = ++generation;
        recording = true;
        SetError(null);
        cancel.Visibility = Visibility.Visible;
        cancel.Focus(FocusState.Programmatic);
        edit.IsEnabled = false; edit.Visibility = Visibility.Collapsed; clear.Visibility = Visibility.Collapsed;
        valueText.Text = "Подготовка…";
        try
        {
            var opened = await HotkeyCaptureConnection.OpenAsync();
            if (!recording || version != generation || !IsLoaded) { opened.Dispose(); return; }
            connection = opened;
            cancel.Focus(FocusState.Programmatic);
            valueText.Text = "Нажмите новое сочетание…";
            status.Text = "Отпустите клавиши для записи. Esc — отмена.";
            status.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            status.Visibility = Visibility.Visible;
            capture = new KeyboardCapture(App.MainWindow, (value, complete) => DispatcherQueue.TryEnqueue(async () =>
            {
                if (!recording || version != generation) return;
                if (complete) await StopAsync(value);
                else valueText.Text = value is null ? "Нажмите новое сочетание…" : value.Length == 0 ? "Нужно одно сочетание" : value.Replace("+", " + ");
            }));
        }
        catch (Exception ex) { await StopAsync(); SetError(ex.Message); }
    }
    internal async Task StopAsync(string? candidate = null)
    {
        if (!recording) return;
        recording = false; ++generation;
        capture?.Dispose(); capture = null;
        var opened = connection; connection = null;
        string? error = null;
        if (opened is not null)
        {
            try { await opened.CompleteAsync(); }
            catch (Exception ex) { error = "Не удалось подтвердить восстановление горячих клавиш: " + ex.Message; }
            finally { opened.Dispose(); }
        }
        if (candidate is not null && error is null)
        {
            if (!HotkeyParser.TryParse(candidate, out _, out _)) error = "Нажмите сочетание с одной основной клавишей.";
            else error = validate(candidate);
            if (error is null && candidate != getValue()) setValue(candidate);
        }
        edit.IsEnabled = true; edit.Visibility = Visibility.Visible;
        if (XamlRoot is not null && Contains(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject)) edit.Focus(FocusState.Programmatic);
        cancel.Visibility = Visibility.Collapsed;
        Refresh(); SetError(error);
    }
}
