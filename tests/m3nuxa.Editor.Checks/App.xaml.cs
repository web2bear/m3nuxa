namespace m3nuxa.Editor;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var resultPath = Environment.GetEnvironmentVariable("m3nuxa_CHECK_RESULT")
            ?? Path.Combine(AppContext.BaseDirectory, "result.txt");
        try
        {
            MainWindow = new Window { Title = "m3nuxa — проверка дерева" };
            if (MainWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;
            MainWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1160, 780));
            var displays = Microsoft.UI.Windowing.DisplayArea.FindAll();
            Microsoft.UI.Windowing.DisplayArea? additionalDisplay = null;
            for (var index = 0; index < displays.Count; index++)
            {
                var display = displays[index];
                if (display.IsPrimary) continue;
                additionalDisplay = display;
                MainWindow.AppWindow.Move(new Windows.Graphics.PointInt32(display.WorkArea.X + 40, display.WorkArea.Y + 40));
                break;
            }
            var page = new Views.MainPage();
            page.DisableConfigurationLoadForChecks();
            MainWindow.Content = page;
            MainWindow.Activate();
            await Task.Delay(100);
            if (additionalDisplay is not null)
            {
                var work = additionalDisplay.WorkArea;
                var position = MainWindow.AppWindow.Position;
                var size = MainWindow.AppWindow.Size;
                if (position.X < work.X || position.Y < work.Y || position.X + size.Width > work.X + work.Width || position.Y + size.Height > work.Y + work.Height)
                    throw new Exception($"Check window is outside the additional screen work area: {position.X},{position.Y}, {size.Width}x{size.Height}.");
            }
            page.CheckLocalization();
            page.CheckConfigurationTitle();
            if (Environment.GetEnvironmentVariable("m3nuxa_DROP_CHECK") is { } snapshotPath)
            {
                page.SetupDropChecks();
                var log = Environment.GetEnvironmentVariable("m3nuxa_OLE_PROBE") ?? "";
                IDisposable hook = new EditorOleDropTarget(MainWindow, page.AddDroppedPaths, page.AddProbeText, log, page.InternalDragActive, page.CompleteInternalDrop);
                using (hook)
                while (true)
                {
                    await File.WriteAllTextAsync(snapshotPath, page.DropCheckSnapshot());
                    await Task.Delay(200);
                }
            }
            var pickerRoot = Path.Combine(Path.GetTempPath(), "m3nuxa-picker-" + Guid.NewGuid().ToString("N"));
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
            await page.CheckPropertyPanelLayoutAsync();
            await page.CheckContextVariablesAsync();
            await page.CheckWorkflowStepsAsync();
            await page.CheckTreeIconConsistencyAsync();
            await page.CheckExplorerIconsAsync();
            await page.CheckTreeReorderAsync();
            page.CheckApplyAvailability();
            await page.CheckShortcutRecordingAsync();
            await File.WriteAllTextAsync(resultPath, "PASS: context variables and button order, icons, tree reorder, apply availability; real shortcut recording, release-to-commit, Undo/Redo, Esc, duplicate conflict, sent shortcut, selection cancellation, clear.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(resultPath, "FAIL: " + ex);
            Environment.Exit(1);
        }
    }
}
