using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;

namespace m3nuxa.App;

public partial class App
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);

    private static async Task CheckSettingsAsync(TrayApplication tray, ConfigStore config)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var activate = typeof(TrayApplication).GetMethod("RegisterHotkeys", flags)!;
        void Activate() => activate.Invoke(tray, null);
        var originalLanguage = L.Language;
        var draft = config.CreateDraft();
        var stale = config.CreateDraft();
        draft.Config.Settings = draft.Config.Settings with { MainHotkey = "Ctrl+Alt+F10", PanelHotkey = "Ctrl+Alt+F9", PanelPosition = "Cursor", MenuFontSize = 18 };
        if (config.Current.Settings.PanelPosition == "Cursor") throw new Exception("Unapplied settings changed running config");
        var invalid = config.CreateDraft();
        invalid.Config.Settings = invalid.Config.Settings with { PanelHotkey = invalid.Config.Settings.MainHotkey };
        if (config.Apply(invalid).Success) throw new Exception("Conflicting global shortcuts accepted");
        if (!config.Apply(draft).Success) throw new Exception("Unified settings and tree did not apply");
        Activate();
        if (config.Current.Settings.PanelPosition != "Cursor" || config.Current.Settings.MenuFontSize != 18)
            throw new Exception("Applied settings missing from active config");
        using (var json = JsonDocument.Parse(File.ReadAllText(config.FilePath)))
            if (json.RootElement.GetProperty("Settings").GetProperty("PanelPosition").GetString() != "Cursor")
                throw new Exception("Settings were not persisted in the configuration file");
        if (config.Apply(stale).Success) throw new Exception("Old draft overwrote newer settings");
        if (RegisterHotKey(0, 28500, 3, 0x79)) { UnregisterHotKey(0, 28500); throw new Exception("Applied global shortcut not registered"); }
        if (!RegisterHotKey(0, 28501, 6, 0x7A)) throw new Exception("Cannot reserve occupied test shortcut");
        try
        {
            var pipe = (AgentPipe)typeof(TrayApplication).GetField("_agentPipe", flags)!.GetValue(tray)!;
            const string session = "unified-settings-conflict-test";
            await pipe.ExecuteAsync(session, "begin");
            var candidate = config.CreateDraft().Config;
            candidate.Settings = candidate.Settings with { MainHotkey = "Ctrl+Shift+F11" };
            candidate.Items.Add(new MenuNode { Name = "Unapplied group", Kind = MenuNodeKind.Group });
            await pipe.ExecuteAsync(session, "replaceDraft", new JsonObject { ["config"] = JsonSerializer.SerializeToNode(candidate) });
            var before = File.ReadAllText(config.FilePath);
            var beforeVersion = config.Version;
            var beforeCount = config.Current.Items.Count;
            var result = await pipe.ExecuteAsync(session, "apply");
            if (result["Success"]?.GetValue<bool>() != false) throw new Exception("Occupied new shortcut was accepted");
            if (config.Version != beforeVersion || config.Current.Items.Count != beforeCount || File.ReadAllText(config.FilePath) != before)
                throw new Exception("Occupied shortcut changed config, tree or version");
            if (RegisterHotKey(0, 28500, 3, 0x79)) { UnregisterHotKey(0, 28500); throw new Exception("Previous binding not restored"); }
        }
        finally { UnregisterHotKey(0, 28501); }
        var suspended = typeof(TrayApplication).GetField("_hotkeysSuspended", flags)!;
        try
        {
            suspended.SetValue(tray, true); Activate();
            var pipe = (AgentPipe)typeof(TrayApplication).GetField("_agentPipe", flags)!.GetValue(tray)!;
            const string session = "unified-settings-capture-test";
            await pipe.ExecuteAsync(session, "begin");
            var candidate = config.CreateDraft().Config;
            candidate.Settings = candidate.Settings with { MainHotkey = "Ctrl+Shift+F11", PanelHotkey = "Ctrl+Shift+F12" };
            await pipe.ExecuteAsync(session, "replaceDraft", new JsonObject { ["config"] = JsonSerializer.SerializeToNode(candidate) });
            var before = File.ReadAllText(config.FilePath);
            var version = config.Version;
            var result = await pipe.ExecuteAsync(session, "apply");
            if (result["Success"]?.GetValue<bool>() != false || File.ReadAllText(config.FilePath) != before || config.Version != version)
                throw new Exception("Applying changed shortcuts while capturing changed configuration");
        }
        finally { suspended.SetValue(tray, false); Activate(); }
        if (RegisterHotKey(0, 28500, 3, 0x79)) { UnregisterHotKey(0, 28500); throw new Exception("Hotkeys were not restored after capture"); }
        var languageDraft = config.CreateDraft();
        var changedLanguage = originalLanguage == "ru-RU" ? "en-US" : "ru-RU";
        languageDraft.Config.Settings = languageDraft.Config.Settings with { Language = changedLanguage };
        if (!config.Apply(languageDraft).Success) throw new Exception("Language change was not applied");
        Activate();
        var menu = (MenuFlyout)typeof(TrayApplication).GetMethod("BuildServiceMenu", flags)!.Invoke(tray, null)!;
        if (((MenuFlyoutItem)menu.Items.Last()).Text != (changedLanguage == "ru-RU" ? "Выход" : "Exit"))
            throw new Exception("Tray did not update its language after applying config");
        var restore = config.CreateDraft();
        restore.Config.Settings = restore.Config.Settings with { Language = originalLanguage };
        if (!config.Apply(restore).Success) throw new Exception("Could not restore test language");
        Activate();
        var items = new List<MenuFlyoutItemBase>();
        LauncherMenu.Fill(items, [new MenuNode { Name = "Group", Kind = MenuNodeKind.Group, Children = [new MenuNode { Name = "Child", Kind = MenuNodeKind.Action }] }], _ => {}, _ => {}, config.Current.Settings.MenuFontSize);
        var group = (MenuFlyoutSubItem)items[0];
        if (group.FontSize != 18 || ((MenuFlyoutItem)group.Items[0]).FontSize != 18) throw new Exception("Font size not propagated to nested menus");
    }
}
