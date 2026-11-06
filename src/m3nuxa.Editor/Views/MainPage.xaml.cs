using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using m3nuxa.App;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage : Page
{
    private string Localize(string key) => L.Get(key);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly EditorPipeClient client = new();
    private readonly EditorDraft draftState = new();
    private AppConfig config { get => draftState.Config; set => draftState.Config = value; }
    private MenuNode? selected;
    private string? stepNodeId;
    private string? activeConfigurationPath;
    private int selectedStepIndex;
    private bool rendering;
    private bool dirty => draftState.IsDirty;
    private bool allowClose;
    private string? dragSnapshot;
    private string? dragNodeId;
    private DragEscapeMonitor? dragEscapeMonitor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer dropIndicatorTimer;
    private readonly Brush dropAccent;
    private string? dropTargetId;
    private int dropTargetZone;
    private bool dropIndicatorValid;
    private bool dragOverLogged;
    private bool draftReady;
    private string? pendingSelection;
    private string? baseSnapshot => draftState.Baseline;
    private string? copiedNode;
    private string? cutNodeId;
    private ShortcutEditor? activeShortcut;
    private string currentMainHotkey = "";
    private readonly Dictionary<ShortcutEditor, Func<string>> globalShortcuts = [];
    private readonly HashSet<string> unavailableHotkeys = [];

    public MainPage()
    {
        DevLog.Write("editor", "MainPage constructor started");
        InitializeComponent();
        dropAccent = DropIndicator.Background;
        dropIndicatorTimer = DispatcherQueue.CreateTimer();
        dropIndicatorTimer.Interval = TimeSpan.FromMilliseconds(40);
        dropIndicatorTimer.Tick += (_, _) => UpdateDropIndicator();
        DevLog.Write("editor", "MainPage XAML initialized");
        UpdateSelectionCommands();
        var pasteText = new KeyboardAccelerator { Key = Windows.System.VirtualKey.V, Modifiers = Windows.System.VirtualKeyModifiers.Control, ScopeOwner = MenuTree };
        pasteText.Invoked += (_, args) => { PasteTextClicked(MenuTree, null!); args.Handled = true; };
        MenuTree.KeyboardAccelerators.Add(pasteText);
        AddTreeAccelerator(Windows.System.VirtualKey.Z, Windows.System.VirtualKeyModifiers.Control, UndoClicked);
        AddTreeAccelerator(Windows.System.VirtualKey.Y, Windows.System.VirtualKeyModifiers.Control, RedoClicked);
        AddTreeAccelerator(Windows.System.VirtualKey.X, Windows.System.VirtualKeyModifiers.Control, CutClicked);
        AddTreeAccelerator(Windows.System.VirtualKey.C, Windows.System.VirtualKeyModifiers.Control, CopyClicked);
        AddTreeAccelerator(Windows.System.VirtualKey.V, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, PasteClicked);
        AddTreeAccelerator(Windows.System.VirtualKey.F2, Windows.System.VirtualKeyModifiers.None, RenameClicked);
        AddTreeAccelerator(Windows.System.VirtualKey.Delete, Windows.System.VirtualKeyModifiers.None, DeleteClicked);
        MenuTree.AddHandler(RightTappedEvent, new RightTappedEventHandler(MenuTreeRightTapped), true);
        MenuPanel.AddHandler(DragOverEvent, new DragEventHandler(MenuTreeDragOver), true);
        MenuPanel.AddHandler(DropEvent, new DragEventHandler(MenuTreeDrop), true);
        Loaded += MainPageLoaded;
        App.MainWindow.AppWindow.Closing += async (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            if (activeShortcut is not null) await activeShortcut.StopAsync();
            if (!dirty) { allowClose = true; App.MainWindow.Close(); return; }
            var answer = await ConfirmAsync(L.Get("Editor_UnsavedChanges_70d744"), L.Get("Editor_ApplyChangesBeforeClosing_c969f8"), L.Get("Editor_Apply_768af6"), L.Get("Editor_Discard_941d9e"));
            if (answer == ContentDialogResult.Primary && !await ApplyAsync()) return;
            if (answer == ContentDialogResult.None) return;
            allowClose = true;
            App.MainWindow.Close();
        };
        ToolTipService.SetToolTip(DropHint, L.Get("Editor_FileExplorerAndTheEditor_36ca57"));
    }

    private void AddTreeAccelerator(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, RoutedEventHandler action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers, ScopeOwner = MenuTree };
        accelerator.Invoked += (_, args) => { action(MenuTree, null!); args.Handled = true; };
        MenuTree.KeyboardAccelerators.Add(accelerator);
    }

    private void MenuTreeRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        var element = args.OriginalSource as DependencyObject;
        while (element is not null && element != MenuTree && element is not TreeViewItem)
            element = VisualTreeHelper.GetParent(element);
        MenuTree.SelectedNode = element is TreeViewItem item ? MenuTree.NodeFromContainer(item) : null;
    }

    private void PinClicked(object sender, RoutedEventArgs args)
    {
        if (App.MainWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = PinButton.IsChecked == true;
            ToolTipService.SetToolTip(PinButton, L.Get(presenter.IsAlwaysOnTop ? "Editor_UnpinWindow" : "Editor_KeepTheEditorAboveOther_eb06b1"));
        }
    }

    private async void SettingsClicked(object sender, RoutedEventArgs args)
    {
        var response = await client.SendAsync("openSettings");
        if (!response.Success) ShowNotice(response.Message ?? L.Get("Editor_CouldNotOpenSettings_4fcb94"), InfoBarSeverity.Error);
    }

    private async void MainPageLoaded(object sender, RoutedEventArgs args) => await LoadCurrentAsync();

    private async Task LoadCurrentAsync()
    {
        DevLog.Write("editor", "Loading current configuration");
        draftReady = false;
        var response = await client.BeginAsync();
        DevLog.Write("editor", $"Begin response success={response.Success} message={response.Message}");
        var draft = EditorPipeClient.ReadDraft(response);
        if (draft is null) { ShowNotice(response.Message ?? L.Get("Editor_CouldNotReadTheConfiguration_d3f938"), InfoBarSeverity.Error); return; }
        UpdateConfigurationTitle(response);
        draftState.Reset(draft.Config);
        try { EnsureBundledIconsAvailable(draft.Config.Items); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowNotice(ex.Message, InfoBarSeverity.Warning); }
        draftReady = true;
        RefreshTree();
        if (pendingSelection is not null) { SelectMenuItem(pendingSelection); pendingSelection = null; }
        UpdateApplyState();
        ShowHotkeyAvailability(await client.ReadAsync());
        DevLog.Write("editor", "Current configuration loaded");
    }

    private void UpdateConfigurationTitle(EditorResponse response)
    {
        activeConfigurationPath = response.Data?["configPath"]?.GetValue<string>();
        ShowConfigurationTitle(activeConfigurationPath);
    }

    private void ShowConfigurationTitle(string? sourcePath)
    {
        var title = L.Get("Editor_M3nuxaMenuEditor_652d6b");
        if (!string.IsNullOrWhiteSpace(sourcePath)) title += $" — {sourcePath}";
        if (!string.IsNullOrWhiteSpace(sourcePath) && !string.IsNullOrWhiteSpace(activeConfigurationPath) &&
            !string.Equals(sourcePath, activeConfigurationPath, StringComparison.OrdinalIgnoreCase))
            title += "  " + L.Format("Editor_ApplyDestination", activeConfigurationPath);
        App.MainWindow.Title = title;
    }

    public void SelectMenuItem(string id)
    {
        if (!draftReady) { pendingSelection = id; return; }
        var node = FindTreeNode(MenuTree.RootNodes, id);
        if (node is null) { ShowNotice(L.Get("Editor_SelectionNotInDraft"), InfoBarSeverity.Warning); return; }
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        MenuTree.SelectedNode = node;
        MenuTree.UpdateLayout();
        if (MenuTree.ContainerFromNode(node) is FrameworkElement container) container.StartBringIntoView();
    }

    private void Remember() => draftState.BeginChange();
    private string Snapshot() => draftState.Snapshot();
    private void MarkDirty()
    {
        UpdateApplyState();
    }
    private void UpdateApplyState()
    {
        ApplyButton.IsEnabled = dirty;
        ToolTipService.SetToolTip(ApplyButton, dirty ? L.Get("Editor_ThereAreUnsavedChangesApply_226d2b") : L.Get("Editor_NoChangesToApply_fefed9"));
    }
    private void ShowNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (severity is InfoBarSeverity.Error or InfoBarSeverity.Warning)
            DevLog.Write("editor", $"{severity}: {message}");
        Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true;
    }

    private void RefreshTree(string? selectId = null)
    {
        rendering = true;
        try
        {
            MenuTree.RootNodes.Clear();
            foreach (var node in config.Items) MenuTree.RootNodes.Add(BuildNode(node));
            MenuTree.SelectedNode = FindTreeNode(MenuTree.RootNodes, selectId);
        }
        finally { rendering = false; }
        RenderProperties();
    }

    private TreeViewNode BuildNode(MenuNode model)
    {
        var display = new NodeDisplay(model);
        var node = new TreeViewNode { Content = display, IsExpanded = true };
        foreach (var child in model.Children) node.Children.Add(BuildNode(child));
        return node;
    }

    private static MenuNode Model(TreeViewNode node) => node.Content is NodeDisplay display
        ? display.Model : throw new InvalidOperationException(L.Get("Editor_TheTreeNodeIsNot_a02a3d"));
    private static TreeViewNode? FindTreeNode(IEnumerable<TreeViewNode> nodes, string? id)
    {
        if (id is null) return null;
        foreach (var node in nodes)
        {
            if (Model(node).Id == id) return node;
            var found = FindTreeNode(node.Children, id);
            if (found is not null) return found;
        }
        return null;
    }

    private void MenuTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    { if (!rendering) RenderProperties(); }

    private void RenderProperties()
    {
        rendering = true;
        try
        {
            selected = MenuTree.SelectedNode?.Content is NodeDisplay display ? display.Model : null;
            UpdateSelectionCommands();
            if (selected?.Kind == MenuNodeKind.Action && stepNodeId != selected.Id) DetailsTabs.SelectedIndex = 0;
            PropertiesIcon.Content = selected is not null && selected.Kind != MenuNodeKind.Separator ? CreateItemIcon(selected, 24) : null;
            PropertiesIcon.Visibility = selected is not null && selected.Kind != MenuNodeKind.Separator ? Visibility.Visible : Visibility.Collapsed;
            foreach (var field in globalShortcuts.Keys.ToArray()) globalShortcuts.Remove(field);
            PropertiesPanel.Children.Clear();
            DetailsTabs.Visibility = selected?.Kind == MenuNodeKind.Action ? Visibility.Visible : Visibility.Collapsed;
            PropertiesHint.Text = selected is null ? L.Get("Editor_SelectAnItemOnThe_2c92bb") : selected.Kind == MenuNodeKind.Group ? L.Get("Editor_MenuGroup_30ae35") : selected.Kind == MenuNodeKind.Separator ? L.Get("Editor_Separator_afb9a7") : L.Get("Editor_Action_4fe9c0");
            if (selected is not null && selected.Kind != MenuNodeKind.Separator) PropertiesHint.Text += " · " + selected.Name;
            if (selected is null)
            {
                PropertiesPanel.Children.Add(new TextBlock { Text = L.Get("Editor_AddAFolderFileOr_029745"), TextWrapping = TextWrapping.Wrap, Opacity = .65, Margin = new Thickness(0, 20, 0, 0) });
                return;
            }
            if (selected.Kind == MenuNodeKind.Separator) return;
            var item = selected;
            AddText(L.Get("Editor_Name_aee78f"), item.Name, value =>
            {
                item.Name = value;
                PropertiesHint.Text = (item.Kind == MenuNodeKind.Group ? L.Get("Editor_MenuGroup_30ae35") : L.Get("Editor_Action_4fe9c0")) + " · " + value;
                // Selection can change while a TextChanged callback is still pending.
                if (FindTreeNode(MenuTree.RootNodes, item.Id) is { } itemNode)
                    itemNode.Content = new NodeDisplay(item);
            });
            PropertiesPanel.Children.Add(CreateShortcutEditor("ItemHotkey", L.Get("Editor_ItemHotkey_77566f"),
                item.Kind == MenuNodeKind.Group ? L.Get("Editor_OpensThisGroupFromAny_904c25") : L.Get("Editor_RunsThisActionFromAny_30cdee"),
                () => item.Hotkey, value => item.Hotkey = value, item.Id));
            AddIconPicker(item);
            WrapPropertiesSection(0, L.Get("Workflow_ItemSettings"));
            if (item.Kind != MenuNodeKind.Action) return;
            if (stepNodeId != item.Id) { stepNodeId = item.Id; selectedStepIndex = 0; }
            if (item.Steps.Count == 0) return;
            selectedStepIndex = Math.Clamp(selectedStepIndex, 0, item.Steps.Count - 1);
            var stepsStart = PropertiesPanel.Children.Count;
            AddStepControls(item);
            WrapPropertiesSection(stepsStart, L.Get("Workflow_Steps"));
            var action = item.Steps[selectedStepIndex];
            var settingsStart = PropertiesPanel.Children.Count;
            if (action.Kind is ActionKind.OpenItem or ActionKind.OpenFolder or ActionKind.RunProgram or ActionKind.RunScript or ActionKind.CopySelection or ActionKind.MoveSelection)
                AddText(action.Kind is ActionKind.CopySelection or ActionKind.MoveSelection ? L.Get("Editor_DestinationFolder_40a307") : L.Get("Editor_Path_1b46c6"), action.Path, value => action.Path = value, variables: true, browse: action.Kind is ActionKind.OpenFolder or ActionKind.CopySelection or ActionKind.MoveSelection ? "folder" : "file", required: true);
            if (action.Kind is ActionKind.RunProgram or ActionKind.RunScript)
            {
                AddText(L.Get("Editor_Arguments_3c3a07"), action.Arguments, value => action.Arguments = value, variables: true);
                AddText(L.Get("Editor_WorkingDirectory_72e5f4"), action.WorkingDirectory, value => action.WorkingDirectory = value, variables: true, browse: "folder");
            }
            if (action.Kind == ActionKind.RunScript)
            {
                AddText(L.Get("Editor_PowershellInterpreter_62e963"), action.Interpreter, value => action.Interpreter = value, browse: "file");
                var hidden = new ToggleSwitch { Header = L.Get("Editor_Console_5a214b"), OnContent = L.Get("Editor_Hidden_b4def7"), OffContent = L.Get("Editor_Visible_60a11e"), IsOn = action.HideConsole };
                hidden.Toggled += (_, _) => { if (rendering) return; Remember(); action.HideConsole = hidden.IsOn; MarkDirty(); };
                PropertiesPanel.Children.Add(hidden);
            }
            if (action.Kind == ActionKind.SendShortcut)
                PropertiesPanel.Children.Add(CreateShortcutEditor("SentShortcut", L.Get("Editor_ShortcutToSend_bc4001"),
                    L.Get("Editor_PressedInTheWindowFrom_9e476c"), () => action.Shortcut, value => action.Shortcut = value, item.Id, sent: true));
            if (action.Kind is ActionKind.InsertText or ActionKind.PasteText or ActionKind.SetClipboardText)
                AddText(L.Get("Editor_SnippetText_d7c024"), action.Text, value => action.Text = value, variables: true, multiline: true, required: action.Kind != ActionKind.SetClipboardText);
            if (action.Kind == ActionKind.PasteText)
            {
                var restore = new ToggleSwitch { Header = L.Get("Workflow_RestoreClipboard"), IsOn = action.RestoreClipboard };
                restore.Toggled += (_, _) => { if (rendering) return; Remember(); action.RestoreClipboard = restore.IsOn; MarkDirty(); };
                PropertiesPanel.Children.Add(restore);
            }
            if (action.Kind == ActionKind.Delay) AddDuration(action);
            if (action.Kind is ActionKind.RunProgram or ActionKind.RunScript) AddProcessWait(action);
            var stepName = ActionChoices().FirstOrDefault(choice => choice.Kind == action.Kind)?.Label ?? action.Kind.ToString();
            WrapPropertiesSection(settingsStart, L.Format("Workflow_StepSettings", selectedStepIndex + 1, stepName));
        }
        finally { rendering = false; UpdateDetailsTab(); }
    }

    private void DetailsTabsSelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateDetailsTab();

    private void UpdateDetailsTab()
    {
        if (PropertiesPanel is null || DetailsTabs is null || selected?.Kind != MenuNodeKind.Action) return;
        for (var i = 0; i < PropertiesPanel.Children.Count; i++)
            PropertiesPanel.Children[i].Visibility = (i == 0) == (DetailsTabs.SelectedIndex == 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<UIElement> CurrentPropertyElements() => PropertiesPanel.Children.SelectMany(child =>
        child is Border { Child: StackPanel section } ? section.Children.Cast<UIElement>() : Enumerable.Repeat(child, 1));

    private void WrapPropertiesSection(int start, string title)
    {
        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(new TextBlock { Text = title, FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        while (PropertiesPanel.Children.Count > start)
        {
            var child = PropertiesPanel.Children[start];
            PropertiesPanel.Children.RemoveAt(start);
            section.Children.Add(child);
        }
        PropertiesPanel.Children.Add(new Border { Style = (Style)Resources["WorkflowSectionStyle"], Child = section });
    }

    private void AddStepControls(MenuNode item)
    {
        var choices = ActionChoices();
        var items = new ObservableCollection<MenuAction>(item.Steps);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 240,
            CanDragItems = true, CanReorderItems = true, AllowDrop = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, L.Get("Workflow_Steps"));
        // Each displayed entry holds the actual step, so native list reordering retains all settings.
        foreach (var step in items)
            list.Items.Add(new ListViewItem { Style = (Style)Resources["WorkflowStepStyle"], Tag = step, Content = choices.FirstOrDefault(c => c.Kind == step.Kind)?.Label ?? step.Kind.ToString() });
        list.SelectedIndex = selectedStepIndex;
        var reordering = false;
        list.SelectionChanged += (_, _) =>
        {
            if (rendering || reordering || list.SelectedIndex < 0 || list.SelectedIndex == selectedStepIndex) return;
            selectedStepIndex = list.SelectedIndex; RenderProperties();
        };
        list.DragItemsStarting += (_, _) => { if (!rendering) { Remember(); reordering = true; } };
        list.DragItemsCompleted += (_, _) =>
        {
            reordering = false;
            var reordered = list.Items.Cast<ListViewItem>().Select(entry => (MenuAction)entry.Tag).ToList();
            if (reordered.Count != item.Steps.Count) return;
            item.Steps.Clear(); item.Steps.AddRange(reordered);
            selectedStepIndex = Math.Max(0, list.SelectedIndex); MarkDirty(); RenderProperties();
        };
        PropertiesPanel.Children.Add(list);
        var commands = new CommandBar { HorizontalAlignment = HorizontalAlignment.Left, DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed, IsDynamicOverflowEnabled = false, OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed, IsOpen = false };
        var add = new AppBarButton { Label = L.Get("Workflow_Add"), Icon = new SymbolIcon(Symbol.Add) };
        ToolTipService.SetToolTip(add, add.Label);
        var flyout = new MenuFlyout();
        foreach (var choice in choices)
        {
            var option = new MenuFlyoutItem { Text = choice.Label };
            option.Click += (_, _) =>
            {
                Remember(); var index = Math.Min(selectedStepIndex + 1, item.Steps.Count);
                item.Steps.Insert(index, new MenuAction { Kind = choice.Kind }); selectedStepIndex = index;
                MarkDirty(); RenderProperties();
            };
            flyout.Items.Add(option);
        }
        add.Flyout = flyout; commands.PrimaryCommands.Add(add);
        void Command(string label, IconElement icon, bool enabled, Action action)
        {
            var button = new AppBarButton { Label = L.Get(label), Icon = icon, IsEnabled = enabled };
            ToolTipService.SetToolTip(button, button.Label);
            button.Click += (_, _) => { Remember(); action(); MarkDirty(); RenderProperties(); };
            commands.PrimaryCommands.Add(button);
        }
        Command("Workflow_Duplicate", new SymbolIcon(Symbol.Copy), true, () =>
        {
            var copy = JsonSerializer.Deserialize<MenuAction>(JsonSerializer.Serialize(item.Steps[selectedStepIndex], JsonOptions), JsonOptions)!;
            item.Steps.Insert(++selectedStepIndex, copy);
        });
        Command("Workflow_Delete", new SymbolIcon(Symbol.Delete), item.Steps.Count > 1, () =>
        { item.Steps.RemoveAt(selectedStepIndex); selectedStepIndex = Math.Min(selectedStepIndex, item.Steps.Count - 1); });
        void Move(int delta)
        {
            var destination = selectedStepIndex + delta;
            (item.Steps[selectedStepIndex], item.Steps[destination]) = (item.Steps[destination], item.Steps[selectedStepIndex]);
            selectedStepIndex = destination;
        }
        Command("Workflow_MoveUp", new FontIcon { Glyph = "\uE70E" }, selectedStepIndex > 0, () => Move(-1));
        Command("Workflow_MoveDown", new FontIcon { Glyph = "\uE70D" }, selectedStepIndex < item.Steps.Count - 1, () => Move(1));
        PropertiesPanel.Children.Add(commands);
    }

    private void AddDuration(MenuAction action)
    {
        var box = new NumberBox { Header = L.Get("Workflow_Duration"), Minimum = 0, Maximum = int.MaxValue,
            SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = action.DelayMilliseconds };
        var units = new ComboBox { Header = L.Get("Workflow_Units") };
        units.Items.Add(L.Get("Workflow_Milliseconds")); units.Items.Add(L.Get("Workflow_Seconds")); units.Items.Add(L.Get("Workflow_Minutes"));
        units.SelectedIndex = 0;
        var factor = 1d;
        var changingUnits = false;
        units.SelectionChanged += (_, _) =>
        {
            if (rendering || units.SelectedIndex < 0) return;
            changingUnits = true;
            factor = units.SelectedIndex == 0 ? 1 : units.SelectedIndex == 1 ? 1000 : 60000;
            box.Maximum = int.MaxValue / factor; box.Value = action.DelayMilliseconds / factor;
            changingUnits = false;
        };
        box.ValueChanged += (_, args) =>
        {
            if (rendering || changingUnits || double.IsNaN(args.NewValue) || args.NewValue < 0 || args.NewValue * factor > int.MaxValue) return;
            var value = (int)Math.Round(args.NewValue * factor);
            if (value == action.DelayMilliseconds) return;
            Remember(); action.DelayMilliseconds = value; MarkDirty();
        };
        PropertiesPanel.Children.Add(box); PropertiesPanel.Children.Add(units);
    }

    private void AddProcessWait(MenuAction action)
    {
        var wait = new ToggleSwitch { Header = L.Get("Workflow_Wait"), IsOn = action.WaitForExit };
        wait.Toggled += (_, _) => { if (rendering) return; Remember(); action.WaitForExit = wait.IsOn; MarkDirty(); RenderProperties(); };
        PropertiesPanel.Children.Add(wait);
        if (!action.WaitForExit) return;
        var enabled = new ToggleSwitch { Header = L.Get("Workflow_TimeoutEnabled"), IsOn = action.TimeoutMilliseconds.HasValue };
        enabled.Toggled += (_, _) => { if (rendering) return; Remember(); action.TimeoutMilliseconds = enabled.IsOn ? 30000 : null; MarkDirty(); RenderProperties(); };
        PropertiesPanel.Children.Add(enabled);
        if (!action.TimeoutMilliseconds.HasValue) return;
        var timeout = new NumberBox { Header = L.Get("Workflow_TimeoutMs"), Minimum = 1, Maximum = int.MaxValue,
            SmallChange = 1000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = action.TimeoutMilliseconds.Value };
        timeout.ValueChanged += (_, args) =>
        {
            if (rendering || double.IsNaN(args.NewValue) || args.NewValue < 1 || args.NewValue > int.MaxValue) return;
            var value = (int)args.NewValue; if (action.TimeoutMilliseconds == value) return;
            Remember(); action.TimeoutMilliseconds = value; MarkDirty();
        };
        PropertiesPanel.Children.Add(timeout);
    }

    private ShortcutEditor CreateShortcutEditor(string id, string label, string help, Func<string> getter, Action<string> setter,
        string? itemId, bool main = false, bool sent = false)
    {
        ShortcutEditor field = null!;
        field = new ShortcutEditor(id, label, help, getter, value =>
        {
            if (getter() == value) return;
            Remember(); setter(value); MarkDirty();
        }, value => sent ? null : FindHotkeyConflict(value, itemId, main), async () =>
        {
            if (activeShortcut is not null && activeShortcut != field) await activeShortcut.StopAsync();
            if (!sent)
            {
                var response = await client.ReadAsync();
                if (response.Success) currentMainHotkey = response.Data?["mainHotkey"]?.GetValue<string>() ?? "";
            }
            activeShortcut = field;
        });
        if (!sent)
        {
            globalShortcuts[field] = getter;
            if (unavailableHotkeys.Contains(CanonicalHotkey(getter()))) field.SetError(L.Get("Editor_TheShortcutIsUnavailableAnother_920562"));
        }
        return field;
    }

    private static string CanonicalHotkey(string value) => HotkeyParser.TryParse(value, out var modifiers, out var key) ? $"{modifiers}:{key}" : value;

    private string? FindHotkeyConflict(string value, string? itemId, bool main)
    {
        var key = CanonicalHotkey(value);
        if (!main && currentMainHotkey.Length > 0 && key == CanonicalHotkey(currentMainHotkey)) return L.Get("Editor_AlreadyAssignedToOpenThe_c893d7");
        return Visit(config.Items, "");
        string? Visit(IEnumerable<MenuNode> nodes, string parent)
        {
            foreach (var node in nodes)
            {
                var path = parent + node.Name;
                if (node.Id != itemId && node.Kind != MenuNodeKind.Separator && !string.IsNullOrWhiteSpace(node.Hotkey) && key == CanonicalHotkey(node.Hotkey))
                    return L.Format("Editor_AlreadyAssignedToItem0_8f0792", path);
                if (Visit(node.Children, path + " / ") is { } conflict) return conflict;
            }
            return null;
        }
    }

    private void ShowHotkeyAvailability(EditorResponse response)
    {
        if (response.Data?["unavailableHotkeys"] is not System.Text.Json.Nodes.JsonArray unavailable) return;
        unavailableHotkeys.Clear();
        foreach (var value in unavailable) if (value?.GetValue<string>() is { } text) unavailableHotkeys.Add(CanonicalHotkey(text));
        foreach (var (field, getter) in globalShortcuts)
            field.SetError(unavailableHotkeys.Contains(CanonicalHotkey(getter())) ? L.Get("Editor_TheShortcutIsUnavailableAnother_920562") : null);
    }

    private void AddText(string label, string value, Action<string> setter, bool variables = false, bool multiline = false, string placeholder = "", string? browse = null, bool required = false)
    {
        var text = new TextBox { Header = label, Text = value, PlaceholderText = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 160 : 0 };
        text.GotFocus += (_, _) => { if (!rendering) Remember(); };
        text.TextChanged += (_, _) => { if (rendering) return; setter(text.Text); MarkDirty(); };
        var error = new TextBlock { Text = L.Get("Workflow_Required"), TextWrapping = TextWrapping.Wrap,
            Visibility = required && (multiline ? string.IsNullOrEmpty(value) : string.IsNullOrWhiteSpace(value)) ? Visibility.Visible : Visibility.Collapsed };
        text.TextChanged += (_, _) => error.Visibility = required && (multiline ? string.IsNullOrEmpty(text.Text) : string.IsNullOrWhiteSpace(text.Text)) ? Visibility.Visible : Visibility.Collapsed;
        if (!variables && browse is null) { PropertiesPanel.Children.Add(text); if (required) PropertiesPanel.Children.Add(error); return; }
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } }, ColumnSpacing = 8 };
        row.Children.Add(text);
        if (browse is not null)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var browseButton = new Button { Content = "…", VerticalAlignment = VerticalAlignment.Bottom, Width = 36, Height = 32 };
            var browseLabel = browse == "folder" ? L.Get("Editor_ChooseFolder_14a658") : L.Get("Editor_ChooseFile_2e31da");
            ToolTipService.SetToolTip(browseButton, browseLabel);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(browseButton, browseLabel + ": " + label);
            browseButton.Click += async (_, _) =>
            {
                try
                {
                    string? path;
                    if (browse == "folder")
                    {
                        var dialog = new FolderPicker(); dialog.FileTypeFilter.Add("*");
                        InitializeWithWindow.Initialize(dialog, WindowNative.GetWindowHandle(App.MainWindow));
                        path = (await dialog.PickSingleFolderAsync())?.Path;
                    }
                    else
                    {
                        var dialog = new FileOpenPicker(); dialog.FileTypeFilter.Add("*");
                        InitializeWithWindow.Initialize(dialog, WindowNative.GetWindowHandle(App.MainWindow));
                        path = (await dialog.PickSingleFileAsync())?.Path;
                    }
                    if (path is null || path == text.Text) return;
                    Remember(); text.Text = path;
                }
                catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
            };
            Grid.SetColumn(browseButton, 1); row.Children.Add(browseButton);
        }
        if (!variables) { PropertiesPanel.Children.Add(row); if (required) PropertiesPanel.Children.Add(error); return; }
        var picker = new Button { Content = new FontIcon { Glyph = "\uE8EF", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Bottom, Width = 36, Height = 32, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(picker, L.Get("Editor_InsertContextVariable_65542b"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, L.Get("Editor_InsertContextVariable_cdf409") + label);
        picker.Click += async (_, _) =>
        {
            var position = text.SelectionStart;
            var (dialog, list) = CreateVariableDialog();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not ListViewItem { Tag: string variable }) return;
            if (!CurrentPropertyElements().Contains(row)) return;
            position = Math.Min(position, text.Text.Length);
            Remember();
            text.Text = text.Text.Insert(position, variable);
            text.SelectionStart = position + variable.Length;
            text.Focus(FocusState.Programmatic);
        };
        Grid.SetColumn(picker, browse is null ? 1 : 2); row.Children.Add(picker); PropertiesPanel.Children.Add(row); if (required) PropertiesPanel.Children.Add(error);
    }

    private (ContentDialog dialog, ListView list) CreateVariableDialog()
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Group(string title, params (string name, string description)[] variables)
        {
            list.Items.Add(new ListViewItem { Content = new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, IsEnabled = false });
            foreach (var (name, description) in variables)
            {
                var columns = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(150) }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } } };
                columns.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap });
                var detail = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(detail, 1); columns.Children.Add(detail);
                list.Items.Add(new ListViewItem { Tag = name, Content = columns, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
        }
        Group(L.Get("Editor_SelectedItems_9bf8fd"), ("{selectedPaths}", L.Get("Editor_AllSelectedPathsOnlyAs_795834")), ("{selectedPath}", L.Get("Editor_PathOfTheFirstSelected_e68d42")), ("{fileName}", L.Get("Editor_NameOfTheFirstSelected_cae089")), ("{extension}", L.Get("Editor_ExtensionOfTheFirstSelected_b49b82")), ("{parentFolder}", L.Get("Editor_ParentFolderOfTheFirst_f82d35")));
        Group(L.Get("Editor_FileExplorer_1fd2be"), ("{currentFolder}", L.Get("Editor_CurrentFolderInFileExplorer_428c9c")));
        Group(L.Get("Workflow_ValueSources"), ("{clipboardText}", L.Get("Workflow_ClipboardValue")),
            ("{env:PATH}", L.Get("Workflow_EnvValue")), ("{env:TEMP}", L.Get("Workflow_EnvValue")),
            ("{knownFolder:Desktop}", L.Get("Workflow_Desktop")), ("{knownFolder:Documents}", L.Get("Workflow_Documents")),
            ("{knownFolder:Downloads}", L.Get("Workflow_Downloads")), ("{knownFolder:Pictures}", L.Get("Workflow_Pictures")),
            ("{knownFolder:Music}", L.Get("Workflow_Music")), ("{knownFolder:Videos}", L.Get("Workflow_Videos")),
            ("{knownFolder:Profile}", L.Get("Workflow_Profile")), ("{knownFolder:Temp}", L.Get("Workflow_Temp")));
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L.Get("Editor_ContextVariables_930107"), Content = list, PrimaryButtonText = L.Get("Editor_Insert_eeb8ca"), CloseButtonText = L.Get("Editor_Cancel_0ec753"), IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.Primary };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem { Tag: string };
        return (dialog, list);
    }

    private async void PropertiesIconClicked(object sender, RoutedEventArgs args)
    {
        if (selected is { Kind: not MenuNodeKind.Separator } item) await ChooseBundledIconAsync(item);
    }

    private void AddIconPicker(MenuNode item)
    {
        var row = new StackPanel { Spacing = 8 };
        row.Children.Add(new TextBlock { Text = L.Get("Editor_Icon_25b8ac") });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var iconButton = new Button { Content = CreateItemIcon(item, 40) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(iconButton, L.Get("Icon_ChooseBundled"));
        iconButton.Click += async (_, _) => await ChooseBundledIconAsync(item);
        controls.Children.Add(iconButton);
        var button = new Button { Content = L.Get("Icon_ChooseFile") };
        button.Click += (_, _) =>
        {
            try
            {
                var path = new IconFilePicker(AppContext.BaseDirectory).Pick(WindowNative.GetWindowHandle(App.MainWindow));
                if (path is null) return;
                if (Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".dll"))
                {
                    var resources = Path.Combine(client.TrayDirectory ?? AppContext.BaseDirectory, "resources"); Directory.CreateDirectory(resources);
                    var copy = Path.Combine(resources, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                    File.Copy(path, copy); path = copy;
                }
                Remember(); item.IconPath = path; item.IconColor = ""; MarkDirty(); RefreshTree(item.Id);
            }
            catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
        };
        controls.Children.Add(button);
        var reset = new Button { Content = L.Get("Editor_UseExplorerIcon_838f6d") };
        reset.Click += (_, _) => { Remember(); item.IconPath = ""; item.IconColor = ""; MarkDirty(); RefreshTree(item.Id); };
        controls.Children.Add(reset);
        row.Children.Add(controls);
        if (!string.IsNullOrWhiteSpace(item.IconPath)) row.Children.Add(new TextBlock { Text = item.IconPath, FontSize = 12, Opacity = .6, TextWrapping = TextWrapping.Wrap });
        PropertiesPanel.Children.Add(row);
    }

    private async Task ChooseBundledIconAsync(MenuNode item)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "icon-packs", "Boxicons");
        if (!Directory.Exists(folder)) { ShowNotice(L.Get("Icon_PackMissing"), InfoBarSeverity.Warning); return; }
        var icons = Directory.GetFiles(folder, "*.png").OrderBy(Path.GetFileName).Select(path => new Uri(path).AbsoluteUri).ToArray();
        var search = new TextBox { PlaceholderText = L.Get("Icon_Search"), Margin = new Thickness(0, 0, 0, 8) };
        var grid = new GridView { Height = 360, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
        var selectedColor = item.IconColor is { Length: 7 } saved && saved[0] == '#' && uint.TryParse(saved.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _) ? saved : "#60A5FA";
        var palette = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        var swatches = new List<(Button Button, string Color)>();
        void SetColor(string color)
        {
            selectedColor = color;
            grid.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><BitmapIcon UriSource='{{Binding}}' ShowAsMonochrome='True' Foreground='{color}' Width='40' Height='40' ToolTipService.ToolTip='{{Binding}}'/></DataTemplate>");
            foreach (var swatch in swatches) swatch.Button.BorderThickness = new Thickness(string.Equals(swatch.Color, color, StringComparison.OrdinalIgnoreCase) ? 2 : 0);
        }
        foreach (var color in new[] { "#E5E7EB", "#EF4444", "#F59E0B", "#EAB308", "#84CC16", "#22C55E", "#14B8A6", "#06B6D4", "#60A5FA", "#6366F1", "#A855F7", "#EC4899" })
        {
            var rgb = Convert.ToUInt32(color[1..], 16);
            var swatch = new Button { Width = 30, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new Thickness(3),
                Content = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)) } };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, L.Get("Icon_Color") + " " + color);
            ToolTipService.SetToolTip(swatch, color);
            swatch.Click += (_, _) => SetColor(color);
            swatches.Add((swatch, color)); palette.Children.Add(swatch);
        }
        var picker = new ColorPicker { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false };
        var initialRgb = Convert.ToUInt32(selectedColor[1..], 16);
        picker.Color = Windows.UI.Color.FromArgb(255, (byte)(initialRgb >> 16), (byte)(initialRgb >> 8), (byte)initialRgb);
        var colorFlyout = new Flyout { Content = picker };
        colorFlyout.Opening += (_, _) =>
        {
            var rgb = Convert.ToUInt32(selectedColor[1..], 16);
            picker.Color = Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        };
        var customColor = new Button { Content = L.Get("Icon_CustomColor"), Flyout = colorFlyout };
        picker.ColorChanged += (_, args) => SetColor($"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}");
        palette.Children.Add(customColor);
        SetColor(selectedColor);
        grid.ItemsSource = icons;
        search.TextChanged += (_, _) => grid.ItemsSource = icons.Where(path => Path.GetFileNameWithoutExtension(path).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var content = new StackPanel(); content.Children.Add(search); content.Children.Add(new ScrollViewer { Content = palette, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }); content.Children.Add(grid);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L.Get("Icon_ChooseBundled"), Content = content, CloseButtonText = L.Get("Editor_Cancel_0ec753") };
        grid.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is not string path) return;
            try
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, new Uri(path).LocalPath);
                EnsureBundledIconAvailable(relative);
                Remember(); item.IconPath = relative; item.IconColor = selectedColor; MarkDirty();
                dialog.Hide(); RefreshTree(item.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { dialog.Hide(); ShowNotice(ex.Message, InfoBarSeverity.Error); }
        };
        await dialog.ShowAsync();
    }

    private void EnsureBundledIconsAvailable(IEnumerable<MenuNode> items)
    {
        foreach (var item in items)
        {
            EnsureBundledIconAvailable(item.IconPath);
            EnsureBundledIconsAvailable(item.Children);
        }
    }

    private void EnsureBundledIconAvailable(string path)
    {
        var tray = client.TrayDirectory;
        if (tray is null || Path.IsPathRooted(path)) return;
        var prefix = Path.Combine("icon-packs", "Boxicons") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != path[prefix.Length..]) return;
        var source = Path.Combine(AppContext.BaseDirectory, path);
        var destination = Path.Combine(tray, path);
        if (!File.Exists(source) || string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(destination) && File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(destination))) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private List<MenuNode> SelectedSiblings() => MenuTree.SelectedNode?.Parent?.Content is NodeDisplay parent
        ? parent.Model.Children : config.Items;
    private void UpdateSelectionCommands()
    {
        var hasSelection = selected is not null;
        CutButton.IsEnabled = hasSelection;
        CopyButton.IsEnabled = hasSelection;
        RenameButton.IsEnabled = hasSelection && selected!.Kind != MenuNodeKind.Separator;
        DuplicateButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
        PasteButton.IsEnabled = copiedNode is not null || cutNodeId is not null;
    }

    private void RenameClicked(object sender, RoutedEventArgs args)
    {
        if (selected is null || selected.Kind == MenuNodeKind.Separator) return;
        if (selected.Kind == MenuNodeKind.Action) DetailsTabs.SelectedIndex = 0;
        if (CurrentPropertyElements().OfType<TextBox>().FirstOrDefault() is TextBox name)
        {
            name.Focus(FocusState.Programmatic);
            name.SelectAll();
        }
    }

    private void CopyClicked(object sender, RoutedEventArgs args)
    {
        if (selected is null) return;
        copiedNode = JsonSerializer.Serialize(selected, JsonOptions);
        cutNodeId = null;
        UpdateSelectionCommands();
    }

    private void CutClicked(object sender, RoutedEventArgs args)
    {
        if (selected is null) return;
        cutNodeId = selected.Id;
        copiedNode = null;
        UpdateSelectionCommands();
    }

    private async void PasteTextClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var data = Clipboard.GetContent();
            if (!data.Contains(StandardDataFormats.Text)) { ShowNotice(L.Get("Editor_ThereIsNoTextOn_7811eb"), InfoBarSeverity.Warning); return; }
            var text = await data.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) { ShowNotice(L.Get("Editor_ThereIsNoTextOn_7811eb"), InfoBarSeverity.Warning); return; }
            var destination = AddDestination();
            AddDroppedText(text, destination, destination.Count);
        }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }

    private void PasteClicked(object sender, RoutedEventArgs args)
    {
        if (cutNodeId is { } id)
        {
            var source = FindModel(config.Items, id);
            if (source is null) { cutNodeId = null; UpdateSelectionCommands(); return; }
            var destination = AddDestination();
            if (ReferenceEquals(destination, source.Children) || ContainsChild(source, selected))
            {
                ShowNotice(L.Get("Editor_AnItemCannotContainItself_756ab5"), InfoBarSeverity.Warning);
                return;
            }
            if (!draftState.MoveToEnd(id, destination)) return;
            cutNodeId = null;
            MarkDirty(); RefreshTree(source.Id);
            return;
        }
        if (copiedNode is null) return;
        var clone = JsonSerializer.Deserialize<MenuNode>(copiedNode, JsonOptions)!;
        AssignNewIds(clone);
        clone.Name += L.Get("Editor_Copy_7d4e5c");
        Add(clone);
    }

    private static MenuNode? FindModel(IEnumerable<MenuNode> nodes, string id) =>
        nodes.SelectMany(node => new[] { node }.Concat(AllChildren(node))).FirstOrDefault(node => node.Id == id);

    private static IEnumerable<MenuNode> AllChildren(MenuNode node) =>
        node.Children.SelectMany(child => new[] { child }.Concat(AllChildren(child)));

    private static bool ContainsChild(MenuNode parent, MenuNode? child) =>
        child is not null && AllChildren(parent).Any(node => node.Id == child.Id);

    private static List<MenuNode>? FindSiblings(List<MenuNode> nodes, string id)
    {
        if (nodes.Any(node => node.Id == id)) return nodes;
        foreach (var node in nodes)
        {
            var found = FindSiblings(node.Children, id);
            if (found is not null) return found;
        }
        return null;
    }

    private static void AssignNewIds(MenuNode node)
    {
        node.Id = Guid.NewGuid().ToString("N");
        foreach (var child in node.Children) AssignNewIds(child);
    }

    private List<MenuNode> AddDestination() => selected?.Kind == MenuNodeKind.Group ? selected.Children : SelectedSiblings();
    private void Add(MenuNode node)
    { var destination = AddDestination(); draftState.Insert(destination, destination.Count, [node]); MarkDirty(); RefreshTree(node.Id); }
    private void AddGroupClicked(object sender, RoutedEventArgs args) => Add(new MenuNode { Name = L.Get("Editor_NewGroup_55c687"), Kind = MenuNodeKind.Group });
    private void AddSeparatorClicked(object sender, RoutedEventArgs args) => Add(new MenuNode { Kind = MenuNodeKind.Separator });
    private void AddSnippetClicked(object sender, RoutedEventArgs args) => Add(new MenuNode { Name = L.Get("Editor_NewSnippet_7f18a1"), Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.InsertText } });
    private void AddActionClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement anchor) return;
        var menu = new MenuFlyout();
        foreach (var choice in ActionChoices())
        {
            var option = new MenuFlyoutItem { Text = choice.Label };
            option.Click += (_, _) => Add(new MenuNode
            {
                Name = choice.Label,
                Kind = MenuNodeKind.Action,
                Action = new MenuAction { Kind = choice.Kind }
            });
            menu.Items.Add(option);
        }
        menu.ShowAt(anchor);
    }

    private async void AddFolderClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        try { var folder = await picker.PickSingleFolderAsync(); if (folder is not null) ImportPaths([folder.Path], AddDestination()); }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }
    private async void AddFileClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FileOpenPicker(); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in new[] { ".exe", ".bat", ".cmd", ".ps1", ".lnk" }) picker.FileTypeFilter.Add(ext);
        try { var files = await picker.PickMultipleFilesAsync(); ImportPaths(files.Select(file => file.Path), AddDestination()); }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }

    private async void ImportDesktopClicked(object sender, RoutedEventArgs args)
    {
        var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        choice.Items.Add(L.Get("Editor_DesktopFoldersAsGroups"));
        choice.Items.Add(L.Get("Editor_DesktopFoldersAsLinks"));
        choice.SelectedIndex = 0;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L.Get("Editor_ImportDesktop"), Content = choice, PrimaryButtonText = L.Get("Editor_PreviewDesktop"), CloseButtonText = L.Get("Editor_Cancel_0ec753") };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var destination = AddDestination();
        try
        {
            var errors = new List<string>();
            var root = new MenuNode { Name = L.Get("Editor_DesktopGroup"), Kind = MenuNodeKind.Group };
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);
            var groupFolders = choice.SelectedIndex == 0;
            var limitMessage = L.Get("Editor_DesktopLimit");
            IsEnabled = false;
            try
            {
                await Task.Run(() =>
                {
                    var remaining = 500;
                    foreach (var path in roots) ImportDesktopDirectory(path, root.Children, groupFolders, errors, 0, ref remaining, limitMessage);
                });
            }
            finally { IsEnabled = true; }
            if (!IsLoaded) return;
            var lines = new List<string>();
            void Describe(IEnumerable<MenuNode> nodes, int depth) { foreach (var node in nodes) { lines.Add(new string(' ', depth * 2) + node.Name); if (node.Kind == MenuNodeKind.Group) Describe(node.Children, depth + 1); } }
            Describe(root.Children, 0);
            if (errors.Count > 0) { lines.Add(""); lines.Add(L.Get("Editor_DesktopSkipped")); lines.AddRange(errors); }
            var preview = new ScrollViewer { MaxHeight = 400, Content = new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } };
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = L.Get("Editor_PreviewDesktop"), Content = preview, PrimaryButtonText = L.Get("Editor_DesktopAdd"), CloseButtonText = L.Get("Editor_Cancel_0ec753"), IsPrimaryButtonEnabled = root.Children.Count > 0 };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                draftState.Insert(destination, destination.Count, [root]); MarkDirty(); RefreshTree(root.Id);
            }
        }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }

    private static void ImportDesktopDirectory(string path, List<MenuNode> destination, bool groupFolders, List<string> errors, int depth, ref int remaining, string limitMessage)
    {
        if (depth > 32) { errors.Add(path); return; }
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(path); }
        catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); return; }
        Array.Sort(entries, StringComparer.CurrentCultureIgnoreCase);
        foreach (var entry in entries)
        {
            if (remaining <= 0) { if (!errors.Contains(limitMessage)) errors.Add(limitMessage); return; }
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0) { errors.Add(entry); continue; }
                remaining--;
                if ((attributes & FileAttributes.Directory) != 0 && groupFolders)
                {
                    var group = new MenuNode { Name = Path.GetFileName(entry), Kind = MenuNodeKind.Group };
                    ImportDesktopDirectory(entry, group.Children, true, errors, depth + 1, ref remaining, limitMessage);
                    destination.Add(group);
                }
                else if ((attributes & FileAttributes.Directory) == 0 && Path.GetExtension(entry).Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    var url = File.ReadLines(entry).FirstOrDefault(line => line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..];
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) throw new InvalidDataException(L.Get("Editor_DesktopInvalidUrl"));
                    destination.Add(new MenuNode { Name = Path.GetFileNameWithoutExtension(entry), Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.OpenItem, Path = url }, IconPath = entry });
                }
                else destination.Add(Import(entry));
            }
            catch (Exception ex) { errors.Add($"{entry}: {ex.Message}"); }
        }
    }

    private void ImportPaths(IEnumerable<string> paths, List<MenuNode> destination, int index = -1)
    {
        var imported = new List<MenuNode>(); var errors = new List<string>();
        foreach (var path in paths) { try { imported.Add(Import(path)); } catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); } }
        if (imported.Count > 0)
        {
            if (index < 0 || index > destination.Count) index = destination.Count;
            draftState.Insert(destination, index, imported); MarkDirty(); RefreshTree(imported[^1].Id);
        }
        if (errors.Count > 0) ShowNotice(string.Join("\n", errors), InfoBarSeverity.Warning);
    }
    internal void AddExternalText(string text, bool isUrl, int x, int y)
    {
        var (destination, index) = NativeDropDestination(x, y);
        if (index < 0) index = destination.Count;
        if (!isUrl) { AddDroppedText(text, destination, index); return; }
        var node = new MenuNode { Name = text, Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.OpenItem, Path = text } };
        draftState.Insert(destination, index, [node]); MarkDirty(); RefreshTree(node.Id);
    }

    internal void AddDroppedPaths(IReadOnlyList<string> paths, int x, int y)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => AddDroppedPaths(paths, x, y));
            return;
        }
        DevLog.Write("editor", $"Explorer native drop: {paths.Count} item(s) at {x},{y}");
        var (destination, index) = NativeDropDestination(x, y);
        ImportPaths(paths, destination, index);
    }

    private (List<MenuNode>, int) NativeDropDestination(int x, int y, bool rootFallback = false)
    {
        foreach (var node in VisibleNodes(MenuTree.RootNodes))
        {
            if (MenuTree.ContainerFromNode(node) is not TreeViewItem container) continue;
            var origin = container.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
            if (x < origin.X || x > origin.X + container.ActualWidth || y < origin.Y || y > origin.Y + container.ActualHeight) continue;
            var model = Model(node);
            var localY = y - origin.Y;
            if (model.Kind == MenuNodeKind.Group && localY > container.ActualHeight / 3 && localY < container.ActualHeight * 2 / 3)
                return (model.Children, model.Children.Count);
            var siblings = node.Parent?.Content is NodeDisplay parent ? parent.Model.Children : config.Items;
            return (siblings, siblings.IndexOf(model) + (localY >= container.ActualHeight / 2 ? 1 : 0));
        }
        return (rootFallback ? config.Items : AddDestination(), -1);
    }

    private static IEnumerable<TreeViewNode> VisibleNodes(IEnumerable<TreeViewNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.IsExpanded) foreach (var child in VisibleNodes(node.Children)) yield return child;
        }
    }
    private static MenuNode Import(string path)
    {
        var directory = Directory.Exists(path); var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!directory && !File.Exists(path)) throw new FileNotFoundException(L.Get("Editor_TheFileIsUnavailable_a1d61d"), path);
        var action = new MenuAction { Path = path, Kind = directory ? ActionKind.OpenFolder : extension is ".bat" or ".cmd" or ".ps1" ? ActionKind.RunScript : extension is ".exe" or ".lnk" ? ActionKind.RunProgram : ActionKind.OpenItem };
        if (extension == ".lnk")
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException(L.Get("Editor_TheShortcutServiceIsUnavailable_c19487"));
            dynamic shell = Activator.CreateInstance(type)!; dynamic link = shell.CreateShortcut(path);
            action.Path = (string)link.TargetPath; action.Arguments = (string)link.Arguments; action.WorkingDirectory = (string)link.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(action.Path)) throw new InvalidOperationException(L.Get("Editor_TheShortcutHasNoTarget_17c4cf"));
            extension = Path.GetExtension(action.Path).ToLowerInvariant();
            action.Kind = Directory.Exists(action.Path) ? ActionKind.OpenFolder : extension is ".bat" or ".cmd" or ".ps1" ? ActionKind.RunScript : extension == ".exe" ? ActionKind.RunProgram : ActionKind.OpenItem;
        }
        if (extension == ".ps1") action.Interpreter = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(part => File.Exists(Path.Combine(part, "pwsh.exe"))) ? "pwsh.exe" : "powershell.exe";
        return new MenuNode { Name = directory ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileNameWithoutExtension(path), Kind = MenuNodeKind.Action, Action = action, IconPath = path };
    }

    private void DuplicateClicked(object sender, RoutedEventArgs args)
    {
        if (selected is null) return;
        var clone = JsonSerializer.Deserialize<MenuNode>(JsonSerializer.Serialize(selected, JsonOptions), JsonOptions)!;
        AssignNewIds(clone); clone.Name += L.Get("Editor_Copy_7d4e5c"); var siblings = SelectedSiblings(); draftState.Insert(siblings, siblings.IndexOf(selected) + 1, [clone]); MarkDirty(); RefreshTree(clone.Id);
    }
    private void DeleteClicked(object sender, RoutedEventArgs args)
    { if (selected is null || !draftState.Delete(selected.Id)) return; MarkDirty(); RefreshTree(); }
    private void UndoClicked(object sender, RoutedEventArgs args)
    { var id = selected?.Id; if (!draftState.Undo()) return; ShowConfigurationTitle(activeConfigurationPath); MarkDirty(); RefreshTree(id); }
    private void RedoClicked(object sender, RoutedEventArgs args)
    { var id = selected?.Id; if (!draftState.Redo()) return; ShowConfigurationTitle(activeConfigurationPath); MarkDirty(); RefreshTree(id); }

    private async void ApplyClicked(object sender, RoutedEventArgs args) => await ApplyAsync();
    private async Task<bool> ApplyAsync(string? destinationPath = null)
    {
        if (activeShortcut is not null) await activeShortcut.StopAsync();
        if (!draftReady && !await RestoreDraftAsync()) return false;
        var staged = await client.ReplaceDraftAsync(config);
        if (!staged.Success && staged.Message == "Сначала вызовите begin.")
        {
            draftReady = false;
            if (!await RestoreDraftAsync()) return false;
            staged = await client.ReplaceDraftAsync(config);
        }
        if (!staged.Success) { ShowNotice(staged.Message ?? L.Get("Editor_CouldNotPrepareTheConfiguration_896ac2"), InfoBarSeverity.Error); return false; }
        var response = destinationPath is null ? await client.ApplyAsync() : await client.SaveAsAsync(destinationPath);
        ShowHotkeyAvailability(response);
        if (!response.Success) { ShowNotice(response.Message ?? L.Get("Editor_CouldNotApplyTheConfiguration_9f63ee"), InfoBarSeverity.Error); return false; }
        draftReady = false;
        var begin = await client.BeginAsync();
        var draft = EditorPipeClient.ReadDraft(begin);
        if (draft is not null) { UpdateConfigurationTitle(begin); draftState.Reset(draft.Config); draftReady = true; RefreshTree(selected?.Id); }
        else draftState.SetBaseline();
        UpdateApplyState(); Notice.IsOpen = false;
        var warnings = response.Data?["Validation"]?["Warnings"]?.Deserialize<string[]>() ?? [];
        if (warnings.Length > 0) ShowNotice(string.Join("\n", warnings), InfoBarSeverity.Warning);
        return true;
    }

    private async Task<bool> RestoreDraftAsync()
    {
        var response = await client.BeginAsync();
        var draft = EditorPipeClient.ReadDraft(response);
        if (draft is null)
        {
            ShowNotice(response.Message ?? L.Get("Editor_CouldNotOpenTheDraft_64ddf1"), InfoBarSeverity.Error);
            return false;
        }
        if (JsonSerializer.Serialize(draft.Config, JsonOptions) != baseSnapshot)
        {
            ShowNotice(L.Get("Editor_TheActiveConfigurationChangedAfter_f05617"), InfoBarSeverity.Warning);
            return false;
        }
        draftReady = true;
        return true;
    }
    private async void LoadClicked(object sender, RoutedEventArgs args)
    {
        if (dirty && await ConfirmAsync(L.Get("Editor_LoadAnotherFile_f18ee0"), L.Get("Editor_UnsavedChangesWillBeDiscarded_0fca95"), L.Get("Editor_Load_f909c7")) != ContentDialogResult.Primary) return;
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        try
        {
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            if (activeConfigurationPath is not null && string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(activeConfigurationPath), StringComparison.OrdinalIgnoreCase))
            {
                var response = await client.ReloadAsync();
                if (!response.Success) { ShowNotice(response.Message ?? L.Get("Editor_CouldNotReload_075f1d"), InfoBarSeverity.Error); return; }
                await LoadCurrentAsync();
                return;
            }
            var loaded = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(file.Path), JsonOptions) ?? throw new InvalidDataException(L.Get("Editor_TheFileDoesNotContain_2469d6"));
            Remember(); config = loaded; MarkDirty(); RefreshTree();
            ShowConfigurationTitle(file.Path);
        }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }
    private async void SaveClicked(object sender, RoutedEventArgs args)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(activeConfigurationPath) ?? "config",
            DefaultFileExtension = ".json"
        };
        picker.FileTypeChoices.Add(L.Get("Editor_JsonConfiguration_453120"), [".json"]);
        try
        {
            var file = await picker.PickSaveFileAsync();
            if (file is not null) await ApplyAsync(file.Path);
        }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }
    private async Task<ContentDialogResult> ConfirmAsync(string title, string message, string primary, string secondary = "")
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, PrimaryButtonText = primary, CloseButtonText = L.Get("Editor_Cancel_0ec753"), SecondaryButtonText = secondary, DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync();
    }

    private void MenuTreeDragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args) { dragEscapeMonitor?.Dispose(); dragEscapeMonitor = new DragEscapeMonitor(); draftState.CommitPending(); dragSnapshot = Snapshot(); dragNodeId = args.Items.FirstOrDefault() is TreeViewNode node ? Model(node).Id : null; sender.CanReorderItems = false; dropIndicatorTimer.Start(); }
    internal bool InternalDragActive() => dragSnapshot is not null && dragNodeId is not null;
    private void HideDropIndicator() { DropIndicator.Visibility = Visibility.Collapsed; dropIndicatorValid = false; }
    private void UpdateDropIndicator()
    {
        HideDropIndicator();
        if (!InternalDragActive() || dragEscapeMonitor?.Cancelled == true || !EditorOleDropTarget.TryGetCursorPosition(App.MainWindow, out var x, out var y)) return;
        var treeOrigin = MenuTree.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
        if (x < treeOrigin.X || x >= treeOrigin.X + MenuTree.ActualWidth || y < treeOrigin.Y || y >= treeOrigin.Y + MenuTree.ActualHeight) return;
        var overlayOrigin = DropOverlay.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
        TreeViewItem? row = null;
        TreeViewNode? target = null;
        foreach (var node in VisibleNodes(MenuTree.RootNodes))
        {
            if (MenuTree.ContainerFromNode(node) is not TreeViewItem candidate) continue;
            var p = candidate.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
            if (x >= p.X && x < p.X + candidate.ActualWidth && y >= p.Y && y < p.Y + candidate.ActualHeight) { row = candidate; target = node; break; }
        }
        var source = dragNodeId is null ? null : FindModel(config.Items, dragNodeId);
        if (source is null) return;
        var (destination, _) = NativeDropDestination(x, y, rootFallback: true);
        if (!draftState.CanMove(source.Id, destination)) return;
        var accent = dropAccent;
        if (row is not null && target is not null)
        {
            var model = Model(target);
            var p = row.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
            var inside = model.Kind == MenuNodeKind.Group && y - p.Y > row.ActualHeight / 3 && y - p.Y < row.ActualHeight * 2 / 3;
            if (inside && (source.Id == model.Id || ContainsChild(source, model))) return;
            if (inside)
            {
                dropTargetId = model.Id; dropTargetZone = 0;
                DropIndicator.Background = null; DropIndicator.BorderBrush = accent; DropIndicator.BorderThickness = new Thickness(2);
                DropIndicator.Width = Math.Max(0, row.ActualWidth - 4); DropIndicator.Height = Math.Max(0, row.ActualHeight - 4);
                Canvas.SetLeft(DropIndicator, p.X - overlayOrigin.X + 2); Canvas.SetTop(DropIndicator, p.Y - overlayOrigin.Y + 2);
            }
            else
            {
                dropTargetId = model.Id; dropTargetZone = y - p.Y >= row.ActualHeight / 2 ? 1 : -1;
                DropIndicator.Background = accent; DropIndicator.BorderThickness = new Thickness(0);
                DropIndicator.Width = Math.Max(0, row.ActualWidth - 8); DropIndicator.Height = 3;
                Canvas.SetLeft(DropIndicator, p.X - overlayOrigin.X + 4); Canvas.SetTop(DropIndicator, p.Y - overlayOrigin.Y + (y - p.Y >= row.ActualHeight / 2 ? row.ActualHeight : 0) - 1);
            }
        }
        else
        {
            dropTargetId = null; dropTargetZone = 1;
            var last = MenuTree.RootNodes.LastOrDefault();
            var lastRow = last is null ? null : MenuTree.ContainerFromNode(last) as TreeViewItem;
            var bottom = lastRow is null ? treeOrigin.Y : lastRow.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point()).Y + lastRow.ActualHeight;
            foreach (var node in VisibleNodes(MenuTree.RootNodes))
                if (MenuTree.ContainerFromNode(node) is TreeViewItem visible) { var p = visible.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point()); bottom = Math.Max(bottom, p.Y + visible.ActualHeight); }
            DropIndicator.Background = accent; DropIndicator.BorderThickness = new Thickness(0); DropIndicator.Width = Math.Max(0, MenuTree.ActualWidth - 8); DropIndicator.Height = 3;
            Canvas.SetLeft(DropIndicator, treeOrigin.X - overlayOrigin.X + 4); Canvas.SetTop(DropIndicator, Math.Min(treeOrigin.Y + MenuTree.ActualHeight - 3, bottom) - overlayOrigin.Y);
        }
        DropIndicator.Visibility = Visibility.Visible;
        dropIndicatorValid = true;
    }
    internal bool CompleteInternalDrop(int x, int y)
    {
        if (dragNodeId is not { } id) return false;
        var origin = MenuTree.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
        if (x < origin.X || y < origin.Y || x >= origin.X + MenuTree.ActualWidth || y >= origin.Y + MenuTree.ActualHeight)
        {
            dragSnapshot = null; dragNodeId = null;
            return false;
        }
        List<MenuNode> destination;
        int index;
        if (dropIndicatorValid && dropTargetId is { } targetId && FindModel(config.Items, targetId) is { } target)
        {
            destination = dropTargetZone == 0 ? target.Children : FindSiblings(config.Items, targetId)!;
            index = dropTargetZone == 0 ? destination.Count : destination.IndexOf(target) + (dropTargetZone > 0 ? 1 : 0);
        }
        else if (dropIndicatorValid && dropTargetId is null) { destination = config.Items; index = destination.Count; }
        else return false;
        if (index < 0) index = destination.Count;
        var moved = draftState.Move(id, destination, index);
        if (!moved) ShowNotice(L.Get("Editor_AnItemCannotContainItself_756ab5"), InfoBarSeverity.Warning);
        dragSnapshot = null; dragNodeId = null; MarkDirty(); RefreshTree(id);
        return moved;
    }
    private void MenuTreeDragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        dropIndicatorTimer.Stop(); DropIndicator.Visibility = Visibility.Collapsed;
        sender.CanReorderItems = true;
        if (dragSnapshot is null) { dragEscapeMonitor?.Dispose(); dragEscapeMonitor = null; return; }
        try
        {
            // TreeView can apply a different drop position than the one shown by our indicator.
            // Reapply the drop to the original draft using the same hit test as the indicator.
            if (dragNodeId is not null && dragEscapeMonitor is { Available: true } monitor)
            {
                var id = dragNodeId;
                var dropX = 0; var dropY = 0;
                var canDrop = !monitor.Cancelled && !EditorOleDropTarget.IsDragButtonDown()
                    && EditorOleDropTarget.TryGetCursorPosition(App.MainWindow, out dropX, out dropY);
                draftState.Restore(dragSnapshot);
                RefreshTree(id);
                if (canDrop) CompleteInternalDrop(dropX, dropY);
                return;
            }
            List<MenuNode> Read(IEnumerable<TreeViewNode> nodes) => nodes.Select(node =>
            {
                var model = Model(node);
                if (model.Kind != MenuNodeKind.Group && node.Children.Count > 0) throw new InvalidOperationException(L.Get("Editor_OnlyAGroupCanContain_04ef69"));
                model.Children = Read(node.Children); return model;
            }).ToList();
            config.Items = Read(MenuTree.RootNodes);
            if (Snapshot() != dragSnapshot) { draftState.RecordChange(dragSnapshot); MarkDirty(); }
            var selectedId = sender.SelectedNode is { } node ? Model(node).Id : null;
            DispatcherQueue.TryEnqueue(() => RefreshTree(selectedId));
        }
        catch (Exception ex) { draftState.Restore(dragSnapshot); RefreshTree(); ShowNotice(ex.Message, InfoBarSeverity.Warning); }
        finally { dragSnapshot = null; dragNodeId = null; dropIndicatorValid = false; dragEscapeMonitor?.Dispose(); dragEscapeMonitor = null; }
    }

    private void MenuTreeDragOver(object sender, DragEventArgs args)
    {
        if (InternalDragActive())
        {
            args.AcceptedOperation = DataPackageOperation.Move;
            args.Handled = true;
            return;
        }
        if (!dragOverLogged)
        {
            DevLog.Write("editor", $"External DragOver: storageItems={args.DataView.Contains(StandardDataFormats.StorageItems)}");
            dragOverLogged = true;
        }
        if (dragSnapshot is not null || (!args.DataView.Contains(StandardDataFormats.StorageItems) && !args.DataView.Contains(StandardDataFormats.Text))) return;
        args.AcceptedOperation = DataPackageOperation.Copy;
        args.DragUIOverride.Caption = L.Get("Editor_AddToMenu_2887bc");
        args.Handled = true;
    }
    private async void MenuTreeDrop(object sender, DragEventArgs args)
    {
        dragOverLogged = false;
        if (InternalDragActive())
        {
            var point = args.GetPosition(null);
            args.Handled = true;
            CompleteInternalDrop((int)point.X, (int)point.Y);
            return;
        }
        DevLog.Write("editor", $"External Drop: storageItems={args.DataView.Contains(StandardDataFormats.StorageItems)}");
        if (dragSnapshot is not null || (!args.DataView.Contains(StandardDataFormats.StorageItems) && !args.DataView.Contains(StandardDataFormats.Text))) return;
        var deferral = args.GetDeferral(); args.Handled = true;
        try
        {
            var (destination, index) = DropDestination(args);
            if (args.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var files = await args.DataView.GetStorageItemsAsync();
                ImportPaths(files.Select(file => file.Path), destination, index);
            }
            else
            {
                var text = await args.DataView.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return;
                var candidate = text.Trim().Trim('"');
                if (File.Exists(candidate) || Directory.Exists(candidate)) ImportPaths([candidate], destination, index);
                else if (Path.IsPathFullyQualified(candidate) || (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
                    ShowNotice(L.Get("Editor_ALinkOrUnavailablePath_7308a5"), InfoBarSeverity.Warning);
                else AddDroppedText(text, destination, index);
            }
        }
        catch (Exception ex) { DevLog.Write("editor", "Explorer drop failed", ex); ShowNotice(ex.Message, InfoBarSeverity.Error); }
        finally { deferral.Complete(); }
    }
    private void AddDroppedText(string text, List<MenuNode> destination, int index)
    {
        var title = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? L.Get("Editor_Snippet_720dc5");
        if (title.Length > 60) title = title[..60] + "…";
        var node = new MenuNode { Name = title, Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.InsertText, Text = text } };
        draftState.Insert(destination, index, [node]); MarkDirty(); RefreshTree(node.Id);
    }

    private (List<MenuNode>, int) DropDestination(DragEventArgs args)
    {
        DependencyObject? current = args.OriginalSource as DependencyObject;
        while (current is not null && current is not TreeViewItem) current = VisualTreeHelper.GetParent(current);
        if (current is not TreeViewItem container) return (config.Items, config.Items.Count);
        var node = MenuTree.NodeFromContainer(container); if (node is null) return (config.Items, config.Items.Count);
        var model = Model(node); var y = args.GetPosition(container).Y;
        if (model.Kind == MenuNodeKind.Group && y > container.ActualHeight / 3 && y < container.ActualHeight * 2 / 3) return (model.Children, model.Children.Count);
        var siblings = node.Parent?.Content is NodeDisplay parent ? parent.Model.Children : config.Items;
        return (siblings, siblings.IndexOf(model) + (y >= container.ActualHeight / 2 ? 1 : 0));
    }

    private static ActionChoice[] ActionChoices() =>
    [new(ActionKind.OpenItem, L.Get("Editor_OpenAFileOrLink_5464f3")), new(ActionKind.OpenFolder, L.Get("Editor_OpenFolder_54724f")), new(ActionKind.RunProgram, L.Get("Editor_RunAProgram_079b79")), new(ActionKind.RunScript, L.Get("Editor_RunAScript_3fa155")), new(ActionKind.SendShortcut, L.Get("Editor_SendShortcut_9a9f31")), new(ActionKind.CopySelection, L.Get("Editor_CopySelectedItems_427b62")), new(ActionKind.MoveSelection, L.Get("Editor_MoveSelectedItems_457829")), new(ActionKind.InsertText, L.Get("Workflow_TypeText")), new(ActionKind.Delay, L.Get("Workflow_Delay")), new(ActionKind.SetClipboardText, L.Get("Workflow_SetClipboard")), new(ActionKind.PasteText, L.Get("Workflow_PasteText"))];
    private sealed record ActionChoice(ActionKind Kind, string Label);
    private static FrameworkElement CreateItemIcon(MenuNode node, int size)
    {
        var panel = new Grid { Width = size, Height = size };
        if (node.Kind == MenuNodeKind.Separator) return panel;
        panel.Children.Add(new SymbolIcon(SymbolFor(node)));
        panel.Loaded += async (_, _) =>
        {
            try
            {
                var bytes = await ItemIcon.PngAsync(node);
                if (bytes is null) return;
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                { writer.WriteBytes(bytes); await writer.StoreAsync(); }
                stream.Seek(0);
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bitmap.SetSourceAsync(stream);
                panel.Children.Clear();
                panel.Children.Add(new Image { Source = bitmap, Width = size, Height = size });
            }
            catch (Exception ex) { DevLog.Write("icons", "Icon preview failed", ex); }
        };
        return panel;
    }
    private static Symbol SymbolFor(MenuNode item) => item.Kind == MenuNodeKind.Group ? Symbol.Folder : item.Action?.Kind == ActionKind.InsertText ? Symbol.Paste : Symbol.Document;
    private sealed class NodeDisplay : StackPanel
    {
        public MenuNode Model { get; }
        private readonly TextBlock label;
        public NodeDisplay(MenuNode model)
        {
            Model = model; Orientation = Orientation.Horizontal; Spacing = 10;
            Children.Add(CreateItemIcon(model, 20));
            label = new TextBlock { VerticalAlignment = VerticalAlignment.Center }; Children.Add(label); Update();
        }
        public void Update() => label.Text = Model.Kind == MenuNodeKind.Separator ? "──────────" : string.IsNullOrWhiteSpace(Model.Name) ? L.Get("Editor_Unnamed_3f239e") : Model.Name;
        public override string ToString() => Model.Kind == MenuNodeKind.Separator ? "──────────" :
            (Model.Kind == MenuNodeKind.Group ? "📁 " : Model.Action?.Kind == ActionKind.InsertText ? "📋 " : "📄 ") +
            (string.IsNullOrWhiteSpace(Model.Name) ? L.Get("Editor_Unnamed_3f239e") : Model.Name);
    }
}



