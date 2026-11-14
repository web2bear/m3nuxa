using m3nuxa.App;
using System.Text.Json;

internal static class UnifiedConfigurationChecks
{
    public static void Run(string root)
    {
        var directory = Path.Combine(root, "unified-config");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        const string original = "{\"SchemaVersion\":2,\"Items\":[]}";
        File.WriteAllText(path, original);
        var store = new ConfigStore(path);
        var initial = store.Current.Settings;
        var draft = store.CreateDraft();
        draft.Config.Settings = draft.Config.Settings with { MenuFontSize = 18, PanelPosition = "Cursor", MenuDensity = "Comfortable", MenuIconSize = "Large",
            MenuBackground = "#223344", PanelBackground = "#8899AA", PanelOpacity = 60, PanelIconSize = "Small", PanelFontSize = 16 };
        draft.Config.Items.Add(new MenuNode { Name = "Group", Kind = MenuNodeKind.Group });
        Check(store.Current.Settings == initial, "settings changes stay in the draft");
        Check(store.Apply(draft).Success, "unified draft applies");
        Check(File.ReadAllText(Directory.GetFiles(Path.Combine(directory, "backups"), "*.json").Single()) == original, "apply backs up original config");
        var reloaded = new ConfigStore(path).Current;
        Check(reloaded.Settings.MenuFontSize == 18 && reloaded.Items.Single().Name == "Group", "settings and tree roundtrip");
        Check(reloaded.Settings.MenuDensity == "Comfortable" && reloaded.Settings.MenuIconSize == "Large" && reloaded.Settings.MenuBackground == "#223344" &&
            reloaded.Settings.PanelBackground == "#8899AA" && reloaded.Settings.PanelOpacity == 60 && reloaded.Settings.PanelIconSize == "Small" && reloaded.Settings.PanelFontSize == 16,
            "appearance settings roundtrip");
        foreach (var invalid in new[] { reloaded.Settings with { PanelOpacity = 39 }, reloaded.Settings with { MenuBackground = "#oops" },
            reloaded.Settings with { PanelFontSize = 25 }, reloaded.Settings with { MenuDensity = "Unknown" } })
        {
            try { SettingsStore.Validate(invalid); throw new Exception("invalid appearance accepted"); }
            catch (InvalidDataException) { }
        }
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            Check(json.RootElement.GetProperty("Settings").GetProperty("PanelPosition").GetString() == "Cursor" && !json.RootElement.GetProperty("Settings").TryGetProperty("Language", out _), "settings are in config file without language");
        var imported = Path.Combine(directory, "old-import.json");
        File.WriteAllText(imported, "{\"SchemaVersion\":2,\"MainHotkey\":\"Alt+F7\",\"Items\":[]}");
        var loaded = store.LoadDraft(imported).Config;
        Check(loaded.Settings.MainHotkey == "Alt+F7", "legacy import does not use installation settings");
        var first = store.CreateDraft();
        var stale = store.CreateDraft();
        first.Config.Settings = first.Config.Settings with { MenuFontSize = 19 };
        Check(store.Apply(first).Success && !store.Apply(stale).Success, "settings change invalidates stale draft");
        var saveAs = store.CreateDraft();
        saveAs.Config.Settings = saveAs.Config.Settings with { MenuFontSize = 20 };
        var other = Path.Combine(directory, "other.json");
        Check(store.SaveAs(saveAs, other).Success && new ConfigStore(other).Current.Settings.MenuFontSize == 20, "save as preserves unified settings");

    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
