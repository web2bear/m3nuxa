using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace m3nuxa.App;

public partial class App
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    private static async Task CheckSettingsAsync(TrayApplication tray, ConfigStore config)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var settings = (SettingsStore)typeof(TrayApplication).GetField("_settings", flags)!.GetValue(tray)!;
        var save = typeof(TrayApplication).GetMethod("SaveSettings", flags)!;
        string? Save(AppSettings candidate) => (string?)save.Invoke(tray, [candidate]);
        var draft = config.CreateDraft();
        if (Save(settings.Current with { MainHotkey = "Ctrl+Alt+F10", MenuFontSize = 18 }) is { } error) throw new Exception(error);
        if (config.Version != draft.BaseVersion) throw new Exception("Settings invalidated menu draft");
        if (!config.Apply(draft).Success || settings.Current.MainHotkey != "Ctrl+Alt+F10") throw new Exception("Menu apply changed settings");
        if (RegisterHotKey(0, 28500, 3, 0x79)) { UnregisterHotKey(0, 28500); throw new Exception("New global shortcut not registered immediately"); }
        if (!RegisterHotKey(0, 28501, 6, 0x7a)) throw new Exception("Cannot reserve test shortcut");
        try
        {
            if (Save(settings.Current with { MainHotkey = "Ctrl+Shift+F11" }) is null) throw new Exception("Busy shortcut accepted");
            if (settings.Current.MainHotkey != "Ctrl+Alt+F10") throw new Exception("Busy shortcut replaced previous setting");
            if (RegisterHotKey(0, 28500, 3, 0x79)) { UnregisterHotKey(0, 28500); throw new Exception("Previous binding not restored after failure"); }
        }
        finally { UnregisterHotKey(0, 28501); }
        var open = typeof(TrayApplication).GetMethod("OpenSettings", flags)!;
        open.Invoke(tray, null); await Task.Delay(180);
        var window = (Window)typeof(TrayApplication).GetField("_settingsWindow", flags)!.GetValue(tray)!;
        open.Invoke(tray, null);
        if (!ReferenceEquals(window, typeof(TrayApplication).GetField("_settingsWindow", flags)!.GetValue(tray))) throw new Exception("Settings window duplicated");
        var language = (ComboBox)typeof(SettingsWindow).GetField("language", flags)!.GetValue(window)!;
        var status = (TextBlock)typeof(SettingsWindow).GetField("status", flags)!.GetValue(window)!;
        var originalLanguage = L.Language;
        var restartLanguage = L.Language == "ru-RU" ? "en-US" : "ru-RU";
        var restartIndex = restartLanguage == "ru-RU" ? 1 : 0;
        // Make sure the next assignment triggers SelectionChanged, regardless of process language.
        language.SelectedIndex = 1 - restartIndex;
        language.SelectedIndex = restartIndex;
        if (settings.Current.Language != restartLanguage || language.SelectedIndex != restartIndex)
            throw new Exception("Language selection was not saved");
        var switchedMenu = (MenuFlyout)typeof(TrayApplication).GetMethod("BuildServiceMenu", flags)!.Invoke(tray, null)!;
        var expectedExit = restartLanguage == "ru-RU" ? "Выход" : "Exit";
        if (((MenuFlyoutItem)switchedMenu.Items.Last()).Text != expectedExit)
            throw new Exception("Tray menu did not switch language after saving settings");
        if (L.Language != restartLanguage || window.Title != L.Get("Settings/WindowTitle"))
            throw new Exception("Settings window did not switch language");
        var savedPath = Path.Combine(Path.GetDirectoryName(config.FilePath)!, "settings.json");
        if (new SettingsStore(savedPath, "").Current.Language != restartLanguage)
            throw new Exception("Language selection did not persist to the isolated settings file");
        if (status.Visibility != Visibility.Visible || status.Text != L.Get("Settings/EditorRestartRequired"))
            throw new Exception("Language change did not explain editor restart");
        language.SelectedIndex = 1 - restartIndex;
        if (settings.Current.Language != originalLanguage || L.Language != originalLanguage || status.Text != L.Get("Settings/EditorRestartRequired"))
            throw new Exception("Switching back did not restore the original language");
        typeof(SettingsWindow).GetMethod("Start", flags)!.Invoke(window, null);
        if (!RegisterHotKey(0, 28500, 3, 0x79)) throw new Exception("Settings recording did not pause hotkeys");
        UnregisterHotKey(0, 28500);
        typeof(SettingsWindow).GetMethod("Stop", flags)!.Invoke(window, null);
        if (RegisterHotKey(0, 28500, 3, 0x79)) { UnregisterHotKey(0, 28500); throw new Exception("Settings cancellation did not restore hotkeys"); }
        window.Close();
        var previousLanguage = settings.Current.Language;
        var failureWindow = new SettingsWindow(settings, _ => "simulated save failure", _ => { }, () => { });
        var displays = Microsoft.UI.Windowing.DisplayArea.FindAll();
        var display = Microsoft.UI.Windowing.DisplayArea.Primary;
        for (var i = 0; i < displays.Count; i++) if (!displays[i].IsPrimary) { display = displays[i]; break; }
        var work = display.WorkArea;
        failureWindow.AppWindow.Move(new Windows.Graphics.PointInt32(work.X + 40, work.Y + 40));
        failureWindow.Activate();
        try
        {
            var failureLanguage = (ComboBox)typeof(SettingsWindow).GetField("language", flags)!.GetValue(failureWindow)!;
            var failureStatus = (TextBlock)typeof(SettingsWindow).GetField("status", flags)!.GetValue(failureWindow)!;
            var initialIndex = failureLanguage.SelectedIndex;
            failureLanguage.SelectedIndex = 1 - initialIndex;
            if (failureLanguage.SelectedIndex != initialIndex || settings.Current.Language != previousLanguage || L.Language != previousLanguage ||
                new SettingsStore(savedPath, "").Current.Language != previousLanguage)
                throw new Exception("Failed language save did not restore selection and saved settings");
            if (failureStatus.Visibility != Visibility.Visible || failureStatus.Text != "simulated save failure")
                throw new Exception("Failed language save was not shown");
        }
        finally { failureWindow.Close(); }
        var items = new List<MenuFlyoutItemBase>();
        LauncherMenu.Fill(items, [new MenuNode { Name = "Group", Kind = MenuNodeKind.Group, Children = [new MenuNode { Name = "Child", Kind = MenuNodeKind.Action }] }], _ => {}, settings.Current.MenuFontSize);
        var group = (MenuFlyoutSubItem)items[0];
        if (group.FontSize != 18 || ((MenuFlyoutItem)group.Items[0]).FontSize != 18) throw new Exception("Font size not propagated to nested menus");
    }
}
