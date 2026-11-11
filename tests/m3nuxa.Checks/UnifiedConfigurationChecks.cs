using m3nuxa.App;
using System.Text.Json;

internal static class UnifiedConfigurationChecks
{
    public static void Run(string root)
    {
        var directory = Path.Combine(root, "unified-config");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        var settingsPath = Path.Combine(directory, "settings.json");
        const string original = "{\"SchemaVersion\":2,\"Items\":[]}";
        const string legacySettings = "{\"MainHotkey\":\"Ctrl+Shift+F7\",\"PanelHotkey\":\"Ctrl+Shift+F8\",\"PanelPosition\":\"Cursor\",\"MenuFontSize\":17,\"Language\":\"ru-RU\"}";
        File.WriteAllText(path, original);
        File.WriteAllText(settingsPath, legacySettings);
        var store = new ConfigStore(path);
        var migrated = store.Current.Settings;
        Check(migrated.MainHotkey == "Ctrl+Shift+F7" && migrated.PanelHotkey == "Ctrl+Shift+F8" &&
              migrated.PanelPosition == "Cursor" && migrated.MenuFontSize == 17 && migrated.Language == "ru-RU", "legacy settings migrate");
        Check(File.ReadAllText(path) == original && File.ReadAllText(settingsPath) == legacySettings, "migration does not rewrite either file");
        var draft = store.CreateDraft();
        draft.Config.Settings = draft.Config.Settings with { Language = "en-US", MenuFontSize = 18 };
        draft.Config.Items.Add(new MenuNode { Name = "Group", Kind = MenuNodeKind.Group });
        Check(store.Current.Settings == migrated, "settings changes stay in the draft");
        Check(store.Apply(draft).Success, "unified draft applies");
        Check(File.ReadAllText(Directory.GetFiles(Path.Combine(directory, "backups"), "*.json").Single()) == original, "migration backs up original config");
        Check(File.ReadAllText(settingsPath) == legacySettings, "migration preserves old settings file");
        var reloaded = new ConfigStore(path).Current;
        Check(reloaded.Settings.Language == "en-US" && reloaded.Settings.MenuFontSize == 18 && reloaded.Items.Single().Name == "Group", "settings and tree roundtrip");
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            Check(json.RootElement.GetProperty("Settings").GetProperty("PanelPosition").GetString() == "Cursor", "settings are in config file");
        var imported = Path.Combine(directory, "old-import.json");
        File.WriteAllText(imported, "{\"SchemaVersion\":2,\"MainHotkey\":\"Alt+F7\",\"Items\":[]}");
        var loaded = store.LoadDraft(imported).Config;
        Check(loaded.Settings.MainHotkey == "Alt+F7" && loaded.Settings.Language == "en-US", "legacy import does not use installation settings");
        var first = store.CreateDraft();
        var stale = store.CreateDraft();
        first.Config.Settings = first.Config.Settings with { MenuFontSize = 19 };
        Check(store.Apply(first).Success && !store.Apply(stale).Success, "settings change invalidates stale draft");
        var saveAs = store.CreateDraft();
        saveAs.Config.Settings = saveAs.Config.Settings with { Language = "ru-RU" };
        var other = Path.Combine(directory, "other.json");
        Check(store.SaveAs(saveAs, other).Success && new ConfigStore(other).Current.Settings.Language == "ru-RU", "save as preserves unified settings");

        var damagedRoot = Path.Combine(root, "damaged-legacy-settings");
        Directory.CreateDirectory(damagedRoot);
        var configPath = Path.Combine(damagedRoot, "config.json");
        var damagedPath = Path.Combine(damagedRoot, "settings.json");
        foreach (var brokenSettings in new[] { "{", "{\"MenuFontSize\":1}", "null" })
        {
            File.WriteAllText(configPath, original);
            File.WriteAllText(damagedPath, brokenSettings);
            try { _ = new ConfigStore(configPath); throw new Exception("bad legacy settings were accepted"); }
            catch (LegacySettingsException ex)
            {
                Check(ex.SettingsPath == Path.GetFullPath(damagedPath) && ex.Message.Contains(Path.GetFullPath(damagedPath)), "error identifies settings file");
                Check(ex.InnerException is not null, "error preserves underlying cause");
            }
            Check(File.ReadAllText(configPath) == original && File.ReadAllText(damagedPath) == brokenSettings, "failed migration preserves both original files");
            try { _ = ConfigStore.CreateDefaultAfterInvalid(configPath); throw new Exception("reset ignored bad legacy settings"); }
            catch (LegacySettingsException) { }
            Check(File.ReadAllText(configPath) == original && File.ReadAllText(damagedPath) == brokenSettings, "reset does not remove config when settings cannot migrate");
            Check(Directory.GetFiles(damagedRoot, "*.bak").Length == 0, "failed reset creates no misleading backup");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
