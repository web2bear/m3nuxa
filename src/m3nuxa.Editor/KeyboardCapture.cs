using System.Runtime.InteropServices;
using m3nuxa.App;
using Microsoft.UI.Xaml;

namespace m3nuxa.Editor;

internal sealed class KeyboardCapture : IDisposable
{
    private readonly HookProc callback;
    private nint hook;
    private readonly nint window;
    private readonly Action<string?, bool> changed;
    private readonly HashSet<uint> pressed = [];
    private string? candidate;
    private bool completed;
    private uint actionKey;
    private bool invalid;

    public KeyboardCapture(Window owner, Action<string?, bool> changed)
    {
        window = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        this.changed = changed;
        callback = OnKey;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint OnKey(int code, nuint message, nint data)
    {
        if (code < 0 || completed) return CallNextHookEx(hook, code, message, data);
        if (GetForegroundWindow() != window)
        {
            completed = true;
            changed(null, true);
            return CallNextHookEx(hook, code, message, data);
        }
        var key = (uint)Marshal.ReadInt32(data);
        var down = message is 0x100 or 0x104;
        if (down)
        {
            if (!pressed.Add(key)) return 1;
            var modifiers = pressed.Aggregate(0u, (mask, k) => mask | HotkeyParser.Modifier(k));
            if (key == 0x1b || (key == 9 && modifiers == 0))
            {
                completed = true;
                changed(null, true);
                return key == 9 ? CallNextHookEx(hook, code, message, data) : 1;
            }
            if (!HotkeyParser.IsModifier(key))
            {
                invalid |= actionKey != 0 && actionKey != key;
                actionKey = key;
                candidate = invalid ? "" : HotkeyParser.Format(modifiers, key);
                changed(candidate, false);
            }
            else if (candidate is null) changed(HotkeyParser.Format(modifiers, 0), false);
        }
        else
        {
            pressed.Remove(key);
            if (pressed.Count == 0 && candidate is not null)
            {
                completed = true;
                changed(candidate, true);
            }
            else if (pressed.Count == 0) changed(null, false);
        }
        return 1;
    }

    public void Dispose() { if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; } }
    private delegate nint HookProc(int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
