using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ContexWin.App;

internal sealed class ForegroundTracker : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private readonly WinEventDelegate _callback;
    private readonly IntPtr _hook;
    public IntPtr LastWindow { get; private set; }

    public ForegroundTracker()
    {
        _callback = (_, _, window, _, _, _, _) => Consider(window);
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, 0);
        Consider(GetForegroundWindow());
    }

    private void Consider(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == Environment.ProcessId) return;
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        if (className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow") return;
        LastWindow = window;
    }

    public void Dispose() { if (_hook != IntPtr.Zero) UnhookWinEvent(_hook); }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder buffer, int maxCount);
}
