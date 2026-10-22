namespace m3nuxa.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private TrayApplication? tray;

    public App() { InitializeComponent(); UnhandledException += (_, e) => DevLog.Write("app", "XAML error", e.Exception); }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        DevLog.Write("app", "Main started");

        ConfigStore store;
        try { store = new ConfigStore(); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            var choice = MessageBox.Show($"Ошибка в файле {path}:\n{ex.Message}\n\nДа — открыть файл для исправления.\nНет — создать пустую конфигурацию с копией ошибочного файла.\nОтмена — выйти.",
                "m3nuxa", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Error);
            if (choice == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
                Exit(); return;
            }
            if (choice != DialogResult.No) { Exit(); return; }
            store = ConfigStore.CreateDefaultAfterInvalid(path);
            var result = store.Apply(store.CreateDraft());
            if (!result.Success) { MessageBox.Show(result.Error, "m3nuxa"); Exit(); return; }
        }
        if (!File.Exists(store.FilePath))
        {
            var initial = store.Apply(store.CreateDraft());
            if (!initial.Success)
            {
                MessageBox.Show(initial.Error ?? "Не удалось создать конфигурацию рядом с приложением.", "m3nuxa");
                Exit(); return;
            }
        }
        try { tray = new TrayApplication(store); }
        catch (Exception ex) { DevLog.Write("app", "Fatal UI error", ex); throw; }

    }    
}
