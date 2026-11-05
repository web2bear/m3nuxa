using m3nuxa.App;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage
{
    internal async Task CheckPropertyPanelLayoutAsync()
    {
        var group = new MenuNode { Name = "Group", Kind = MenuNodeKind.Group };
        var action = new MenuNode { Name = "Image", Kind = MenuNodeKind.Action, IconPath = @"C:\Pictures\" + new string('x', 130) + ".jpg", Steps = [new MenuAction { Kind = ActionKind.OpenItem, Path = @"C:\Pictures\" + new string('x', 130) + ".jpg" }] };
        config = new AppConfig { Items = [group, action] };
        var originalSize = App.MainWindow.AppWindow.Size;
        try
        {
            foreach (var width in new[] { 1160, 900 })
            {
                App.MainWindow.AppWindow.Resize(new SizeInt32(width, 780));
                double? left = null;
                foreach (var node in new[] { group, action })
                {
                    RefreshTree(node.Id);
                    await Task.Delay(200);
                    DetailsPanel.UpdateLayout();
                    foreach (var section in PropertiesPanel.Children.OfType<Border>())
                    {
                        var rect = section.TransformToVisual(DetailsPanel).TransformBounds(new Rect(0, 0, section.ActualWidth, section.ActualHeight));
                        if (rect.Left < 0 || rect.Right > DetailsPanel.ActualWidth + 1)
                            throw new Exception($"Properties panel is outside viewport: node={node.Name}, width={width}, left={rect.Left:F1}, right={rect.Right:F1}, viewport={DetailsPanel.ActualWidth:F1}");
                        if (left.HasValue && Math.Abs(left.Value - rect.Left) > 1)
                            throw new Exception($"Properties panel shifted between nodes: width={width}, before={left:F1}, after={rect.Left:F1}");
                        left = rect.Left;
                    }
                }
            }
        }
        finally { App.MainWindow.AppWindow.Resize(originalSize); }
    }
}
