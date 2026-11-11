using Microsoft.UI.Windowing;
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
    private readonly Stack<MenuNode> path = new();
    private readonly GridView grid = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly HyperlinkButton home = new();
    private readonly HyperlinkButton back = new();
    private readonly TextBlock title = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        FontSize = 13, Opacity = .6, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = .65 };
    private bool activated;
    private PanelSize? resizedSize;
    private PointInt32? movedPosition;
    private readonly Grid dragRegion = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly SubclassProc frameCallback;

    private LauncherPanel(IEnumerable<MenuNode> nodes, Action<MenuNode> execute, Action openEditor, bool atCursor)
    {
        root = nodes.ToList(); this.execute = execute;
        Title = "m3nuxa";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = true; presenter.IsAlwaysOnTop = true;
        // The presenter can leave WS_DLGFRAME behind, adding a visible 3-pixel
        // non-client frame even when HasBorder is false. Keep the DWM outline.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        frameCallback = ProcessFrameMessage;
        if (!SetWindowSubclass(hwnd, frameCallback, 1, 0)) throw new System.ComponentModel.Win32Exception();
        var style = GetWindowLong(hwnd, -16);
        SetWindowLong(hwnd, -16, (style & ~0x00400000) | 0x00040000); // WS_DLGFRAME / WS_THICKFRAME
        SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0, 0x0037); // FRAMECHANGED, NOMOVE, NOSIZE, NOZORDER, NOACTIVATE
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
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(home, L.Language == "ru-RU" ? "Домой" : "Home");
        AutomationProperties.SetName(back, L.Language == "ru-RU" ? "На уровень вверх" : "Up one level");
        ConfigureNavigation(home, "\uE80F", L.Language == "ru-RU" ? "Домой" : "Home");
        ConfigureNavigation(back, "\uE72B", L.Language == "ru-RU" ? "Вверх" : "Up");
        ToolTipService.SetToolTip(home, L.Language == "ru-RU" ? "Домой" : "Home");
        ToolTipService.SetToolTip(back, L.Language == "ru-RU" ? "На уровень вверх" : "Up one level");
        home.Click += (_, _) => { path.Clear(); Refresh(); };
        back.Click += (_, _) => { if (path.Count > 0) path.Pop(); Refresh(); };
        header.Children.Add(home); header.Children.Add(back); Grid.SetColumn(back, 1);
        dragRegion.Children.Add(title);
        header.Children.Add(dragRegion); Grid.SetColumn(dragRegion, 2);
        var editor = new HyperlinkButton { HorizontalAlignment = HorizontalAlignment.Right };
        ConfigureNavigation(editor, "\uE70F", L.Language == "ru-RU" ? "Правка" : "Edit");
        AutomationProperties.SetName(editor, L.Get("TrayEditor"));
        editor.Click += (_, _) => { Close(); openEditor(); };
        header.Children.Add(editor); Grid.SetColumn(editor, 3);
        var close = new HyperlinkButton { Content = new FontIcon { Glyph = "\uE8BB", FontSize = 12 },
            Padding = new Thickness(4, 3, 4, 3), MinWidth = 0, MinHeight = 28 };
        AutomationProperties.SetName(close, L.Language == "ru-RU" ? "Закрыть" : "Close");
        ToolTipService.SetToolTip(close, L.Language == "ru-RU" ? "Закрыть" : "Close");
        close.Click += (_, _) => Close();
        header.Children.Add(close); Grid.SetColumn(close, 4); layout.Children.Add(header);
        var divider = new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 0, 0, 2), IsHitTestVisible = false };
        layout.Children.Add(divider); Grid.SetRow(divider, 1);
        grid.ItemsPanel = CreateItemsPanel();
        grid.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is not FrameworkElement { Tag: MenuNode node }) return;
            if (node.Kind == MenuNodeKind.Group) { path.Push(node); Refresh(); }
            else { Close(); execute(node); }
        };
        layout.Children.Add(grid); Grid.SetRow(grid, 2);
        layout.Children.Add(empty); Grid.SetRow(empty, 2);
        grid.Loaded += (_, _) => grid.Focus(FocusState.Programmatic);
        layout.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { e.Handled = true; Close(); } };
        var surface = new Grid();
        surface.Children.Add(layout);
        void AddResizeGrip(HorizontalAlignment horizontal, VerticalAlignment vertical, int direction)
        {
            var shape = direction switch
            {
                10 or 11 => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
                12 or 15 => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
                13 or 17 => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
                _ => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest
            };
            var grip = new ResizeGrip(shape) { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = horizontal, VerticalAlignment = vertical,
                Width = horizontal == HorizontalAlignment.Stretch ? double.NaN : 8,
                Height = vertical == VerticalAlignment.Stretch ? double.NaN : 8 };
            grip.PointerPressed += (_, e) => BeginWindowDrag(hwnd, direction, e);
            surface.Children.Add(grip);
        }
        AddResizeGrip(HorizontalAlignment.Stretch, VerticalAlignment.Top, 12);
        AddResizeGrip(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, 15);
        AddResizeGrip(HorizontalAlignment.Left, VerticalAlignment.Stretch, 10);
        AddResizeGrip(HorizontalAlignment.Right, VerticalAlignment.Stretch, 11);
        AddResizeGrip(HorizontalAlignment.Left, VerticalAlignment.Top, 13);
        AddResizeGrip(HorizontalAlignment.Right, VerticalAlignment.Top, 14);
        AddResizeGrip(HorizontalAlignment.Left, VerticalAlignment.Bottom, 16);
        AddResizeGrip(HorizontalAlignment.Right, VerticalAlignment.Bottom, 17);
        dragRegion.PointerPressed += (_, e) => BeginWindowDrag(hwnd, 2, e);
        Content = surface;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) activated = true;
            else if (activated) Close();
        };
        Closed += (_, _) => { SaveSize(); SavePosition(); if (active == this) active = null; };
        Refresh();
    }

    private sealed class ResizeGrip : UserControl
    {
        public ResizeGrip(Microsoft.UI.Input.InputSystemCursorShape shape)
        {
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(shape);
            Content = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        }
    }

    private static void BeginWindowDrag(nint hwnd, int direction, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        args.Handled = true;
        ReleaseCapture();
        GetCursorPos(out var point);
        var coordinates = (nint)((point.Y << 16) | (point.X & 0xffff));
        SendMessage(hwnd, 0x00A1, (nint)direction, coordinates); // WM_NCLBUTTONDOWN
    }

    private nint ProcessFrameMessage(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint reference)
    {
        // Use the full window for content while retaining native resize behavior.
        if (message == 0x0083) return 0; // WM_NCCALCSIZE
        if (message == 0x0084 && GetWindowRect(hwnd, out var rect)) // WM_NCHITTEST
        {
            var packed = lparam.ToInt64();
            var x = (short)(packed & 0xffff);
            var y = (short)((packed >> 16) & 0xffff);
            var grip = Math.Max(1, (int)Math.Round(8 * GetDpiForWindow(hwnd) / 96.0));
            var left = x < rect.Left + grip; var right = x >= rect.Right - grip;
            var top = y < rect.Top + grip; var bottom = y >= rect.Bottom - grip;
            if (top && left) return 13; if (top && right) return 14;
            if (bottom && left) return 16; if (bottom && right) return 17;
            if (left) return 10; if (right) return 11;
            if (top) return 12; if (bottom) return 15;
            if (Content is UIElement content && dragRegion.XamlRoot is not null && dragRegion.ActualWidth > 0)
            {
                var origin = dragRegion.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point());
                var scale = GetDpiForWindow(hwnd) / 96.0;
                var cx = (x - rect.Left) / scale; var cy = (y - rect.Top) / scale;
                if (cx >= origin.X && cx < origin.X + dragRegion.ActualWidth &&
                    cy >= origin.Y && cy < origin.Y + dragRegion.ActualHeight) return 2; // HTCAPTION
            }
        }
        return DefSubclassProc(hwnd, message, wparam, lparam);
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

    private void Refresh()
    {
        home.IsEnabled = back.IsEnabled = path.Count > 0;
        title.Text = path.Count == 0 ? "" : path.Peek().Name;
        grid.Items.Clear();
        empty.Text = L.Language == "ru-RU" ? "Здесь пока нет пунктов" : "No items here yet";
        foreach (var node in (path.Count == 0 ? root : path.Peek().Children).Where(n => n.Kind != MenuNodeKind.Separator))
        {
            var icon = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = node.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.WrapWholeWords,
                MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Stretch };
            var body = new StackPanel { Width = 92, Spacing = 3, Padding = new Thickness(4, 4, 4, 4), Tag = node };
            body.Children.Add(icon); body.Children.Add(label);
            var item = new GridViewItem { Content = body, Tag = node, Width = 104, MinHeight = 88, Padding = new Thickness(0) };
            AutomationProperties.SetName(item, node.Name + (node.Kind == MenuNodeKind.Group ? (L.Language == "ru-RU" ? ", группа" : ", group") : ""));
            var detail = node.Kind == MenuNodeKind.Group
                ? L.Format("PanelGroupDetails", node.Children.Count(child => child.Kind != MenuNodeKind.Separator))
                : L.Format("PanelActionDetails", node.Steps.Count);
            if (node.Kind == MenuNodeKind.Action && node.Steps.Count == 1 && !string.IsNullOrWhiteSpace(node.Steps[0].Path))
                detail += "\n" + L.Format("PanelPath", node.Steps[0].Path);
            var hint = node.Name + "\n" + detail;
            ToolTipService.SetToolTip(item, new ToolTip { Content = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } });
            AutomationProperties.SetHelpText(item, detail);
            grid.Items.Add(item); LoadIcon(icon, node);
        }
        empty.Visibility = grid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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

    public static void Show(IEnumerable<MenuNode> nodes, Action<MenuNode> execute, Action openEditor, bool atCursor)
    {
        active?.Close(); active = new LauncherPanel(nodes, execute, openEditor, atCursor); active.Activate();
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint reference);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
