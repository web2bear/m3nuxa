namespace ContexWin.Editor;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var resultPath = Environment.GetEnvironmentVariable("CONTEXWIN_CHECK_RESULT")
            ?? Path.Combine(AppContext.BaseDirectory, "result.txt");
        try
        {
            MainWindow = new Window { Title = "ContexWin — проверка дерева" };
            MainWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1160, 780));
            var displays = Microsoft.UI.Windowing.DisplayArea.FindAll();
            for (var index = 0; index < displays.Count; index++)
            {
                var display = displays[index];
                if (display.IsPrimary) continue;
                MainWindow.AppWindow.Move(new Windows.Graphics.PointInt32(display.WorkArea.X + 40, display.WorkArea.Y + 40));
                break;
            }
            var page = new Views.MainPage();
            page.DisableConfigurationLoadForChecks();
            MainWindow.Content = page;
            MainWindow.Activate();
            await Task.Delay(100);
            var pickerRoot = Path.Combine(Path.GetTempPath(), "contexwin-picker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pickerRoot);
            try
            {
                var picker = new IconFilePicker(pickerRoot);
                var bundled = Path.Combine(pickerRoot, "icon-packs", "Fluent");
                if (picker.StartFolder != bundled) throw new Exception("First icon selection must start in the bundled folder.");
                var alternate = Path.Combine(pickerRoot, "other-icons"); Directory.CreateDirectory(alternate);
                picker.RememberSelection(Path.Combine(alternate, "test.png"));
                if (new IconFilePicker(pickerRoot).StartFolder != alternate) throw new Exception("Last icon folder was not restored by a new picker.");
                Directory.Delete(alternate);
                if (picker.StartFolder != bundled) throw new Exception("Deleted last folder must fall back to the bundle.");
            }
            finally { Directory.Delete(pickerRoot, true); }
            await page.CheckTreeIconConsistencyAsync();
            await page.CheckExplorerIconsAsync();
            await page.CheckTreeReorderAsync();
            await File.WriteAllTextAsync(resultPath, "PASS: initial tree and property icons agree; Explorer icons, shortcut import, image preview; tree labels, models and properties after reparenting and refresh.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(resultPath, "FAIL: " + ex);
            Environment.Exit(1);
        }
    }
}
