using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace m3nuxa.App;

public partial class App
{
    [StructLayout(LayoutKind.Sequential)] private struct FrameRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FramePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out FrameRect rect);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out FramePoint point);

    private static async Task CheckPanelFrameAsync()
    {
        var child = new MenuNode { Name = "Child", Kind = MenuNodeKind.Group };
        var group = new MenuNode { Name = "Group", Kind = MenuNodeKind.Group, Children = [child,
            new MenuNode { Kind = MenuNodeKind.Separator },
            new MenuNode { Name = "Two", Kind = MenuNodeKind.Group },
            new MenuNode { Name = "Three", Kind = MenuNodeKind.Group },
            new MenuNode { Name = "Four", Kind = MenuNodeKind.Group },
            new MenuNode { Name = "Five", Kind = MenuNodeKind.Group }] };
        var other = new MenuNode { Name = "Other", Kind = MenuNodeKind.Group };
        var longName = new MenuNode { Name = "A long label that wraps to a second line", Kind = MenuNodeKind.Action, Steps = [new MenuAction()] };
        var displaysForOpacity = Microsoft.UI.Windowing.DisplayArea.FindAll();
        var opacityArea = displaysForOpacity[0].WorkArea;
        for (var i = 0; i < displaysForOpacity.Count; i++) if (!displaysForOpacity[i].IsPrimary) { opacityArea = displaysForOpacity[i].WorkArea; break; }
        var backgroundWindow = new Window { Content = new Microsoft.UI.Xaml.Controls.Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red) } };
        backgroundWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(opacityArea.X + 100, opacityArea.Y + 60, 800, 600));
        backgroundWindow.Activate();
        LauncherPanel.Show(new[] { group }, _ => { }, () => { }, new AppSettings { PanelOpacity = 40, PanelBackground = "#202020" });
        await Task.Delay(100);
        var translucent = (Window)typeof(LauncherPanel).GetField("active", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        if (translucent.SystemBackdrop is not WinUIEx.TransparentTintBackdrop { TintColor.A: 102 })
            throw new Exception("Launcher backdrop ignores selected opacity");
        translucent.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(opacityArea.X + 150, opacityArea.Y + 100, 500, 400));
        await Task.Delay(400);
        static System.Drawing.Color ScreenPixel(int x, int y)
        {
            using var image = new System.Drawing.Bitmap(1, 1);
            using var graphics = System.Drawing.Graphics.FromImage(image);
            graphics.CopyFromScreen(x, y, 0, 0, image.Size);
            return image.GetPixel(0, 0);
        }
        var partialPixel = ScreenPixel(opacityArea.X + 600, opacityArea.Y + 300);
        LauncherPanel.Show(new[] { group }, _ => { }, () => { }, new AppSettings { PanelOpacity = 100, PanelBackground = "#202020" });
        var opaque = (Window)typeof(LauncherPanel).GetField("active", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        opaque.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(opacityArea.X + 150, opacityArea.Y + 100, 500, 400));
        await Task.Delay(400);
        var opaquePixel = ScreenPixel(opacityArea.X + 600, opacityArea.Y + 300);
        backgroundWindow.Close();
        if (partialPixel.R < opaquePixel.R + 35)
            throw new Exception($"Launcher transparency not visible: 40%={partialPixel}, 100%={opaquePixel}");
        LauncherPanel.Show(new[] { group, other, longName }, _ => { }, () => { }, new AppSettings());
        await Task.Delay(300);
        var panel = (Window)typeof(LauncherPanel).GetField("active", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var panelGrid = (Microsoft.UI.Xaml.Controls.GridView)typeof(LauncherPanel).GetField("grid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var groupItem = (Microsoft.UI.Xaml.Controls.GridViewItem)panelGrid.Items[0];
        var groupBody = (Microsoft.UI.Xaml.Controls.StackPanel)groupItem.Content;
        var longBody = (Microsoft.UI.Xaml.Controls.StackPanel)((Microsoft.UI.Xaml.Controls.GridViewItem)panelGrid.Items[2]).Content;
        var groupY = ((FrameworkElement)groupBody.Children[0]).TransformToVisual(panelGrid).TransformPoint(new Windows.Foundation.Point()).Y;
        var longY = ((FrameworkElement)longBody.Children[0]).TransformToVisual(panelGrid).TransformPoint(new Windows.Foundation.Point()).Y;
        if (Math.Abs(groupY - longY) > 1) throw new Exception($"Icons do not align for wrapped labels: {groupY} vs {longY}");
        var preview = (Microsoft.UI.Xaml.Controls.Border)groupBody.Children[0];
        var previewGrid = (Microsoft.UI.Xaml.Controls.Grid)preview.Child;
        if (previewGrid.Children.Count != 4 || Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(groupItem) != L.Format("PanelGroupDetails", 5))
            throw new Exception("Group preview or child count is incorrect");
        for (var i = 0; i < 4; i++)
        {
            var icon = (Microsoft.UI.Xaml.Controls.Image)previewGrid.Children[i];
            if (Microsoft.UI.Xaml.Controls.Grid.GetRow(icon) != i / 2 || Microsoft.UI.Xaml.Controls.Grid.GetColumn(icon) != i % 2)
                throw new Exception("Group preview icon order is incorrect");
        }
        for (var attempt = 0; attempt < 30 && previewGrid.Children.OfType<Microsoft.UI.Xaml.Controls.Image>().Any(icon => icon.Source is null); attempt++)
            await Task.Delay(100);
        if (previewGrid.Children.OfType<Microsoft.UI.Xaml.Controls.Image>().Any(icon => icon.Source is null))
            throw new Exception("Group preview icons did not load");
        var emptyPreview = (Microsoft.UI.Xaml.Controls.Border)((Microsoft.UI.Xaml.Controls.StackPanel)((Microsoft.UI.Xaml.Controls.GridViewItem)panelGrid.Items[1]).Content).Children[0];
        if (((Microsoft.UI.Xaml.Controls.Grid)emptyPreview.Child).Children.Count != 0) throw new Exception("Empty group preview contains icons");
        groupItem.Focus(FocusState.Programmatic);
        var details = (Microsoft.UI.Xaml.Controls.TextBlock)typeof(LauncherPanel).GetField("itemDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        if (details.Text != L.Format("PanelGroupDetails", 5)) throw new Exception("Group child count was not shown on focus");
        var displays = Microsoft.UI.Windowing.DisplayArea.FindAll();
        var area = displays[0].WorkArea;
        for (var i = 0; i < displays.Count; i++) if (!displays[i].IsPrimary) { area = displays[i].WorkArea; break; }
        panel.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + 150, area.Y + 100, 660, 500));
        await Task.Delay(300);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(panel);
        GetWindowRect(hwnd, out var rect);
        GetCursorPos(out var oldCursor);
        async Task<FrameRect> DragWhileHeld(int startX, int startY, int deltaX, int deltaY)
        {
            SetCursorPos(startX, startY);
            await Task.Delay(100);
            var input = Task.Run(async () =>
            {
                mouse_event(0x0002, 0, 0, 0, 0);
                await Task.Delay(150);
                SetCursorPos(startX + deltaX, startY + deltaY);
                await Task.Delay(700);
                GetWindowRect(hwnd, out var during);
                mouse_event(0x0004, 0, 0, 0, 0);
                return during;
            });
            return await input;
        }
        try
        {
            var moved = await DragWhileHeld(rect.Right - 100, rect.Top + 30, 80, 50);
            if (moved.Left == rect.Left || moved.Top == rect.Top) throw new Exception("Panel did not move while dragging its header");
            GetWindowRect(hwnd, out rect);
            var resized = await DragWhileHeld(rect.Right - 2, (rect.Top + rect.Bottom) / 2, 80, 0);
            if (resized.Right <= rect.Right) throw new Exception("Panel did not resize while dragging its edge");
        }
        finally { mouse_event(0x0004, 0, 0, 0, 0); SetCursorPos(oldCursor.X, oldCursor.Y); }
        var type = typeof(LauncherPanel);
        var navigate = type.GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var moveHistory = type.GetMethod("MoveHistory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var back = (Microsoft.UI.Xaml.Controls.HyperlinkButton)type.GetField("back", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var forward = (Microsoft.UI.Xaml.Controls.HyperlinkButton)type.GetField("forward", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var crumbs = (Microsoft.UI.Xaml.Controls.BreadcrumbBar)type.GetField("breadcrumbs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        void Check(string[] expected, bool canBack, bool canForward)
        {
            if (!((IEnumerable<string>)crumbs.ItemsSource).SequenceEqual(expected) || back.IsEnabled != canBack || forward.IsEnabled != canForward)
                throw new Exception("Panel navigation path or history state is incorrect");
        }
        var home = L.Language == "ru-RU" ? "Главная" : "Home";
        Check([home], false, false);
        navigate.Invoke(panel, [new[] { group }]);
        navigate.Invoke(panel, [new[] { group, child }]);
        Check([home, "Group", "Child"], true, false);
        navigate.Invoke(panel, [new[] { group }]);
        moveHistory.Invoke(panel, [-1]);
        Check([home, "Group", "Child"], true, true);
        moveHistory.Invoke(panel, [-1]);
        navigate.Invoke(panel, [new[] { other }]);
        Check([home, "Other"], true, false);
        moveHistory.Invoke(panel, [-1]);
        moveHistory.Invoke(panel, [1]);
        Check([home, "Other"], true, false);
        panel.Close();
    }
}
