namespace ContexWin.App;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        DevLog.Write("app", "Main started");
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        ConfigStore store;
        try { store = new ConfigStore(); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            var choice = MessageBox.Show($"Ошибка в файле {path}:\n{ex.Message}\n\nДа — открыть файл для исправления.\nНет — создать пустую конфигурацию с копией ошибочного файла.\nОтмена — выйти.",
                "ContexWin", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Error);
            if (choice == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
                return;
            }
            if (choice != DialogResult.No) return;
            store = ConfigStore.CreateDefaultAfterInvalid(path);
            var result = store.Apply(store.CreateDraft());
            if (!result.Success) { MessageBox.Show(result.Error, "ContexWin"); return; }
        }
        if (!File.Exists(store.FilePath))
        {
            var initial = store.Apply(store.CreateDraft());
            if (!initial.Success)
            {
                MessageBox.Show(initial.Error ?? "Не удалось создать конфигурацию рядом с приложением.", "ContexWin");
                return;
            }
        }
        try { Application.Run(new TrayApplication(store)); }
        catch (Exception ex) { DevLog.Write("app", "Fatal UI error", ex); throw; }
        finally { DevLog.Write("app", "Main stopped"); }
    }    
}
