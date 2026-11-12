using m3nuxa.App;

internal static class ConfigCatalogChecks
{
    public static void Run(string root)
    {
        CheckStartupSelection(root);
        CheckLegacyMigration(root);
        CheckDeleteActive(root);
        var directory = Path.Combine(root, "catalog");
        Directory.CreateDirectory(directory);
        var active = Path.Combine(directory, "config.json");
        var selection = Path.Combine(directory, "active-config.txt");
        var store = new ConfigStore(active, selection, directory);
        Check(store.Apply(store.CreateDraft()).Success, "initial config saved");
        var invalidNamed = Path.Combine(directory, "invalid.json");
        File.WriteAllText(invalidNamed, "{\"SchemaVersion\":3,\"Name\":\"Reserved\",\"Settings\":{},\"Items\":null}");
        try { _ = store.CreateConfigurationDraft("reserved"); throw new Exception("duplicate invalid configuration name accepted"); }
        catch (InvalidOperationException) { }
        File.Delete(invalidNamed);
        var draft = store.CreateConfigurationDraft("Work");
        Check(!File.Exists(draft.SourcePath) && store.Current.Name == "Default", "new draft does not activate or write");
        Check(store.Apply(draft).Success && store.Current.Name == "Work", "applying new config activates it");
        var workPath = store.FilePath;
        Check(ConfigStore.StartupPath(selection) == workPath && store.ListConfigurations().Count == 2, "selection and catalog persist");
        var other = store.LoadDraft(active);
        File.AppendAllText(active, " ");
        Check(!store.Apply(other).Success && store.FilePath == workPath, "external target modification prevents switch");
        other = store.LoadDraft(active);
        other.Config.Name = "work";
        Check(!store.Apply(other).Success, "duplicate names are rejected ignoring case");
        other.Config.Name = "Personal";
        Check(store.Apply(other).Success && store.Current.Name == "Personal", "renamed target applies");
        store.DeleteActiveConfiguration(store.Version);
        Check(!File.Exists(active) && store.FilePath == workPath && store.Current.Name == "Work", "active deletion switches to remaining configuration");
        try { store.LoadDraft(Path.Combine(root, "outside.json")); throw new Exception("outside path accepted"); }
        catch (InvalidOperationException) { }
    }

    private static void CheckDeleteActive(string root)
    {
        var directory = Path.Combine(root, "delete-active");
        Directory.CreateDirectory(directory);
        var selection = Path.Combine(directory, "active-config.txt");
        var active = Path.Combine(directory, "active.json");
        File.WriteAllText(active, "{\"SchemaVersion\":3,\"Name\":\"Original\",\"Settings\":{},\"Items\":[]}");
        var beta = Path.Combine(directory, "first-file.json");
        var alpha = Path.Combine(directory, "last-file.json");
        File.WriteAllText(beta, "{\"SchemaVersion\":3,\"Name\":\"Beta\",\"Settings\":{},\"Items\":[]}");
        File.WriteAllText(alpha, "{\"SchemaVersion\":3,\"Name\":\"Alpha\",\"Settings\":{},\"Items\":[]}");
        var store = new ConfigStore(active, selection, directory);
        var stale = store.CreateDraft();
        File.AppendAllText(active, " ");
        try { store.DeleteActiveConfiguration(store.Version); throw new Exception("externally changed active configuration deleted"); }
        catch (InvalidOperationException) { }
        Check(File.Exists(active) && store.FilePath == active, "external changes prevent deletion");
        Check(store.Reload().Success, "reload after external change");
        stale = store.CreateDraft();
        try { store.DeleteActiveConfiguration(store.Version, _ => "preflight failed"); throw new Exception("preflight failure ignored"); }
        catch (InvalidOperationException ex) when (ex.Message == "preflight failed") { }
        Check(File.Exists(active) && store.FilePath == active && !File.Exists(selection), "preflight failure preserves active file and selection");
        store.DeleteActiveConfiguration(store.Version);
        Check(!File.Exists(active) && store.FilePath == alpha && store.Current.Name == "Alpha", "delete activates alphabetically first name");
        Check(ConfigStore.StartupPath(selection) == alpha && !store.Apply(stale).Success, "selection persists and drafts become stale");
        try { store.DeleteActiveConfiguration(stale.BaseVersion); throw new Exception("stale deletion accepted"); }
        catch (InvalidOperationException) { }
        Check(File.Exists(alpha) && store.FilePath == alpha && store.Current.Name == "Alpha", "stale deletion preserves newly active config");
        store.DeleteActiveConfiguration(store.Version);
        Check(!File.Exists(alpha) && store.FilePath == beta, "delete switches to final remaining configuration");
        store.DeleteActiveConfiguration(store.Version);
        Check(!File.Exists(beta) && store.Current.Name == "Default" && store.Current.Items.Count == 0 && File.Exists(store.FilePath), "delete last creates empty default");
        Check(ConfigStore.StartupPath(selection) == store.FilePath && store.ListConfigurations().Count == 1, "default selection persists");
    }

