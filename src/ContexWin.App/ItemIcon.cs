using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace ContexWin.App;

public static class ItemIcon
{
    private static readonly BlockingCollection<(MenuNode Node, TaskCompletionSource<byte[]?> Result)> Requests = new();

    static ItemIcon()
    {
        // Shell icon handlers can require STA; ThreadPool workers are MTA.
        var worker = new Thread(() =>
        {
            foreach (var request in Requests.GetConsumingEnumerable())
            {
                try { request.Result.SetResult(Png(request.Node)); }
                catch (Exception ex) { request.Result.SetException(ex); }
            }
        }) { IsBackground = true, Name = "ContexWin shell icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static Task<byte[]?> PngAsync(MenuNode node)
    {
        var result = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.Add((node, result));
        return result.Task;
    }

    public static Bitmap? Load(MenuNode node)
    {
        var custom = Resolve(node.IconPath);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                if (Path.GetExtension(custom).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".ico")
                {
                    using var image = System.Drawing.Image.FromFile(custom);
                    return new Bitmap(image);
                }
                if (Path.GetExtension(custom).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractIconEx(custom, 0, out var resource, out var small, 1);
                    try { if (resource != IntPtr.Zero) { using var icon = Icon.FromHandle(resource); return icon.ToBitmap(); } }
                    finally { if (resource != IntPtr.Zero) DestroyIcon(resource); if (small != IntPtr.Zero) DestroyIcon(small); }
                }
                if (Shell(custom) is { } shellIcon) return shellIcon;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or OutOfMemoryException) { }
        }
        var path = Resolve(node.Action?.Path ?? "");
        return Shell(path) ?? (node.Kind == MenuNodeKind.Group ? Shell(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) : null);
    }

    private static string Resolve(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        return string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }

    public static byte[]? Png(MenuNode node)
    {
        using var image = Load(node);
        if (image is null) return null;
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static Bitmap? Shell(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // SHGetFileInfo requires COM on the calling thread.
        var initialized = CoInitializeEx(IntPtr.Zero, 2);
        try
        {
            if (SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x20) == IntPtr.Zero) return null;
            if (info.Icon == IntPtr.Zero) return null;
            try { using var icon = Icon.FromHandle(info.Icon); return icon.ToBitmap(); }
            finally { DestroyIcon(info.Icon); }
        }
        finally { if (initialized >= 0) CoUninitialize(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string path, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}


