using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Reflection;
namespace m3nuxa.App;
public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ScreenPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint WindowFromPoint(ScreenPoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var root = Path.Combine(Path.GetTempPath(), "m3nuxa-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TrayApplication? tray = null;
        try
        {
            var config = new ConfigStore(Path.Combine(root, "config.json"));
            tray = new TrayApplication(config);
            await CheckSettingsAsync(tray, config);
            var displays = Microsoft.UI.Windowing.DisplayArea.FindAll();
            var testArea = displays[0].WorkArea;
            for (var i = 0; i < displays.Count; i++) if (!displays[i].IsPrimary) { testArea = displays[i].WorkArea; break; }
            SetCursorPos(testArea.X + 160, testArea.Y + 160);
            var iconPath = Path.Combine(root, "icon.png");
            using (var bitmap = new System.Drawing.Bitmap(24, 24)) { using var graphics = System.Drawing.Graphics.FromImage(bitmap); graphics.Clear(System.Drawing.Color.Magenta); bitmap.Save(iconPath); }
            var group = new MenuNode { Name = "Тестовая группа", IconPath = iconPath, Kind = MenuNodeKind.Group, Children = [new MenuNode { Name = "Тестовое действие", IconPath = iconPath, Kind = MenuNodeKind.Action }] };
            var rootAction = new MenuNode { Name = "Действие верхнего уровня", IconPath = iconPath, Kind = MenuNodeKind.Action };
            typeof(TrayApplication).GetMethod("ShowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, [new List<MenuNode> { group, rootAction }]);
            await Task.Delay(500);
            var window = (Window)typeof(TrayApplication).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;

            await Task.Delay(300);
            var flyout = (MenuFlyout)typeof(TrayApplication).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
            if (flyout.Items.Count != 2 || flyout.Items[0] is not MenuFlyoutSubItem sub || sub.Items.Count != 1) throw new Exception("Menu hierarchy lost");
            var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)window.Content).XamlRoot);
            if (popups.Count == 0) throw new Exception("Menu popup did not open");
            CheckIcon(sub.Icon, "Root group");
            CheckIcon(((MenuFlyoutItem)flyout.Items[1]).Icon, "Root action");
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(sub);
            var expand = (Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse);
            expand.Expand();
            await Task.Delay(500);
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)window.Content).XamlRoot).Count < 2) throw new Exception("Nested menu did not open");
            CheckIcon(((MenuFlyoutItem)sub.Items[0]).Icon, "Nested action");
            await Task.Delay(3000);
            flyout.Hide();
            await Task.Delay(100);
            if (window.AppWindow.IsVisible) throw new Exception("Menu host did not hide");
            typeof(TrayApplication).GetMethod("ShowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, [new List<MenuNode>()]);
            await Task.Delay(300);
            flyout = (MenuFlyout)typeof(TrayApplication).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
            if (flyout.Items[0] is not MenuFlyoutItem { IsEnabled: false }) throw new Exception("Empty menu placeholder lost");
            flyout.Hide();
            var service = (MenuFlyout)typeof(TrayApplication).GetMethod("BuildServiceMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, null)!;
            typeof(TrayApplication).GetMethod("ShowFlyout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, [service]);
            await Task.Delay(500);
            // A mouse invocation can put ordinary items in NarrowPadding while toggles
            // still use DefaultPadding; labels must stay in the same column.
            VisualStateManager.GoToState((Control)service.Items[0], "NarrowPadding", false);
            VisualStateManager.GoToState((Control)service.Items[1], "NarrowPadding", false);
            window.Content.UpdateLayout();
            var labelPositions = service.Items.OfType<Control>().Where(item => item is not MenuFlyoutSeparator)
                .Select(item => Descendants(item).OfType<TextBlock>().First(text => text.Name == "TextBlock")
                    .TransformToVisual(null).TransformPoint(new Windows.Foundation.Point()).X).ToArray();
            if (labelPositions.Max() - labelPositions.Min() > 1)
                throw new Exception("Service labels are misaligned: " + string.Join(", ", labelPositions));
            foreach (var item in service.Items.OfType<Control>())
            {
                if (item is MenuFlyoutSeparator) continue;
                if (item.FontSize != 12 || item.Padding.Top != 4 || item.ActualHeight > 32)
                    throw new Exception($"Service menu density: {item.GetType().Name}, font={item.FontSize}, padding={item.Padding}, height={item.ActualHeight}");
            }
            service.Hide();
            // Model the topmost tray overflow panel near the monitor's bottom edge.
            var overflow = new Window { Title = "Test tray overflow", Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.DimGray) } };
            var overflowPresenter = (Microsoft.UI.Windowing.OverlappedPresenter)overflow.AppWindow.Presenter;
            overflowPresenter.SetBorderAndTitleBar(false, false);
            overflowPresenter.IsAlwaysOnTop = true;
            overflow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(testArea.X + 80, testArea.Y + testArea.Height - 160, 400, 140));
            overflow.Activate();
            await Task.Delay(300);
            service = (MenuFlyout)typeof(TrayApplication).GetMethod("BuildServiceMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, null)!;
            SetCursorPos(testArea.X + 160, testArea.Y + testArea.Height - 70);
            typeof(TrayApplication).GetMethod("ShowFlyout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, [service]);
            await Task.Delay(500);
            var exitItem = (FrameworkElement)service.Items.Last();
            var exitBounds = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(exitItem).GetBoundingRectangle();
            var exitPoint = new ScreenPoint { X = window.AppWindow.Position.X + (int)(exitBounds.X + exitBounds.Width / 2), Y = window.AppWindow.Position.Y + (int)(exitBounds.Y + exitBounds.Height / 2) };
            var hit = WindowFromPoint(exitPoint);
            var overflowHandle = WinRT.Interop.WindowNative.GetWindowHandle(overflow);
            if (hit == overflowHandle || IsChild(overflowHandle, hit)) throw new Exception("Exit is covered by the tray overflow panel");
            if (exitPoint.Y < testArea.Y || exitPoint.Y >= testArea.Y + testArea.Height) throw new Exception($"Exit is outside the work area: bounds={exitBounds}, work={testArea}, owner={window.AppWindow.Position}, scale={exitItem.XamlRoot.RasterizationScale}, hit={hit}, overlay={overflowHandle}");
            var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)window.Content).XamlRoot).Last().Child as FrameworkElement
                ?? throw new Exception("Service menu presenter missing");
            var origin = presenter.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
            using (var screenshot = new System.Drawing.Bitmap((int)Math.Ceiling(presenter.ActualWidth), (int)Math.Ceiling(presenter.ActualHeight)))
            {
                var scale = presenter.XamlRoot.RasterizationScale;
                var position = window.AppWindow.Position;
                using var graphics = System.Drawing.Graphics.FromImage(screenshot);
                graphics.CopyFromScreen(position.X + (int)(origin.X * scale), position.Y + (int)(origin.Y * scale), 0, 0, screenshot.Size);
                screenshot.Save(Path.Combine(AppContext.BaseDirectory, "service-menu.png"));
            }
            service.Hide();
            overflow.Close();
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "result.txt"), "PASS: settings immediate registration, occupied-key rollback, independent menu drafts, singleton settings window, recording pause/cancel, nested font size; visible icons in root group, root action and nested action; WinUI menu popup, nested hierarchy, hide/reopen, empty menu; compact aligned service items and toggles; Exit above topmost overflow at bottom edge; isolated configuration.");
            tray.Dispose(); Directory.Delete(root, true); Environment.Exit(0);
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "result.txt"), "FAIL: " + ex); tray?.Dispose(); Directory.Delete(root, true); Environment.Exit(1); }
        finally { tray?.Dispose(); Directory.Delete(root, true); }
    }
    private static void CheckIcon(IconElement? candidate, string name)
    {
        if (candidate is not ImageIcon { Source: Microsoft.UI.Xaml.Media.Imaging.BitmapImage { PixelWidth: > 0 } } icon) throw new Exception(name + " image not loaded");
        for (DependencyObject? visual = icon; visual is not null; visual = VisualTreeHelper.GetParent(visual))
            if (visual is FrameworkElement element && (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0))
                throw new Exception($"{name} icon is hidden by {element.GetType().Name} {element.Name}: {element.Visibility} {element.ActualWidth}x{element.ActualHeight}");
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

}
