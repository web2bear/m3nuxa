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
    }
}
