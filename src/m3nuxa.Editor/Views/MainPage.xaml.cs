using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
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
    private long activeConfigurationVersion;
    private string? draftConfigurationPath;
    private int selectedStepIndex;
    private ListView? stepList;
    private Border? stepDropIndicator;
    private MenuNode? stepDragItem;
    private MenuAction? draggedStep;
    private bool stepDropHandled;
    private bool rendering;
    private int settingsTabIndex;
    private Action? updateAppearancePreviews;
    private bool dirty => draftState.IsDirty;
    private bool allowClose;
    private string? dragSnapshot;
    private string? dragNodeId;
    private DragEscapeMonitor? dragEscapeMonitor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer dropIndicatorTimer;
    private readonly Brush dropAccent;
    private bool dragOverLogged;
    private bool draftReady;
    private bool pendingActivation;
    private bool unsavedNewConfiguration;
    private bool HasUnsavedChanges => dirty || unsavedNewConfiguration;
    private string T(string en, string ru) => L.Language == "ru-RU" ? ru : en;
    private string? pendingSelection;
    private string? baseSnapshot => draftState.Baseline;
    private string? copiedNode;
    private string? cutNodeId;
    private ShortcutEditor? activeShortcut;
    private readonly Dictionary<ShortcutEditor, Func<string>> globalShortcuts = [];
    private readonly HashSet<string> unavailableHotkeys = [];

    public MainPage()
    {
        DevLog.Write("editor", "MainPage constructor started");
        InitializeComponent();
        DetailsTabs.SelectedItem = DetailsSettingsItem;
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
        foreach (var (key, handler) in new (Windows.System.VirtualKey, RoutedEventHandler)[] { (Windows.System.VirtualKey.Z, UndoClicked), (Windows.System.VirtualKey.Y, RedoClicked) })
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Control, ScopeOwner = SettingsPage };
            accelerator.Invoked += (_, args) => { handler(SettingsPage, null!); args.Handled = true; };
            SettingsPage.KeyboardAccelerators.Add(accelerator);
        }
        MenuTree.AddHandler(RightTappedEvent, new RightTappedEventHandler(MenuTreeRightTapped), true);
        MenuPanel.AddHandler(DragOverEvent, new DragEventHandler(MenuTreeDragOver), true);
        MenuPanel.AddHandler(DropEvent, new DragEventHandler(MenuTreeDrop), true);
        Loaded += MainPageLoaded;
        App.MainWindow.AppWindow.Closing += async (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            if (activeShortcut is not null) await activeShortcut.StopAsync();
            if (!HasUnsavedChanges) { allowClose = true; App.MainWindow.Close(); return; }
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

    private void SettingsClicked(object sender, RoutedEventArgs args) => ShowSettings();
    private void MenuSectionClicked(object sender, RoutedEventArgs args) => ShowMenuSection();

    public void ShowSettings()
    {
        if (MenuTree.RootNodes.Count > 0) MenuTree.SelectedNode = MenuTree.RootNodes[0];
    }

    private void ShowMenuSection() { }

    private void RenderSettings()
    {
        rendering = true;
        try
        {
            globalShortcuts.Clear();
            updateAppearancePreviews = null;
            PropertiesPanel.Children.Clear();
            AddText(L.Get("Editor_Name_aee78f"), config.Name, value =>
            {
                config.Name = value;
                PropertiesHint.Text = value;
                if (MenuTree.RootNodes.Count > 0) MenuTree.RootNodes[0].Content = CreateConfigurationDisplay(value);
                UpdateConfigurationSelector();
            }, required: true);
            PropertiesPanel.Children.Add(new TextBlock { Text = L.Get("Editor_SettingsDraftHint"), TextWrapping = TextWrapping.Wrap, Opacity = .7 });
            var navigation = new NavigationView { PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
                IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = false,
                IsPaneToggleButtonVisible = false, SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Enabled,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(navigation, "AppearanceNavigation");
            var menuItem = new NavigationViewItem { Content = T("Popup menu", "Всплывающее меню") };
            var panelItem = new NavigationViewItem { Content = T("Launcher", "Ланчер") };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(menuItem, "MenuAppearanceTab");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(panelItem, "PanelAppearanceTab");
            navigation.MenuItems.Add(menuItem); navigation.MenuItems.Add(panelItem);
            var menu = new StackPanel { Spacing = 12 };
            var panel = new StackPanel { Spacing = 12 };
            PropertiesPanel.Children.Add(navigation);
            menu.Children.Add(CreateShortcutEditor("SettingsHotkey", L.Get("Editor_SettingsMenuShortcut"), "",
                () => config.Settings.MainHotkey, value => config.Settings = config.Settings with { MainHotkey = value }, null, main: true));
            panel.Children.Add(CreateShortcutEditor("SettingsPanelHotkey", L.Get("Editor_SettingsPanelShortcut"), "",
                () => config.Settings.PanelHotkey, value => config.Settings = config.Settings with { PanelHotkey = value }, null, panel: true));
            AddChoice(panel, "SettingsPanelPosition", L.Get("Editor_SettingsPanelPosition"),
                [L.Get("Editor_SettingsPositionCenter"), L.Get("Editor_SettingsPositionCursor")], ["Center", "Cursor"],
                config.Settings.PanelPosition, v => config.Settings = config.Settings with { PanelPosition = v });
            AddNumber(menu, "MenuFontSize", L.Get("Editor_SettingsFontSize"), config.Settings.MenuFontSize,
                v => config.Settings = config.Settings with { MenuFontSize = v });
            AddChoice(menu, "MenuDensity", T("Item spacing", "Плотность пунктов"),
                [T("Compact", "Компактная"), T("Normal", "Обычная"), T("Comfortable", "Свободная")], ["Compact", "Normal", "Comfortable"],
                config.Settings.MenuDensity, v => config.Settings = config.Settings with { MenuDensity = v });
            AddChoice(menu, "MenuIconSize", T("Icon size", "Размер иконок"),
                [T("Small", "Маленький"), T("Normal", "Обычный"), T("Large", "Крупный")], ["Small", "Normal", "Large"],
                config.Settings.MenuIconSize, v => config.Settings = config.Settings with { MenuIconSize = v });
            AddAppearanceColor(menu, "MenuBackground", () => config.Settings.MenuBackground,
                v => config.Settings = config.Settings with { MenuBackground = v });
            AddReset(menu, () => config.Settings = config.Settings with { MenuFontSize = 12, MenuDensity = "Normal", MenuIconSize = "Normal", MenuBackground = "" });
            panel.Children.Add(new TextBlock { Text = T("Appearance", "Внешний вид"), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            AddAppearanceColor(panel, "PanelBackground", () => config.Settings.PanelBackground,
                v => config.Settings = config.Settings with { PanelBackground = v });
            var opacityValue = new TextBlock { Text = $"{config.Settings.PanelOpacity}%", VerticalAlignment = VerticalAlignment.Center };
            var opacity = new Slider { Header = T("Background opacity", "Непрозрачность фона"), Minimum = 40, Maximum = 100, StepFrequency = 1, Value = config.Settings.PanelOpacity, IsThumbToolTipEnabled = true };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(opacity, "PanelOpacity");
            opacity.ValueChanged += (_, _) => { if (rendering || (int)opacity.Value == config.Settings.PanelOpacity) return; Remember(); config.Settings = config.Settings with { PanelOpacity = (int)opacity.Value }; opacityValue.Text = $"{(int)opacity.Value}%"; MarkDirty(); updateAppearancePreviews?.Invoke(); };
            var opacityRow = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            opacityRow.Children.Add(opacity); Grid.SetColumn(opacityValue, 1); opacityRow.Children.Add(opacityValue); panel.Children.Add(opacityRow);
            AddChoice(panel, "PanelIconSize", T("Icon size", "Размер иконок"),
                [T("Small", "Маленький"), T("Normal", "Средний"), T("Large", "Большой")], ["Small", "Normal", "Large"],
                config.Settings.PanelIconSize, v => config.Settings = config.Settings with { PanelIconSize = v });
            AddNumber(panel, "PanelFontSize", T("Label size", "Размер подписей"), config.Settings.PanelFontSize,
                v => config.Settings = config.Settings with { PanelFontSize = v });
            AddReset(panel, () => config.Settings = config.Settings with { PanelBackground = "", PanelOpacity = 85, PanelIconSize = "Normal", PanelFontSize = 12 });
            var menuPreview = new StackPanel { Spacing = 6, Margin = new Thickness(0, 16, 0, 0) };
            var panelPreview = new StackPanel { Spacing = 6, Margin = new Thickness(0, 16, 0, 0) };
            Grid PreviewLayout(StackPanel fields, StackPanel preview)
            {
                var layout = new Grid { Padding = new Thickness(16), ColumnSpacing = 20, RowSpacing = 8 };
                layout.ColumnDefinitions.Add(new ColumnDefinition());
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.Children.Add(fields); layout.Children.Add(preview);
                void Arrange(double width)
                {
                    var sideBySide = width >= 740;
                    Grid.SetColumn(preview, sideBySide ? 1 : 0); Grid.SetRow(preview, sideBySide ? 0 : 1);
                    layout.ColumnDefinitions[1].Width = sideBySide ? GridLength.Auto : new GridLength(0);
                }
                layout.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
                Arrange(0);
                return layout;
            }
            var menuContent = PreviewLayout(menu, menuPreview);
            var panelContent = PreviewLayout(panel, panelPreview);
            navigation.SelectionChanged += (_, args) =>
            {
                if (ReferenceEquals(args.SelectedItem, menuItem)) { settingsTabIndex = 0; navigation.Content = menuContent; }
                else if (ReferenceEquals(args.SelectedItem, panelItem)) { settingsTabIndex = 1; navigation.Content = panelContent; }
            };
            navigation.SelectedItem = settingsTabIndex == 1 ? panelItem : menuItem;
            navigation.Content = settingsTabIndex == 1 ? panelContent : menuContent;
            updateAppearancePreviews = () => { RenderAppearancePreview(menuPreview, false); RenderAppearancePreview(panelPreview, true); };
            updateAppearancePreviews();
            var deleteConfiguration = new Button { Content = T("Delete configuration", "Удалить конфигурацию"),
                Style = (Style)Resources["DeleteConfigurationButtonStyle"],
                IsEnabled = draftReady && !unsavedNewConfiguration && string.Equals(draftConfigurationPath, activeConfigurationPath, StringComparison.OrdinalIgnoreCase) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(deleteConfiguration, "DeleteConfiguration");
            deleteConfiguration.Click += DeleteConfigurationClicked;
            PropertiesPanel.Children.Add(deleteConfiguration);
        }
        finally { rendering = false; }
    }

    private void AddChoice(Panel parent, string id, string title, string[] labels, string[] values, string current, Action<string> set)
    {
        var choice = new ComboBox { Header = title, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var label in labels) choice.Items.Add(label);
        choice.SelectedIndex = Array.IndexOf(values, current);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(choice, id);
        choice.SelectionChanged += (_, _) => { if (rendering || choice.SelectedIndex < 0) return; Remember(); set(values[choice.SelectedIndex]); MarkDirty(); updateAppearancePreviews?.Invoke(); };
        parent.Children.Add(choice);
    }

    private void AddNumber(Panel parent, string id, string title, double current, Action<double> set)
    {
        var number = new NumberBox { Header = title, Minimum = 10, Maximum = 24, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Value = current };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(number, id);
        number.ValueChanged += (_, _) => { if (rendering || !double.IsFinite(number.Value)) return; Remember(); set(number.Value); MarkDirty(); updateAppearancePreviews?.Invoke(); };
        parent.Children.Add(number);
    }

    private void AddAppearanceColor(Panel parent, string id, Func<string> get, Action<string> set)
    {
        var label = new TextBlock { Text = T("Background color", "Цвет фона") };
        var button = new Button();
        void UpdateButton()
        {
            var color = get();
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (color.Length == 7)
                content.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(color.Substring(1, 2), 16), Convert.ToByte(color.Substring(3, 2), 16), Convert.ToByte(color.Substring(5, 2), 16))) });
            content.Children.Add(new TextBlock { Text = color.Length == 0 ? T("System", "Системный") : color });
            button.Content = content;
        }
        UpdateButton();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, T("Background color", "Цвет фона"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) =>
        {
            var current = get();
            var initial = current.Length == 7 ? current : "#303840";
            var picker = new ColorPicker { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false,
                Color = Windows.UI.Color.FromArgb(255, Convert.ToByte(initial.Substring(1, 2), 16), Convert.ToByte(initial.Substring(3, 2), 16), Convert.ToByte(initial.Substring(5, 2), 16)) };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = T("Background color", "Цвет фона"), Content = picker,
                PrimaryButtonText = T("Choose", "Выбрать"), SecondaryButtonText = T("System", "Системный"), CloseButtonText = T("Cancel", "Отмена") };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) return;
            var c = picker.Color; var value = result == ContentDialogResult.Secondary ? "" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            if (value == get()) return;
            Remember(); set(value); MarkDirty(); updateAppearancePreviews?.Invoke(); UpdateButton();
        };
        var group = new StackPanel { Spacing = 6 }; group.Children.Add(label); group.Children.Add(button); parent.Children.Add(group);
    }

    private void AddReset(Panel parent, Action reset)
    {
        var button = new Button { Content = T("Reset appearance", "Сбросить внешний вид"), Margin = new Thickness(0, 8, 0, 0) };
        button.Click += (_, _) => { Remember(); reset(); MarkDirty(); RenderSettings(); };
        parent.Children.Add(button);
    }

    private void RenderAppearancePreview(StackPanel host, bool panel)
    {
        host.Children.Clear();
        host.Children.Add(new TextBlock { Text = T("Preview", "Предпросмотр"), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var color = panel ? config.Settings.PanelBackground : config.Settings.MenuBackground;
        var background = color.Length == 7 ? new SolidColorBrush(Windows.UI.Color.FromArgb(255,
            Convert.ToByte(color.Substring(1, 2), 16), Convert.ToByte(color.Substring(3, 2), 16), Convert.ToByte(color.Substring(5, 2), 16))) :
            panel ? new SolidColorBrush(host.ActualTheme == ElementTheme.Light ? Windows.UI.Color.FromArgb(255, 245, 245, 245) : Windows.UI.Color.FromArgb(255, 32, 32, 32)) :
            (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        if (panel && background is SolidColorBrush solid) solid.Opacity = config.Settings.PanelOpacity / 100.0;
        var surface = new StackPanel { Spacing = panel ? 8 : 2 };
        if (color.Length > 0)
        {
            var c = ((SolidColorBrush)background).Color;
            static double Linear(byte value) { var v = value / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            surface.RequestedTheme = 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B) > 0.179 ? ElementTheme.Light : ElementTheme.Dark;
        }
        var size = panel ? config.Settings.PanelFontSize : config.Settings.MenuFontSize;
        var iconSize = panel ? config.Settings.PanelIconSize switch { "Small" => 32, "Large" => 52, _ => 40 } : config.Settings.MenuIconSize switch { "Small" => 12, "Large" => 22, _ => 16 };
        var tiles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        if (panel) surface.Children.Add(new TextBlock { Text = T("Home", "Главная"), Margin = new Thickness(4, 0, 0, 6) });
        foreach (var (glyph, label) in new[] { ("\uE8B7", T("Folders", "Папки")), ("\uE71D", T("Applications", "Приложения")), ("\uE70F", T("Editor", "Редактор")) })
        {
            var row = new StackPanel { Orientation = panel ? Orientation.Vertical : Orientation.Horizontal, Spacing = panel ? 3 : 10,
                Width = panel ? 92 : double.NaN, HorizontalAlignment = panel ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                Padding = new Thickness(8, panel ? 4 : config.Settings.MenuDensity switch { "Compact" => 2, "Comfortable" => 8, _ => 4 }, 8, 4) };
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = iconSize });
            row.Children.Add(new TextBlock { Text = label, FontSize = size, VerticalAlignment = VerticalAlignment.Center });
            if (panel) tiles.Children.Add(row); else surface.Children.Add(row);
        }
        if (panel) surface.Children.Add(tiles);
        host.Children.Add(new Border { Background = background, CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220, Child = surface });
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
        pendingActivation = false;
        unsavedNewConfiguration = false;
        try { EnsureBundledIconsAvailable(draft.Config.Items); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowNotice(ex.Message, InfoBarSeverity.Warning); }
        draftReady = true;
        RefreshTree();
        if (SettingsPage.Visibility == Visibility.Visible) RenderSettings();
        if (pendingSelection is not null) { SelectMenuItem(pendingSelection); pendingSelection = null; }
        UpdateApplyState();
        UpdateConfigurationSelector();
        ShowHotkeyAvailability(await client.ReadAsync());
        DevLog.Write("editor", "Current configuration loaded");
    }

    private void UpdateConfigurationTitle(EditorResponse response)
    {
        activeConfigurationPath = response.Data?["configPath"]?.GetValue<string>();
        activeConfigurationVersion = response.Data?["version"]?.GetValue<long>() ?? activeConfigurationVersion;
        draftConfigurationPath = activeConfigurationPath;
        ShowConfigurationTitle(activeConfigurationPath);
    }

    private void ShowConfigurationTitle(string? sourcePath)
    {
        var title = L.Get("Editor_M3nuxaMenuEditor_652d6b");
        if (!string.IsNullOrWhiteSpace(sourcePath)) title += $" — {sourcePath}";
        App.MainWindow.Title = title;
    }

    public void SelectMenuItem(string id)
    {
        if (!draftReady) { pendingSelection = id; return; }
        ShowMenuSection();
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
        ApplyButton.IsEnabled = dirty || pendingActivation;
        ToolTipService.SetToolTip(ApplyButton, dirty ? L.Get("Editor_ThereAreUnsavedChangesApply_226d2b") : pendingActivation
            ? T("Apply and activate this configuration", "Применить и активировать эту конфигурацию")
            : L.Get("Editor_NoChangesToApply_fefed9"));
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
            var root = new TreeViewNode { Content = CreateConfigurationDisplay(config.Name), IsExpanded = true };
            foreach (var node in config.Items) root.Children.Add(BuildNode(node));
            MenuTree.RootNodes.Add(root);
            MenuTree.SelectedNode = FindTreeNode(root.Children, selectId) ?? root;
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
            if (node.Content is NodeDisplay display && display.Model.Id == id) return node;
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
            if (selected?.Kind == MenuNodeKind.Action && stepNodeId != selected.Id) DetailsTabIndex = 0;
            PropertiesIcon.Content = selected is not null && selected.Kind != MenuNodeKind.Separator ? CreateItemIcon(selected, 24) : null;
            PropertiesIcon.Visibility = selected is not null && selected.Kind != MenuNodeKind.Separator ? Visibility.Visible : Visibility.Collapsed;
            ConfigurationPropertiesIcon.Visibility = MenuTree.SelectedNode is not null && MenuTree.SelectedNode == MenuTree.RootNodes.FirstOrDefault() ? Visibility.Visible : Visibility.Collapsed;
            foreach (var field in globalShortcuts.Keys.ToArray()) globalShortcuts.Remove(field);
            PropertiesPanel.Children.Clear();
            DetailsTabs.Visibility = selected?.Kind == MenuNodeKind.Action ? Visibility.Visible : Visibility.Collapsed;
            PropertiesHint.Text = selected is null ? L.Get("Editor_SelectAnItemOnThe_2c92bb") : selected.Kind == MenuNodeKind.Separator ? L.Get("Editor_Separator_afb9a7") : selected.Name;
            PropertiesType.Text = selected is null || selected.Kind == MenuNodeKind.Separator ? "" : selected.Kind == MenuNodeKind.Group ? L.Get("Editor_MenuGroup_30ae35") : L.Get("Editor_Action_4fe9c0");
            ToolTipService.SetToolTip(PropertiesHint, PropertiesHint.Text);
            if (selected is null && MenuTree.SelectedNode == MenuTree.RootNodes.FirstOrDefault())
            {
                PropertiesHint.Text = config.Name;
                PropertiesType.Text = T("Configuration", "Конфигурация");
                RenderSettings();
                return;
            }
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
                PropertiesHint.Text = value;
                ToolTipService.SetToolTip(PropertiesHint, value);
                // Selection can change while a TextChanged callback is still pending.
                if (FindTreeNode(MenuTree.RootNodes, item.Id) is { } itemNode)
                    itemNode.Content = new NodeDisplay(item);
            });
            PropertiesPanel.Children.Add(CreateShortcutEditor("ItemHotkey", L.Get("Editor_ItemHotkey_77566f"),
                item.Kind == MenuNodeKind.Group ? L.Get("Editor_OpensThisGroupFromAny_904c25") : L.Get("Editor_RunsThisActionFromAny_30cdee"),
                () => item.Hotkey, value => item.Hotkey = value, item.Id));
            WrapPropertiesSection(0, L.Get("Workflow_ItemSettings"));
            if (item.Kind != MenuNodeKind.Action) return;
            if (stepNodeId != item.Id) { stepNodeId = item.Id; selectedStepIndex = 0; }
            selectedStepIndex = item.Steps.Count == 0 ? 0 : Math.Clamp(selectedStepIndex, 0, item.Steps.Count - 1);
            var stepsStart = PropertiesPanel.Children.Count;
            AddStepControls(item);
            WrapPropertiesSection(stepsStart, null);
            if (item.Steps.Count == 0) return;
            var action = item.Steps[selectedStepIndex];
            var settingsStart = PropertiesPanel.Children.Count;
            AddText(L.Get("Workflow_StepName"), action.Name, value =>
            {
                action.Name = value;
                if (stepList is null) return;
                for (var index = 0; index < stepList.Items.Count; index++)
                {
                    var entry = (ListViewItem)stepList.Items[index];
                    var name = StepDisplayName(item, index);
                    if (entry.Content is StackPanel row && row.Children.LastOrDefault() is TextBlock label) label.Text = name;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(entry, name);
                    ToolTipService.SetToolTip(entry, name);
                }
            }, placeholder: StepDisplayName(item, selectedStepIndex));
            if (action.Kind is ActionKind.OpenItem or ActionKind.OpenFolder or ActionKind.RunProgram or ActionKind.RunScript or ActionKind.CopySelection or ActionKind.MoveSelection)
                AddText(action.Kind is ActionKind.CopySelection or ActionKind.MoveSelection ? L.Get("Editor_DestinationFolder_40a307") : L.Get("Editor_Path_1b46c6"), action.Path, value => action.Path = value, variables: true, browse: action.Kind is ActionKind.OpenFolder or ActionKind.CopySelection or ActionKind.MoveSelection ? "folder" : "file");
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
                AddText(L.Get("Editor_SnippetText_d7c024"), action.Text, value => action.Text = value, variables: true, multiline: true);
            if (action.Kind == ActionKind.PasteText)
            {
                var restore = new ToggleSwitch { Header = L.Get("Workflow_RestoreClipboard"), OnContent = L.Get("Editor_On"), OffContent = L.Get("Editor_Off"), IsOn = action.RestoreClipboard };
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

    private int DetailsTabIndex
    {
        get => ReferenceEquals(DetailsTabs.SelectedItem, DetailsStepsItem) ? 1 : 0;
        set => DetailsTabs.SelectedItem = value == 1 ? DetailsStepsItem : DetailsSettingsItem;
    }

    private void DetailsTabsSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) => UpdateDetailsTab();

    private void UpdateDetailsTab()
    {
        if (PropertiesPanel is null || DetailsTabs is null || selected?.Kind != MenuNodeKind.Action) return;
        for (var i = 0; i < PropertiesPanel.Children.Count; i++)
            PropertiesPanel.Children[i].Visibility = (i == 0) == (DetailsTabIndex == 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<UIElement> CurrentPropertyElements() => PropertiesPanel.Children.SelectMany(child =>
        child is Border { Child: StackPanel section } ? section.Children.Cast<UIElement>() : Enumerable.Repeat(child, 1));

    private void WrapPropertiesSection(int start, string? title)
    {
        var section = new StackPanel { Spacing = 10 };
        if (title is not null) section.Children.Add(new TextBlock { Text = title, FontSize = 16,
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
        stepList = list;
        foreach (var step in items)
        {
            var name = StepDisplayName(item, list.Items.Count);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var handle = new FontIcon { Glyph = "\uE76F", FontSize = 14, Opacity = .65 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(handle, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            row.Children.Add(handle);
            row.Children.Add(new SymbolIcon(Symbol.Play));
            var stepLabel = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            row.Children.Add(stepLabel);
            var entry = new ListViewItem { Style = (Style)Resources["WorkflowStepStyle"], Tag = step, Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(entry, name);
            ToolTipService.SetToolTip(entry, name);
            list.Items.Add(entry);
        }
        list.SelectedIndex = selectedStepIndex;
        var reordering = false;
        list.SelectionChanged += (_, _) =>
        {
            if (rendering || reordering || list.SelectedIndex < 0 || list.SelectedIndex == selectedStepIndex) return;
            selectedStepIndex = list.SelectedIndex; RenderProperties();
        };
        list.DragItemsStarting += (_, args) =>
        {
            if (rendering) return;
            var dragged = args.Items.FirstOrDefault();
            var entry = list.Items.OfType<ListViewItem>().FirstOrDefault(row => ReferenceEquals(row, dragged) || ReferenceEquals(row.Content, dragged));
            if (entry?.Tag is not MenuAction step) return;
            Remember(); reordering = true; stepDragItem = item; draggedStep = step; stepDropHandled = false;
            dropIndicatorTimer.Start();
        };
        list.DragItemsCompleted += (_, _) =>
        {
            reordering = false;
            dropIndicatorTimer.Stop();
            if (stepDropIndicator is not null) stepDropIndicator.Visibility = Visibility.Collapsed;
            stepDragItem = null; draggedStep = null;
            if (stepDropHandled) { stepDropHandled = false; RenderProperties(); return; }
            var reordered = list.Items.Cast<ListViewItem>().Select(entry => (MenuAction)entry.Tag).ToList();
            if (reordered.Count != item.Steps.Count) return;
            item.Steps.Clear(); item.Steps.AddRange(reordered);
            selectedStepIndex = Math.Max(0, list.SelectedIndex); MarkDirty(); RenderProperties();
        };
        var commands = new CommandBar { HorizontalAlignment = HorizontalAlignment.Left, DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed, IsDynamicOverflowEnabled = false, OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed, IsOpen = false };
        var add = new Button { Content = L.Get("Workflow_AddStep") };
        ToolTipService.SetToolTip(add, L.Get("Workflow_AddStep"));
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        foreach (var choice in choices)
        {
            var option = new MenuFlyoutItem { Text = choice.Label };
            option.Click += (_, _) =>
            {
                Remember(); var index = Math.Min(selectedStepIndex + 1, item.Steps.Count);
                item.Steps.Insert(index, new MenuAction { Kind = choice.Kind, Name = UniqueStepName(item, choice.Label) }); selectedStepIndex = index;
                MarkDirty(); RenderProperties();
            };
            flyout.Items.Add(option);
        }
        add.Flyout = flyout;
        void Command(string label, IconElement icon, bool enabled, Action action)
        {
            var button = new AppBarButton { Label = L.Get(label), Icon = icon, IsEnabled = enabled };
            ToolTipService.SetToolTip(button, button.Label);
            button.Click += (_, _) => { Remember(); action(); MarkDirty(); RenderProperties(); };
            commands.PrimaryCommands.Add(button);
        }
        Command("Workflow_Duplicate", new SymbolIcon(Symbol.Copy), item.Steps.Count > 0, () =>
        {
            var copy = JsonSerializer.Deserialize<MenuAction>(JsonSerializer.Serialize(item.Steps[selectedStepIndex], JsonOptions), JsonOptions)!;
            copy.Name = UniqueStepName(item, StepDisplayName(item, selectedStepIndex));
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
        Command("Workflow_MoveUp", new FontIcon { Glyph = "\uE70E" }, item.Steps.Count > 0 && selectedStepIndex > 0, () => Move(-1));
        Command("Workflow_MoveDown", new FontIcon { Glyph = "\uE70D" }, selectedStepIndex < item.Steps.Count - 1, () => Move(1));
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingText = new TextBlock { Text = L.Get("Workflow_Sequence"), FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        heading.Children.Add(headingText); Grid.SetColumn(add, 1); heading.Children.Add(add);
        var listHost = new Grid();
        listHost.Children.Add(list);
        var overlay = new Canvas { IsHitTestVisible = false };
        stepDropIndicator = new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = dropAccent, Visibility = Visibility.Collapsed };
        overlay.Children.Add(stepDropIndicator);
        listHost.Children.Add(overlay);
        PropertiesPanel.Children.Add(heading);
        PropertiesPanel.Children.Add(listHost);
        PropertiesPanel.Children.Add(commands);
    }

    private string StepDisplayName(MenuNode item, int index)
    {
        var step = item.Steps[index];
        if (!string.IsNullOrWhiteSpace(step.Name)) return step.Name;
        var used = item.Steps.Where(other => !string.IsNullOrWhiteSpace(other.Name))
            .Select(other => other.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var current = 0; current <= index; current++)
        {
            var candidateStep = item.Steps[current];
            if (!string.IsNullOrWhiteSpace(candidateStep.Name)) continue;
            var label = ActionChoices().FirstOrDefault(choice => choice.Kind == candidateStep.Kind)?.Label ?? candidateStep.Kind.ToString();
            var candidate = label;
            for (var number = 2; !used.Add(candidate); number++) candidate = $"{label} ({number})";
            if (current == index) return candidate;
        }
        return "";
    }

    private string UniqueStepName(MenuNode item, string baseName)
    {
        var names = Enumerable.Range(0, item.Steps.Count).Select(index => StepDisplayName(item, index)).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var number = 2; ; number++)
        {
            var candidate = $"{baseName} ({number})";
            if (!names.Contains(candidate)) return candidate;
        }
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
        var wait = new ToggleSwitch { Header = L.Get("Workflow_Wait"), OnContent = L.Get("Editor_On"), OffContent = L.Get("Editor_Off"), IsOn = action.WaitForExit };
        wait.Toggled += (_, _) => { if (rendering) return; Remember(); action.WaitForExit = wait.IsOn; MarkDirty(); RenderProperties(); };
        PropertiesPanel.Children.Add(wait);
        if (!action.WaitForExit) return;
        var enabled = new ToggleSwitch { Header = L.Get("Workflow_TimeoutEnabled"), OnContent = L.Get("Editor_On"), OffContent = L.Get("Editor_Off"), IsOn = action.TimeoutMilliseconds.HasValue };
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
        string? itemId, bool main = false, bool sent = false, bool panel = false)
    {
        ShortcutEditor field = null!;
        field = new ShortcutEditor(id, label, help, getter, value =>
        {
            if (getter() == value) return;
            Remember(); setter(value); MarkDirty();
        }, value => sent ? null : FindHotkeyConflict(value, itemId, main, panel), async () =>
        {
            if (activeShortcut is not null && activeShortcut != field) await activeShortcut.StopAsync();
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

    private string? FindHotkeyConflict(string value, string? itemId, bool main, bool panel = false)
    {
        var key = CanonicalHotkey(value);
        if (!main && config.Settings.MainHotkey.Length > 0 && key == CanonicalHotkey(config.Settings.MainHotkey)) return L.Get("Editor_AlreadyAssignedToOpenThe_c893d7");
        if (!panel && config.Settings.PanelHotkey.Length > 0 && key == CanonicalHotkey(config.Settings.PanelHotkey)) return L.Get("Editor_SettingsPanelConflict");
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

    private async Task ChooseBundledIconAsync(MenuNode item)
    {
        var selection = CreateIconSelectionDialog(item);
        await selection.Dialog.ShowAsync();
    }

    private IconSelectionDialog CreateIconSelectionDialog(MenuNode item)
    {
        var selection = new IconSelectionDialog(item, XamlRoot, ActualHeight - 240, CreateItemIcon);
        selection.Dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var path = selection.SelectedPath;
                var color = selection.SelectedColor;
                if (path == item.IconPath && color == item.IconColor) return;
                if (selection.FileSelected && Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".dll"))
                {
                    var resources = Path.Combine(client.TrayDirectory ?? AppContext.BaseDirectory, "resources");
                    Directory.CreateDirectory(resources);
                    var copy = Path.Combine(resources, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                    File.Copy(path, copy); path = copy;
                }
                else if (!selection.FileSelected) EnsureBundledIconAvailable(path);
                Remember(); item.IconPath = path; item.IconColor = color; MarkDirty(); RefreshTree(item.Id);
            }
            catch (Exception ex) { args.Cancel = true; selection.ShowError(ex.Message); }
        };
        return selection;
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
        if (selected.Kind == MenuNodeKind.Action) DetailsTabIndex = 0;
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
        var copyDestination = AddDestination();
        draftState.Insert(copyDestination, copyDestination.Count, [clone]); MarkDirty(); RefreshTree(clone.Id);
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
    private int AddIndex(List<MenuNode> destination) =>
        selected?.Kind == MenuNodeKind.Group || (MenuTree.SelectedNode is not null && MenuTree.SelectedNode == MenuTree.RootNodes.FirstOrDefault())
            ? 0 : destination.Count;
    private void Add(MenuNode node)
    { var destination = AddDestination(); draftState.Insert(destination, AddIndex(destination), [node]); MarkDirty(); RefreshTree(node.Id); }
    private void AddGroupClicked(object sender, RoutedEventArgs args) => Add(new MenuNode { Name = L.Get("Editor_NewGroup_55c687"), Kind = MenuNodeKind.Group });
    private void AddSeparatorClicked(object sender, RoutedEventArgs args) => Add(new MenuNode { Kind = MenuNodeKind.Separator });
    private void AddSnippetClicked(object sender, RoutedEventArgs args) => Add(new MenuNode { Name = L.Get("Editor_NewSnippet_7f18a1"), Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.InsertText } });
    private void AddActionClicked(object sender, RoutedEventArgs args)
    {
        Add(new MenuNode { Name = L.Get("Editor_NewAction_991100"), Kind = MenuNodeKind.Action });
        DetailsTabIndex = 1;
    }

    private async void AddFolderClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var destination = AddDestination(); var index = AddIndex(destination);
        try { var folder = await picker.PickSingleFolderAsync(); if (folder is not null) ImportPaths([folder.Path], destination, index); }
        catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
    }
    private async void AddFileClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FileOpenPicker(); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in new[] { ".exe", ".bat", ".cmd", ".ps1", ".lnk" }) picker.FileTypeFilter.Add(ext);
        var destination = AddDestination(); var index = AddIndex(destination);
        try { var files = await picker.PickMultipleFilesAsync(); ImportPaths(files.Select(file => file.Path), destination, index); }
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
        var destination = AddDestination(); var index = AddIndex(destination);
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
                draftState.Insert(destination, index, [root]); MarkDirty(); RefreshTree(root.Id);
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
    internal void AddExternalText(string text, bool isUrl, double x, double y)
    {
        var (destination, index) = NativeDropDestination(x, y);
        if (index < 0) index = destination.Count;
        if (!isUrl) { AddDroppedText(text, destination, index); return; }
        var node = new MenuNode { Name = text, Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.OpenItem, Path = text } };
        draftState.Insert(destination, index, [node]); MarkDirty(); RefreshTree(node.Id);
    }

    internal void AddDroppedPaths(IReadOnlyList<string> paths, double x, double y)
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

    private static DropBounds BoundsInWindow(FrameworkElement element)
    {
        var origin = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
        return new(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    private IEnumerable<MenuDropRow> MenuDropRows()
    {
        foreach (var node in VisibleNodes(MenuTree.RootNodes))
        {
            if (MenuTree.ContainerFromNode(node) is not TreeViewItem container) continue;
            if (node.Content is not NodeDisplay display) continue;
            var model = display.Model;
            yield return new(model.Id, model.Kind == MenuNodeKind.Group, BoundsInWindow(container));
        }
    }

    private MenuDropHit MenuHit(double x, double y) =>
        DropGeometry.Menu(x, y, BoundsInWindow(MenuTree), MenuDropRows());

    private (List<MenuNode> Items, int Index)? RowDestination(MenuDropHit hit)
    {
        if (hit.TargetId is not { } id || FindSiblings(config.Items, id) is not { } siblings) return null;
        var index = siblings.FindIndex(node => node.Id == id);
        var target = siblings[index];
        return hit.Zone == MenuDropZone.Into
            ? (target.Children, target.Children.Count)
            : (siblings, index + (hit.Zone == MenuDropZone.After ? 1 : 0));
    }

    private (List<MenuNode>, int) NativeDropDestination(double x, double y) =>
        RowDestination(MenuHit(x, y)) ?? (config.Items, config.Items.Count);

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
    { var id = selected?.Id; if (!draftState.Undo()) return; ShowConfigurationTitle(activeConfigurationPath); MarkDirty(); RefreshTree(id); if (SettingsPage.Visibility == Visibility.Visible) RenderSettings(); }
    private void RedoClicked(object sender, RoutedEventArgs args)
    { var id = selected?.Id; if (!draftState.Redo()) return; ShowConfigurationTitle(activeConfigurationPath); MarkDirty(); RefreshTree(id); if (SettingsPage.Visibility == Visibility.Visible) RenderSettings(); }

    private async void ApplyClicked(object sender, RoutedEventArgs args) => await ApplyAsync();
    private async Task<bool> ApplyAsync(string? destinationPath = null)
    {
        if (activeShortcut is not null) await activeShortcut.StopAsync();
        if (!draftReady && !await RestoreDraftAsync()) return false;
        var original = baseSnapshot is null ? null : JsonSerializer.Deserialize<AppConfig>(baseSnapshot, JsonOptions);
        var saveCopy = false;
        if (!unsavedNewConfiguration && original is not null && !string.Equals(original.Name, config.Name, StringComparison.Ordinal))
        {
            var choice = await ConfirmAsync(T("Save renamed configuration", "Сохранить переименованную конфигурацию"),
                T("Overwrite the existing configuration or save a copy?", "Перезаписать существующую конфигурацию или сохранить копию?"),
                T("Overwrite", "Перезаписать"), T("Save copy", "Сохранить копию"));
            if (choice == ContentDialogResult.None) return false;
            saveCopy = choice == ContentDialogResult.Secondary;
        }
        var staged = await client.ReplaceDraftAsync(config);
        if (!staged.Success && staged.ErrorCode == "draft_not_started")
        {
            draftReady = false;
            if (!await RestoreDraftAsync()) return false;
            staged = await client.ReplaceDraftAsync(config);
        }
        if (!staged.Success) { ShowNotice(staged.Message ?? L.Get("Editor_CouldNotPrepareTheConfiguration_896ac2"), InfoBarSeverity.Error); return false; }
        if (saveCopy)
        {
            var copied = await client.SendAsync("saveAs", new JsonObject { ["name"] = config.Name });
            if (!copied.Success) { ShowNotice(copied.Message ?? T("Could not create a copy", "Не удалось создать копию"), InfoBarSeverity.Error); return false; }
            unsavedNewConfiguration = true;
            draftConfigurationPath = null;
        }
        var response = destinationPath is null ? await client.ApplyAsync() : await client.SaveAsAsync(destinationPath);
        ShowHotkeyAvailability(response);
        if (!response.Success) { ShowNotice(response.Message ?? L.Get("Editor_CouldNotApplyTheConfiguration_9f63ee"), InfoBarSeverity.Error); return false; }
        draftReady = false;
        var begin = await client.BeginAsync();
        var draft = EditorPipeClient.ReadDraft(begin);
        if (draft is not null) { UpdateConfigurationTitle(begin); draftState.Reset(draft.Config); draftReady = true; RefreshTree(selected?.Id); if (SettingsPage.Visibility == Visibility.Visible) RenderSettings(); }
        else draftState.SetBaseline();
        pendingActivation = false;
        unsavedNewConfiguration = false;
        UpdateApplyState(); UpdateConfigurationSelector(); Notice.IsOpen = false;
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
    private async Task<bool> ConfirmSwitchAsync()
    {
        if (!HasUnsavedChanges) return true;
        var answer = await ConfirmAsync(T("Unsaved changes", "Несохранённые изменения"),
            T("Apply changes before switching?", "Применить изменения перед переключением?"),
            L.Get("Editor_Apply_768af6"), L.Get("Editor_Discard_941d9e"));
        return answer == ContentDialogResult.Secondary || (answer == ContentDialogResult.Primary && await ApplyAsync());
    }

    private void OpenDraft(EditorResponse response, string? path = null, bool isNew = false)
    {
        var draft = EditorPipeClient.ReadDraft(response);
        if (draft is null) { ShowNotice(response.Message ?? T("Could not open configuration", "Не удалось открыть конфигурацию"), InfoBarSeverity.Error); return; }
        draftState.Reset(draft.Config);
        draftConfigurationPath = path;
        ShowConfigurationTitle(path);
        pendingActivation = true;
        unsavedNewConfiguration = isNew;
        draftReady = true;
        RefreshTree();
        UpdateApplyState();
        UpdateConfigurationSelector();
    }

    private void UpdateConfigurationSelector() => ConfigurationSelector.Content = config.Name;

    private async void ConfigurationsFlyoutOpening(object sender, object args)
    {
        ConfigurationsFlyout.Items.Clear();
        var loading = new MenuFlyoutItem { Text = T("Loading…", "Загрузка…"), IsEnabled = false };
        ConfigurationsFlyout.Items.Add(loading);
        var response = await client.SendAsync("listConfigs");
        ConfigurationsFlyout.Items.Clear();
        if (!response.Success)
        {
            ConfigurationsFlyout.Items.Add(new MenuFlyoutItem { Text = response.Message ?? "Error", IsEnabled = false });
            return;
        }
        if (response.Data?["configs"] is JsonArray entries) foreach (var entry in entries)
        {
            if (entry is null) continue;
            var name = entry["name"]?.GetValue<string>() ?? entry["Name"]?.GetValue<string>() ?? "?";
            var path = entry["path"]?.GetValue<string>() ?? entry["Path"]?.GetValue<string>() ?? "";
            var error = entry["error"]?.GetValue<string>() ?? entry["Error"]?.GetValue<string>();
            var active = entry["isActive"]?.GetValue<bool>() ?? entry["IsActive"]?.GetValue<bool>() ?? false;
            var label = name + (active ? T(" (active)", " (активна)") : "");
            var option = new ToggleMenuFlyoutItem { Text = error is null ? label : label + " — " + error,
                IsChecked = string.Equals(path, draftConfigurationPath, StringComparison.OrdinalIgnoreCase), IsEnabled = error is null };
            option.Click += async (_, _) =>
            {
                if (string.Equals(path, draftConfigurationPath, StringComparison.OrdinalIgnoreCase)) return;
                if (!await ConfirmSwitchAsync()) return;
                OpenDraft(await client.SendAsync("loadConfig", new JsonObject { ["path"] = path }), path);
            };
            ConfigurationsFlyout.Items.Add(option);
        }
        ConfigurationsFlyout.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = T("New configuration…", "Новая конфигурация…") };
        create.Click += async (_, _) =>
        {
            if (!await ConfirmSwitchAsync()) return;
            var name = await AskConfigurationNameAsync(T("New configuration", "Новая конфигурация"));
            if (name is not null) OpenDraft(await client.SendAsync("newConfig", new JsonObject { ["name"] = name }), isNew: true);
        };
        ConfigurationsFlyout.Items.Add(create);
    }

    private async Task<string?> AskConfigurationNameAsync(string title)
    {
        var input = new TextBox { Header = L.Get("Editor_Name_aee78f") };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = input,
            PrimaryButtonText = T("Create", "Создать"), CloseButtonText = L.Get("Editor_Cancel_0ec753") };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }

    private async void DeleteConfigurationClicked(object sender, RoutedEventArgs args)
    {
        if (!string.Equals(draftConfigurationPath, activeConfigurationPath, StringComparison.OrdinalIgnoreCase) || unsavedNewConfiguration) return;
        var requestedPath = activeConfigurationPath;
        if (await ConfirmAsync(T("Delete configuration", "Удалить конфигурацию"),
            T("Delete the active configuration?", "Удалить активную конфигурацию?"), L.Get("Editor_Delete_86ea33")) != ContentDialogResult.Primary) return;
        if (!await ConfirmSwitchAsync()) return;
        if (!string.Equals(requestedPath, activeConfigurationPath, StringComparison.OrdinalIgnoreCase))
        {
            ShowNotice(T("The active configuration changed; deletion was cancelled.", "Активная конфигурация изменилась; удаление отменено."), InfoBarSeverity.Warning);
            return;
        }
        var response = await client.SendAsync("deleteActiveConfig", new JsonObject { ["version"] = activeConfigurationVersion });
        var draft = EditorPipeClient.ReadDraft(response);
        if (draft is null) { ShowNotice(response.Message ?? T("Could not delete configuration", "Не удалось удалить конфигурацию"), InfoBarSeverity.Error); return; }
        UpdateConfigurationTitle(response);
        draftState.Reset(draft.Config);
        pendingActivation = false; unsavedNewConfiguration = false; draftReady = true;
        RefreshTree(); UpdateApplyState(); UpdateConfigurationSelector();
    }

    private async Task<ContentDialogResult> ConfirmAsync(string title, string message, string primary, string secondary = "")
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, PrimaryButtonText = primary, CloseButtonText = L.Get("Editor_Cancel_0ec753"), SecondaryButtonText = secondary, DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync();
    }

    private void MenuTreeDragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args) { if (args.Items.FirstOrDefault() is not TreeViewNode { Content: NodeDisplay }) { args.Cancel = true; return; } dragEscapeMonitor?.Dispose(); dragEscapeMonitor = new DragEscapeMonitor(); draftState.CommitPending(); dragSnapshot = Snapshot(); dragNodeId = args.Items.FirstOrDefault() is TreeViewNode node ? Model(node).Id : null; sender.CanReorderItems = false; dropIndicatorTimer.Start(); }
    internal bool InternalDragActive() => (dragSnapshot is not null && dragNodeId is not null) || (stepDragItem is not null && draggedStep is not null);
    private void UpdateDropIndicator()
    {
        DropIndicator.Visibility = Visibility.Collapsed;
        if (stepDragItem is not null && draggedStep is not null &&
            EditorOleDropTarget.TryGetCursorPosition(App.MainWindow, out var stepX, out var stepY))
            UpdateStepDropIndicator(stepX, stepY);
        else if (stepDropIndicator is not null) stepDropIndicator.Visibility = Visibility.Collapsed;
        if (dragNodeId is null || dragSnapshot is null || dragEscapeMonitor?.Cancelled == true || !EditorOleDropTarget.TryGetCursorPosition(App.MainWindow, out var x, out var y)) return;
        ShowMenuDropIndicator(dragNodeId, MenuHit(x, y));
    }

    private (List<MenuNode> Items, int Index)? InternalDestination(MenuDropHit hit) =>
        hit.Zone == MenuDropZone.Empty ? (config.Items, config.Items.Count) : RowDestination(hit);

    private void ShowMenuDropIndicator(string sourceId, MenuDropHit hit)
    {
        if (InternalDestination(hit) is not { } destination || !draftState.CanMove(sourceId, destination.Items)) return;
        var overlay = BoundsInWindow(DropOverlay);
        var anchor = hit.MarkerAnchor;
        var inside = hit.Zone == MenuDropZone.Into;
        DropIndicator.Background = inside ? null : dropAccent;
        DropIndicator.BorderBrush = dropAccent;
        DropIndicator.BorderThickness = new Thickness(inside ? 2 : 0);
        DropIndicator.Width = Math.Max(0, anchor.Width - (inside ? 4 : 8));
        DropIndicator.Height = inside ? Math.Max(0, anchor.Height - 4) : 3;
        Canvas.SetLeft(DropIndicator, anchor.X - overlay.X + (inside ? 2 : 4));
        var top = hit.Zone switch
        {
            MenuDropZone.Into => anchor.Y + 2,
            MenuDropZone.Before => anchor.Y - 1,
            MenuDropZone.After => anchor.Bottom - 1,
            _ => anchor.Y
        };
        Canvas.SetTop(DropIndicator, top - overlay.Y);
        DropIndicator.Visibility = Visibility.Visible;
    }

    internal bool CompleteInternalDrop(double x, double y)
    {
        if (stepDragItem is { } item && draggedStep is { } step && stepList is { } list)
            return CompleteStepDrop(item, step, list, x, y);
        if (dragNodeId is not { } id) return false;
        var hit = MenuHit(x, y);
        dragSnapshot = null;
        dragNodeId = null;
        if (dragEscapeMonitor?.Cancelled == true || !MoveToHit(id, hit)) return false;
        RefreshTree(id);
        return true;
    }

    private bool MoveToHit(string id, MenuDropHit hit)
    {
        if (InternalDestination(hit) is not { } destination || !draftState.Move(id, destination.Items, destination.Index))
            return false;
        MarkDirty();
        return true;
    }

    private void UpdateStepDropIndicator(double x, double y)
    {
        if (stepDropIndicator is not { } marker || stepList is not { } list) return;
        marker.Visibility = Visibility.Collapsed;
        if (!TryGetStepInsertion(list, x, y, out _, out var lineY)) return;
        marker.Width = Math.Max(0, list.ActualWidth - 8);
        Canvas.SetLeft(marker, 4);
        Canvas.SetTop(marker, Math.Clamp(lineY - 1.5, 0, Math.Max(0, list.ActualHeight - 3)));
        marker.Visibility = Visibility.Visible;
    }

    private static bool TryGetStepInsertion(ListView list, double x, double y, out int target, out double lineY)
    {
        var insertion = DropGeometry.Steps(x, y, BoundsInWindow(list), StepRows(list));
        target = insertion?.Index ?? 0;
        lineY = insertion?.LineY ?? 0;
        return insertion.HasValue;
    }

    private static IEnumerable<StepDropRow> StepRows(ListView list)
    {
        for (var index = 0; index < list.Items.Count; index++)
            if (list.Items[index] is FrameworkElement row && row.ActualHeight > 0)
                yield return new(index, BoundsInWindow(row));
    }

    private bool CompleteStepDrop(MenuNode item, MenuAction step, ListView list, double x, double y)
    {
        if (!TryGetStepInsertion(list, x, y, out var target, out _)) return false;
        var source = item.Steps.IndexOf(step);
        if (source < 0) return false;
        if (target > source) target--;
        target = Math.Clamp(target, 0, item.Steps.Count - 1);
        stepDropHandled = true;
        if (target != source)
        {
            item.Steps.RemoveAt(source); item.Steps.Insert(target, step); selectedStepIndex = target; MarkDirty();
        }
        return true;
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
                double dropX = 0, dropY = 0;
                var canDrop = !monitor.Cancelled && !EditorOleDropTarget.IsDragButtonDown()
                    && EditorOleDropTarget.TryGetCursorPosition(App.MainWindow, out dropX, out dropY);
                var hit = canDrop ? MenuHit(dropX, dropY) : default;
                draftState.Restore(dragSnapshot);
                if (canDrop) MoveToHit(id, hit);
                RefreshTree(id);
                return;
            }
            List<MenuNode> Read(IEnumerable<TreeViewNode> nodes) => nodes.Select(node =>
            {
                var model = Model(node);
                if (model.Kind != MenuNodeKind.Group && node.Children.Count > 0) throw new InvalidOperationException(L.Get("Editor_OnlyAGroupCanContain_04ef69"));
                model.Children = Read(node.Children); return model;
            }).ToList();
            config.Items = Read(MenuTree.RootNodes[0].Children);
            if (Snapshot() != dragSnapshot) { draftState.RecordChange(dragSnapshot); MarkDirty(); }
            var selectedId = sender.SelectedNode?.Content is NodeDisplay selectedDisplay ? selectedDisplay.Model.Id : null;
            DispatcherQueue.TryEnqueue(() => RefreshTree(selectedId));
        }
        catch (Exception ex) { draftState.Restore(dragSnapshot); RefreshTree(); ShowNotice(ex.Message, InfoBarSeverity.Warning); }
        finally { dragSnapshot = null; dragNodeId = null; dragEscapeMonitor?.Dispose(); dragEscapeMonitor = null; }
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
            CompleteInternalDrop(point.X, point.Y);
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
        var point = args.GetPosition(null);
        return RowDestination(MenuHit(point.X, point.Y)) ?? (config.Items, config.Items.Count);
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
    private static StackPanel CreateConfigurationDisplay(string name)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(panel, name);
        panel.Children.Add(new SymbolIcon(Symbol.Setting) { Width = 20, Height = 20 });
        panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    private sealed class NodeDisplay : StackPanel
    {
        public MenuNode Model { get; }
        private readonly TextBlock label;
        public NodeDisplay(MenuNode model)
        {
            Model = model; Orientation = Orientation.Horizontal; Spacing = 10;
            Children.Add(CreateItemIcon(model, 20));
            label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis }; Children.Add(label); Update();
        }
        public void Update()
        {
            label.Text = Model.Kind == MenuNodeKind.Separator ? "──────────" : string.IsNullOrWhiteSpace(Model.Name) ? L.Get("Editor_Unnamed_3f239e") : Model.Name;
            ToolTipService.SetToolTip(label, label.Text);
        }
        public override string ToString() => Model.Kind == MenuNodeKind.Separator ? "──────────" :
            (Model.Kind == MenuNodeKind.Group ? "📁 " : Model.Action?.Kind == ActionKind.InsertText ? "📋 " : "📄 ") +
            (string.IsNullOrWhiteSpace(Model.Name) ? L.Get("Editor_Unnamed_3f239e") : Model.Name);
    }
}



