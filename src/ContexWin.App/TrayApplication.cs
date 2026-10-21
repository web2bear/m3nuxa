using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ContexWin.App;

internal sealed class TrayApplication : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly ConfigStore _store;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys;
    private readonly ForegroundTracker _foreground;
    private AgentPipe? _agentPipe;
    private EditorForm? _editor;
    private ContextMenuStrip? _menu;

    public TrayApplication(ConfigStore store)
    {
        _store = store;
        _foreground = new ForegroundTracker();
        _hotkeys = new HotkeyWindow(OnHotkey);
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "ContexWin", Visible = true, ContextMenuStrip = BuildServiceMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(_store.Current.Items); };
        RegisterHotkeys();
        Application.Idle += StartAgentPipe;
    }

    private void StartAgentPipe(object? sender, EventArgs e)
    {
        Application.Idle -= StartAgentPipe;
        _agentPipe = new AgentPipe(_store, RegisterHotkeys);
    }

    private ContextMenuStrip BuildServiceMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть редактор", null, (_, _) => OpenEditor());
        menu.Items.Add("Перезагрузить конфигурацию", null, (_, _) => Reload());
        menu.Items.Add("Автозапуск", null, (_, _) => ToggleStartup());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => { if (menu.Items[2] is ToolStripMenuItem item) item.Checked = IsStartupEnabled(); };
        return menu;
    }

    private void OpenEditor()
    {
        if (_editor is { IsDisposed: false }) { _editor.Activate(); return; }
        _editor = new EditorForm(_store, RegisterHotkeys);
        _editor.Show();
    }

    private void Reload()
    {
        var result = _store.Reload();
        if (!result.Success) MessageBox.Show(result.Error ?? "Не удалось загрузить конфигурацию.", "ContexWin");
        else RegisterHotkeys();
    }

    private void ShowMenu(IReadOnlyList<MenuNode> nodes)
    {
        var invocation = InvocationContext.Capture(_foreground.LastWindow);
        _menu?.Dispose();
        _menu = new ContextMenuStrip();
        FillMenu(_menu.Items, nodes, invocation);
        if (_menu.Items.Count == 0) _menu.Items.Add("Меню пусто").Enabled = false;
        _menu.Show(Cursor.Position);
    }

    private void FillMenu(ToolStripItemCollection target, IEnumerable<MenuNode> nodes, InvocationContext invocation)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == MenuNodeKind.Separator) { target.Add(new ToolStripSeparator()); continue; }
            var item = new ToolStripMenuItem(node.Name);
            if (!string.IsNullOrWhiteSpace(node.IconPath))
            {
                try { item.Image = Image.FromFile(node.IconPath); } catch { }
            }
            target.Add(item);
            if (node.Kind == MenuNodeKind.Group) FillMenu(item.DropDownItems, node.Children, invocation);
            else item.Click += async (_, _) => await Execute(node, invocation);
        }
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
        return key?.GetValue("ContexWin") is string;
    }

    private static void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue("ContexWin") is not null) key.DeleteValue("ContexWin", false);
        else key.SetValue("ContexWin", $"\"{Application.ExecutablePath}\"");
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _menu?.Dispose();
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
