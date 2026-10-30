using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using m3nuxa.Editor;

namespace m3nuxa.App;

internal sealed class SettingsWindow : Window
{
    private readonly TextBlock shortcut = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly Button record = new() { Content = "Изменить" };
    private readonly Button cancel = new() { Content = "Отмена", Visibility = Visibility.Collapsed };
    private readonly NumberBox font;
    private readonly TextBlock sample = new() { Text = "Пример пункта меню", Margin = new Thickness(10) };
    private readonly SettingsStore store;
    private readonly Func<AppSettings, string?> save;
    private readonly Action<bool> suspend;
    private KeyboardCapture? capture;
    private bool recording;
    private bool updating;
    public SettingsWindow(SettingsStore store, Func<AppSettings, string?> save, Action<bool> suspend, Action preview)
    {
        this.store = store; this.save = save; this.suspend = suspend;
        Title = "m3nuxa — настройки";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(580, 620));
        var panel = new StackPanel { Padding = new Thickness(20), Spacing = 14 };
        void UpdateTitleBarTheme() => AppWindow.TitleBar.PreferredTheme =
            panel.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        panel.Loaded += (_, _) => UpdateTitleBarTheme();
        panel.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
        panel.Children.Add(new TextBlock { Text = "Настройки", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Изменения сохраняются автоматически", FontSize = 12, Opacity = .65 });
        panel.Children.Add(new TextBlock { Text = "Сочетание вызова меню" });
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(shortcut); row.Children.Add(record); Grid.SetColumn(record, 1);
        row.Children.Add(cancel); Grid.SetColumn(cancel, 2); panel.Children.Add(row);
        var clear = new Button { Content = "Убрать сочетание" };
        clear.Click += (_, _) => Save(store.Current with { MainHotkey = "" }); panel.Children.Add(clear);
        panel.Children.Add(status);
        font = new NumberBox { Header = "Размер шрифта меню", Minimum = 10, Maximum = 24, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = store.Current.MenuFontSize };
        AutomationProperties.SetAutomationId(record, "SettingsHotkeyEdit");
        AutomationProperties.SetAutomationId(shortcut, "SettingsHotkeyValue");
        AutomationProperties.SetAutomationId(font, "MenuFontSize");
        font.ValueChanged += (_, _) => { if (!updating) Save(store.Current with { MenuFontSize = font.Value }); };
        panel.Children.Add(font); panel.Children.Add(sample);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var show = new Button { Content = "Показать меню" }; show.Click += (_, _) => { Stop(); preview(); };
        var close = new Button { Content = "Закрыть" }; close.Click += (_, _) => Close();
        buttons.Children.Add(show); buttons.Children.Add(close); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel };
        record.Click += (_, _) => Start(); cancel.Click += (_, _) => Stop();
        Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated) Stop(); };
        Closed += (_, _) => Stop(); Refresh();
    }
    private void Refresh()
    {
        updating = true;
        shortcut.Text = store.Current.MainHotkey.Length == 0 ? "Не назначено" : store.Current.MainHotkey.Replace("+", " + ");
        font.Value = store.Current.MenuFontSize; sample.FontSize = store.Current.MenuFontSize;
        updating = false;
    }
    private void Save(AppSettings candidate)
    {
        Stop();
        var error = save(candidate);
        status.Text = error ?? "Сохранено";
        status.Foreground = (Brush)Application.Current.Resources[error is null ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush"];
        status.Visibility = Visibility.Visible;
        Refresh();
    }
    private void Start()
    {
        if (recording) return;
        try
        {
            suspend(true); recording = true; record.Visibility = Visibility.Collapsed; cancel.Visibility = Visibility.Visible;
            cancel.Focus(FocusState.Programmatic);
            status.Visibility = Visibility.Visible; shortcut.Text = "Нажмите сочетание…"; status.Text = "Отпустите клавиши для записи. Esc — отмена.";
            capture = new KeyboardCapture(this, (value, complete) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!recording) return;
                if (complete) { Stop(); if (!string.IsNullOrEmpty(value)) Save(store.Current with { MainHotkey = value }); else Refresh(); }
                else if (value is not null) shortcut.Text = value.Replace("+", " + ");
            }));
        }
        catch (Exception ex) { Stop(); status.Text = ex.Message; status.Visibility = Visibility.Visible; }
    }
    private void Stop()
    {
        if (!recording) return;
        recording = false; capture?.Dispose(); capture = null; suspend(false);
        record.Visibility = Visibility.Visible; cancel.Visibility = Visibility.Collapsed; status.Visibility = Visibility.Collapsed; Refresh();
    }
}
