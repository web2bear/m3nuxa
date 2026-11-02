using System.Runtime.InteropServices;

namespace m3nuxa.Editor;

internal sealed class DragEscapeMonitor : IDisposable
{
    private readonly HookProc callback;
    private nint hook;
    public bool Cancelled { get; private set; }
    public bool Available => hook != 0;
    public DragEscapeMonitor()
    {
        callback = OnKey;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
    }
    private nint OnKey(int code, nuint message, nint data)
    {
        if (code >= 0 && message is 0x100 or 0x104 && Marshal.ReadInt32(data) == 0x1b) Cancelled = true;
        return CallNextHookEx(hook, code, message, data);
    }
    public void Dispose() { if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; } }
    private delegate nint HookProc(int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
