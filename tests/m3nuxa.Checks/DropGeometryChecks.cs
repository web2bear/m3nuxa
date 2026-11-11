using m3nuxa.Editor;

internal static class DropGeometryChecks
{
    public static void Run()
    {
        var viewport = new DropBounds(10, 20, 100, 100);
        MenuDropRow[] rows = [new("group", true, new(10, 20, 100, 30)), new("action", false, new(10, 50, 100, 20))];
        foreach (var (y, zone) in new[]
        {
            (30, MenuDropZone.Before), (31, MenuDropZone.Into), (39, MenuDropZone.Into),
            (40, MenuDropZone.After), (50, MenuDropZone.Before), (60, MenuDropZone.After),
            (70, MenuDropZone.Empty), (120, MenuDropZone.Outside)
        })
        {
            var hit = DropGeometry.Menu(10, y, viewport, rows);
            if (hit.Zone != zone || (y == 50 && hit.TargetId != "action")) throw new Exception($"Menu drop at {y}: {hit}");
        }
        if (DropGeometry.Menu(110, 25, viewport, rows).Zone != MenuDropZone.Outside ||
            DropGeometry.Menu(10, 25, viewport, []).Zone != MenuDropZone.Empty)
            throw new Exception("Menu viewport or empty tree drop incorrect.");
        MenuDropRow[] scaledRows = [new("upper", false, new(10, 20, 100, 30.5)), new("lower", false, new(10, 50.5, 100, 20))];
        if (DropGeometry.Menu(20, 50.25, viewport, scaledRows).TargetId != "upper" ||
            DropGeometry.Menu(20, 50.75, viewport, scaledRows).TargetId != "lower")
            throw new Exception("Fractional row boundary lost at scaled DPI.");

        StepDropRow[] steps = [new(2, new(10, 0, 90, 20)), new(3, new(10, 10, 90, 30)), new(4, new(10, 40, 90, 20))];
        if (DropGeometry.Steps(109, 20, viewport, steps) != new StepInsertion(3, 0) ||
            DropGeometry.Steps(109, 50, viewport, steps) != new StepInsertion(5, 40) ||
            DropGeometry.Steps(110, 50, viewport, steps) != null ||
            DropGeometry.Steps(10, 20, viewport, []) != null)
            throw new Exception("Scrolled step insertion or viewport clipping incorrect.");
    }
}
