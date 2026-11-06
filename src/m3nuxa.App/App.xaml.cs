namespace m3nuxa.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private TrayApplication? tray;

    public App() { _ = L.Language; InitializeComponent(); UnhandledException += (_, e) => DevLog.Write("app", "XAML error", e.Exception); }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        DevLog.Write("app", "Main started");

        var selectionFile = Path.Combine(AppContext.BaseDirectory, "active-config.txt");
        string path;
        try { path = ConfigStore.StartupPath(selectionFile); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error); Exit(); return; }
        ConfigStore store;
        try { store = new ConfigStore(selectionFile: selectionFile); }
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
            store = ConfigStore.CreateDefaultAfterInvalid(path, selectionFile);
            var result = store.Apply(store.CreateDraft());
            if (!result.Success) { MessageBox.Show(result.Error, "m3nuxa"); Exit(); return; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"{path}: {ex.Message}", "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Exit(); return;
        }
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
}
