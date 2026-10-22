using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Reflection;
namespace m3nuxa.App;
public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
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
            var displays = Microsoft.UI.Windowing.DisplayArea.FindAll();
            for (var i = 0; i < displays.Count; i++) if (!displays[i].IsPrimary) { SetCursorPos(displays[i].WorkArea.X + 160, displays[i].WorkArea.Y + 160); break; }
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
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "result.txt"), "PASS: visible icons in root group, root action and nested action; WinUI menu popup, nested hierarchy, hide/reopen, empty menu; isolated configuration.");
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

}
