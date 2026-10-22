using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace m3nuxa.App;

internal static class LauncherMenu
{
    private const int MenuCornerDiameter = 20;

    public static void Style(ToolStripDropDown menu)
    {
        var dark = true;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            dark = key?.GetValue("AppsUseLightTheme") is not int value || value == 0;
        var highContrast = SystemInformation.HighContrast;
        var background = highContrast ? SystemColors.Menu : dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
        var foreground = highContrast ? SystemColors.MenuText : dark ? Color.FromArgb(242, 242, 242) : Color.FromArgb(30, 30, 30);
        var separator = highContrast ? SystemColors.MenuText : dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(218, 218, 218);
        menu.BackColor = background;
        menu.ForeColor = foreground;
        menu.Font = new Font("Segoe UI Variable Text", 11.5f);
        menu.Padding = new Padding(8);
        menu.ImageScalingSize = new Size(24, 24);
        menu.DropShadowEnabled = true;
        menu.Renderer = new MenuRenderer(background, foreground, highContrast ? SystemColors.Highlight : dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(234, 234, 234), separator);
        menu.SizeChanged += (_, _) =>
        {
            if (menu.Width < 12 || menu.Height < 12) return;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(new Rectangle(0, 0, menu.Width, menu.Height), new Size(MenuCornerDiameter, MenuCornerDiameter));
            var previous = menu.Region;
            menu.Region = new Region(shape);
            previous?.Dispose();
        };
        if (menu is ToolStripDropDownMenu dropDown) { dropDown.ShowImageMargin = true; dropDown.ShowCheckMargin = false; }
    }

    private sealed class MenuRenderer(Color background, Color foreground, Color selection, Color separator) : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(background);
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var availableWidth = e.Item.Owner is null ? e.Item.Width - 4 :
                Math.Min(e.Item.Width - 4, e.Item.Owner.ClientSize.Width - e.Item.Bounds.Left - 10);
            if (availableWidth <= 0) return;
            using var brush = new SolidBrush(selection);
            using var path = new GraphicsPath();
            path.AddRoundedRectangle(new Rectangle(2, 1, availableWidth, e.Item.Height - 2), new Size(8, 8));
            e.Graphics.FillPath(brush, path);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? SystemColors.GrayText : SystemInformation.HighContrast && e.Item.Selected ? SystemColors.HighlightText : foreground;
            // Center the visible glyphs against the image slot, including app icons.
            e.TextRectangle = new Rectangle(e.TextRectangle.X, e.TextRectangle.Y + 4, e.TextRectangle.Width, e.TextRectangle.Height);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = foreground;
            e.ArrowRectangle = new Rectangle(e.ArrowRectangle.X - 12, e.ArrowRectangle.Y, e.ArrowRectangle.Width, e.ArrowRectangle.Height);
            base.OnRenderArrow(e);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(separator);
            e.Graphics.DrawLine(pen, 6, e.Item.Height / 2, e.Item.Width - 6, e.Item.Height / 2);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(separator);
            using var path = new GraphicsPath();
            path.AddRoundedRectangle(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), new Size(MenuCornerDiameter, MenuCornerDiameter));
            e.Graphics.DrawPath(pen, path);
        }
    }
}
