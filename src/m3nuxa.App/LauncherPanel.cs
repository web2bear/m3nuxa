using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.System;

namespace m3nuxa.App;

// A fresh window for each invocation keeps navigation and invocation context independent.
internal sealed class LauncherPanel : Window
{
    private static LauncherPanel? active;
    private readonly IReadOnlyList<MenuNode> root;
    private readonly Action<MenuNode> execute;
    private readonly AppSettings settings;
    private readonly Stack<MenuNode> path = new();
    private readonly GridView grid = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly HyperlinkButton back = new();
    private readonly HyperlinkButton forward = new();
    private readonly BreadcrumbBar breadcrumbs = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly List<MenuNode[]> history = new() { Array.Empty<MenuNode>() };
    private int historyIndex;
    private readonly TextBlock empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = .65 };
    private bool activated;
    private PanelSize? resizedSize;
    private PointInt32? movedPosition;
    private readonly Grid header = new() { ColumnSpacing = 6 };
    private readonly TextBlock itemDetails = new() { FontSize = 12, Opacity = .68, MaxLines = 1,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private HyperlinkButton close = null!;
    private TextBlock? hoveredCrumb;
    private (GridViewItem Item, string Hint)? hoveredItem;
    private (GridViewItem Item, string Hint)? focusedItem;

    private LauncherPanel(IEnumerable<MenuNode> nodes, Action<MenuNode> execute, Action openEditor, AppSettings settings)
    {
        root = nodes.ToList(); this.execute = execute; this.settings = settings;
        var atCursor = settings.PanelPosition == "Cursor";
        Title = "m3nuxa";
        AppWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false); presenter.IsResizable = true; presenter.IsAlwaysOnTop = true;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var corner = 2; DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 33, ref corner, sizeof(int));
        GetCursorPos(out var cursor);
        var savedPosition = atCursor ? null : LoadPosition();
        var (savedWidth, savedHeight) = LoadSize();
        long Overlap(DisplayArea area, PointInt32 saved)
        {
            var bounds = area.WorkArea;
            var handle = MonitorFromPoint(new Point { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 }, 2);
            var factor = GetDpiForMonitor(handle, 0, out var monitorDpi, out _) == 0 ? monitorDpi / 96.0 : 1.0;
            var right = (long)saved.X + (long)Math.Round(savedWidth * factor);
            var bottom = (long)saved.Y + (long)Math.Round(savedHeight * factor);
            return Math.Max(0L, Math.Min(right, (long)bounds.X + bounds.Width) - Math.Max(saved.X, bounds.X)) *
                Math.Max(0L, Math.Min(bottom, (long)bounds.Y + bounds.Height) - Math.Max(saved.Y, bounds.Y));
        }
        DisplayArea? positionDisplay = null;
        if (savedPosition is { } saved)
        {
            var displays = DisplayArea.FindAll();
            long bestOverlap = 0;
            for (var i = 0; i < displays.Count; i++)
            {
                var overlap = Overlap(displays[i], saved);
                if (overlap > bestOverlap) { bestOverlap = overlap; positionDisplay = displays[i]; }
            }
        }
        var display = positionDisplay ?? DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var monitorPoint = positionDisplay is null ? cursor : new Point { X = work.X + work.Width / 2, Y = work.Y + work.Height / 2 };
        // Move first so Windows has switched the window to the target monitor's DPI before sizing it.
        AppWindow.Move(new PointInt32(monitorPoint.X, monitorPoint.Y));
        var monitor = MonitorFromPoint(monitorPoint, 2);
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var width = Math.Min((int)Math.Round(savedWidth * scale), work.Width); var height = Math.Min((int)Math.Round(savedHeight * scale), work.Height);
        var x = positionDisplay is not null ? savedPosition!.Value.X : atCursor ? cursor.X : work.X + (work.Width - width) / 2;
        var y = positionDisplay is not null ? savedPosition!.Value.Y : atCursor ? cursor.Y : work.Y + (work.Height - height) / 2;
        x = Math.Clamp(x, work.X, work.X + work.Width - width);
        y = Math.Clamp(y, work.Y, work.Y + work.Height - height);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange) movedPosition = AppWindow.Position;
            if (!args.DidSizeChange) return;
            var targetMonitor = MonitorFromWindow(hwnd, 2);
            var windowScale = GetDpiForMonitor(targetMonitor, 0, out var windowDpi, out var ignoredDpi) == 0 ? windowDpi / 96.0 : 1.0;
            var actual = AppWindow.Size;
            var minimumWidth = (int)Math.Round(320 * windowScale);
            var minimumHeight = (int)Math.Round(240 * windowScale);
            if (actual.Width < minimumWidth || actual.Height < minimumHeight)
            {
                AppWindow.Resize(new SizeInt32(Math.Max(actual.Width, minimumWidth), Math.Max(actual.Height, minimumHeight)));
                return;
            }
            resizedSize = new PanelSize(actual.Width / windowScale, actual.Height / windowScale);
        };
        var layout = new Grid { Padding = new Thickness(20, 16, 20, 16), RowSpacing = 8 };
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            var backdrop = new WinUIEx.TransparentTintBackdrop();
            SystemBackdrop = backdrop;
            layout.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (settings.PanelBackground.Length > 0) layout.RequestedTheme = LauncherMenu.ContrastTheme(settings.PanelBackground);
            void UpdateBackdrop()
            {
                var color = settings.PanelBackground.Length > 0 ? LauncherMenu.ParseColor(settings.PanelBackground) :
                    layout.ActualTheme == ElementTheme.Light ? Windows.UI.Color.FromArgb(255, 245, 245, 245) : Windows.UI.Color.FromArgb(255, 32, 32, 32);
                color.A = (byte)Math.Round(settings.PanelOpacity * 255 / 100.0);
                backdrop.TintColor = color;
            }
            layout.Loaded += (_, _) => UpdateBackdrop();
            layout.ActualThemeChanged += (_, _) => UpdateBackdrop();
            UpdateBackdrop();
        }
        else SystemBackdrop = new DesktopAcrylicBackdrop();
        void UpdateBorder()
        {
            // Let Windows choose its accessible border in high contrast mode.
            var border = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? -1 :
                layout.ActualTheme == ElementTheme.Dark ? 0x454545 : 0xC8C8C8;
            DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 34, ref border, sizeof(int));
        }
        layout.Loaded += (_, _) => UpdateBorder();
        layout.ActualThemeChanged += (_, _) => UpdateBorder();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(back, L.Language == "ru-RU" ? "Назад" : "Back");
        AutomationProperties.SetName(forward, L.Language == "ru-RU" ? "Вперёд" : "Forward");
        ConfigureArrow(back, "\uE76B", L.Language == "ru-RU" ? "Назад" : "Back");
        ConfigureArrow(forward, "\uE76C", L.Language == "ru-RU" ? "Вперёд" : "Forward");
        back.Click += (_, _) => MoveHistory(-1);
        forward.Click += (_, _) => MoveHistory(1);
        breadcrumbs.ItemClicked += (_, e) => Navigate(path.Reverse().Take(e.Index).ToArray());
        header.Children.Add(back); header.Children.Add(forward); Grid.SetColumn(forward, 1);
        header.Children.Add(breadcrumbs); Grid.SetColumn(breadcrumbs, 2);
        var editor = new HyperlinkButton { HorizontalAlignment = HorizontalAlignment.Right };
        ConfigureNavigation(editor, "\uE70F", L.Language == "ru-RU" ? "Правка" : "Edit");
        AutomationProperties.SetName(editor, L.Get("TrayEditor"));
        editor.Click += (_, _) => { Close(); openEditor(); };
        close = new HyperlinkButton { Content = new FontIcon { Glyph = "\uE8BB", FontSize = 12 },
            Padding = new Thickness(4, 3, 4, 3), MinWidth = 0, MinHeight = 28 };
        AutomationProperties.SetName(close, L.Language == "ru-RU" ? "Закрыть" : "Close");
        ToolTipService.SetToolTip(close, L.Language == "ru-RU" ? "Закрыть" : "Close");
        close.Click += (_, _) => Close();
        header.Children.Add(close); Grid.SetColumn(close, 3); layout.Children.Add(header);
        breadcrumbs.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnBreadcrumbPointerMoved), true);
        breadcrumbs.PointerExited += (_, _) => ClearBreadcrumbHover();
        var divider = new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 0, 0, 2), IsHitTestVisible = false };
        layout.Children.Add(divider); Grid.SetRow(divider, 1);
        grid.ItemsPanel = CreateItemsPanel();
        grid.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is not FrameworkElement { Tag: MenuNode node }) return;
            if (node.Kind == MenuNodeKind.Group) Navigate(path.Reverse().Append(node).ToArray());
            else { Close(); execute(node); }
        };
        layout.Children.Add(grid); Grid.SetRow(grid, 2);
        layout.Children.Add(empty); Grid.SetRow(empty, 2);
        var footer = new Grid { ColumnSpacing = 12, MinHeight = 28 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(itemDetails);
        footer.Children.Add(editor); Grid.SetColumn(editor, 1);
        layout.Children.Add(footer); Grid.SetRow(footer, 3);
        grid.Loaded += (_, _) => grid.Focus(FocusState.Programmatic);
        layout.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { e.Handled = true; Close(); } };
        var surface = new Grid();
        surface.Children.Add(layout);
        void UpdateTitleBarRegions()
        {
            if (header.XamlRoot is null || header.ActualHeight <= 0) return;
            var scale = header.XamlRoot.RasterizationScale;
            RectInt32 Bounds(FrameworkElement element)
            {
                var origin = element.TransformToVisual(surface).TransformPoint(new Windows.Foundation.Point());
                return new RectInt32((int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale),
                    (int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale));
            }
            var headerBounds = Bounds(header);
            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Caption, new[] { new RectInt32(0, 0, AppWindow.Size.Width, headerBounds.Y + headerBounds.Height) });
            source.SetRegionRects(NonClientRegionKind.Passthrough, new[] { Bounds(back), Bounds(forward), Bounds(breadcrumbs), Bounds(close) });
        }
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateTitleBarRegions();
        header.Loaded += (_, _) =>
        {
            header.XamlRoot.Changed += OnRootChanged;
            UpdateTitleBarRegions();
        };
        Closed += (_, _) => { if (header.XamlRoot is not null) header.XamlRoot.Changed -= OnRootChanged; };
        header.SizeChanged += (_, _) => UpdateTitleBarRegions();
        breadcrumbs.SizeChanged += (_, _) => UpdateTitleBarRegions();
        Content = surface;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) activated = true;
            else if (activated) Close();
        };
        Closed += (_, _) => { SaveSize(); SavePosition(); if (active == this) active = null; };
        Refresh();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private void OnBreadcrumbPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        while (source is not null && source is not BreadcrumbBarItem && source != breadcrumbs)
            source = VisualTreeHelper.GetParent(source);
        var label = source is BreadcrumbBarItem item ? Descendants(item).OfType<TextBlock>().FirstOrDefault() : null;
        if (label == hoveredCrumb) return;
        ClearBreadcrumbHover();
        if (label is not null)
        {
            hoveredCrumb = label;
            label.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        }
    }

    private void ClearBreadcrumbHover()
    {
        if (hoveredCrumb is not null) hoveredCrumb.TextDecorations = Windows.UI.Text.TextDecorations.None;
        hoveredCrumb = null;
    }

    private static string PositionFile => Path.Combine(AppContext.BaseDirectory, "panel-position.json");
    private sealed record PanelPosition(int X, int Y);

    private static PointInt32? LoadPosition()
    {
        try
        {
            if (File.Exists(PositionFile) && JsonSerializer.Deserialize<PanelPosition>(File.ReadAllText(PositionFile)) is { } position)
                return new PointInt32(position.X, position.Y);
        }
        catch (Exception ex) { DevLog.Write("panel", "Could not load panel position", ex); }
        return null;
    }

    private void SavePosition()
    {
        try
        {
            if (movedPosition is { } position)
                File.WriteAllText(PositionFile, JsonSerializer.Serialize(new PanelPosition(position.X, position.Y)));
        }
        catch (Exception ex) { DevLog.Write("panel", "Could not save panel position", ex); }
    }

    private static string SizeFile => Path.Combine(AppContext.BaseDirectory, "panel-size.json");
    private sealed record PanelSize(double Width, double Height);

    private static (double Width, double Height) LoadSize()
    {
        try
        {
            if (File.Exists(SizeFile) && JsonSerializer.Deserialize<PanelSize>(File.ReadAllText(SizeFile)) is { } size &&
                double.IsFinite(size.Width) && double.IsFinite(size.Height))
                return (Math.Clamp(size.Width, 320, 3000), Math.Clamp(size.Height, 240, 3000));
        }
        catch (Exception ex) { DevLog.Write("panel", "Could not load panel size", ex); }
        return (660, 620);
    }

    private void SaveSize()
    {
        try
        {
            if (resizedSize is not null)
                File.WriteAllText(SizeFile, JsonSerializer.Serialize(resizedSize));
        }
        catch (Exception ex) { DevLog.Write("panel", "Could not save panel size", ex); }
    }

    private static ItemsPanelTemplate CreateItemsPanel() => (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' /></ItemsPanelTemplate>");

    private static void ConfigureNavigation(HyperlinkButton button, string glyph, string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        button.Padding = new Thickness(4, 3, 4, 3);
        button.MinWidth = 0;
        button.MinHeight = 28;
    }

    private static void ConfigureArrow(HyperlinkButton button, string glyph, string tooltip)
    {
        button.Content = new FontIcon { Glyph = glyph, FontSize = 12 };
        button.Padding = new Thickness(4, 3, 4, 3);
        button.MinWidth = 0;
        button.MinHeight = 28;
        ToolTipService.SetToolTip(button, tooltip);
    }

    private void Navigate(MenuNode[] destination)
    {
        if (path.Reverse().SequenceEqual(destination)) return;
        history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        history.Add(destination);
        historyIndex++;
        SetPath(destination);
    }

    private void MoveHistory(int offset)
    {
        var target = historyIndex + offset;
        if (target < 0 || target >= history.Count) return;
        historyIndex = target;
        SetPath(history[historyIndex]);
    }

    private void SetPath(IEnumerable<MenuNode> destination)
    {
        path.Clear();
        foreach (var node in destination) path.Push(node);
        Refresh();
    }

    private void Refresh()
    {
        back.IsEnabled = historyIndex > 0;
        forward.IsEnabled = historyIndex + 1 < history.Count;
        breadcrumbs.ItemsSource = new[] { L.Language == "ru-RU" ? "Главная" : "Home" }
            .Concat(path.Reverse().Select(node => node.Name)).ToArray();
        ClearBreadcrumbHover();
        hoveredItem = focusedItem = null;
        itemDetails.Text = "";
        grid.Items.Clear();
        empty.Text = L.Language == "ru-RU" ? "Здесь пока нет пунктов" : "No items here yet";
        foreach (var node in (path.Count == 0 ? root : path.Peek().Children).Where(n => n.Kind != MenuNodeKind.Separator))
        {
            var iconSize = settings.PanelIconSize switch { "Small" => 32, "Large" => 52, _ => 40 };
            FrameworkElement icon = node.Kind == MenuNodeKind.Group ? CreateGroupPreview(node, iconSize) : CreateActionIcon(node, iconSize);
            var label = new TextBlock { Text = node.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.WrapWholeWords,
                MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = settings.PanelFontSize };
            var body = new StackPanel { Width = 92, Spacing = 3, Padding = new Thickness(4, 4, 4, 4), Tag = node };
            body.Children.Add(icon); body.Children.Add(label);
            var item = new GridViewItem { Content = body, Tag = node, Width = 104, MinHeight = 88,
                Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(item, node.Name + (node.Kind == MenuNodeKind.Group ? (L.Language == "ru-RU" ? ", группа" : ", group") : ""));
            var detail = L.Format("PanelGroupDetails", node.Children.Count(child => child.Kind != MenuNodeKind.Separator));
            var firstStep = node.Steps.FirstOrDefault();
            var stepName = firstStep is null ? "" : !string.IsNullOrWhiteSpace(firstStep.Name)
                ? firstStep.Name : L.Get("WorkflowKind" + firstStep.Kind);
            stepName = string.Join(" ", stepName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var count = L.Format("PanelStepsCount", node.Steps.Count);
            var hint = node.Kind == MenuNodeKind.Group ? detail : (stepName.Length > 0 ? stepName + " · " + count : count);
            AutomationProperties.SetHelpText(item, node.Kind == MenuNodeKind.Group ? detail : hint);
            item.PointerEntered += (_, _) => { hoveredItem = (item, hint); UpdateItemDetails(); };
            item.PointerExited += (_, _) => { if (hoveredItem?.Item == item) hoveredItem = null; UpdateItemDetails(); };
            item.GotFocus += (_, _) => { focusedItem = (item, hint); UpdateItemDetails(); };
            item.LostFocus += (_, _) => { if (focusedItem?.Item == item) focusedItem = null; UpdateItemDetails(); };
            grid.Items.Add(item);
        }
        empty.Visibility = grid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Image CreateActionIcon(MenuNode node, int size)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        LoadIcon(image, node);
        return image;
    }

    private static Border CreateGroupPreview(MenuNode group, int size)
    {
        var icons = new Grid { RowSpacing = 2, ColumnSpacing = 2, Padding = new Thickness(3) };
        icons.RowDefinitions.Add(new RowDefinition()); icons.RowDefinitions.Add(new RowDefinition());
        icons.ColumnDefinitions.Add(new ColumnDefinition()); icons.ColumnDefinitions.Add(new ColumnDefinition());
        var children = group.Children.Where(child => child.Kind != MenuNodeKind.Separator).Take(4).ToArray();
        for (var i = 0; i < children.Length; i++)
        {
            var image = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
            Grid.SetRow(image, i / 2); Grid.SetColumn(image, i % 2);
            icons.Children.Add(image);
            LoadIcon(image, children[i]);
        }
        return new Border
        {
            Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"], Child = icons
        };
    }

    private void UpdateItemDetails() => itemDetails.Text = hoveredItem?.Hint ?? focusedItem?.Hint ?? "";

    private static async void LoadIcon(Image image, MenuNode node)
    {
        try
        {
            var bytes = await ItemIcon.PngAsync(node); if (bytes is null) return;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer()); stream.Seek(0);
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); image.Source = bitmap;
        }
        catch (Exception ex) { DevLog.Write("panel", "Icon failed", ex); }
    }

    public static void Show(IEnumerable<MenuNode> nodes, Action<MenuNode> execute, Action openEditor, AppSettings settings)
    {
        active?.Close(); active = new LauncherPanel(nodes, execute, openEditor, settings); active.Activate();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
