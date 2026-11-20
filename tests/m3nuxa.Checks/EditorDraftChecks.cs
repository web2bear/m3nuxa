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
        draft.BeginChange();
        draft.Config.Settings = draft.Config.Settings with { PanelPosition = "Cursor", MenuFontSize = 17 };
        draft.CommitPending();
        Check(draft.IsDirty && draft.Config.Settings.PanelPosition == "Cursor", "settings join the draft");
        Check(draft.Undo() && draft.Config.Settings.PanelPosition == "Center" && !draft.IsDirty, "undo restores settings with the tree");
        Check(draft.Redo() && draft.Config.Settings.MenuFontSize == 17 && draft.IsDirty, "redo restores settings changes");
        draft.SetBaseline();
        Check(!draft.IsDirty && !draft.Undo(), "applying the unified draft resets settings history");
        var initial = new EditorDraft();
        initial.Config.Items.Add(new MenuNode { Name = "Local" });
        Check(initial.IsDirty, "offline initial draft detects changes");
        CheckCopy();
        CheckTransfer();
        Console.WriteLine("PASS: editor draft history and tree operations");
    }

    private static void CheckCopy()
    {
        var sourceGroup = new MenuNode { Name = "Template", Kind = MenuNodeKind.Group, IconPath = @"icon-packs\Boxicons\bx-home.png",
            Children = [
                new MenuNode { Name = "Action", Kind = MenuNodeKind.Action, Steps = [new() { Kind = ActionKind.RunScript, Path = @"scripts\run.ps1", WorkingDirectory = @"{knownFolder:Profile}" }, new() { Kind = ActionKind.Delay, DelayMilliseconds = 25 }] },
                new MenuNode { Kind = MenuNodeKind.Separator }
            ] };
        var source = new EditorDraft();
        source.Reset(new AppConfig { Items = [sourceGroup] });
        var primary = new EditorDraft();
        var sibling = new MenuNode { Name = "Sibling", Kind = MenuNodeKind.Group };
        primary.Reset(new AppConfig { Items = [sibling] });
        primary.BeginChange();
        var copied = primary.TryInsertCopy(sourceGroup, primary.Config.Items, 0)!;
        Check(primary.IsDirty && !source.IsDirty && primary.Config.Items[0] == copied, "copy dirties only the primary draft at the target index");
        Check(primary.UndoCount == 1, "focusing a field without editing does not add a duplicate copy undo entry");
        Check(copied.Id != sourceGroup.Id && copied.Children.Select(child => child.Id).Intersect(sourceGroup.Children.Select(child => child.Id)).Any() == false,
            "copy allocates fresh IDs for every node");
        Check(copied.IconPath == sourceGroup.IconPath && copied.Children[1].Kind == MenuNodeKind.Separator &&
            copied.Children[0].Steps[0].WorkingDirectory == @"{knownFolder:Profile}" && copied.Children[0].Steps[1].DelayMilliseconds == 25,
            "copy preserves resource references, separator, and ordered workflow");
        copied.Children[0].Steps[0].Path = "changed";
        Check(!source.IsDirty && sourceGroup.Children[0].Steps[0].Path == @"scripts\run.ps1", "copy does not share mutable steps");
        Check(primary.Undo() && !primary.IsDirty && primary.Config.Items.Single().Id == sibling.Id, "copy is one undo");
        Check(primary.Redo() && primary.Config.Items[0].Id == copied.Id, "redo restores the same independently identified copy");
        var (primaryRoot, leaf) = NestedGroups();
        var (sourceRoot, _) = NestedGroups();
        primary.Reset(new AppConfig { Items = [primaryRoot] });
        Check(primary.TryInsertCopy(sourceRoot, leaf.Children, 0) is null &&
            leaf.Children.Count == 0 && !primary.IsDirty && !primary.Undo(), "copy exceeding the pipe JSON depth leaves no partial insertion");
    }

    private static (MenuNode Root, MenuNode Leaf) NestedGroups()
    {
        var root = new MenuNode { Kind = MenuNodeKind.Group };
        var leaf = root;
        for (var depth = 1; depth < 16; depth++)
        {
            var child = new MenuNode { Kind = MenuNodeKind.Group };
            leaf.Children.Add(child);
            leaf = child;
        }
        return (root, leaf);
    }

    private static void CheckTransfer()
    {
        var (group, leaf) = NestedGroups();
        var (otherGroup, otherLeaf) = NestedGroups();
        var origin = new EditorDraft();
        var target = new EditorDraft();
        origin.Reset(new AppConfig { Items = [group] });
        target.Reset(new AppConfig { Items = [otherGroup] });
        Check(origin.TryTransfer(target, group.Id, otherLeaf.Children, 0, out _) is null &&
            origin.Config.Items.Single() == group && !origin.IsDirty && !target.IsDirty, "rejected transfer preserves both drafts");
        Check(origin.TryTransfer(target, group.Id, target.Config.Items, 0, out _) == group &&
            origin.Config.Items.Count == 0 && target.Config.Items.First() == group && origin.HasOutgoingTransfer,
            "transfer inserts before removing the original and preserves IDs");
        Check(target.Undo() && !origin.IsDirty && !target.IsDirty && !origin.HasOutgoingTransfer, "linked undo restores both drafts");
        Check(origin.Redo() && origin.Config.Items.Count == 0 && target.Config.Items.First().Id == group.Id,
            "linked redo works from either draft");
        target.Insert(target.Config.Items, 0, [new MenuNode { Name = "New edit" }]);
        Check(!origin.Undo() && target.Undo() && origin.Undo() && origin.Config.Items.Single().Id == group.Id,
            "linked undo does not undo later edits in the other pane");
        var duplicate = new MenuNode { Id = leaf.Id, Name = "Collision" };
        target.Insert(target.Config.Items, 0, [duplicate]);
        var moved = origin.TryTransfer(target, group.Id, target.Config.Items, 1, out _);
        Check(moved is not null && moved.Id != group.Id && target.Config.Items[0] == duplicate && origin.Config.Items.Count == 0,
            "transfer resolves ID collisions without changing existing items");

        var item = new MenuNode { Name = "Saved item", Hotkey = "Ctrl+Shift+K" };
        origin.Reset(new AppConfig { Items = [item] });
        target.Reset(new AppConfig());
        target.Insert(target.Config.Items, 0, [new MenuNode { Name = "Earlier edit" }]);
        Check(origin.TryTransfer(target, item.Id, target.Config.Items, 0, out _) is not null &&
            origin.Undo() && target.Undo(), "undo transfer and earlier destination edit");
        origin.Insert(origin.Config.Items, 0, [new MenuNode { Name = "New origin edit" }]);
        Check(target.Redo() && target.Config.Items.Single().Name == "Earlier edit" && !target.Redo(),
            "branching discards transfer redo but preserves independent redo in the other draft");

        var conflict = new MenuNode { Name = "Conflicting item", Hotkey = "Control+Shift+K" };
        origin.Reset(new AppConfig { Items = [item] });
        target.Reset(new AppConfig { Items = [conflict] });
        Check(origin.TryTransfer(target, item.Id, target.Config.Items, 0, out var conflictingHotkey) is null &&
            conflictingHotkey == "Ctrl+Shift+K" && !origin.IsDirty && !target.IsDirty,
            "transfer rejects equivalent hotkeys without editing either draft");

        target.Reset(new AppConfig());
        Check(origin.TryTransfer(target, item.Id, target.Config.Items, 0, out _) is not null, "move before discarding destination");
        target.Reset(new AppConfig());
        Check(origin.HasDiscardedOutgoingTransfer && origin.Undo() && !origin.IsDirty && !origin.HasOutgoingTransfer,
            "a discarded destination does not permit saving the removal, and undo restores the original");

        Check(origin.TryTransfer(target, item.Id, target.Config.Items, 0, out _) is not null, "move before saving destination first");
        target.SetBaseline();
        target.Reset(new AppConfig());
        Check(!origin.HasOutgoingTransfer && origin.IsDirty, "a saved destination permits saving the removal even after switching");

        origin.Reset(new AppConfig { Items = [item] });
        Check(origin.TryTransfer(target, item.Id, target.Config.Items, 0, out _) is not null, "move before saving origin first");
        origin.SetBaseline();
        Check(target.HasOnlyUnsavedCopy && !target.Undo() && target.Config.Items.Single().Id == item.Id,
            "undo cannot erase the only unsaved copy after the origin was saved");
        target.SetBaseline();
        Check(!target.HasOnlyUnsavedCopy, "saving the moved item clears the discard guard");

        var child = new MenuNode { Name = "Child" };
        var subtree = new MenuNode { Name = "Parent", Kind = MenuNodeKind.Group, Children = [child] };
        origin.Reset(new AppConfig { Items = [subtree] });
        target.Reset(new AppConfig());
        Check(origin.TryTransfer(target, subtree.Id, target.Config.Items, 0, out _) is not null, "transfer before saving source of subtree");
        origin.SetBaseline();
        Check(target.TryTransfer(origin, child.Id, origin.Config.Items, 0, out _) is null && target.MustSaveBeforeMoving(child),
            "a descendant of the only unsaved copy cannot be moved again before saving it");
        target.SetBaseline();
        Check(target.TryTransfer(origin, child.Id, origin.Config.Items, 0, out _) is not null,
            "a descendant can be moved again after saving the subtree");

        origin.Reset(new AppConfig { Items = [item] });
        target.Reset(new AppConfig());
        Check(origin.TryTransfer(target, item.Id, target.Config.Items, 0, out _) is not null, "transfer before saving destination");
        target.SetBaseline();
        Check(target.TryTransfer(origin, item.Id, origin.Config.Items, 0, out _) is not null &&
            !origin.IsDirty && !target.HasOutgoingTransfer, "the original saved copy permits saving a round-trip removal");
        target.SetBaseline();
        Check(!origin.HasOnlyUnsavedCopy, "a clean original configuration can close after a round trip");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
