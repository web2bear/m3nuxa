using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace m3nuxa.App;

public static class ItemIcon
{
    private const int MaximumIconDimension = 64;
    private const int MaximumCachedIcons = 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private static readonly BlockingCollection<(MenuNode Node, TaskCompletionSource<byte[]?> Result)> Requests = new();
    // Only the STA worker touches this cache; shell handlers remain on their required apartment.
    private static readonly Dictionary<IconCacheKey, IconCacheEntry> Cache = new();
    private sealed class IconCacheEntry(byte[]? png, DateTime expiresAt, long lastUsed)
    {
        public byte[]? Png { get; } = png;
        public DateTime ExpiresAt { get; } = expiresAt;
        public long LastUsed { get; set; } = lastUsed;
    }
    private static long cacheSequence;
    private readonly record struct FileStamp(string Path, long Length, long LastWriteTicks);
    private readonly record struct IconCacheKey(MenuNodeKind Kind, FileStamp Custom, FileStamp Action);

    static ItemIcon()
    {
        // Shell icon handlers can require STA; ThreadPool workers are MTA.
        var worker = new Thread(() =>
        {
            foreach (var request in Requests.GetConsumingEnumerable())
            {
                try { request.Result.SetResult(CachedPng(request.Node)); }
                catch (Exception ex) { request.Result.SetException(ex); }
            }
        }) { IsBackground = true, Name = "m3nuxa shell icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static Task<byte[]?> PngAsync(MenuNode node)
    {
        var result = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Capture only the values used for the icon, so later edits cannot change a queued request.
        var snapshot = new MenuNode { Kind = node.Kind, IconPath = node.IconPath,
            Action = node.Action is null ? null : new MenuAction { Path = node.Action.Path } };
        Requests.Add((snapshot, result));
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
                    if (image.Width <= MaximumIconDimension && image.Height <= MaximumIconDimension)
                        return new Bitmap(image);
                    var scale = Math.Min((double)MaximumIconDimension / image.Width, (double)MaximumIconDimension / image.Height);
                    var width = Math.Max(1, (int)Math.Round(image.Width * scale));
                    var height = Math.Max(1, (int)Math.Round(image.Height * scale));
                    var thumbnail = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(thumbnail))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(image, new Rectangle(0, 0, width, height));
                    }
                    return thumbnail;
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

    private static byte[]? CachedPng(MenuNode node)
    {
        var key = new IconCacheKey(node.Kind, Stamp(node.IconPath), Stamp(node.Action?.Path ?? ""));
        var now = DateTime.UtcNow;
        if (Cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            cached.LastUsed = ++cacheSequence;
            return cached.Png;
        }
        var png = Png(node);
        if (!Cache.ContainsKey(key) && Cache.Count >= MaximumCachedIcons)
            Cache.Remove(Cache.MinBy(entry => entry.Value.LastUsed).Key);
        // Retry missing or temporarily inaccessible icons promptly; refresh shell associations periodically.
        Cache[key] = new IconCacheEntry(png, now.Add(png is null ? TimeSpan.FromSeconds(5) : CacheLifetime), ++cacheSequence);
        return png;
    }

    private static FileStamp Stamp(string path)
    {
        try
        {
            var resolved = Resolve(path);
            if (string.IsNullOrWhiteSpace(resolved)) return new FileStamp("", 0, 0);
            // Probing an offline network share here can stall every icon in the STA queue.
            // Network sources are refreshed by the cache lifetime instead of per-request metadata.
            if (resolved.StartsWith(@"\\", StringComparison.Ordinal)) return new FileStamp(resolved, -1, 0);
            var info = new FileInfo(resolved);
            return info.Exists ? new FileStamp(resolved, info.Length, info.LastWriteTimeUtc.Ticks) : new FileStamp(resolved, -1, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new FileStamp(path ?? "", -1, 0); }
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


