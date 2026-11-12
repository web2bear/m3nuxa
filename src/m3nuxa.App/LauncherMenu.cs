using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace m3nuxa.App;

internal static class LauncherMenu
{
    public static MenuFlyout Create(AppSettings? settings = null)
    {
        var basis = (Style)Application.Current.Resources["LauncherPresenterStyle"];
        if (settings is null || settings.MenuBackground.Length == 0 || new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            return new MenuFlyout { MenuFlyoutPresenterStyle = basis };
        var style = new Style(typeof(MenuFlyoutPresenter)) { BasedOn = basis };
        style.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty, new SolidColorBrush(ParseColor(settings.MenuBackground))));
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ContrastTheme(settings.MenuBackground)));
        return new MenuFlyout { MenuFlyoutPresenterStyle = style };
    }

    internal static Windows.UI.Color ParseColor(string hex) => Windows.UI.Color.FromArgb(255,
        Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));

    internal static ElementTheme ContrastTheme(string hex)
    {
        var color = ParseColor(hex);
        static double Linear(byte channel) { var c = channel / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B) > 0.179 ? ElementTheme.Light : ElementTheme.Dark;
    }

    public static void Fill(IList<MenuFlyoutItemBase> target, IEnumerable<MenuNode> nodes, Action<MenuNode> execute, Action<MenuNode> edit, AppSettings settings)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == MenuNodeKind.Separator) { target.Add(new MenuFlyoutSeparator()); continue; }
            if (node.Kind == MenuNodeKind.Group)
            {
                var group = new MenuFlyoutSubItem { Text = node.Name, FontSize = settings.MenuFontSize };
                Configure(group, settings);
                group.RightTapped += (_, e) => { e.Handled = true; edit(node); };
                target.Add(group); Fill(group.Items, node.Children, execute, edit, settings); LoadIcon(group, node, settings);
            }
            else
            {
                var item = new MenuFlyoutItem { Text = node.Name, FontSize = settings.MenuFontSize, KeyboardAcceleratorTextOverride = node.Hotkey ?? "" };
                Configure(item, settings);
                item.Click += (_, _) => execute(node);
                item.RightTapped += (_, e) => { e.Handled = true; edit(node); };
                target.Add(item); LoadIcon(item, node, settings);
            }
        }
    }
    private static void Configure(MenuFlyoutItemBase item, AppSettings settings)
    {
        var vertical = settings.MenuDensity switch { "Compact" => 2, "Comfortable" => 9, _ => 4 };
        var size = IconSize(settings);
        item.Padding = new Thickness(11, vertical, 11, vertical + 1);
        item.MinHeight = Math.Max(size + vertical * 2 + 1, settings.MenuDensity switch { "Compact" => 24, "Comfortable" => 38, _ => 28 });
        item.Loaded += (_, _) =>
        {
            // The WinUI template wraps Icon in a fixed 16x16 Viewbox; sizing ImageIcon alone is scaled back down.
            foreach (var child in Descendants(item))
            {
                if (child is Viewbox { Name: "IconRoot" } box) box.Width = box.Height = size;
                else if (child is TextBlock { Name: "TextBlock" } text) text.Margin = new Thickness(size + 8, 0, 0, 0);
            }
        };
    }
    private static int IconSize(AppSettings settings) => settings.MenuIconSize switch { "Small" => 12, "Large" => 22, _ => 16 };
    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static async void LoadIcon(MenuFlyoutItemBase item, MenuNode node, AppSettings settings)
    {
        // The presenter determines its icon column when it opens.
        var size = IconSize(settings);
        var icon = new ImageIcon { Width = size, Height = size };
        if (item is MenuFlyoutItem action) action.Icon = icon;
        else if (item is MenuFlyoutSubItem group) group.Icon = icon;
        try
        {
            var bytes = await ItemIcon.PngAsync(node);
            if (bytes is null) return;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer()); stream.Seek(0);
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
            icon.Source = bitmap;
        }
        catch (Exception ex) { DevLog.Write("menu", "Icon failed", ex); }
    }
}
