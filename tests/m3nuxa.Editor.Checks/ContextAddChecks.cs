using m3nuxa.App;
using Microsoft.UI.Xaml.Controls;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage
{
    internal void CheckContextAdd()
    {
        var existing = new MenuNode { Name = "Existing", Kind = MenuNodeKind.Group };
        var group = new MenuNode { Name = "Group", Kind = MenuNodeKind.Group, Children = [existing] };
        config = new AppConfig { Items = [group] };
        draftState.SetBaseline();
        RefreshTree();
        AddSeparatorClicked(this, null!);
        if (config.Items[0].Kind != MenuNodeKind.Separator || config.Items[1] != group)
            throw new Exception("Separator added to configuration must precede existing items");
        RefreshTree(group.Id);
        AddGroupClicked(this, null!);
        if (group.Children.Count != 2 || group.Children[0] == existing) throw new Exception("Added group must be first inside selected group");
        RefreshTree(group.Id);
        AddActionClicked(this, null!);
        var action = group.Children[0];
        if (action.Kind != MenuNodeKind.Action || string.IsNullOrWhiteSpace(action.Name) || action.Steps.Count != 0 || DetailsTabIndex != 1)
            throw new Exception("New action must be selected on its steps tab with a default name and no steps");
        if (!CurrentPropertyElements().OfType<Grid>().SelectMany(grid => grid.Children).OfType<Button>().Any(button => Equals(button.Content, L.Get("Workflow_AddStep"))))
            throw new Exception("New action must allow adding its first step");
        CheckSeparatorInsertion();
    }

    private void CheckSeparatorInsertion()
    {
        foreach (var nested in new[] { false, true })
        foreach (var kind in new[] { MenuNodeKind.Action, MenuNodeKind.Separator })
        foreach (var atEnd in new[] { false, true })
        {
            var before = new MenuNode { Name = "Before" };
            var anchor = new MenuNode { Name = "Selected", Kind = kind };
            var after = new MenuNode { Name = "After" };
            var siblings = new List<MenuNode> { before, anchor };
            if (!atEnd) siblings.Add(after);
            draftState.Reset(new AppConfig { Items = nested
                ? [new MenuNode { Name = "Parent", Kind = MenuNodeKind.Group, Children = siblings }]
                : siblings });
            RefreshTree(anchor.Id);
            var original = Snapshot();

            AddSeparatorClicked(this, null!);
            if (siblings.Count != (atEnd ? 3 : 4) || siblings[2].Kind != MenuNodeKind.Separator ||
                siblings[1].Id != anchor.Id || (!atEnd && siblings[3].Id != after.Id))
                throw new Exception($"Separator must immediately follow the selected {kind} at {(nested ? "nested" : "root")} level (atEnd={atEnd}).");
            var insertedId = siblings[2].Id;
            if (selected?.Id != insertedId)
                throw new Exception("Inserted separator must be selected.");
            var changed = Snapshot();
            if (draftState.UndoCount != 1 || !draftState.Undo() || Snapshot() != original ||
                !draftState.Redo() || Snapshot() != changed)
                throw new Exception("Separator insertion must be one undoable change with working Redo.");
        }
    }
}
