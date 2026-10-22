using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace m3nuxa.App;

internal static class LauncherMenu
{
    public static void Style(ToolStripDropDown menu)
    {
        var dark = true;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            dark = key?.GetValue("AppsUseLightTheme") is not int value || value == 0;
        var highContrast = SystemInformation.HighContrast;
        var background = highContrast ? SystemColors.Menu : dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(249, 249, 249);
        var foreground = highContrast ? SystemColors.MenuText : dark ? Color.FromArgb(242, 242, 242) : Color.FromArgb(30, 30, 30);
        menu.BackColor = background;
        menu.ForeColor = foreground;
        menu.Font = new Font("Segoe UI", 10);
        menu.Padding = new Padding(8);
        menu.ImageScalingSize = new Size(20, 20);
        menu.DropShadowEnabled = true;
        menu.Renderer = new MenuRenderer(background, foreground, highContrast ? SystemColors.Highlight : dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(234, 234, 234));
        menu.SizeChanged += (_, _) =>
        {
            if (menu.Width < 12 || menu.Height < 12) return;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(new Rectangle(0, 0, menu.Width, menu.Height), new Size(8, 8));
            var previous = menu.Region;
            menu.Region = new Region(shape);
            previous?.Dispose();
        };
        if (menu is ToolStripDropDownMenu dropDown) { dropDown.ShowImageMargin = true; dropDown.ShowCheckMargin = false; }
    }

    private sealed class MenuRenderer(Color background, Color foreground, Color selection) : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(background);
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            using var brush = new SolidBrush(selection);
            using var path = new GraphicsPath();
            path.AddRoundedRectangle(new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2), new Size(4, 4));
            e.Graphics.FillPath(brush, path);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? SystemColors.GrayText : SystemInformation.HighContrast && e.Item.Selected ? SystemColors.HighlightText : foreground;
            // Segoe UI glyphs sit optically above the center of the image slot.
            e.TextRectangle = new Rectangle(e.TextRectangle.X, e.TextRectangle.Y + 2, e.TextRectangle.Width, e.TextRectangle.Height);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        { e.ArrowColor = foreground; base.OnRenderArrow(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(65, foreground));
            e.Graphics.DrawLine(pen, 10, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(65, foreground));
            using var path = new GraphicsPath();
            path.AddRoundedRectangle(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), new Size(8, 8));
            e.Graphics.DrawPath(pen, path);
        }
    }
}
