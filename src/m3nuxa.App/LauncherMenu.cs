using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace m3nuxa.App;

internal static class LauncherMenu
{
    public static MenuFlyout Create() => new() { MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["LauncherPresenterStyle"] };

    public static void Fill(IList<MenuFlyoutItemBase> target, IEnumerable<MenuNode> nodes, Action<MenuNode> execute)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == MenuNodeKind.Separator) { target.Add(new MenuFlyoutSeparator()); continue; }
            if (node.Kind == MenuNodeKind.Group)
            {
                var group = new MenuFlyoutSubItem { Text = node.Name };
                target.Add(group); Fill(group.Items, node.Children, execute); LoadIcon(group, node);
            }
            else
            {
                var item = new MenuFlyoutItem { Text = node.Name, KeyboardAcceleratorTextOverride = node.Hotkey ?? "" };
                item.Click += (_, _) => execute(node);
                target.Add(item); LoadIcon(item, node);
            }
        }
    }
    private static async void LoadIcon(MenuFlyoutItemBase item, MenuNode node)
    {
        try
        {
            var bytes = await ItemIcon.PngAsync(node);
            if (bytes is null) return;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer()); stream.Seek(0);
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
            var icon = new ImageIcon { Source = bitmap, Width = (double)Application.Current.Resources["LauncherIconSize"], Height = (double)Application.Current.Resources["LauncherIconSize"] };
            if (item is MenuFlyoutItem action) action.Icon = icon;
            else if (item is MenuFlyoutSubItem group) group.Icon = icon;
        }
        catch (Exception ex) { DevLog.Write("menu", "Icon failed", ex); }
    }
}
