namespace m3nuxa.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private TrayApplication? tray;
    private Mutex? instanceMutex;

    public App() { _ = L.Language; InitializeComponent(); UnhandledException += (_, e) => DevLog.Write("app", "XAML error", e.Exception); }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        instanceMutex = new Mutex(false, @"Local\m3nuxa.TrayApplication", out var isFirstInstance);
        if (!isFirstInstance)
        {
            instanceMutex.Dispose();
            instanceMutex = null;
            Exit();
            return;
        }

        DevLog.Write("app", "Main started");

        string configDirectory;
        string? migratedLanguageSource = null;
        try
        {
            configDirectory = ResolveConfigDirectory(Environment.GetCommandLineArgs().Skip(1).ToArray());
            Directory.CreateDirectory(configDirectory);
            ConfigStore.OfferLegacyMigration(configDirectory, Path.Combine(AppContext.BaseDirectory, "active-config.txt"),
                (legacyPath, directory) =>
                {
                    var accepted = MessageBox.Show(L.Format("AppMigrateConfig", legacyPath, directory), "m3nuxa",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    if (accepted && !Directory.EnumerateFiles(directory, "*.json").Any()) migratedLanguageSource = legacyPath;
                    return accepted;
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Exit(); return;
        }
        var selectionFile = Path.Combine(configDirectory, "active-config.txt");
        string path;
        try { path = ConfigStore.StartupPath(selectionFile, selectFromCatalog: true); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error); Exit(); return; }
        ConfigStore store;
        try { store = new ConfigStore(selectionFile: selectionFile, configDirectory: configDirectory); }
        catch (LegacySettingsException ex)
        {
            MessageBox.Show(ex.Message, "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Exit(); return;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show($"{selectionFile}: {ex.Message}", "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Exit(); return;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            var choice = MessageBox.Show(L.Format("AppInvalidConfig", path, ex.Message),
                "m3nuxa", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Error);
            if (choice == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
                Exit(); return;
            }
            if (choice != DialogResult.No) { Exit(); return; }
            store = ConfigStore.CreateDefaultAfterInvalid(path, selectionFile, configDirectory);
            var result = store.Apply(store.CreateDraft());
            if (!result.Success) { MessageBox.Show(result.Error, "m3nuxa"); Exit(); return; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"{path}: {ex.Message}", "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Exit(); return;
        }
        L.MigrateLanguage(migratedLanguageSource ?? store.FilePath, Path.Combine(AppContext.BaseDirectory, "settings.json"));
        if (!File.Exists(store.FilePath))
        {
            var initial = store.Apply(store.CreateDraft());
            if (!initial.Success)
            {
                MessageBox.Show(initial.Error ?? L.Get("AppCreateConfigFailed"), "m3nuxa");
                Exit(); return;
            }
        }
        try { tray = new TrayApplication(store); }
        catch (Exception ex) { DevLog.Write("app", "Fatal UI error", ex); throw; }

    }    

    internal static string ResolveConfigDirectory(string[] args)
    {
        string? specified = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config-dir")
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("--config-dir requires a directory path.");
                specified = args[i];
            }
            else if (args[i].StartsWith("--config-dir=", StringComparison.Ordinal))
                specified = args[i]["--config-dir=".Length..];
        }
        if (specified is not null && string.IsNullOrWhiteSpace(specified))
            throw new ArgumentException("--config-dir requires a directory path.");
        var configured = specified ?? Environment.GetEnvironmentVariable("M3NUXA_CONFIG_DIR");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "configs") : configured);
    }

}