    private static void CheckLegacyMigration(string root)
    {
        var source = Path.Combine(root, "legacy-install");
        var directory = Path.Combine(root, "migration-catalog");
        var declined = Path.Combine(root, "migration-declined");
        var empty = Path.Combine(root, "migration-empty");
        foreach (var folder in new[] { source, directory, declined, empty }) Directory.CreateDirectory(folder);
        var legacy = Path.Combine(source, "config.json");
        var legacySelection = Path.Combine(source, "active-config.txt");
        const string legacyJson = "{\"SchemaVersion\":2,\"Items\":[]}";
        File.WriteAllText(legacy, legacyJson);
        File.WriteAllText(Path.Combine(source, "settings.json"), "{\"Language\":\"ru-RU\"}");
        var existing = Path.Combine(directory, "config.json");
        const string existingJson = "{\"SchemaVersion\":3,\"Name\":\"config\",\"Settings\":{},\"Items\":[]}";
        File.WriteAllText(existing, existingJson);
        var prompts = 0;
        bool Confirm(string _, string __) { prompts++; return true; }
        ConfigStore.OfferLegacyMigration(directory, legacySelection, Confirm);
        var files = Directory.GetFiles(directory, "*.json");
        Check(files.Length == 2 && File.ReadAllText(existing) == existingJson, "migration preserves existing config");
        var store = new ConfigStore(selectionFile: Path.Combine(directory, "active-config.txt"), configDirectory: directory);
        Check(store.FilePath == existing, "migration preserves existing catalog selection");
        var migrated = store.ListConfigurations().Single(entry => entry.Path != existing);
        Check(migrated.Name == "config (2)", "migration preserves settings and assigns a unique name");
        ConfigStore.OfferLegacyMigration(directory, legacySelection, Confirm);
        Check(prompts == 1 && File.ReadAllText(legacy) == legacyJson, "migration is offered once and leaves source untouched");
        ConfigStore.OfferLegacyMigration(declined, legacySelection, (_, _) => { prompts++; return false; });
        ConfigStore.OfferLegacyMigration(declined, legacySelection, Confirm);
        Check(prompts == 2 && Directory.GetFiles(declined, "*.json").Length == 0, "declined migration is not offered again");
        ConfigStore.OfferLegacyMigration(empty, legacySelection, Confirm);
        Check(File.Exists(Path.Combine(empty, "config.json")) && prompts == 3, "empty catalog activates migrated config");
    }

    private static void CheckStartupSelection(string root)
    {
        var directory = Path.Combine(root, "catalog-startup");
        Directory.CreateDirectory(directory);
        var selection = Path.Combine(directory, "active-config.txt");
        var first = Path.Combine(directory, "a.json");
        var second = Path.Combine(directory, "b.json");
        var preferred = Path.Combine(directory, "config.json");
        const string config = "{\"SchemaVersion\":3,\"Name\":\"Default\",\"Settings\":{},\"Items\":[]}";
        File.WriteAllText(second, config);
        File.WriteAllText(first, config);
        Check(ConfigStore.StartupPath(selection, true) == first, "existing configurations selected in filename order");
        var store = new ConfigStore(selectionFile: selection, configDirectory: directory);
        Check(store.FilePath == first && store.Current.Name == "Default", "existing catalog starts without writing a duplicate");
        Check(!File.Exists(preferred) && File.ReadAllText(first) == config, "startup does not create or overwrite files");
        File.WriteAllText(preferred, config);
        Check(ConfigStore.StartupPath(selection, true) == preferred, "config.json has priority");
        File.WriteAllText(first, "invalid");
        File.Delete(preferred);
        try { _ = new ConfigStore(selectionFile: selection, configDirectory: directory); throw new Exception("invalid selected configuration accepted"); }
        catch (System.Text.Json.JsonException) { }
        Check(File.ReadAllText(first) == "invalid", "invalid selected configuration remains untouched");
        Check(ConfigStore.StartupPath(selection) == preferred, "legacy startup ignores unrelated JSON files");
        File.WriteAllText(selection, Path.Combine(root, "outside.json"));
        try { _ = new ConfigStore(selectionFile: selection, configDirectory: directory); throw new Exception("outside active selection accepted"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("configuration directory", StringComparison.OrdinalIgnoreCase)) { }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
