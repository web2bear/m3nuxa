using System.Runtime.InteropServices;
using WinRT.Interop;

namespace ContexWin.Editor;

internal sealed class ExplorerFileDropHook : IDisposable
{
    private const int GwlExStyle = -20;
    private const int WsExAcceptFiles = 0x00000010;
    private const uint WmDropFiles = 0x0233;
    private const nuint SubclassId = 0x43575844;

    private readonly List<nint> hwnds = [];
    private readonly SubclassProc subclassProc;
    private readonly Action<IReadOnlyList<string>, int, int> onDropped;
    private nint rootWindow;

    private ExplorerFileDropHook(Action<IReadOnlyList<string>, int, int> onDropped)
    {
        this.onDropped = onDropped;
        subclassProc = OnSubclass;
    }

    public static ExplorerFileDropHook Attach(Window window, Action<IReadOnlyList<string>, int, int> onDropped)
    {
        var hook = new ExplorerFileDropHook(onDropped);
        hook.AttachCore(WindowNative.GetWindowHandle(window));
        return hook;
    }

    public void Dispose()
    {
        foreach (var hwnd in hwnds) RemoveWindowSubclass(hwnd, subclassProc, SubclassId);
        hwnds.Clear();
    }

    private void AttachCore(nint root)
    {
        rootWindow = root;
        foreach (var hwnd in EnumerateWindows(root))
        {
            _ = RevokeDragDrop(hwnd);
            var style = GetWindowLongPtr(hwnd, GwlExStyle);
            _ = SetWindowLongPtr(hwnd, GwlExStyle, style | WsExAcceptFiles);
            DragAcceptFiles(hwnd, true);
            if (SetWindowSubclass(hwnd, subclassProc, SubclassId, 0)) hwnds.Add(hwnd);
        }
    }

    private nint OnSubclass(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nint data)
    {
        if (message != WmDropFiles) return DefSubclassProc(hwnd, message, wParam, lParam);
        _ = DragQueryPoint(wParam, out var point);
        _ = ClientToScreen(hwnd, ref point);
        _ = ScreenToClient(rootWindow, ref point);
        var paths = ReadDropFiles(wParam);
        DragFinish(wParam);
        if (paths.Count > 0) onDropped(paths, point.X, point.Y);
        return 1;
    }

    private static List<nint> EnumerateWindows(nint root)
    {
        var result = new List<nint> { root };
        EnumChildWindows(root, (hwnd, _) => { result.Add(hwnd); return true; }, 0);
        return result;
    }

    private static List<string> ReadDropFiles(nint drop)
    {
        var count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
        var paths = new List<string>((int)count);
        for (uint index = 0; index < count; index++)
        {
            var length = DragQueryFile(drop, index, null, 0);
            var buffer = new char[length + 1];
            var copied = DragQueryFile(drop, index, buffer, (uint)buffer.Length);
            if (copied > 0) paths.Add(new string(buffer, 0, (int)copied));
        }
        return paths;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint hwnd);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(nint hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(nint drop, uint index, char[]? path, uint size);
    [DllImport("shell32.dll")] private static extern void DragFinish(nint drop);
    [DllImport("shell32.dll")] private static extern bool DragQueryPoint(nint drop, out Point point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint hwnd, EnumWindowsProc callback, nint data);
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nint data);
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
