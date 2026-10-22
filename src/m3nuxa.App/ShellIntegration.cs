using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.System;

namespace m3nuxa.App;

// Native integration only: the UI and message loop belong to WinUI.
internal sealed class ShellIntegration : IDisposable
{
    private readonly nint hwnd;
    private readonly SubclassProc callback;
    private readonly Action<bool> clicked;
    private readonly Action<MenuNode?> hotkey;
    private readonly Dictionary<int, MenuNode?> bindings = [];
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyData data;
    public ShellIntegration(Window window, Action<bool> clicked, Action<MenuNode?> hotkey)
    {
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // Keep the flyout owner invisible; popup windows have their own surfaces.
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, (nint)((style | 0x80000 | 0x80) & ~0x40000));
        SetLayeredWindowAttributes(hwnd, 0, 0, 2);
        this.clicked = clicked; this.hotkey = hotkey;
        callback = ProcessMessage;
        if (!SetWindowSubclass(hwnd, callback, 1, 0)) throw new System.ComponentModel.Win32Exception();
        data = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = hwnd, Id = 1, Flags = 7, Message = 0x8001,
            Icon = LoadIcon(0, (nint)32512), Tip = "m3nuxa", Info = "", Title = "" };
        AddIcon();
    }
    private void AddIcon() { if (!Shell_NotifyIcon(0, ref data)) DevLog.Write("tray", "Shell_NotifyIcon failed"); }
    private nint ProcessMessage(nint window, uint message, nuint wparam, nint lparam, nuint id, nuint reference)
    {
        try
        {
            if (message == taskbarCreated) AddIcon();
            if (message == 0x8001 && (int)lparam is 0x202 or 0x205) clicked((int)lparam == 0x205);
            if (message == 0x312 && bindings.TryGetValue((int)wparam, out var node)) hotkey(node);
        }
        catch (Exception ex) { DevLog.Write("shell", "Callback failed", ex); }
        return DefSubclassProc(window, message, wparam, lparam);
    }
    public List<string> Register(IEnumerable<(string Hotkey, MenuNode? Node)> items)
    {
        foreach (var id in bindings.Keys) UnregisterHotKey(hwnd, id);
        bindings.Clear();
        var unavailable = new List<string>(); int next = 0;
        foreach (var (text, node) in items)
        {
            if (!HotkeyParser.TryParse(text, out var modifiers, out var key) || !RegisterHotKey(hwnd, ++next, modifiers | 0x4000, key)) unavailable.Add(text);
            else bindings.Add(next, node);
        }
        return unavailable;
    }
    public void ShowBalloonTip(int timeout, string title, string text, ToolTipIcon icon)
    {
        var notification = data; notification.Flags = 0x10; notification.Title = title[..Math.Min(title.Length, 63)];
        notification.Info = text[..Math.Min(text.Length, 255)]; notification.InfoFlags = icon == ToolTipIcon.Error ? 3u : 2u;
        Shell_NotifyIcon(1, ref notification);
    }
    public void Dispose()
    {
        foreach (var id in bindings.Keys) UnregisterHotKey(hwnd, id);
        Shell_NotifyIcon(2, ref data); RemoveWindowSubclass(hwnd, callback, 1);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, Message; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(nint window, int index);
    private static nint GetWindowLongPtr(nint window, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : (nint)GetWindowLong32(window, index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr64(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(nint window, int index, int value);
    private static void SetWindowLongPtr(nint window, int index, nint value) { if (IntPtr.Size == 8) SetWindowLongPtr64(window, index, value); else SetWindowLong32(window, index, value.ToInt32()); }
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wparam, nint lparam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint module, nint name);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
}
internal enum ToolTipIcon { Error, Warning }

// Win32 message boxes also work before a XAML root exists (configuration recovery).
internal static class MessageBox
{
    public static DialogResult Show(string? text, string title, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.Information)
        => (DialogResult)MessageBoxW(0, text ?? "", title, (uint)buttons | (uint)icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint owner, string text, string title, uint flags);
}
internal enum DialogResult { Yes = 6, No = 7, Cancel = 2 }
internal enum MessageBoxButtons : uint { OK = 0, YesNoCancel = 3 }
internal enum MessageBoxIcon : uint { Error = 16, Question = 32, Warning = 48, Information = 64 }
