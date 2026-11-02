using m3nuxa.App;
using m3nuxa.Editor;

internal static class EditorDraftChecks
{
    public static void Run()
    {
        var group = new MenuNode { Name = "Group", Kind = MenuNodeKind.Group };
        var child = new MenuNode { Name = "Child", Kind = MenuNodeKind.Group };
        group.Children.Add(child);
        var draft = new EditorDraft();
        draft.Reset(new AppConfig { Items = [group] });
        Check(!draft.MoveToEnd(group.Id, child.Children), "reject descendant move");
        Check(!draft.IsDirty && !draft.Undo(), "rejected move leaves history unchanged");
        draft.BeginChange(); group.Name = "Changed"; group.Name = "Renamed";
        draft.CommitPending();
        draft.Insert(draft.Config.Items, 1, [new MenuNode { Name = "One" }, new MenuNode { Name = "Two" }]);
        Check(draft.Undo() && draft.Config.Items.Count == 1, "batch insert is one undo");
        Check(draft.Undo() && draft.Config.Items[0].Name == "Group" && !draft.IsDirty, "text edit is one undo");
        Check(draft.Redo() && draft.Config.Items[0].Name == "Renamed", "redo text edit");
        draft.Config.Items[0].Name = "Group";
        Check(!draft.IsDirty, "manual restoration clears dirty");
        draft.SetBaseline();
        Check(!draft.Undo() && !draft.Redo(), "apply resets history");
        var initial = new EditorDraft();
        initial.Config.Items.Add(new MenuNode { Name = "Local" });
        Check(initial.IsDirty, "offline initial draft detects changes");
        Console.WriteLine("PASS: editor draft history and tree operations");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
