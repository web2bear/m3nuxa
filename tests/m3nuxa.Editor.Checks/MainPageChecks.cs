using m3nuxa.App;
using Microsoft.UI.Xaml.Media;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage
{
    internal void DisableConfigurationLoadForChecks() => Loaded -= MainPageLoaded;

    internal async Task CheckTreeIconConsistencyAsync()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Bitwarden", "Bitwarden.exe");
        if (!File.Exists(executable)) executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        config = new AppConfig { Items = Enumerable.Range(0, 4).Select(i => new MenuNode
        { Name = "Icon check " + i, Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.RunProgram, Path = executable } }).ToList() };
        RefreshTree(config.Items[0].Id);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            MenuTree.UpdateLayout();
            if (MenuTree.RootNodes.All(node => MenuTree.ContainerFromNode(node) is TreeViewItem row && Descendants(row).OfType<Image>().Any(image => image.Source is not null))) break;
        }
        foreach (var node in MenuTree.RootNodes)
        {
            var row = (TreeViewItem)MenuTree.ContainerFromNode(node);
            if (!Descendants(row).OfType<Image>().Any(image => image.Source is not null))
                throw new Exception("Tree kept its placeholder while properties show the application icon: " + Model(node).Name);
        }
        var properties = Descendants(PropertiesPanel).OfType<Image>().Single();
        if (properties.Source is null) throw new Exception("Properties application icon missing.");
    }

    internal async Task CheckExplorerIconsAsync()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var imported = Import(folder);
        if (imported.IconPath != folder) throw new Exception("Folder import lost the Explorer icon source.");
        var program = Import(Path.Combine(folder, "explorer.exe"));
        if (program.IconPath != program.Action?.Path) throw new Exception("Program import lost its icon source.");
        var linkPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".lnk");
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = program.Action!.Path;
            link.IconLocation = Path.Combine(folder, "System32", "shell32.dll") + ",3";
            link.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            var shortcut = Import(linkPath);
            if (shortcut.IconPath != linkPath || !string.Equals(shortcut.Action?.Path, program.Action.Path, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Shortcut icon must use the original .lnk while execution uses its target.");
        }
        finally { File.Delete(linkPath); }
        var visual = (Grid)CreateItemIcon(imported, 32);
        PropertiesPanel.Children.Add(visual);
        for (var attempt = 0; attempt < 50 && visual.Children[0] is not Image; attempt++) await Task.Delay(100);
        if (visual.Children[0] is not Image { Source: not null }) throw new Exception("Explorer icon preview did not load.");
        PropertiesPanel.Children.Remove(visual);
    }

    internal async Task CheckTreeReorderAsync()
    {
        var action = new MenuNode { Name = "VLC media player", Kind = MenuNodeKind.Action, Action = new MenuAction() };
        var group = new MenuNode { Name = "Новая группа", Kind = MenuNodeKind.Group };
        var others = Enumerable.Range(1, 4).Select(index => new MenuNode
            { Name = $"Other action {index}", Kind = MenuNodeKind.Action, Action = new MenuAction() }).ToArray();
        config = new AppConfig { Items = [action, .. others, group] };
        RefreshTree(action.Id);
        await SettleAsync();
        CheckRows("initial");

        // Exercise the same node move and completion handler as native TreeView drag/drop.
        dragSnapshot = Snapshot();
        var actionNode = MenuTree.RootNodes[0];
        var groupNode = MenuTree.RootNodes.Last();
        MenuTree.RootNodes.Remove(actionNode);
        groupNode.Children.Add(actionNode);
        groupNode.IsExpanded = true;
        MenuTreeDragItemsCompleted(MenuTree, null!);
        await SettleAsync();
        CheckRows("after reparenting");
        if (config.Items.Count != others.Length + 1 || config.Items.Last().Id != group.Id || group.Children.Single().Id != action.Id)
            throw new Exception("The action was not moved into the group.");

        foreach (var id in new[] { group.Id, action.Id, group.Id })
        {
            MenuTree.SelectedNode = FindTreeNode(MenuTree.RootNodes, id);
            await SettleAsync();
            var model = id == group.Id ? group : action;
            if (selected?.Id != id || PropertiesPanel.Children[0] is not TextBox name || name.Text != model.Name)
                throw new Exception($"Properties show the wrong model for {model.Name}.");
            CheckRows("after selection");
        }

        // A pending name edit must still target its original item after selection changes.
        MenuTree.SelectedNode = FindTreeNode(MenuTree.RootNodes, action.Id);
        await SettleAsync();
        var actionName = (TextBox)PropertiesPanel.Children[0];
        MenuTree.SelectedNode = FindTreeNode(MenuTree.RootNodes, group.Id);
        await SettleAsync();
        // Keep the old editor loaded while delivering its pending edit.
        PropertiesPanel.Children.Add(actionName);
        actionName.Text = "VLC renamed";
        await SettleAsync();
        if (group.Name != "Новая группа" || action.Name != "VLC renamed")
            throw new Exception($"Pending edit: group={group.Name}, action={action.Name}.");
        PropertiesPanel.Children.Remove(actionName);
        CheckRows("after pending name edit");

        UndoClicked(this, null!);
        await SettleAsync();
        CheckRows("after undo");
        if (config.Items.Count != others.Length + 2 || config.Items[0].Id != action.Id || config.Items.Last().Id != group.Id)
            throw new Exception("Undo did not restore the original hierarchy.");
        RedoClicked(this, null!);
        await SettleAsync();
        CheckRows("after redo");
        if (config.Items.Last().Id != group.Id || config.Items.Last().Children.Single().Id != action.Id)
            throw new Exception("Redo did not restore the moved action.");

        async Task SettleAsync()
        {
            await Task.Delay(100);
            MenuTree.UpdateLayout();
        }
        void CheckRows(string stage)
        {
            foreach (var node in VisibleNodes(MenuTree.RootNodes))
            {
                if (MenuTree.ContainerFromNode(node) is not TreeViewItem container)
                    throw new Exception($"{stage}: row missing for {Model(node).Name}.");
                var labels = Descendants(container).OfType<TextBlock>().Select(label => label.Text).ToArray();
                if (!labels.Any(label => label.Contains(Model(node).Name, StringComparison.Ordinal)))
                    throw new Exception($"{stage}: model={Model(node).Name}, rendered=[{string.Join(" | ", labels)}].");
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
