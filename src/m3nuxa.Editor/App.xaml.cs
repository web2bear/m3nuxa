using Microsoft.UI.Windowing;
using System.IO.Pipes;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using m3nuxa.App;

namespace m3nuxa.Editor;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;
    private EditorOleDropTarget? dropHook;
    private bool dropHookScheduled;
    private readonly CancellationTokenSource selectionStop = new();

    public App()
    {
        _ = L.Language;
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
        MainWindow = new Window { Title = L.Get("Editor_M3nuxaMenuEditor_652d6b"), SystemBackdrop = new MicaBackdrop() };
        DevLog.Write("editor", "Window created");
        MainWindow.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "EditorIcon.ico"));
        MainWindow.AppWindow.Resize(new SizeInt32(1160, 780));
        var position = new EditorWindowPosition(MainWindow.AppWindow);
        MainWindow.AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;
        DevLog.Write("editor", "Creating MainPage");
        var page = new MainPage();
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length >= 3 && arguments[1] == "--select") page.SelectMenuItem(arguments[2]);
        else if (arguments.Length >= 2 && arguments[1] == "--settings") page.ShowSettings();
        _ = ListenForSelectionAsync(page);
        MainWindow.Content = page;
        page.Loaded += (_, _) =>
        {
            if (page.XamlRoot is not { } root) return;
            void UpdateMinimumSize()
            {
                if (MainWindow.AppWindow.Presenter is not OverlappedPresenter presenter) return;
                var width = (int)Math.Ceiling(900 * root.RasterizationScale);
                var height = (int)Math.Ceiling(550 * root.RasterizationScale);
                if (presenter.PreferredMinimumWidth == width && presenter.PreferredMinimumHeight == height) return;
                presenter.PreferredMinimumWidth = width;
                presenter.PreferredMinimumHeight = height;
                var size = MainWindow.AppWindow.Size;
                if (size.Width < width || size.Height < height)
                    MainWindow.AppWindow.Resize(new SizeInt32(Math.Max(size.Width, width), Math.Max(size.Height, height)));
            }
            root.Changed += (_, _) => UpdateMinimumSize();
            UpdateMinimumSize();
        };
        MainWindow.Activated += (_, _) =>
        {
            if (dropHookScheduled) return;
            dropHookScheduled = true;
            AttachDropHook();
            MainWindow.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, AttachDropHook);
        };
        MainWindow.Closed += (_, _) => { position.Save(); selectionStop.Cancel(); dropHook?.Dispose(); };
        void AttachDropHook()
        {
            dropHook?.Dispose();
            dropHook = new EditorOleDropTarget(MainWindow, page.AddDroppedPaths, page.AddExternalText, "", page.InternalDragActive, page.CompleteInternalDrop);
            DevLog.Write("editor", "OLE drop target attached");
        }
        DevLog.Write("editor", "Activating window");
        MainWindow.Activate();
        DevLog.Write("editor", "OnLaunched completed");
        }
        catch (Exception ex) { DevLog.Write("editor", "OnLaunched failed", ex); throw; }
    }
    private async Task ListenForSelectionAsync(MainPage page)
    {
        while (!selectionStop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream($"m3nuxa-editor-{Environment.UserName}-{Environment.ProcessId}", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(selectionStop.Token);
                using var reader = new StreamReader(pipe);
                var id = await reader.ReadLineAsync(selectionStop.Token);
                if (!string.IsNullOrEmpty(id)) MainWindow.DispatcherQueue.TryEnqueue(() => { if (id == "--settings") page.ShowSettings(); else page.SelectMenuItem(id); MainWindow.Activate(); });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { DevLog.Write("editor", "Selection listener failed", ex); await Task.Delay(250); }
        }
    }
}
