using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using WinRT.Interop;

namespace m3nuxa.Editor;

[ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEditorDropTarget
{
    [PreserveSig] int DragEnter(IDataObject data, uint keys, EditorDropPoint point, ref uint effect);
    [PreserveSig] int DragOver(uint keys, EditorDropPoint point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(IDataObject data, uint keys, EditorDropPoint point, ref uint effect);
}
[StructLayout(LayoutKind.Sequential)] public struct EditorDropPoint { public int X, Y; }

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class EditorOleDropTarget : IEditorDropTarget, IDisposable
{
    private readonly List<nint> windows = [];
    private readonly nint root;
    private readonly Action<IReadOnlyList<string>, int, int> onPaths;
    private readonly Action<string, bool, int, int> onText;
    private readonly string log;
    private readonly bool oleInitialized;
    public EditorOleDropTarget(Window window, Action<IReadOnlyList<string>, int, int> onPaths, Action<string, bool, int, int> onText, string log)
    {
        root = WindowNative.GetWindowHandle(window); this.onPaths = onPaths; this.onText = onText; this.log = log;
        var init = OleInitialize(0);
        oleInitialized = init >= 0;
        Log($"OleInitialize={init:X8}");
        var handles = new List<nint> { root };
        EnumChildWindows(root, (h, _) => { handles.Add(h); return true; }, 0);
        foreach (var h in handles)
        {
            var revoke = RevokeDragDrop(h);
            var register = RegisterDragDrop(h, this);
            Log($"register hwnd={h} revoke={revoke:X8} register={register:X8}");
            if (register == 0) windows.Add(h);
        }
    }
    private void Log(string message) { if (log.Length != 0) File.AppendAllText(log, message + Environment.NewLine); else m3nuxa.App.DevLog.Write("editor", "OLE " + message); }
    public int DragEnter(IDataObject data, uint keys, EditorDropPoint point, ref uint effect)
    {
        var f = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        var hr = data.QueryGetData(ref f); var text = Supports(data, 13); var url = Supports(data, (short)RegisterClipboardFormat("UniformResourceLocatorW"));
        Log($"enter file={hr:X8} text={text} url={url} effect={effect} at={point.X},{point.Y}");
        effect = (hr == 0 || text || url) ? effect & 1u : 0; return 0;
    }
    public int DragOver(uint keys, EditorDropPoint point, ref uint effect) { effect &= 1; return 0; }
    public int DragLeave() { Log("leave"); return 0; }
    public int Drop(IDataObject data, uint keys, EditorDropPoint point, ref uint effect)
    {
        try
        {
            var location = point; ScreenToClient(root, ref location);
            var dpi = GetDpiForWindow(root) / 96.0;
            int x = (int)(location.X / dpi), y = (int)(location.Y / dpi);
            var url = ReadText(data, (short)RegisterClipboardFormat("UniformResourceLocatorW"));
            if (url is not null && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") { Log("drop url"); onText(url.Trim(), true, x, y); return 0; }
            var f = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            if (data.QueryGetData(ref f) != 0)
            {
                var text = ReadText(data, 13);
                if (!string.IsNullOrWhiteSpace(text)) { Log("drop text"); onText(text, false, x, y); }
                return 0;
            }
            data.GetData(ref f, out var medium);
            try
            {
                var count = DragQueryFile(medium.unionmember, uint.MaxValue, null, 0); var paths = new List<string>();
                for (uint i = 0; i < count; i++) { var n = DragQueryFile(medium.unionmember, i, null, 0); var b = new char[n + 1]; var read = DragQueryFile(medium.unionmember, i, b, (uint)b.Length); if (read != 0) paths.Add(new string(b, 0, (int)read)); }
                ScreenToClient(root, ref point);
                var scale = GetDpiForWindow(root) / 96.0;
                Log($"drop count={paths.Count} at={point.X},{point.Y} scale={scale}");
                onPaths(paths, (int)(point.X / scale), (int)(point.Y / scale));
            }
            finally { ReleaseStgMedium(ref medium); }
        }
        catch (Exception ex) { Log($"drop error {ex}"); effect = 0; }
        return 0;
    }
    private static bool Supports(IDataObject data, short format) { var f = new FORMATETC { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL }; return data.QueryGetData(ref f) == 0; }
    private static string? ReadText(IDataObject data, short format)
    {
        if (!Supports(data, format)) return null;
        var f = new FORMATETC { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        data.GetData(ref f, out var m);
        try { var p = GlobalLock(m.unionmember); if (p == 0) return null; try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(m.unionmember); } }
        finally { ReleaseStgMedium(ref m); }
    }
    public void Dispose() { foreach (var h in windows) RevokeDragDrop(h); if (oleInitialized) OleUninitialize(); }
    private delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint hwnd, EnumProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref EditorDropPoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint hwnd, IEditorDropTarget target);
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint hwnd);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(nint drop, uint index, char[]? path, uint size);
}
