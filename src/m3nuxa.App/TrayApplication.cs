using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace m3nuxa.App;

internal sealed class TrayApplication : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly ConfigStore _store;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys;
    private readonly ForegroundTracker _foreground;
    private AgentPipe? _agentPipe;
    private readonly McpHttpServer _mcpHttp = new();
    private readonly string _mcpSettingsPath = Path.Combine(AppContext.BaseDirectory, "mcp-http.json");
    private ToolStripMenuItem _mcpMenuItem = null!;
    private ToolStripMenuItem _copyMcpAddressItem = null!;
    private ToolStripMenuItem _startupMenuItem = null!;
    private Process? _editor;
    private ContextMenuStrip? _menu;

    public TrayApplication(ConfigStore store)
    {
        DevLog.Write("app", "TrayApplication starting");
        _store = store;
        _foreground = new ForegroundTracker();
        _hotkeys = new HotkeyWindow(OnHotkey);
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "m3nuxa", Visible = true, ContextMenuStrip = BuildServiceMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(_store.Current.Items); };
        RegisterHotkeys();
        Application.Idle += StartAgentPipe;
        DevLog.Write("app", "TrayApplication ready");
    }

    private void StartAgentPipe(object? sender, EventArgs e)
    {
        Application.Idle -= StartAgentPipe;
        _agentPipe = new AgentPipe(_store, RegisterHotkeys);
        if (ReadMcpEnabled()) _ = SetMcpEnabledAsync(true, false);
    }

    private ContextMenuStrip BuildServiceMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть редактор", null, (_, _) => OpenEditor());
        menu.Items.Add("Перезагрузить конфигурацию", null, (_, _) => Reload());
        _mcpMenuItem = new ToolStripMenuItem("MCP HTTP-сервер", null, (_, _) => _ = SetMcpEnabledAsync(!_mcpHttp.IsRunning, true));
        menu.Items.Add(_mcpMenuItem);
        _copyMcpAddressItem = new ToolStripMenuItem("Копировать адрес MCP", null, (_, _) => Clipboard.SetText(McpHttpServer.Endpoint));
        menu.Items.Add(_copyMcpAddressItem);
        _startupMenuItem = new ToolStripMenuItem("Автозапуск", null, (_, _) => ToggleStartup());
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, async (_, _) => { await _mcpHttp.StopAsync(); ExitThread(); });
        menu.Opening += (_, _) =>
        {
            _mcpMenuItem.Checked = _mcpHttp.IsRunning;
            _mcpMenuItem.Enabled = _agentPipe is not null;
            _copyMcpAddressItem.Enabled = _mcpHttp.IsRunning;
            _startupMenuItem.Checked = IsStartupEnabled();
        };
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
        _mcpMenuItem.Enabled = false;
        try
        {
            if (enabled) await _mcpHttp.StartAsync(_agentPipe ?? throw new InvalidOperationException("Редакторский канал ещё не готов."));
            else await _mcpHttp.StopAsync();
            if (save) File.WriteAllText(_mcpSettingsPath, JsonSerializer.Serialize(new { enabled }));
        }
        catch (Exception ex)
        {
            DevLog.Write("mcp-http", "Toggle failed", ex);
            _tray.ShowBalloonTip(5000, "MCP HTTP-сервер", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _mcpMenuItem.Checked = _mcpHttp.IsRunning;
            _mcpMenuItem.Enabled = true;
            _copyMcpAddressItem.Enabled = _mcpHttp.IsRunning;
        }
    }

    private void OpenEditor()
    {
        DevLog.Write("app", "OpenEditor requested");
        if (_editor is { HasExited: false })
        {
            DevLog.Write("app", $"Editor already running pid={_editor.Id} hwnd={_editor.MainWindowHandle}");
            if (_editor.MainWindowHandle != IntPtr.Zero) NativeMethods.SetForegroundWindow(_editor.MainWindowHandle);
            return;
        }
        var path = Path.Combine(AppContext.BaseDirectory, "m3nuxa.Editor.exe");
        if (!File.Exists(path))
        {
            DevLog.Write("app", $"Editor not found: {path}");
            MessageBox.Show($"Редактор WinUI 3 не найден: {path}", "m3nuxa", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try
        {
            _editor = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
        if (!result.Success) MessageBox.Show(result.Error ?? "Не удалось загрузить конфигурацию.", "m3nuxa");
        else RegisterHotkeys();
    }

    private void ShowMenu(IReadOnlyList<MenuNode> nodes)
    {
        var invocation = InvocationContext.Capture(_foreground.LastWindow);
        DisposeMenu();
        _menu = new ContextMenuStrip { AutoClose = true };
        LauncherMenu.Style(_menu);
        FillMenu(_menu.Items, nodes, invocation);
        if (_menu.Items.Count == 0) _menu.Items.Add("Меню пусто").Enabled = false;
        _menu.Show(Cursor.Position);
        // A menu opened from a global hotkey has no active owner window. Give the
        // visible drop-down foreground activation so outside clicks dismiss it.
        NativeMethods.SetForegroundWindow(_menu.Handle);
    }

    private void FillMenu(ToolStripItemCollection target, IEnumerable<MenuNode> nodes, InvocationContext invocation)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == MenuNodeKind.Separator) { target.Add(new ToolStripSeparator { Margin = new Padding(0, 2, 0, 2) }); continue; }
            var item = new LauncherMenu.Item(node.Name)
            {
                Image = ItemIcon.Load(node),
                ShortcutKeyDisplayString = node.Hotkey,
                Margin = new Padding(20, 0, 8, 0),
                Padding = new Padding(8, 10, 14, 10),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleLeft
            };
            target.Add(item);
            if (node.Kind == MenuNodeKind.Group) { LauncherMenu.Style(item.DropDown); FillMenu(item.DropDownItems, node.Children, invocation); }
            else item.Click += async (_, _) => await Execute(node, invocation);
        }
    }

    private void DisposeMenu()
    {
        if (_menu is null) return;
        void Release(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.Image?.Dispose(); item.Image = null;
                if (item is ToolStripMenuItem child && child.HasDropDownItems) Release(child.DropDownItems);
            }
        }
        Release(_menu.Items);
        _menu.Dispose();
        _menu = null;
    }

    private async Task Execute(MenuNode node, InvocationContext? context)
    {
        if (node.Action is null) return;
        try
        {
            await ActionExecutor.ExecuteAsync(node.Action, context ?? InvocationContext.Capture());
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, node.Name); }
    }

    private void OnHotkey(MenuNode? node)
    {
        if (node is null) ShowMenu(_store.Current.Items);
        else if (node.Kind == MenuNodeKind.Group) ShowMenu(node.Children);
        else _ = Execute(node, InvocationContext.Capture(_foreground.LastWindow));
    }

    private void RegisterHotkeys()
    {
        var bindings = new List<(string Hotkey, MenuNode? Node)> { (_store.Current.MainHotkey, null) };
        void Walk(IEnumerable<MenuNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Kind != MenuNodeKind.Separator && !string.IsNullOrWhiteSpace(node.Hotkey)) bindings.Add((node.Hotkey, node));
                Walk(node.Children);
            }
        }
        Walk(_store.Current.Items);
        var unavailable = _hotkeys.Register(bindings);
        if (unavailable.Count > 0) _tray.ShowBalloonTip(5000, "Горячие клавиши недоступны", string.Join(", ", unavailable), ToolTipIcon.Warning);
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
        else key.SetValue("m3nuxa", $"\"{Application.ExecutablePath}\"");
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        DisposeMenu();
        _hotkeys.Dispose();
        _foreground.Dispose();
        _agentPipe?.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly Action<MenuNode?> _onHotkey;
    private readonly Dictionary<int, MenuNode?> _bindings = [];
    private int _nextId;

    public HotkeyWindow(Action<MenuNode?> onHotkey) { _onHotkey = onHotkey; CreateHandle(new CreateParams()); }

    public List<string> Register(IEnumerable<(string Hotkey, MenuNode? Node)> bindings)
    {
        foreach (var id in _bindings.Keys) UnregisterHotKey(Handle, id);
        _bindings.Clear();
        var unavailable = new List<string>();
        foreach (var (hotkey, node) in bindings)
        {
            if (!TryParse(hotkey, out var modifiers, out var key)) { if (!string.IsNullOrWhiteSpace(hotkey)) unavailable.Add(hotkey); continue; }
            var id = ++_nextId;
            if (RegisterHotKey(Handle, id, modifiers | 0x4000, key)) _bindings.Add(id, node);
            else unavailable.Add(hotkey);
        }
        return unavailable;
    }

    private static bool TryParse(string hotkey, out uint modifiers, out uint key)
    {
        modifiers = key = 0;
        foreach (var part in hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= 2; break;
                case "alt": modifiers |= 1; break;
                case "shift": modifiers |= 4; break;
                case "win": modifiers |= 8; break;
                default: if (!Enum.TryParse<Keys>(part, true, out var parsed)) return false; key = (uint)parsed & 0xffff; break;
            }
        }
        return key != 0;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && _bindings.TryGetValue(message.WParam.ToInt32(), out var node)) _onHotkey(node);
        base.WndProc(ref message);
    }

    public void Dispose() { foreach (var id in _bindings.Keys) UnregisterHotKey(Handle, id); DestroyHandle(); }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
