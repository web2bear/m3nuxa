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
    private readonly Button clear = new() { Content = L.Get("Editor_Clear_9c7449") };
    private readonly Button cancel = new() { Content = L.Get("Editor_Cancel_0ec753"), Visibility = Visibility.Collapsed };
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
        AutomationProperties.SetName(edit, L.Get("Editor_Change_d06470") + label);
        AutomationProperties.SetAutomationId(clear, id + "Clear");
        AutomationProperties.SetName(clear, L.Get("Editor_Clear_f61ac5") + label);
        AutomationProperties.SetAutomationId(cancel, id + "Cancel");
        AutomationProperties.SetName(cancel, L.Get("Editor_CancelRecording_bd0b25") + label);
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
            if (recording && XamlRoot is not null && !Contains(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject))
            { DevLog.Write("shortcut", "Recording canceled on focus loss"); await StopAsync(); }
        });
        Refresh();
    }

    private bool Contains(DependencyObject? element)
    {
        while (element is not null) { if (element == this) return true; element = VisualTreeHelper.GetParent(element); }
        return false;
    }
    private async void WindowActivated(object sender, WindowActivatedEventArgs args)
    { if (args.WindowActivationState == WindowActivationState.Deactivated && recording) { DevLog.Write("shortcut", "Recording canceled on window deactivation"); await StopAsync(); } }

    internal void Refresh()
    {
        var value = getValue();
        valueText.Text = string.IsNullOrWhiteSpace(value) ? L.Get("Editor_NotAssigned_bd9326") : value.Replace("+", " + ");
        edit.Content = string.IsNullOrWhiteSpace(value) ? L.Get("Editor_Assign_fe1285") : L.Get("Editor_Change_9d809f");
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
        valueText.Text = L.Get("Editor_Preparing_bd54fe");
        try
        {
            var opened = await HotkeyCaptureConnection.OpenAsync();
            if (!recording || version != generation || !IsLoaded) { opened.Dispose(); return; }
            connection = opened;
            cancel.Focus(FocusState.Programmatic);
            valueText.Text = L.Get("Editor_PressANewShortcut_379ccc");
            status.Text = L.Get("Editor_ReleaseTheKeysToRecord_3239a3");
            status.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            status.Visibility = Visibility.Visible;
            capture = new KeyboardCapture(App.MainWindow, (value, complete) => DispatcherQueue.TryEnqueue(async () =>
            {
                if (!recording || version != generation) return;
                if (complete) { DevLog.Write("shortcut", value is null ? "Keyboard capture canceled" : "Keyboard capture completed"); await StopAsync(value); }
                else valueText.Text = value is null ? L.Get("Editor_PressANewShortcut_379ccc") : value.Length == 0 ? L.Get("Editor_OneShortcutIsRequired_8480d7") : value.Replace("+", " + ");
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
            catch (Exception ex) { error = L.Get("Editor_CouldNotConfirmHotkeysWere_c43cf6") + ex.Message; }
            finally { opened.Dispose(); }
        }
        if (candidate is not null && error is null)
        {
            if (!HotkeyParser.TryParse(candidate, out _, out _)) error = L.Get("Editor_PressAShortcutWithOne_f1ed8c");
            else error = validate(candidate);
            if (error is null && candidate != getValue()) setValue(candidate);
        }
        edit.IsEnabled = true; edit.Visibility = Visibility.Visible;
        if (XamlRoot is not null && Contains(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject)) edit.Focus(FocusState.Programmatic);
        cancel.Visibility = Visibility.Collapsed;
        Refresh(); SetError(error);
    }
}
