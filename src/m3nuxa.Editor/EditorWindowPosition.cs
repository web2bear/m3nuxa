using System.Text.Json;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using m3nuxa.App;

namespace m3nuxa.Editor;

internal sealed class EditorWindowPosition
{
    private readonly AppWindow window;
    private readonly string path = Path.Combine(AppContext.BaseDirectory, "editor-window.json");
    private PointInt32? normalPosition;
    private SizeInt32 normalSize;

    public EditorWindowPosition(AppWindow window)
    {
        this.window = window;
        try
        {
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<Position>(File.ReadAllText(path));
                if (saved is not null)
                {
                    var size = saved.Width > 0 && saved.Height > 0 ? new SizeInt32(saved.Width, saved.Height) : window.Size;
                    var displays = DisplayArea.FindAll();
                    DisplayArea? display = null;
                    for (var i = 0; i < displays.Count; i++)
                        if (ContainsVisibleTitleBar(displays[i].WorkArea, saved.X, saved.Y, size.Width)) { display = displays[i]; break; }
                    var work = (display ?? DisplayArea.Primary).WorkArea;
                    size = new SizeInt32(Math.Min(size.Width, work.Width), Math.Min(size.Height, work.Height));
                    if (display is not null && ContainsVisibleTitleBar(work, saved.X, saved.Y, size.Width)) window.Move(new PointInt32(saved.X, saved.Y));
                    else
                    {
                        var x = (int)Math.Clamp((long)saved.X, work.X, Math.Max((long)work.X, (long)work.X + work.Width - size.Width));
                        var y = (int)Math.Clamp((long)saved.Y, work.Y, Math.Max((long)work.Y, (long)work.Y + work.Height - size.Height));
                        window.Move(new PointInt32(x, y));
                    }
                    window.Resize(size);
                }
            }
        }
        catch (Exception ex) { DevLog.Write("editor", "Could not restore window position", ex); }
        normalPosition = window.Position;
        normalSize = window.Size;
        window.Changed += WindowChanged;
    }

    private static bool ContainsVisibleTitleBar(RectInt32 area, int x, int y, int width) =>
        (long)x + width >= (long)area.X + 64 && x <= (long)area.X + area.Width - 64 && y >= area.Y && y < (long)area.Y + area.Height;

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
        if (args.DidPositionChange) normalPosition = sender.Position;
        if (args.DidSizeChange) normalSize = sender.Size;
    }

    public void Save()
    {
        if (normalPosition is not { } point) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Position(point.X, point.Y, normalSize.Width, normalSize.Height)));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        catch (Exception ex) { DevLog.Write("editor", "Could not save window position", ex); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    private sealed record Position(int X, int Y, int Width = 0, int Height = 0);
}
