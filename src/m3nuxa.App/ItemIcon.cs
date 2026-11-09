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
    private static readonly BlockingCollection<(MenuNode Node, TaskCompletionSource<byte[]?> Result)> NetworkRequests = new();
    private static readonly Dictionary<IconCacheKey, IconCacheEntry> Cache = new();
    private static readonly object CacheGate = new();
    private sealed class IconCacheEntry(byte[]? png, DateTime expiresAt, long lastUsed)
    {
        public byte[]? Png { get; } = png;
        public DateTime ExpiresAt { get; } = expiresAt;
        public long LastUsed { get; set; } = lastUsed;
    }
    private static long cacheSequence;
    private readonly record struct FileStamp(string Path, long Length, long LastWriteTicks);
    private readonly record struct IconCacheKey(MenuNodeKind Kind, ActionKind? ActionKind, FileStamp Custom, FileStamp Action, string Color);

    static ItemIcon()
    {
        StartWorker(Requests, "m3nuxa shell icons");
        StartWorker(NetworkRequests, "m3nuxa network icons");
    }

    private static void StartWorker(BlockingCollection<(MenuNode Node, TaskCompletionSource<byte[]?> Result)> requests, string name)
    {
        // Shell icon handlers can require STA; keep network resources off the local queue.
        var worker = new Thread(() =>
        {
            foreach (var request in requests.GetConsumingEnumerable())
            {
                try { request.Result.SetResult(CachedPng(request.Node)); }
                catch (Exception ex) { request.Result.SetException(ex); }
            }
        }) { IsBackground = true, Name = name };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static Task<byte[]?> PngAsync(MenuNode node)
    {
        var result = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Capture only the values used for the icon, so later edits cannot change a queued request.
        var snapshot = new MenuNode { Kind = node.Kind, IconPath = node.IconPath, IconColor = node.IconColor,
            Action = node.Action is null ? null : new MenuAction { Kind = node.Action.Kind, Path = node.Action.Path } };
        (IsNetworkPath(Resolve(snapshot.IconPath)) ? NetworkRequests : Requests).Add((snapshot, result));
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
                    {
                        var result = new Bitmap(image);
                        ApplyBundledColor(result, node);
                        return result;
                    }
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
        var action = node.Action;
        var rawPath = action?.Path ?? "";
        // A context-dependent path cannot be resolved while the menu is being built.
        // Avoid probing network paths on the single shell worker: an offline host can
        // otherwise hold up every icon requested after it.
        if (action?.Kind == ActionKind.OpenFolder && (rawPath.Contains('{') || IsNetworkPath(rawPath)))
            return GenericFolder();
        var path = Resolve(rawPath);
        var shellImage = !IsNetworkPath(path) && !rawPath.Contains('{') ? Shell(path) : null;
        if (shellImage is not null) return shellImage;
        if (node.Kind == MenuNodeKind.Group || action?.Kind == ActionKind.OpenFolder) return GenericFolder();
        return action is null ? null : SystemIcons.Application.ToBitmap();
    }

    private static byte[]? CachedPng(MenuNode node)
    {
        var key = new IconCacheKey(node.Kind, node.Action?.Kind, Stamp(node.IconPath), Stamp(node.Action?.Path ?? ""), node.IconColor ?? "");
        var now = DateTime.UtcNow;
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            {
                cached.LastUsed = ++cacheSequence;
                return cached.Png;
            }
        }
        var png = Png(node);
        lock (CacheGate)
        {
            if (!Cache.ContainsKey(key) && Cache.Count >= MaximumCachedIcons)
                Cache.Remove(Cache.MinBy(entry => entry.Value.LastUsed).Key);
            // Retry missing or temporarily inaccessible icons promptly; refresh shell associations periodically.
            Cache[key] = new IconCacheEntry(png, DateTime.UtcNow.Add(png is null ? TimeSpan.FromSeconds(5) : CacheLifetime), ++cacheSequence);
        }
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

    private static bool IsNetworkPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    private static Bitmap? GenericFolder() => Shell("folder", 0x10, 0x10);

    private static void ApplyBundledColor(Bitmap bitmap, MenuNode node)
    {
        var prefix = Path.Combine("icon-packs", "Boxicons") + Path.DirectorySeparatorChar;
        if (!node.IconPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            node.IconColor is not { Length: 7 } color || color[0] != '#' ||
            !int.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                bitmap.SetPixel(x, y, Color.FromArgb(pixel.A, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255));
            }
    }

    public static byte[]? Png(MenuNode node)
    {
        using var image = Load(node);
        if (image is null) return null;
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static Bitmap? Shell(string path, uint attributes = 0, uint extraFlags = 0)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // SHGetFileInfo requires COM on the calling thread.
        var initialized = CoInitializeEx(IntPtr.Zero, 2);
        try
        {
            if (SHGetFileInfo(path, attributes, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x20 | extraFlags) == IntPtr.Zero) return null;
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


