using m3nuxa.App;
using Microsoft.UI.Xaml.Media;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage
{
    private IEnumerable<UIElement> PropertyElements() => PropertiesPanel.Children.SelectMany(child =>
        child is Border { Child: StackPanel section } ? section.Children.Cast<UIElement>() : Enumerable.Repeat(child, 1));

    internal void CheckConfigurationTitle()
    {
        var path = Path.Combine(Path.GetTempPath(), "config.json");
        var imported = Path.Combine(Path.GetTempPath(), "imported.json");
        UpdateConfigurationTitle(new EditorResponse(true, null, new System.Text.Json.Nodes.JsonObject { ["configPath"] = path }));
        if (!App.MainWindow.Title.Contains(path, StringComparison.Ordinal)) throw new Exception("Active configuration path missing from title.");
        ShowConfigurationTitle(imported);
        if (!App.MainWindow.Title.Contains(path, StringComparison.Ordinal) || !App.MainWindow.Title.Contains(imported, StringComparison.Ordinal))
            throw new Exception("Imported source and apply destination should both be shown.");
        UpdateConfigurationTitle(new EditorResponse(true, null, new System.Text.Json.Nodes.JsonObject()));
        if (App.MainWindow.Title.Contains(path, StringComparison.Ordinal)) throw new Exception("Unsaved configuration should not show an old path.");
    }

    internal void CheckLocalization()
    {
        var expectedLanguage = Environment.GetEnvironmentVariable("m3nuxa_CHECK_LANGUAGE") ?? "en-US";
        if (L.Language != expectedLanguage) throw new Exception($"Expected language {expectedLanguage}, got {L.Language}.");
        var expectedApply = expectedLanguage == "ru-RU" ? "Сохранить" : "Save";
        var expectedPath = expectedLanguage == "ru-RU" ? "Путь" : "Path";
        if (L.Get("Editor_Apply_768af6") != expectedApply || ApplyButton.Label != expectedApply)
            throw new Exception("The localized Apply resource was not loaded into XAML.");
        if (L.Get("Editor_Path_1b46c6") != expectedPath)
            throw new Exception("The localized Path resource was not loaded from the editor PRI.");
        var commands = MainCommandBar.PrimaryCommands.OfType<Microsoft.UI.Xaml.Controls.AppBarButton>().ToArray();
        var expected = expectedLanguage == "ru-RU" ? new[] { "Сохранить", "Сохранить как…", "Загрузить из файла…" } : new[] { "Save", "Save as…", "Load from file…" };
        if (!commands.Take(3).Select(command => command.Label).SequenceEqual(expected)) throw new Exception("File command labels or order are wrong.");
        if (commands[0].Icon is not Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol: Microsoft.UI.Xaml.Controls.Symbol.Save } ||
            commands[1].Icon is not Microsoft.UI.Xaml.Controls.FontIcon { Glyph: "\uE792" } ||
            commands[2].Icon is not Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol: Microsoft.UI.Xaml.Controls.Symbol.OpenFile }) throw new Exception("File command icons are wrong.");
        if (commands.Any(command => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(command) == "ReloadConfiguration")) throw new Exception("Reload command still exists.");
    }

    internal void SetupDropChecks()
    {
        config = new AppConfig { Items = [new MenuNode { Name = "Drop group", Kind = MenuNodeKind.Group }] };
        draftState.SetBaseline();
        RefreshTree();
    }
    internal string DropCheckSnapshot() => Snapshot();
    internal void AddProbeText(string text, bool isUrl, double x, double y)
    {
        var (destination, index) = NativeDropDestination(x, y);
        if (index < 0) index = destination.Count;
        if (!isUrl) { AddDroppedText(text, destination, index); return; }
        var node = new MenuNode { Name = text, Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.OpenItem, Path = text } };
        Remember(); destination.Insert(index, node); MarkDirty(); RefreshTree(node.Id);
    }
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
        var properties = Descendants(PropertiesIcon).OfType<Image>().Single();
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
        var documentPath = Path.ChangeExtension(linkPath, ".txt");
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

            File.WriteAllText(documentPath, "Desktop import document check");
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            link = shell.CreateShortcut(linkPath);
            link.TargetPath = documentPath;
            link.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            var documentShortcut = Import(linkPath);
            if (documentShortcut.Action?.Kind != ActionKind.OpenItem || documentShortcut.Action.Path != documentPath || documentShortcut.IconPath != linkPath)
                throw new Exception("A document shortcut must open its target through the associated app and retain its shortcut icon.");
        }
        finally { File.Delete(linkPath); File.Delete(documentPath); }
        var visual = (Grid)CreateItemIcon(imported, 32);
        PropertiesPanel.Children.Add(visual);
        for (var attempt = 0; attempt < 50 && visual.Children[0] is not Image; attempt++) await Task.Delay(100);
        if (visual.Children[0] is not Image { Source: not null }) throw new Exception("Explorer icon preview did not load.");
        PropertiesPanel.Children.Remove(visual);
    }

    internal void CheckApplyAvailability()
    {
        config = new AppConfig { Items = [new MenuNode { Name = "Исходный пункт", Kind = MenuNodeKind.Action }] };
        draftState.SetBaseline();
        RefreshTree(); UpdateApplyState();
        Check(false, "initial state");

        Remember();
        config.Items[0].Name = "Изменённый пункт";
        MarkDirty();
        Check(true, "after edit");

        UndoClicked(this, null!);
        Check(false, "after undo to original");

        RedoClicked(this, null!);
        Check(true, "after redo");

        config.Items[0].Name = "Исходный пункт";
        MarkDirty();
        Check(false, "after matching original without undo");

        void Check(bool expected, string stage)
        {
            if (dirty != expected || ApplyButton.IsEnabled != expected)
                throw new Exception($"Apply availability is wrong {stage}.");
            var tooltip = ToolTipService.GetToolTip(ApplyButton) as string;
            if (tooltip != L.Get(expected ? "Editor_ThereAreUnsavedChangesApply_226d2b" : "Editor_NoChangesToApply_fefed9"))
                throw new Exception($"Apply tooltip is wrong {stage}: {tooltip}.");
        }
    }

    internal void CheckDropAtRelease(bool showPreview)
    {
        var source = new MenuNode { Name = "Source" };
        var first = new MenuNode { Name = "Preview group", Kind = MenuNodeKind.Group };
        var second = new MenuNode { Name = "Release group", Kind = MenuNodeKind.Group };
        draftState.Reset(new AppConfig { Items = [source, first, second] });
        RefreshTree(source.Id);
        MenuTree.UpdateLayout();
        dragSnapshot = Snapshot();
        dragNodeId = source.Id;

        var preview = Center(first);
        var release = Center(second);
        if (showPreview) ShowMenuDropIndicator(source.Id, MenuHit(preview.X, preview.Y));
        if (!CompleteInternalDrop(release.X, release.Y) || second.Children.Single().Id != source.Id || first.Children.Count != 0)
            throw new Exception("Tree drop did not use release coordinates.");
        MenuTreeDragItemsCompleted(MenuTree, null!);
        if (draftState.UndoCount != 1 || !draftState.Undo() || config.Items[0].Id != source.Id)
            throw new Exception("Tree drop must produce one undoable move.");

        (int X, int Y) Center(MenuNode node)
        {
            var bounds = MenuDropRows().Single(row => row.Id == node.Id).Bounds;
            return ((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
        }
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
            if (selected?.Id != id || PropertyElements().OfType<TextBox>().FirstOrDefault() is not TextBox name || name.Text != model.Name)
                throw new Exception($"Properties show the wrong model for {model.Name}.");
            CheckRows("after selection");
        }

        // A pending name edit must still target its original item after selection changes.
        MenuTree.SelectedNode = FindTreeNode(MenuTree.RootNodes, action.Id);
        await SettleAsync();
        var actionName = PropertyElements().OfType<TextBox>().First();
        var oldSection = (StackPanel)((Border)PropertiesPanel.Children[0]).Child;
        MenuTree.SelectedNode = FindTreeNode(MenuTree.RootNodes, group.Id);
        await SettleAsync();
        // Keep the old editor loaded while delivering its pending edit.
        oldSection.Children.Remove(actionName);
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
