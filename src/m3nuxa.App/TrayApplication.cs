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
    private readonly SettingsStore _settings;
    private SettingsWindow? _settingsWindow;
    private bool _settingsRecording;
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
        _settings = new SettingsStore(Path.Combine(store.SettingsDirectory, "settings.json"), store.Current.MainHotkey, L.Get);
        _store.GlobalHotkey = () => _settings.Current.MainHotkey;
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
            if (service) ShowFlyout(BuildServiceMenu()); else ShowMenu(_store.Current.Items);
        }), node => _window.DispatcherQueue.TryEnqueue(() => OnHotkey(node)));
        RegisterHotkeys();
        _agentPipe = new AgentPipe(_store, RegisterHotkeys, suspended =>
        {
            _hotkeysSuspended = suspended;
            return RegisterHotkeyBindings();
        }, () => _unavailableHotkeys);
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
        Add(L.Get("TraySettings"), OpenSettings);
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
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L.Get("TrayExit"), async () => await ExitAsync());
        return menu;
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

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, SaveSettings, suspended =>
            {
                _settingsRecording = suspended; RegisterHotkeyBindings();
            }, () => ShowMenu(_store.Current.Items));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            // Keep app and test windows on the additional screen when available.
            var displays = DisplayArea.FindAll();
            var display = DisplayArea.Primary;
            for (var i = 0; i < displays.Count; i++) if (!displays[i].IsPrimary) { display = displays[i]; break; }
            var work = display.WorkArea;
            _settingsWindow.AppWindow.Move(new PointInt32(work.X + Math.Max(0, (work.Width - _settingsWindow.AppWindow.Size.Width) / 2), work.Y + Math.Max(0, (work.Height - _settingsWindow.AppWindow.Size.Height) / 2)));
        }
        _settingsWindow.Activate();
    }

    private string? SaveSettings(AppSettings candidate)
    {
        try
        {
            SettingsStore.Validate(candidate, L.Get);
            if (candidate.MainHotkey == _settings.Current.MainHotkey) { _settings.Save(candidate); return null; }
            if (_hotkeysSuspended) return L.Get("SettingsCaptureInEditor");
            var config = _store.Current;
            // Validate against the candidate without changing the running settings.
            string Key(string text) => HotkeyParser.TryParse(text, out var modifiers, out var key) ? $"{modifiers}:{key}" : text;
            bool Conflicts(IEnumerable<MenuNode> nodes) => nodes.Any(node =>
                (node.Kind != MenuNodeKind.Separator && node.Hotkey.Length > 0 && Key(node.Hotkey) == Key(candidate.MainHotkey)) || Conflicts(node.Children));
            if (candidate.MainHotkey.Length > 0 && Conflicts(config.Items)) return L.Get("SettingsHotkeyConflict");
            var unavailable = RegisterHotkeyBindings(candidate.MainHotkey);
            if (candidate.MainHotkey.Length > 0 && unavailable.Contains(candidate.MainHotkey))
            {
                RegisterHotkeyBindings();
                return L.Get("SettingsHotkeyUnavailable");
            }
            _settings.Save(candidate);
            return null;
        }
        catch (Exception ex)
        {
            RegisterHotkeyBindings();
            DevLog.Write("settings", "Save failed", ex);
            return ex.Message;
        }
    }

    private void OpenEditor() => OpenEditor(null);

    private async void OpenEditor(string? nodeId)
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
            if (nodeId is not null)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", $"m3nuxa-editor-{Environment.UserName}-{_editor.Id}", PipeDirection.Out);
                    await pipe.ConnectAsync(1000);
                    using var writer = new StreamWriter(pipe) { AutoFlush = true };
                    await writer.WriteLineAsync(nodeId);
                }
                catch (Exception ex) { DevLog.Write("app", "Could not select editor item", ex); }
            }
            _editor.Refresh();
            if (_editor.MainWindowHandle != IntPtr.Zero) NativeMethods.SetForegroundWindow(_editor.MainWindowHandle);
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
            if (nodeId is not null) { start.ArgumentList.Add("--select"); start.ArgumentList.Add(nodeId); }
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

    private void ShowMenu(IReadOnlyList<MenuNode> nodes)
    {
        var invocation = InvocationContext.Capture(_foreground.LastWindow);
        var menu = LauncherMenu.Create();
        LauncherMenu.Fill(menu.Items, nodes, node => _ = Execute(node, invocation), node => { _menu?.Hide(); OpenEditor(node.Id); }, _settings.Current.MenuFontSize);
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
        if (_exiting || node.Steps.Count == 0) return;
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
                    run.StepKind = L.Get("WorkflowKind" + step.Kind);
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

    private void OnHotkey(MenuNode? node)
    {
        if (_hotkeysSuspended || _settingsRecording) return;
        if (node is null) ShowMenu(_store.Current.Items);
        else if (node.Kind == MenuNodeKind.Group) ShowMenu(node.Children);
        else _ = Execute(node, InvocationContext.Capture(_foreground.LastWindow));
    }

    private void RegisterHotkeys()
        => RegisterHotkeyBindings();

    private IReadOnlyList<string> RegisterHotkeyBindings(string? mainHotkey = null)
    {
        if (_hotkeysSuspended || _settingsRecording) return _tray.Register([]);
        var bindings = new List<(string Hotkey, MenuNode? Node)>();
        mainHotkey ??= _settings.Current.MainHotkey;
        if (!string.IsNullOrWhiteSpace(mainHotkey)) bindings.Add((mainHotkey, null));
        void Walk(IEnumerable<MenuNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Kind != MenuNodeKind.Separator && !string.IsNullOrWhiteSpace(node.Hotkey)) bindings.Add((node.Hotkey, node));
                Walk(node.Children);
            }
        }
        Walk(_store.Current.Items);
        var unavailable = _tray.Register(bindings);
        _unavailableHotkeys = unavailable;
        if (unavailable.Count > 0) _tray.ShowBalloonTip(5000, L.Get("TrayHotkeysUnavailable"), string.Join(", ", unavailable), ToolTipIcon.Warning);
        return unavailable;
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("m3nuxa") is string;
    }

    private static void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue("m3nuxa") is not null) key.DeleteValue("m3nuxa", false);
        else key.SetValue("m3nuxa", $"\"{Environment.ProcessPath}\"");
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
