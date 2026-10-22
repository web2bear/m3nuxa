param([string]$ProcessName = 'ContexWin.Editor')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ContexWinTestDisplay {
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }
    delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rectangle, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static bool Move(IntPtr window) {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            MonitorInfo second = default;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, dc, rect, data) => {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info) && (info.Flags & 1) == 0) { second = info; return false; }
                return true;
            }, IntPtr.Zero);
            if (second.Size == 0) return false;
            if (!GetWindowRect(window, out var bounds)) throw new InvalidOperationException("Cannot read window bounds.");
            int x = second.Work.Left + Math.Max(0, (second.Work.Right - second.Work.Left - (bounds.Right - bounds.Left)) / 2);
            int y = second.Work.Top + Math.Max(0, (second.Work.Bottom - second.Work.Top - (bounds.Bottom - bounds.Top)) / 2);
            if (!SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, 0x15)) throw new InvalidOperationException("Cannot move test window.");
            return true;
        } finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
}
'@
$window = [IntPtr]::Zero
for ($attempt = 0; $attempt -lt 30 -and $window -eq [IntPtr]::Zero; $attempt++) {
    $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
    if ($process) { $window = $process.MainWindowHandle } else { Start-Sleep -Milliseconds 100 }
}
if ($window -eq [IntPtr]::Zero) { throw "No visible test window for $ProcessName" }
if ([ContexWinTestDisplay]::Move($window)) { Write-Output 'Test window moved to the second monitor.' }
else { Write-Output 'Only one monitor is available; using the current monitor.' }
