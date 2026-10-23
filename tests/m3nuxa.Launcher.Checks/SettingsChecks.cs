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
        typeof(SettingsWindow).GetMethod("Start", flags)!.Invoke(window, null);
        if (!RegisterHotKey(0, 28500, 3, 0x79)) throw new Exception("Settings recording did not pause hotkeys");
        UnregisterHotKey(0, 28500);
        typeof(SettingsWindow).GetMethod("Stop", flags)!.Invoke(window, null);
        if (RegisterHotKey(0, 28500, 3, 0x79)) { UnregisterHotKey(0, 28500); throw new Exception("Settings cancellation did not restore hotkeys"); }
        window.Close();
        var items = new List<MenuFlyoutItemBase>();
        LauncherMenu.Fill(items, [new MenuNode { Name = "Group", Kind = MenuNodeKind.Group, Children = [new MenuNode { Name = "Child", Kind = MenuNodeKind.Action }] }], _ => {}, settings.Current.MenuFontSize);
        var group = (MenuFlyoutSubItem)items[0];
        if (group.FontSize != 18 || ((MenuFlyoutItem)group.Items[0]).FontSize != 18) throw new Exception("Font size not propagated to nested menus");
    }
}
