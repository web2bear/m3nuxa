using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace m3nuxa.App;

public partial class App
{
    [StructLayout(LayoutKind.Sequential)] private struct FrameRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FramePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out FrameRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref FramePoint point);

    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wparam, nint lparam);

    private static async Task CheckPanelFrameAsync()
    {
        LauncherPanel.Show([], _ => { }, () => { }, false);
        await Task.Delay(300);
        var panel = (Window)typeof(LauncherPanel).GetField("active", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(panel);
        GetWindowRect(hwnd, out var rect);
        var origin = new FramePoint();
        ClientToScreen(hwnd, ref origin);
        if (origin.Y != rect.Top) throw new Exception($"Panel top has a non-client strip: {origin.Y - rect.Top}px");
        nint Hit(int x, int y) => SendMessage(hwnd, 0x84, 0, (nint)((y << 16) | (x & 0xffff)));
        var midX = (rect.Left + rect.Right) / 2; var midY = (rect.Top + rect.Bottom) / 2;
        if (Hit(rect.Left + 1, midY) != 10 || Hit(rect.Right - 2, midY) != 11 ||
            Hit(midX, rect.Top + 1) != 12 || Hit(midX, rect.Bottom - 2) != 15 ||
            Hit(rect.Left + 1, rect.Top + 1) != 13 || Hit(rect.Right - 2, rect.Top + 1) != 14 ||
            Hit(rect.Left + 1, rect.Bottom - 2) != 16 || Hit(rect.Right - 2, rect.Bottom - 2) != 17)
            throw new Exception("Panel resize hit testing failed");
        panel.Close();
    }
}
