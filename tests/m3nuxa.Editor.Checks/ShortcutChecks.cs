using System.Runtime.InteropServices;
using m3nuxa.App;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage
{
    internal async Task CheckShortcutRecordingAsync()
    {
        var group = new MenuNode { Name = "Терминал", Kind = MenuNodeKind.Group, Hotkey = "Control+Shift+K" };
        var send = new MenuNode { Name = "Вставить", Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.SendShortcut, Shortcut = "Ctrl+V" } };
        config = new AppConfig { Items = [group, send] };
        baseSnapshot = Snapshot(); undo.Clear(); redo.Clear(); dirty = false;
        RefreshTree(group.Id); UpdateApplyState();
        await Task.Delay(100);
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        await mainShortcut.StartAsync();
        Assert(Descendants(mainShortcut).OfType<TextBlock>().Any(text => text.Text.Contains("Нажмите новое")), "recording did not start: " + string.Join(" | ", Descendants(mainShortcut).OfType<TextBlock>().Select(text => text.Text)));
        Send((0x11, false), (0x12, false), (0x78, false));
        await Task.Delay(80);
        Assert(config.MainHotkey == "Ctrl+Alt+Space" && !dirty, "key-down changed the draft");
        Send((0x78, true)); await Task.Delay(50);
        Assert(!dirty, "committed before modifiers released");
        Send((0x12, true), (0x11, true)); await Task.Delay(180);
        Assert(config.MainHotkey == "Ctrl+Alt+F9" && dirty, "recorded combination was not committed: " + config.MainHotkey + " | " + string.Join(" | ", Descendants(mainShortcut).OfType<TextBlock>().Select(text => text.Text)));
        UndoClicked(this, null!);
        Assert(config.MainHotkey == "Ctrl+Alt+Space" && !dirty, "one undo must restore the whole combination: " + config.MainHotkey + " dirty=" + dirty + " history=" + undo.Count);
        RedoClicked(this, null!);
        Assert(config.MainHotkey == "Ctrl+Alt+F9", "redo lost the combination");

        var before = Snapshot(); var undoCount = undo.Count;
        await mainShortcut.StartAsync();
        Send((0x1b, false), (0x1b, true)); await Task.Delay(150);
        Assert(Snapshot() == before && undo.Count == undoCount, "Esc changed the draft/history");

        await mainShortcut.StartAsync();
        Send((0x11, false), (0x10, false), (0x4b, false), (0x4b, true), (0x10, true), (0x11, true));
        await Task.Delay(180);
        Assert(Snapshot() == before && Descendants(mainShortcut).OfType<TextBlock>().Any(text => text.Text.Contains("Терминал")), "duplicate did not preserve old value or explain conflict");

        MenuTree.SelectedNode = FindTreeNode(MenuTree.RootNodes, group.Id); await Task.Delay(80);
        await mainShortcut.StartAsync();
        ((TextBox)PropertiesPanel.Children[0]).Focus(FocusState.Programmatic);
        await Task.Delay(180);
        Assert(Snapshot() == before && Descendants(mainShortcut).OfType<Button>().Single(button => button.Content as string == "Отмена").Visibility == Visibility.Collapsed,
            "moving focus out did not cancel recording");

        MenuTree.SelectedNode = FindTreeNode(MenuTree.RootNodes, send.Id); await Task.Delay(80);
        var sent = PropertiesPanel.Children.OfType<ShortcutEditor>().Last();
        await sent.StartAsync();
        Send((0x11, false), (0x10, false), (0x4b, false), (0x4b, true), (0x10, true), (0x11, true));
        await Task.Delay(180);
        Assert(config.Items[1].Action?.Shortcut == "Ctrl+Shift+K", "sent shortcut incorrectly shares global duplicate restrictions");

        var item = PropertiesPanel.Children.OfType<ShortcutEditor>().First();
        await item.StartAsync();
        MenuTree.SelectedNode = FindTreeNode(MenuTree.RootNodes, group.Id); await Task.Delay(180);
        Assert(config.Items[1].Hotkey == "", "changing selection committed a recording");

        var clear = Descendants(mainShortcut).OfType<Button>().Single(button => button.Content as string == "Убрать");
        ((IInvokeProvider)new ButtonAutomationPeer(clear).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(50);
        Assert(config.MainHotkey == "", "clear failed");
        UndoClicked(this, null!);
        Assert(config.MainHotkey == "Ctrl+Alt+F9", "clear is not undoable");

        ShowHotkeyAvailability(new EditorResponse(true, null, System.Text.Json.Nodes.JsonNode.Parse("{\"unavailableHotkeys\":[\"Control+Alt+F9\"]}")!));
        Assert(Descendants(mainShortcut).OfType<TextBlock>().Any(text => text.Text.Contains("Сочетание недоступно")), "registration failure missing next to field");
        ShowHotkeyAvailability(new EditorResponse(true, null, System.Text.Json.Nodes.JsonNode.Parse("{\"unavailableHotkeys\":[]}")!));
        Assert(!Descendants(mainShortcut).OfType<TextBlock>().Any(text => text.Text.Contains("Сочетание недоступно")), "stale registration failure retained");

        // Check the actual tray binding, without applying the synthetic editor draft.
        var actual = await client.ReadAsync();
        var main = actual.Data?["config"]?["MainHotkey"]?.GetValue<string>() ?? "";
        if (HotkeyParser.TryParse(main, out var modifiers, out var key))
        {
            Assert(!CanRegister(modifiers, key), "tray binding was not restored after recording");
            using (var connection = await HotkeyCaptureConnection.OpenAsync())
                Assert(CanRegister(modifiers, key), "tray binding remained registered during capture");
            for (var attempt = 0; attempt < 20 && CanRegister(modifiers, key); attempt++) await Task.Delay(50);
            Assert(!CanRegister(modifiers, key), "EOF did not restore the tray binding");
        }
        Assert(FindHotkeyConflict("Ctrl+Shift+K", null, true)?.Contains("Терминал") == true, "modifier aliases not normalized");
        void Assert(bool condition, string message) { if (!condition) throw new Exception("Shortcut UX: " + message); }
    }

    private static bool CanRegister(uint modifiers, uint key)
    {
        if (!RegisterHotKey(0, 25100, modifiers, key)) return false;
        UnregisterHotKey(0, 25100); return true;
    }
    private static void Send(params (ushort key, bool up)[] keys)
    {
        var inputs = keys.Select(key => new KeyboardInput { Type = 1, Key = key.key, Flags = key.up ? 2u : 0u }).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyboardInput>()) != inputs.Length)
            throw new Exception("Cannot inject shortcut test input.");
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct KeyboardInput
    { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public ushort Key; [FieldOffset(12)] public uint Flags; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, KeyboardInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
