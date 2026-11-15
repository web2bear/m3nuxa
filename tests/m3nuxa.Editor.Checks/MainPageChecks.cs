using m3nuxa.App;
using Microsoft.UI.Xaml.Media;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage
{
    internal void CheckAppearanceSettings()
    {
        config = new AppConfig();
        RenderSettings();
        var navigation = PropertiesPanel.Children.OfType<Microsoft.UI.Xaml.Controls.NavigationView>().Single();
        if (navigation.MenuItems.Count != 2 || navigation.PaneDisplayMode != Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.Top ||
            navigation.SelectionFollowsFocus != Microsoft.UI.Xaml.Controls.NavigationViewSelectionFollowsFocus.Enabled)
            throw new Exception("Appearance navigation must have two top items and follow focus.");
        navigation.SelectedItem = navigation.MenuItems[1];
        var layout = (Microsoft.UI.Xaml.Controls.Grid)navigation.Content;
        var fields = (Microsoft.UI.Xaml.Controls.StackPanel)layout.Children[0];
        var slider = fields.Children.OfType<Microsoft.UI.Xaml.Controls.Grid>().SelectMany(x => x.Children.OfType<Microsoft.UI.Xaml.Controls.Slider>()).Single();
        slider.Value = 60;
        if (config.Settings.PanelOpacity != 60) throw new Exception("Panel opacity was not updated in the draft.");
        RenderSettings();
        navigation = PropertiesPanel.Children.OfType<Microsoft.UI.Xaml.Controls.NavigationView>().Single();
        if (navigation.SelectedItem != navigation.MenuItems[1])
            throw new Exception("Settings section was not preserved after redraw.");
        navigation.SelectedItem = navigation.MenuItems[0];
        if (navigation.Content is not Microsoft.UI.Xaml.Controls.Grid menuLayout ||
            menuLayout.Children[0] is not Microsoft.UI.Xaml.Controls.StackPanel menuFields ||
            !menuFields.Children.OfType<Microsoft.UI.Xaml.Controls.NumberBox>().Any())
            throw new Exception("Selecting the menu section did not show its settings.");
    }
    private IEnumerable<UIElement> PropertyElements() => PropertiesPanel.Children.SelectMany(child =>
        child is Border { Child: StackPanel section } ? section.Children.Cast<UIElement>() : Enumerable.Repeat(child, 1));

    internal void CheckConfigurationTitle()
    {
        var path = Path.Combine(Path.GetTempPath(), "config.json");
        var imported = Path.Combine(Path.GetTempPath(), "imported.json");
        UpdateConfigurationTitle(new EditorResponse(true, null, new System.Text.Json.Nodes.JsonObject { ["configPath"] = path }));
        if (!App.MainWindow.Title.Contains(path, StringComparison.Ordinal)) throw new Exception("Active configuration path missing from title.");
        ShowConfigurationTitle(imported);
        if (App.MainWindow.Title.Contains(path, StringComparison.Ordinal) || !App.MainWindow.Title.Contains(imported, StringComparison.Ordinal))
            throw new Exception("The title must identify the selected draft, not the active configuration.");
        UpdateConfigurationTitle(new EditorResponse(true, null, new System.Text.Json.Nodes.JsonObject()));
        if (App.MainWindow.Title.Contains(path, StringComparison.Ordinal)) throw new Exception("Unsaved configuration should not show an old path.");
    }

    internal void CheckNewDraftRequiresConfirmation()
    {
        var response = new EditorResponse(true, null, System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            version = 1L,
            config = new AppConfig { Name = "Copy" }
        }));
        OpenDraft(response, isNew: true);
        if (dirty || !HasUnsavedChanges || !ApplyButton.IsEnabled)
            throw new Exception("A new or copied configuration must require confirmation before it is saved.");
        OpenDraft(response, path: "existing.json");
        if (HasUnsavedChanges || !ApplyButton.IsEnabled)
            throw new Exception("Opening an existing configuration should permit activation without inventing changes.");
        Remember(); config.Name = "Edited"; MarkDirty();
        if (!HasUnsavedChanges) throw new Exception("Changes to an existing configuration must require confirmation.");
        unsavedNewConfiguration = false;
        pendingActivation = false;
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
        if (commands.Length < 1 || commands[0].Label != expectedApply ||
            commands[0].Icon is not Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol: Microsoft.UI.Xaml.Controls.Symbol.Save })
            throw new Exception("Save command is missing.");
        if (commands.Any(command => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(command) is "SaveDraft" or "LoadConfiguration"))
            throw new Exception("Obsolete configuration commands are still present.");
        if (Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(ConfigurationSelector) != "ConfigurationSelector")
            throw new Exception("Configuration selector is missing.");
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
            if (MenuTree.RootNodes[0].Children.All(node => MenuTree.ContainerFromNode(node) is TreeViewItem row && Descendants(row).OfType<Image>().Any(image => image.Source is not null))) break;
        }
        foreach (var node in MenuTree.RootNodes[0].Children)
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

    internal async Task CheckExternalTreeDropsAsync()
    {
        var failures = new List<string>();
        var path = Path.GetTempFileName();
        try
        {
            foreach (var zone in new[] { MenuDropZone.Before, MenuDropZone.After, MenuDropZone.Into })
            {
                var first = new MenuNode { Name = "First" };
                var target = new MenuNode { Name = "Target", Kind = MenuNodeKind.Group,
                    Children = [new MenuNode { Name = "Existing child" }] };
                var last = new MenuNode { Name = "Last", Kind = MenuNodeKind.Group,
                    Children = [new MenuNode { Name = "Visible child" }] };
                draftState.Reset(new AppConfig { Items = [first, target, last] });
                RefreshTree();
                FindTreeNode(MenuTree.RootNodes, target.Id)!.IsExpanded = false;
                await Task.Delay(50);
                MenuTree.UpdateLayout();
                var bounds = MenuDropRows().Single(row => row.Id == target.Id).Bounds;
                var x = bounds.X + bounds.Width / 2;
                var y = bounds.Y + bounds.Height * (zone == MenuDropZone.Before ? 0.1 : zone == MenuDropZone.After ? 0.9 : 0.5);
                using (var ole = new EditorOleDropTarget(App.MainWindow, AddDroppedPaths, AddExternalText, "", InternalDragActive, CompleteInternalDrop, UpdateExternalDropIndicator, ClearExternalDropIndicator))
                {
                    var point = DropScreenPoint(x, y);
                    uint effect = 1;
                    var data = new FileDropData(path);
                    ole.DragEnter(data, 0, point, ref effect);
                    ole.DragOver(0, point, ref effect);
                    if (effect != 1 || DropIndicator.Visibility != Visibility.Visible)
                        failures.Add($"External {zone} drag has no insertion indicator.");
                    var externalTop = Canvas.GetTop(DropIndicator);
                    var externalHeight = DropIndicator.Height;
                    ShowMenuDropIndicator(first.Id, MenuHit(x, y));
                    if (externalTop != Canvas.GetTop(DropIndicator) || externalHeight != DropIndicator.Height)
                        failures.Add($"External {zone} indicator differs from the internal indicator.");
                    ole.DragLeave();
                    if (DropIndicator.Visibility != Visibility.Collapsed)
                        failures.Add("External drag leave did not clear its indicator.");
                    effect = 1;
                    // The release position must win over an earlier preview on another row.
                    var preview = DropScreenPoint(x, bounds.Y + 1);
                    ole.DragEnter(data, 0, preview, ref effect);
                    ole.Drop(data, 0, point, ref effect);
                    if (effect != 1 || DropIndicator.Visibility != Visibility.Collapsed)
                        failures.Add("External drop did not copy and clear its indicator.");
                }
                var items = zone == MenuDropZone.Into ? target.Children : config.Items;
                var expectedIndex = zone == MenuDropZone.After ? 2 : 1;
                if (items.Count <= expectedIndex || items[expectedIndex].Action?.Path != path)
                    failures.Add($"External {zone} drop did not insert at the indicated position.");
                if (FindTreeNode(MenuTree.RootNodes, target.Id)!.IsExpanded)
                    failures.Add($"External {zone} drop unexpectedly expanded the collapsed group.");
                if (!FindTreeNode(MenuTree.RootNodes, last.Id)!.IsExpanded)
                    failures.Add($"External {zone} drop collapsed an expanded group.");
                if (draftState.UndoCount != 1 || !draftState.Undo() || config.Items.Count != 3 || config.Items[1].Children.Count != 1)
                    failures.Add($"External {zone} drop was not one undoable insertion.");
            }
            var source = new MenuNode { Name = "Move me" };
            var closed = new MenuNode { Name = "Closed", Kind = MenuNodeKind.Group,
                Children = [new MenuNode { Name = "Child" }] };
            draftState.Reset(new AppConfig { Items = [source, closed] });
            RefreshTree();
            FindTreeNode(MenuTree.RootNodes, closed.Id)!.IsExpanded = false;
            MenuTree.UpdateLayout();
            var row = MenuDropRows().Single(item => item.Id == closed.Id).Bounds;
            dragSnapshot = Snapshot(); dragNodeId = source.Id;
            if (!CompleteInternalDrop(row.X + row.Width / 2, row.Y + row.Height / 2))
                failures.Add("Internal drop into a collapsed group failed.");
            MenuTreeDragItemsCompleted(MenuTree, null!);
            if (FindTreeNode(MenuTree.RootNodes, closed.Id)!.IsExpanded)
                failures.Add("Internal drop unexpectedly expanded the collapsed group.");
            MenuTree.RootNodes[0].IsExpanded = false;
            RefreshTree();
            if (MenuTree.RootNodes[0].IsExpanded)
                failures.Add("Tree refresh expanded the collapsed configuration root.");
        }
        finally { File.Delete(path); }
        if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
    }

    private static EditorDropPoint DropScreenPoint(double x, double y)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var scale = DropCheckGetDpi(hwnd) / 96.0;
        var point = new EditorDropPoint { X = (int)(x * scale), Y = (int)(y * scale) };
        DropCheckClientToScreen(hwnd, ref point);
        return point;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint DropCheckGetDpi(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    private static extern bool DropCheckClientToScreen(nint hwnd, ref EditorDropPoint point);

    private sealed class FileDropData(string path) : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        public int QueryGetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC format) => format.cfFormat == 15 ? 0 : unchecked((int)0x80040064);
        public void GetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, out System.Runtime.InteropServices.ComTypes.STGMEDIUM medium)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(path + '\0' + '\0');
            var memory = DropCheckGlobalAlloc(0x42, (nuint)(20 + bytes.Length));
            if (memory == 0) throw new OutOfMemoryException();
            var pointer = DropCheckGlobalLock(memory);
            if (pointer == 0) throw new OutOfMemoryException();
            System.Runtime.InteropServices.Marshal.WriteInt32(pointer, 0, 20);
            System.Runtime.InteropServices.Marshal.WriteInt32(pointer, 16, 1);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, pointer + 20, bytes.Length);
            DropCheckGlobalUnlock(memory);
            medium = new() { tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL, unionmember = memory };
        }
        public void GetDataHere(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, out System.Runtime.InteropServices.ComTypes.FORMATETC output) { output = default; return 1; }
        public void SetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public System.Runtime.InteropServices.ComTypes.IEnumFORMATETC EnumFormatEtc(System.Runtime.InteropServices.ComTypes.DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, System.Runtime.InteropServices.ComTypes.ADVF flags, System.Runtime.InteropServices.ComTypes.IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out System.Runtime.InteropServices.ComTypes.IEnumSTATDATA result) { result = null!; return unchecked((int)0x80040003); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GlobalAlloc")]
    private static extern nint DropCheckGlobalAlloc(uint flags, nuint size);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GlobalLock")]
    private static extern nint DropCheckGlobalLock(nint memory);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GlobalUnlock")]
    private static extern bool DropCheckGlobalUnlock(nint memory);

    internal void CheckDropAtRelease(bool showPreview, bool simulateNativeReorder = false)
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
        if (simulateNativeReorder)
        {
            CaptureMenuDropGeometry();
            // Native TreeView may reparent its visual nodes before drag completion.
            // The model and the geometry used by the insertion marker still precede that move.
            var sourceNode = FindTreeNode(MenuTree.RootNodes, source.Id)!;
            var destinationNode = FindTreeNode(MenuTree.RootNodes, second.Id)!;
            sourceNode.Parent.Children.Remove(sourceNode);
            destinationNode.Children.Add(sourceNode);
            MenuTree.UpdateLayout();
        }
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
        var rootChildren = MenuTree.RootNodes[0].Children;
        var actionNode = rootChildren[0];
        var groupNode = rootChildren.Last();
        rootChildren.Remove(actionNode);
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
            foreach (var node in VisibleNodes(MenuTree.RootNodes[0].Children))
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
