using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using m3nuxa.App;

namespace m3nuxa.Editor;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;
    private EditorOleDropTarget? dropHook;
    private bool dropHookScheduled;

    public App()
    {
        DevLog.Write("editor", "App constructor started");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DevLog.Write("editor", "AppDomain unhandled exception", e.ExceptionObject as Exception);
        UnhandledException += (_, e) => DevLog.Write("editor", "XAML unhandled exception", e.Exception);
        InitializeComponent();
        DevLog.Write("editor", "App constructor completed");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DevLog.Write("editor", "OnLaunched started");
        try
        {
        MainWindow = new Window { Title = "m3nuxa — редактор меню", SystemBackdrop = new MicaBackdrop() };
        DevLog.Write("editor", "Window created");
        MainWindow.AppWindow.Resize(new SizeInt32(1160, 780));
        MainWindow.AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;
        DevLog.Write("editor", "Creating MainPage");
        var page = new MainPage();
        MainWindow.Content = page;
        MainWindow.Activated += (_, _) =>
        {
            if (dropHookScheduled) return;
            dropHookScheduled = true;
            AttachDropHook();
            MainWindow.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, AttachDropHook);
        };
        MainWindow.Closed += (_, _) => dropHook?.Dispose();
        void AttachDropHook()
        {
            dropHook?.Dispose();
            dropHook = new EditorOleDropTarget(MainWindow, page.AddDroppedPaths, page.AddExternalText, "");
            DevLog.Write("editor", "OLE drop target attached");
        }
        DevLog.Write("editor", "Activating window");
        MainWindow.Activate();
        DevLog.Write("editor", "OnLaunched completed");
        }
        catch (Exception ex) { DevLog.Write("editor", "OnLaunched failed", ex); throw; }
    }
}
