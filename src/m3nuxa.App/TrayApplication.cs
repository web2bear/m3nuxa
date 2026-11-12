using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Win32;

namespace m3nuxa.App;

internal sealed class TrayApplication : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly ConfigStore _store;
    private readonly ShellIntegration _tray;
    private readonly Window _window = new() { Title = "m3nuxa" };
    private readonly Grid _root = new();
    private readonly ForegroundTracker _foreground;
    private AgentPipe? _agentPipe;
    private readonly McpHttpServer _mcpHttp = new();
    private readonly string _mcpSettingsPath = Path.Combine(AppContext.BaseDirectory, "mcp-http.json");
    private ToggleMenuFlyoutItem _mcpMenuItem = new();
    private ToggleMenuFlyoutItem _copyMcpAddressItem = new();
    private ToggleMenuFlyoutItem _startupMenuItem = null!;
    private Process? _editor;
    private MenuFlyout? _menu;
    private bool _hotkeysSuspended;
    private bool _exiting;
    private IReadOnlyList<string> _unavailableHotkeys = [];
    private readonly List<WorkflowRun> _workflowRuns = [];
    private WorkflowRunsWindow? _workflowWindow;

    public TrayApplication(ConfigStore store)
    {
        DevLog.Write("app", "TrayApplication starting");
        _store = store;
        _foreground = new ForegroundTracker();
        _window.Content = _root;
        _window.AppWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)_window.AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        // Owned menu popups must stay above the topmost tray overflow panel.
        presenter.IsAlwaysOnTop = true;
        _window.AppWindow.Resize(new SizeInt32(2, 2));
        _tray = new ShellIntegration(_window, service => _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (service) ShowFlyout(BuildServiceMenu()); else OpenEditor();
        }), (node, panel) => _window.DispatcherQueue.TryEnqueue(() => OnHotkey(node, panel)));
        RegisterHotkeys();
        _agentPipe = new AgentPipe(_store, RegisterHotkeys, suspended =>
        {
            _hotkeysSuspended = suspended;
            return RegisterHotkeyBindings();
        }, () => _unavailableHotkeys, ProbeGlobalHotkeys);
        _agentPipe.OpenSettings = OpenSettings;
        if (ReadMcpEnabled()) _ = SetMcpEnabledAsync(true, false);
        _window.Closed += (_, _) => Dispose();
        DevLog.Write("app", "TrayApplication ready");
    }

    private MenuFlyout BuildServiceMenu()
    {
        var menu = LauncherMenu.Create();
        void Add(string text, Action action) { var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add(L.Get("TrayEditor"), OpenEditor);
        Add(L.Get("WorkflowRuns"), ShowWorkflowRuns);
        Add(L.Get("TrayReload"), Reload);
        _mcpMenuItem = new ToggleMenuFlyoutItem { Text = L.Get("TrayMcp"), IsChecked = _mcpHttp.IsRunning };
        _mcpMenuItem.Click += (_, _) => _ = SetMcpEnabledAsync(!_mcpHttp.IsRunning, true);
        menu.Items.Add(_mcpMenuItem);
        _copyMcpAddressItem = new ToggleMenuFlyoutItem { Text = L.Get("TrayCopyMcp"), IsEnabled = _mcpHttp.IsRunning };
        _copyMcpAddressItem.Click += (_, _) => { var data = new DataPackage(); data.SetText(McpHttpServer.Endpoint); Clipboard.SetContent(data); };
        menu.Items.Add(_copyMcpAddressItem);
        _startupMenuItem = new ToggleMenuFlyoutItem { Text = L.Get("TrayStartup"), IsChecked = IsStartupEnabled() };
        _startupMenuItem.Click += (_, _) => ToggleStartup(); menu.Items.Add(_startupMenuItem);
        var languageMenu = new MenuFlyoutSubItem { Text = L.Get("TrayLanguage") };
        foreach (var (label, language) in new[] { ("English", "en-US"), ("Русский", "ru-RU") })
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = L.Language == language };
            item.Click += (_, _) => ChangeLanguage(language);
            languageMenu.Items.Add(item);
        }
        menu.Items.Add(languageMenu);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L.Get("TrayExit"), async () => await ExitAsync());
        return menu;
    }

    private void ChangeLanguage(string language)
    {
        if (L.Language == language) return;
        try
        {
            L.SaveLanguage(language);
            _tray.ShowBalloonTip(5000, L.Get("TrayLanguage"), L.Get("Settings/EditorRestartRequired"), ToolTipIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool ReadMcpEnabled()
    {
        try
        {
            if (!File.Exists(_mcpSettingsPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(_mcpSettingsPath));
            return document.RootElement.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean();
        }
        catch (Exception ex)
        {
            DevLog.Write("mcp-http", "Could not read saved state", ex);
            return false;
        }
    }

    private async Task SetMcpEnabledAsync(bool enabled, bool save)
    {
        _mcpMenuItem.IsEnabled = false;
        try
        {
            if (enabled) await _mcpHttp.StartAsync(_agentPipe ?? throw new InvalidOperationException(L.Get("TrayPipeNotReady")));
            else await _mcpHttp.StopAsync();
            if (save) File.WriteAllText(_mcpSettingsPath, JsonSerializer.Serialize(new { enabled }));
        }
        catch (Exception ex)
        {
            DevLog.Write("mcp-http", "Toggle failed", ex);
            _tray.ShowBalloonTip(5000, L.Get("TrayMcp"), ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _mcpMenuItem.IsChecked = _mcpHttp.IsRunning;
            _mcpMenuItem.IsEnabled = true;
            _copyMcpAddressItem.IsEnabled = _mcpHttp.IsRunning;
        }
    }

    private void OpenSettings() => OpenEditor(null, showSettings: true);

    private void OpenEditor() => OpenEditor(null);

    private async void OpenEditor(string? nodeId, bool showSettings = false)
    {
        DevLog.Write("app", "OpenEditor requested");
        var path = Path.Combine(AppContext.BaseDirectory, "m3nuxa.Editor.exe");
        if (_editor is null || _editor.HasExited)
        {
            _editor = Process.GetProcessesByName("m3nuxa.Editor").FirstOrDefault(process =>
            {
                try { return string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase); }
                catch (Exception ex) { DevLog.Write("app", "Could not inspect editor process", ex); return false; }
            });
        }
        if (_editor is { HasExited: false })
        {
            DevLog.Write("app", $"Editor already running pid={_editor.Id} hwnd={_editor.MainWindowHandle}");
            if (nodeId is not null || showSettings)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", $"m3nuxa-editor-{Environment.UserName}-{_editor.Id}", PipeDirection.Out);
                    await pipe.ConnectAsync(1000);
                    using var writer = new StreamWriter(pipe) { AutoFlush = true };
                    await writer.WriteLineAsync(showSettings ? "--settings" : nodeId);
                }
                catch (Exception ex) { DevLog.Write("app", "Could not select editor item", ex); }
            }
            _editor.Refresh();
            if (_editor.MainWindowHandle != IntPtr.Zero)
            {
                if (IsIconic(_editor.MainWindowHandle)) ShowWindow(_editor.MainWindowHandle, 9);
                NativeMethods.SetForegroundWindow(_editor.MainWindowHandle);
            }
            return;
        }
        if (!File.Exists(path))
        {
            DevLog.Write("app", $"Editor not found: {path}");
            MessageBox.Show(L.Format("TrayEditorMissing", path), "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = true };
            if (showSettings) start.ArgumentList.Add("--settings");
            else if (nodeId is not null) { start.ArgumentList.Add("--select"); start.ArgumentList.Add(nodeId); }
            _editor = Process.Start(start);
            DevLog.Write("app", $"Editor process started pid={_editor?.Id}");
            if (_editor is not null)
            {
                var launchedEditor = _editor;
                launchedEditor.Exited += (_, _) => DevLog.Write("app", $"Editor process exited pid={launchedEditor.Id} code={launchedEditor.ExitCode}");
                launchedEditor.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            DevLog.Write("app", "Editor process failed to start", ex);
            MessageBox.Show(ex.Message, "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Reload()
    {
        var result = _store.Reload();
        if (!result.Success) MessageBox.Show(result.Error ?? L.Get("TrayReloadFailed"), "m3nuxa");
        else RegisterHotkeys();
    }

    private void ShowLauncherPanel()
    {
        var invocation = InvocationContext.Capture(_foreground.LastWindow);
        _menu?.Hide();
        LauncherPanel.Show(_store.Current.Items, node => _ = Execute(node, invocation), OpenEditor,
            _store.Current.Settings);
    }

    private void ShowMenu(IReadOnlyList<MenuNode> nodes)
    {
        var invocation = InvocationContext.Capture(_foreground.LastWindow);
        var menu = LauncherMenu.Create(_store.Current.Settings);
        LauncherMenu.Fill(menu.Items, nodes, node => _ = Execute(node, invocation), node => { _menu?.Hide(); OpenEditor(node.Id); }, _store.Current.Settings);
        if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = L.Get("TrayEmptyMenu"), IsEnabled = false });
        ShowFlyout(menu);
    }

    private void ShowFlyout(MenuFlyout menu)
    {
        _menu?.Hide();
        _menu = menu;
        GetCursorPos(out var cursor);
        _window.AppWindow.Move(new PointInt32(cursor.X, cursor.Y));
        _window.Activate();
        SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(_window), new IntPtr(-1), 0, 0, 0, 0, 0x43);
        NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_menu, menu)) return;
            _menu = null;
            if (!_disposed) _window.AppWindow?.Hide();
        };
        void Show() { if (ReferenceEquals(_menu, menu)) { menu.ShowAt(_root, new Windows.Foundation.Point(0, 0)); DevLog.Write("menu", "WinUI menu opened"); } }
        if (_root.XamlRoot is null) { RoutedEventHandler? loaded = null; loaded = (_, _) => { _root.Loaded -= loaded; Show(); }; _root.Loaded += loaded; }
        else Show();
    }
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        foreach (var run in _workflowRuns.Where(run => !run.Finished)) run.Cancellation.Cancel();
        await Task.WhenAll(_workflowRuns.Where(run => !run.Finished).Select(run => run.Completion.Task));
        await _mcpHttp.StopAsync();
        Dispose();
        _window.Close();
        Application.Current.Exit();
    }

    private void ShowWorkflowRuns()
    {
        if (_workflowWindow is null)
        {
            _workflowWindow = new WorkflowRunsWindow(_workflowRuns);
            _workflowWindow.Closed += (_, _) => _workflowWindow = null;
        }
        _workflowWindow.Refresh();
        _workflowWindow.Activate();
    }

    private async Task Execute(MenuNode node, InvocationContext? context)
    {
        if (_exiting) return;
        if (_workflowRuns.Any(run => run.NodeId == node.Id && !run.Finished))
        {
            _tray.ShowBalloonTip(5000, L.Get("WorkflowRuns"), L.Get("WorkflowAlreadyRunning"), ToolTipIcon.Warning, ShowWorkflowRuns);
            ShowWorkflowRuns();
            return;
        }
        // A running action must be isolated from later configuration edits.
        var steps = JsonSerializer.Deserialize<List<MenuAction>>(JsonSerializer.Serialize(node.Steps))!;
        var run = new WorkflowRun { NodeId = node.Id, Name = node.Name };
        _workflowRuns.Add(run);
        _workflowWindow?.Refresh();
        try
        {
            var result = await WorkflowExecutor.RunAsync(steps, context ?? InvocationContext.Capture(), run.Cancellation.Token,
                (index, step) =>
                {
                    run.StepIndex = index;
                    run.StepKind = Enum.IsDefined(step.Kind) ? L.Get("WorkflowKind" + step.Kind) : L.Get("ActionUnknown");
                    _workflowWindow?.Refresh();
                });
            run.FileSteps = result.FileSteps;
            run.Cancelled = result.Cancelled;
            run.Error = result.Error;
            run.StepIndex = result.StepIndex;
            if (run.Error is not null && !_disposed)
                _tray.ShowBalloonTip(5000, run.Name, run.Error, ToolTipIcon.Error, ShowWorkflowRuns);
        }
        catch (Exception ex)
        {
            run.Error = ex.Message;
            if (!_disposed) _tray.ShowBalloonTip(5000, run.Name, run.Error, ToolTipIcon.Error, ShowWorkflowRuns);
        }
        finally
        {
            run.Finished = true;
            run.Completion.TrySetResult();
            _workflowWindow?.Refresh();
        }
    }

    private void OnHotkey(MenuNode? node, bool panel)
    {
        if (_hotkeysSuspended) return;
        if (panel) ShowLauncherPanel();
        else if (node is null) ShowMenu(_store.Current.Items);
        else if (node.Kind == MenuNodeKind.Group) ShowMenu(node.Children);
        else _ = Execute(node, InvocationContext.Capture(_foreground.LastWindow));
    }

    private void RegisterHotkeys()
    {
        RegisterHotkeyBindings();
    }

    private IReadOnlyList<string> RegisterHotkeyBindings()
    {
        if (_hotkeysSuspended) return _tray.Register([]);
        var unavailable = _tray.Register(HotkeyBindings(_store.Current));
        _unavailableHotkeys = unavailable;
        if (unavailable.Count > 0) _tray.ShowBalloonTip(5000, L.Get("TrayHotkeysUnavailable"), string.Join(", ", unavailable), ToolTipIcon.Warning);
        return unavailable;
    }

    private string? ProbeGlobalHotkeys(AppConfig candidate)
    {
        static string Key(string text) => HotkeyParser.TryParse(text, out var modifiers, out var key) ? $"{modifiers}:{key}" : "";
        var current = _store.Current.Settings;
        var settings = candidate.Settings;
        var mainChanged = Key(settings.MainHotkey) != Key(current.MainHotkey);
        var panelChanged = Key(settings.PanelHotkey) != Key(current.PanelHotkey);
        if (!mainChanged && !panelChanged) return null;
        if (_hotkeysSuspended) return L.Get("SettingsCaptureInEditor");
        try
        {
            var unavailable = _tray.Register(HotkeyBindings(candidate));
            if ((mainChanged && unavailable.Contains(settings.MainHotkey)) || (panelChanged && unavailable.Contains(settings.PanelHotkey)))
                return L.Get("SettingsHotkeyUnavailable");
            return null;
        }
        finally { RegisterHotkeyBindings(); }
    }

    private static List<(string Hotkey, MenuNode? Node, bool Panel)> HotkeyBindings(AppConfig config)
    {
        var bindings = new List<(string Hotkey, MenuNode? Node, bool Panel)>();
        var mainHotkey = config.Settings.MainHotkey;
        if (!string.IsNullOrWhiteSpace(mainHotkey)) bindings.Add((mainHotkey, null, false));
        var panelHotkey = config.Settings.PanelHotkey;
        if (!string.IsNullOrWhiteSpace(panelHotkey)) bindings.Add((panelHotkey, null, true));
        void Walk(IEnumerable<MenuNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Kind != MenuNodeKind.Separator && !string.IsNullOrWhiteSpace(node.Hotkey)) bindings.Add((node.Hotkey, node, false));
                Walk(node.Children);
            }
        }
        Walk(config.Items);
        return bindings;
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("m3nuxa") is string;
    }

    private void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue("m3nuxa") is not null) key.DeleteValue("m3nuxa", false);
        else
        {
            var command = $"\"{Environment.ProcessPath}\"";
            if (Environment.GetCommandLineArgs().Skip(1).Any(arg => arg == "--config-dir" || arg.StartsWith("--config-dir=", StringComparison.Ordinal)))
                command += $" --config-dir \"{_store.ConfigDirectory}\"";
            key.SetValue("m3nuxa", command);
        }
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var run in _workflowRuns.Where(run => !run.Finished)) run.Cancellation.Cancel();
        _workflowWindow?.Close();
        _tray.Dispose();
        _menu?.Hide();
        _foreground.Dispose();
        _agentPipe?.Dispose();
    }
}
