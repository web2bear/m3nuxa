namespace m3nuxa.Editor;

internal readonly record struct DropBounds(double X, double Y, double Width, double Height)
{
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Bottom;
}

internal enum MenuDropZone { Outside, Empty, Before, Into, After }
internal readonly record struct MenuDropRow(string Id, bool IsGroup, DropBounds Bounds);
internal readonly record struct MenuDropHit(MenuDropZone Zone, string? TargetId, DropBounds MarkerAnchor);
internal readonly record struct StepDropRow(int Index, DropBounds Bounds);
internal readonly record struct StepInsertion(int Index, double LineY);

internal static class DropGeometry
{
    public static MenuDropHit Menu(double x, double y, DropBounds tree, IEnumerable<MenuDropRow> rows)
    {
        if (!tree.Contains(x, y)) return default;
        var bottom = tree.Y;
        foreach (var row in rows)
        {
            var bounds = row.Bounds;
            bottom = Math.Max(bottom, bounds.Bottom);
            if (!bounds.Contains(x, y)) continue;
            var offset = y - bounds.Y;
            var zone = offset < bounds.Height / 2 ? MenuDropZone.Before : MenuDropZone.After;
            if (row.IsGroup && offset > bounds.Height / 3 && offset < 2 * bounds.Height / 3)
                zone = MenuDropZone.Into;
            return new(zone, row.Id, bounds);
        }
        return new(MenuDropZone.Empty, null, new(tree.X, Math.Min(tree.Bottom - 3, bottom), tree.Width, 0));
    }

    public static StepInsertion? Steps(double x, double y, DropBounds list, IEnumerable<StepDropRow> rows)
    {
        if (!list.Contains(x, y)) return null;
        StepInsertion? last = null;
        foreach (var row in rows)
        {
            var bounds = row.Bounds;
            if (bounds.Height <= 0 || bounds.Bottom <= list.Y || bounds.Y >= list.Bottom) continue;
            if (y < bounds.Y + bounds.Height / 2)
                return new(row.Index, Math.Max(0, bounds.Y - list.Y));
            last = new(row.Index + 1, Math.Min(list.Height, bounds.Bottom - list.Y));
        }
        return last;
    }
}
