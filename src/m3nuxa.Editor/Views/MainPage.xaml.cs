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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly EditorPipeClient client = new();
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();
    private AppConfig config = new();
    private MenuNode? selected;
    private bool rendering;
    private bool dirty;
    private bool allowClose;
    private string? dragSnapshot;
    private bool dragOverLogged;
    private bool draftReady;
    private string? baseSnapshot;
    private string? copiedNode;
    private string? cutNodeId;

    public MainPage()
    {
        DevLog.Write("editor", "MainPage constructor started");
        InitializeComponent();
        DevLog.Write("editor", "MainPage XAML initialized");
        UpdateSelectionCommands();
        MenuPanel.AddHandler(DragOverEvent, new DragEventHandler(MenuTreeDragOver), true);
        MenuPanel.AddHandler(DropEvent, new DragEventHandler(MenuTreeDrop), true);
        Loaded += MainPageLoaded;
        MainHotkeyBox.GotFocus += (_, _) => { if (!rendering) Remember(); };
        App.MainWindow.AppWindow.Closing += async (_, args) =>
        {
            if (!dirty || allowClose) return;
            args.Cancel = true;
            var answer = await ConfirmAsync("Несохранённые изменения", "Применить изменения перед закрытием?", "Применить", "Отбросить");
            if (answer == ContentDialogResult.Primary && !await ApplyAsync()) return;
            if (answer == ContentDialogResult.None) return;
            allowClose = true;
            App.MainWindow.Close();
        };
        DropHint.Text = "Перетащите файлы и папки сюда. Проводник и редактор должны работать с одинаковыми правами.";
    }

    private async void MainPageLoaded(object sender, RoutedEventArgs args) => await LoadCurrentAsync();

    private async Task LoadCurrentAsync()
    {
        DevLog.Write("editor", "Loading current configuration");
        baseSnapshot ??= Snapshot();
        draftReady = false;
        var response = await client.BeginAsync();
        DevLog.Write("editor", $"Begin response success={response.Success} message={response.Message}");
        var draft = EditorPipeClient.ReadDraft(response);
        if (draft is null) { ShowNotice(response.Message ?? "Не удалось прочитать конфигурацию.", InfoBarSeverity.Error); return; }
        config = draft.Config;
        baseSnapshot = Snapshot();
        draftReady = true;
        dirty = false; undo.Clear(); redo.Clear();
        RefreshTree(); SetStatus();
        DevLog.Write("editor", "Current configuration loaded");
    }

    private void Remember() { undo.Push(Snapshot()); redo.Clear(); }
    private string Snapshot() => JsonSerializer.Serialize(config, JsonOptions);
    private void MarkDirty() { dirty = true; SetStatus(); }
    private void SetStatus() => StatusText.Text = dirty ? "Есть несохранённые изменения" : "Все изменения сохранены";
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
            MainHotkeyBox.Text = config.MainHotkey;
            MenuTree.SelectedNode = FindTreeNode(MenuTree.RootNodes, selectId);
        }
        finally { rendering = false; }
        RenderProperties();
    }

    private TreeViewNode BuildNode(MenuNode model)
    {
        var display = new NodeDisplay(model);
        var node = new TreeViewNode { Content = display, IsExpanded = true };
        display.RightTapped += (_, _) => MenuTree.SelectedNode = node;
        foreach (var child in model.Children) node.Children.Add(BuildNode(child));
        return node;
    }

    private static MenuNode Model(TreeViewNode node) => node.Content is NodeDisplay display
        ? display.Model : throw new InvalidOperationException("Узел дерева не связан с пунктом меню.");
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
            PropertiesPanel.Children.Clear();
            PropertiesHint.Text = selected is null ? "Выберите пункт слева" : selected.Kind == MenuNodeKind.Group ? "Группа меню" : selected.Kind == MenuNodeKind.Separator ? "Разделитель" : "Действие";
            if (selected is null)
            {
                PropertiesPanel.Children.Add(new TextBlock { Text = "Добавьте папку, файл или сниппет,\nлибо выберите существующий пункт.", TextWrapping = TextWrapping.Wrap, Opacity = .65, Margin = new Thickness(0, 20, 0, 0) });
                return;
            }
            if (selected.Kind == MenuNodeKind.Separator) return;
            var item = selected;
            AddText("Имя", item.Name, value =>
            {
                item.Name = value;
                // Selection can change while a TextChanged callback is still pending.
                if (FindTreeNode(MenuTree.RootNodes, item.Id) is { } itemNode)
                    itemNode.Content = new NodeDisplay(item);
            });
            AddText("Горячая клавиша", item.Hotkey, value => item.Hotkey = value, placeholder: "Например, Ctrl+Alt+K");
            AddIconPicker(item);
            if (item.Kind != MenuNodeKind.Action) return;
            item.Action ??= new MenuAction();
            var action = item.Action;
            var choices = ActionChoices();
            var combo = new ComboBox { Header = "Тип действия", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = choices, DisplayMemberPath = "Label", SelectedItem = choices.First(choice => choice.Kind == action.Kind) };
            combo.SelectionChanged += (_, _) =>
            {
                if (rendering || combo.SelectedItem is not ActionChoice choice || choice.Kind == action.Kind) return;
                Remember(); action.Kind = choice.Kind; MarkDirty(); RenderProperties();
            };
            PropertiesPanel.Children.Add(combo);
            if (action.Kind is ActionKind.OpenFolder or ActionKind.RunProgram or ActionKind.RunScript or ActionKind.CopySelection or ActionKind.MoveSelection)
                AddText(action.Kind is ActionKind.CopySelection or ActionKind.MoveSelection ? "Папка назначения" : "Путь", action.Path, value => action.Path = value, variables: true);
            if (action.Kind is ActionKind.RunProgram or ActionKind.RunScript)
            {
                AddText("Аргументы", action.Arguments, value => action.Arguments = value, variables: true);
                AddText("Рабочая папка", action.WorkingDirectory, value => action.WorkingDirectory = value, variables: true);
            }
            if (action.Kind == ActionKind.RunScript)
            {
                AddText("Интерпретатор PowerShell", action.Interpreter, value => action.Interpreter = value);
                var hidden = new ToggleSwitch { Header = "Консоль", OnContent = "Скрыта", OffContent = "Видима", IsOn = action.HideConsole };
                hidden.Toggled += (_, _) => { if (rendering) return; Remember(); action.HideConsole = hidden.IsOn; MarkDirty(); };
                PropertiesPanel.Children.Add(hidden);
            }
            if (action.Kind == ActionKind.SendShortcut) AddText("Отправляемое сочетание", action.Shortcut, value => action.Shortcut = value);
            if (action.Kind == ActionKind.InsertText) AddText("Текст сниппета", action.Text, value => action.Text = value, multiline: true);
        }
        finally { rendering = false; }
    }

    private void AddText(string label, string value, Action<string> setter, bool variables = false, bool multiline = false, string placeholder = "")
    {
        var text = new TextBox { Header = label, Text = value, PlaceholderText = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 160 : 0 };
        text.GotFocus += (_, _) => { if (!rendering) Remember(); };
        text.TextChanged += (_, _) => { if (rendering) return; setter(text.Text); MarkDirty(); };
        if (!variables) { PropertiesPanel.Children.Add(text); return; }
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } }, ColumnSpacing = 8 };
        row.Children.Add(text);
        var picker = new Button { Content = new FontIcon { Glyph = "\uE943", FontSize = 16 }, VerticalAlignment = VerticalAlignment.Bottom, Width = 36, Height = 32 };
        ToolTipService.SetToolTip(picker, "Вставить переменную");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "Вставить переменную");
        var flyout = new MenuFlyout();
        foreach (var variable in new[] { "selectedPaths", "selectedPath", "currentFolder", "fileName", "extension", "parentFolder" })
        {
            var option = new MenuFlyoutItem { Text = "{" + variable + "}" };
            option.Click += (_, _) => { var start = text.SelectionStart; text.Text = text.Text.Insert(start, option.Text); text.SelectionStart = start + option.Text.Length; text.Focus(FocusState.Programmatic); };
            flyout.Items.Add(option);
        }
        picker.Flyout = flyout; Grid.SetColumn(picker, 1); row.Children.Add(picker); PropertiesPanel.Children.Add(row);
    }

    private void AddIconPicker(MenuNode item)
    {
        var row = new StackPanel { Spacing = 8 };
        row.Children.Add(new TextBlock { Text = "Иконка" });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        controls.Children.Add(CreateItemIcon(item, 32));
        var button = new Button { Content = "Выбрать…" };
        button.Click += (_, _) =>
        {
            try
            {
                var path = new IconFilePicker(AppContext.BaseDirectory).Pick(WindowNative.GetWindowHandle(App.MainWindow));
                if (path is null) return;
                if (Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".dll"))
                {
                    var resources = Path.Combine(AppContext.BaseDirectory, "resources"); Directory.CreateDirectory(resources);
                    var copy = Path.Combine(resources, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                    File.Copy(path, copy); path = copy;
                }
                Remember(); item.IconPath = path; MarkDirty(); RefreshTree(item.Id);
            }
            catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
        };
        controls.Children.Add(button);
        var reset = new Button { Content = "Как в Explorer" };
        reset.Click += (_, _) => { Remember(); item.IconPath = ""; MarkDirty(); RefreshTree(item.Id); };
        controls.Children.Add(reset);
        row.Children.Add(controls);
        if (!string.IsNullOrWhiteSpace(item.IconPath)) row.Children.Add(new TextBlock { Text = item.IconPath, FontSize = 12, Opacity = .6, TextWrapping = TextWrapping.Wrap });
        PropertiesPanel.Children.Add(row);
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
        if (PropertiesPanel.Children.FirstOrDefault() is TextBox name)
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

    private void PasteClicked(object sender, RoutedEventArgs args)
    {
        if (cutNodeId is { } id)
        {
            var source = FindModel(config.Items, id);
            if (source is null) { cutNodeId = null; UpdateSelectionCommands(); return; }
            var destination = AddDestination();
            if (ReferenceEquals(destination, source.Children) || ContainsChild(source, selected))
            {
                ShowNotice("Нельзя вложить пункт в самого себя или его дочернюю группу.", InfoBarSeverity.Warning);
                return;
            }
            Remember();
            FindSiblings(config.Items, id)?.Remove(source);
            destination.Add(source);
            cutNodeId = null;
            MarkDirty(); RefreshTree(source.Id);
            return;
        }
        if (copiedNode is null) return;
        var clone = JsonSerializer.Deserialize<MenuNode>(copiedNode, JsonOptions)!;
        AssignNewIds(clone);
        clone.Name += " (копия)";
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
    { Remember(); AddDestination().Add(node); MarkDirty(); RefreshTree(node.Id); }
    private void AddGroupClicked(object sender, RoutedEventArgs args) => Add(new MenuNode { Name = "Новая группа", Kind = MenuNodeKind.Group });
    private void AddSeparatorClicked(object sender, RoutedEventArgs args) => Add(new MenuNode { Kind = MenuNodeKind.Separator });
    private void AddSnippetClicked(object sender, RoutedEventArgs args) => Add(new MenuNode { Name = "Новый сниппет", Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.InsertText } });
    private void AddActionClicked(object sender, RoutedEventArgs args) => Add(new MenuNode { Name = "Новое действие", Kind = MenuNodeKind.Action, Action = new MenuAction() });

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

    private void ImportPaths(IEnumerable<string> paths, List<MenuNode> destination, int index = -1)
    {
        var imported = new List<MenuNode>(); var errors = new List<string>();
        foreach (var path in paths) { try { imported.Add(Import(path)); } catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); } }
        if (imported.Count > 0)
        {
            Remember(); if (index < 0 || index > destination.Count) index = destination.Count;
            destination.InsertRange(index, imported); MarkDirty(); RefreshTree(imported[^1].Id);
        }
        if (errors.Count > 0) ShowNotice(string.Join("\n", errors), InfoBarSeverity.Warning);
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

    private (List<MenuNode>, int) NativeDropDestination(int x, int y)
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
        return (AddDestination(), -1);
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
        if (!directory && extension is not (".exe" or ".bat" or ".cmd" or ".ps1" or ".lnk")) throw new InvalidOperationException("Тип файла не поддерживается.");
        var action = new MenuAction { Path = path, Kind = directory ? ActionKind.OpenFolder : extension is ".bat" or ".cmd" or ".ps1" ? ActionKind.RunScript : ActionKind.RunProgram };
        if (extension == ".lnk")
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Служба ярлыков недоступна.");
            dynamic shell = Activator.CreateInstance(type)!; dynamic link = shell.CreateShortcut(path);
            action.Path = (string)link.TargetPath; action.Arguments = (string)link.Arguments; action.WorkingDirectory = (string)link.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(action.Path)) throw new InvalidOperationException("У ярлыка нет цели.");
            extension = Path.GetExtension(action.Path).ToLowerInvariant();
            action.Kind = Directory.Exists(action.Path) ? ActionKind.OpenFolder : extension is ".bat" or ".cmd" or ".ps1" ? ActionKind.RunScript : ActionKind.RunProgram;
        }
        if (extension == ".ps1") action.Interpreter = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(part => File.Exists(Path.Combine(part, "pwsh.exe"))) ? "pwsh.exe" : "powershell.exe";
        return new MenuNode { Name = directory ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileNameWithoutExtension(path), Kind = MenuNodeKind.Action, Action = action, IconPath = path };
    }

    private void DuplicateClicked(object sender, RoutedEventArgs args)
    {
        if (selected is null) return;
        var clone = JsonSerializer.Deserialize<MenuNode>(JsonSerializer.Serialize(selected, JsonOptions), JsonOptions)!;
        AssignNewIds(clone); clone.Name += " (копия)"; Remember(); var siblings = SelectedSiblings(); siblings.Insert(siblings.IndexOf(selected) + 1, clone); MarkDirty(); RefreshTree(clone.Id);
    }
    private void DeleteClicked(object sender, RoutedEventArgs args)
    { if (selected is null) return; Remember(); SelectedSiblings().Remove(selected); MarkDirty(); RefreshTree(); }
    private void MainHotkeyChanged(object sender, TextChangedEventArgs args)
    { if (rendering) return; config.MainHotkey = MainHotkeyBox.Text; MarkDirty(); }
    private void UndoClicked(object sender, RoutedEventArgs args)
    { if (undo.Count == 0) return; redo.Push(Snapshot()); config = JsonSerializer.Deserialize<AppConfig>(undo.Pop(), JsonOptions)!; MarkDirty(); RefreshTree(); }
    private void RedoClicked(object sender, RoutedEventArgs args)
    { if (redo.Count == 0) return; undo.Push(Snapshot()); config = JsonSerializer.Deserialize<AppConfig>(redo.Pop(), JsonOptions)!; MarkDirty(); RefreshTree(); }

    private async void ApplyClicked(object sender, RoutedEventArgs args) => await ApplyAsync();
    private async Task<bool> ApplyAsync()
    {
        if (!draftReady && !await RestoreDraftAsync()) return false;
        var staged = await client.ReplaceDraftAsync(config);
        if (!staged.Success && staged.Message == "Сначала вызовите begin.")
        {
            draftReady = false;
            if (!await RestoreDraftAsync()) return false;
            staged = await client.ReplaceDraftAsync(config);
        }
        if (!staged.Success) { ShowNotice(staged.Message ?? "Не удалось подготовить конфигурацию.", InfoBarSeverity.Error); return false; }
        var response = await client.ApplyAsync();
        if (!response.Success) { ShowNotice(response.Message ?? "Не удалось применить конфигурацию.", InfoBarSeverity.Error); return false; }
        draftReady = false;
        var draft = EditorPipeClient.ReadDraft(await client.BeginAsync());
        if (draft is not null) { config = draft.Config; baseSnapshot = Snapshot(); draftReady = true; }
        dirty = false; undo.Clear(); redo.Clear(); SetStatus(); Notice.IsOpen = false;
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
            ShowNotice(response.Message ?? "Не удалось открыть черновик в приложении.", InfoBarSeverity.Error);
            return false;
        }
        if (JsonSerializer.Serialize(draft.Config, JsonOptions) != baseSnapshot)
        {
            ShowNotice("Действующая конфигурация изменилась после открытия редактора. Сохраните правки отдельно и перезагрузите конфигурацию.", InfoBarSeverity.Warning);
            return false;
        }
        draftReady = true;
        return true;
    }
    private async void ReloadClicked(object sender, RoutedEventArgs args)
    {
        if (dirty && await ConfirmAsync("Перезагрузить конфигурацию?", "Несохранённые правки будут отброшены.", "Перезагрузить") != ContentDialogResult.Primary) return;
        var response = await client.ReloadAsync();
        if (!response.Success) { ShowNotice(response.Message ?? "Не удалось перезагрузить.", InfoBarSeverity.Error); return; }
        await LoadCurrentAsync();
    }
    private async void LoadClicked(object sender, RoutedEventArgs args)
    {
        if (dirty && await ConfirmAsync("Загрузить другой файл?", "Несохранённые правки будут отброшены.", "Загрузить") != ContentDialogResult.Primary) return;
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        try
        {
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            var loaded = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(file.Path), JsonOptions) ?? throw new InvalidDataException("Файл не содержит конфигурацию.");
            Remember(); config = loaded; MarkDirty(); RefreshTree();
        }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }
    private async void SaveClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FileSavePicker { SuggestedFileName = "m3nuxa-draft" }; picker.FileTypeChoices.Add("Конфигурация JSON", [".json"]); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        try { var file = await picker.PickSaveFileAsync(); if (file is not null) await File.WriteAllTextAsync(file.Path, Snapshot()); }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }
    private async Task<ContentDialogResult> ConfirmAsync(string title, string message, string primary, string secondary = "")
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, PrimaryButtonText = primary, CloseButtonText = "Отмена", SecondaryButtonText = secondary, DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync();
    }

    private void MenuTreeDragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args) => dragSnapshot = Snapshot();
    private void MenuTreeDragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        if (dragSnapshot is null) return;
        try
        {
            List<MenuNode> Read(IEnumerable<TreeViewNode> nodes) => nodes.Select(node =>
            {
                var model = Model(node);
                if (model.Kind != MenuNodeKind.Group && node.Children.Count > 0) throw new InvalidOperationException("Вкладывать пункты можно только в группу.");
                model.Children = Read(node.Children); return model;
            }).ToList();
            config.Items = Read(MenuTree.RootNodes);
            if (Snapshot() != dragSnapshot) { undo.Push(dragSnapshot); redo.Clear(); MarkDirty(); }
            var selectedId = sender.SelectedNode is { } node ? Model(node).Id : null;
            DispatcherQueue.TryEnqueue(() => RefreshTree(selectedId));
        }
        catch (Exception ex) { config = JsonSerializer.Deserialize<AppConfig>(dragSnapshot, JsonOptions)!; RefreshTree(); ShowNotice(ex.Message, InfoBarSeverity.Warning); }
        finally { dragSnapshot = null; }
    }

    private void MenuTreeDragOver(object sender, DragEventArgs args)
    {
        if (!dragOverLogged)
        {
            DevLog.Write("editor", $"External DragOver: storageItems={args.DataView.Contains(StandardDataFormats.StorageItems)}");
            dragOverLogged = true;
        }
        if (!args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        args.AcceptedOperation = DataPackageOperation.Copy;
        args.DragUIOverride.Caption = "Добавить в меню";
        args.Handled = true;
    }
    private async void MenuTreeDrop(object sender, DragEventArgs args)
    {
        dragOverLogged = false;
        DevLog.Write("editor", $"External Drop: storageItems={args.DataView.Contains(StandardDataFormats.StorageItems)}");
        if (!args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = args.GetDeferral(); args.Handled = true;
        try
        {
            var (destination, index) = DropDestination(args);
            var files = await args.DataView.GetStorageItemsAsync();
            ImportPaths(files.Select(file => file.Path), destination, index);
        }
        catch (Exception ex) { DevLog.Write("editor", "Explorer drop failed", ex); ShowNotice(ex.Message, InfoBarSeverity.Error); }
        finally { deferral.Complete(); }
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
    [new(ActionKind.OpenFolder, "Открыть папку"), new(ActionKind.RunProgram, "Запустить программу"), new(ActionKind.RunScript, "Запустить скрипт"), new(ActionKind.SendShortcut, "Отправить сочетание"), new(ActionKind.CopySelection, "Копировать выделенные объекты"), new(ActionKind.MoveSelection, "Переместить выделенные объекты"), new(ActionKind.InsertText, "Вставить текст")];
    private sealed record ActionChoice(ActionKind Kind, string Label);
    private static FrameworkElement CreateItemIcon(MenuNode node, int size)
    {
        var panel = new Grid { Width = size, Height = size };
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
    private static Symbol SymbolFor(MenuNode item) => item.Kind == MenuNodeKind.Group ? Symbol.Folder : item.Kind == MenuNodeKind.Separator ? Symbol.Remove : item.Action?.Kind == ActionKind.InsertText ? Symbol.Paste : Symbol.Document;
    private sealed class NodeDisplay : StackPanel
    {
        public MenuNode Model { get; }
        private readonly TextBlock label;
        public NodeDisplay(MenuNode model)
        {
            Model = model; Orientation = Orientation.Horizontal; Spacing = 10;
            Children.Add(CreateItemIcon(model, 18));
            label = new TextBlock { VerticalAlignment = VerticalAlignment.Center }; Children.Add(label); Update();
        }
        public void Update() => label.Text = Model.Kind == MenuNodeKind.Separator ? "──────────" : string.IsNullOrWhiteSpace(Model.Name) ? "(без имени)" : Model.Name;
        public override string ToString() => Model.Kind == MenuNodeKind.Separator ? "──────────" :
            (Model.Kind == MenuNodeKind.Group ? "📁 " : Model.Action?.Kind == ActionKind.InsertText ? "📋 " : "📄 ") +
            (string.IsNullOrWhiteSpace(Model.Name) ? "(без имени)" : Model.Name);
    }
}



