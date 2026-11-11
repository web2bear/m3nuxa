using m3nuxa.App;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace m3nuxa.Editor;

internal sealed class IconSelectionDialog
{
    private readonly MenuNode previewNode;
    private readonly Func<MenuNode, int, FrameworkElement> createIcon;
    private readonly ContentControl preview = new();
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly List<(Button Button, string Color)> swatches = [];
    private readonly StackPanel palette = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private string color;
    private readonly string originalPath;
    private readonly string originalColor;
    private bool colorChanged;

    internal ContentDialog Dialog { get; }
    internal GridView Icons { get; } = new() { SelectionMode = ListViewSelectionMode.Single };
    internal TextBox Search { get; } = new() { PlaceholderText = L.Get("Icon_Search") };
    internal string SelectedPath => previewNode.IconPath;
    internal string SelectedColor => IsBundled ? colorChanged || SelectedPath != originalPath ? color : originalColor : "";
    internal bool FileSelected { get; private set; }
    private bool IsBundled => previewNode.IconPath.Replace('\\', '/').StartsWith("icon-packs/Boxicons/", StringComparison.OrdinalIgnoreCase);

    internal IconSelectionDialog(MenuNode item, XamlRoot root, double height, Func<MenuNode, int, FrameworkElement> createIcon)
    {
        this.createIcon = createIcon;
        originalPath = item.IconPath;
        originalColor = item.IconColor;
        previewNode = new MenuNode { Kind = item.Kind, Action = item.Action, IconPath = item.IconPath, IconColor = item.IconColor };
        color = item.IconColor is { Length: 7 } saved && saved[0] == '#' && uint.TryParse(saved.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _) ? saved : "#60A5FA";
        Dialog = new ContentDialog { XamlRoot = root, Title = L.Get("Icon_ChooseBundled"),
            PrimaryButtonText = L.Get("Icon_Confirm"), CloseButtonText = L.Get("Editor_Cancel_0ec753"), DefaultButton = ContentDialogButton.Primary };

        var previewRow = new Grid { ColumnSpacing = 16, ColumnDefinitions = {
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        preview.Width = preview.Height = 32;
        preview.HorizontalAlignment = HorizontalAlignment.Left;
        preview.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(preview, "IconPreview");
        previewRow.Children.Add(preview);

        var sources = new Grid { Width = 288, ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() } };
        var file = new Button { Content = L.Get("Icon_ChooseFile"), Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
        var explorer = new Button { Content = L.Get("Editor_UseExplorerIcon_838f6d"), Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(file, "IconChooseFile");
        AutomationProperties.SetAutomationId(explorer, "IconUseExplorer");
        file.Click += (_, _) =>
        {
            try
            {
                var path = new IconFilePicker(AppContext.BaseDirectory).Pick(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                if (path is not null) SelectFile(path);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        explorer.Click += (_, _) => SelectExplorer();
        sources.Children.Add(file); Grid.SetColumn(explorer, 1); sources.Children.Add(explorer);
        Grid.SetColumn(sources, 1); previewRow.Children.Add(sources);

        foreach (var value in new[] { "#E5E7EB", "#EF4444", "#F59E0B", "#EAB308", "#84CC16", "#22C55E", "#14B8A6", "#06B6D4", "#60A5FA", "#6366F1", "#A855F7", "#EC4899" })
        {
            var swatch = new Button { Width = 30, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new Thickness(3),
                Content = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(ParseColor(value)) } };
            AutomationProperties.SetName(swatch, L.Get("Icon_Color") + " " + value);
            AutomationProperties.SetAutomationId(swatch, "IconColor" + value[1..]);
            ToolTipService.SetToolTip(swatch, value);
            swatch.Click += (_, _) => SetColor(value);
            swatches.Add((swatch, value)); palette.Children.Add(swatch);
        }
        var picker = new ColorPicker { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false, Color = ParseColor(color) };
        var flyout = new Flyout { Content = picker };
        flyout.Opening += (_, _) => picker.Color = ParseColor(color);
        picker.ColorChanged += (_, args) => SetColor($"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}");
        var customColor = new Button { Content = "…", Flyout = flyout, Width = 30, Height = 30,
            MinWidth = 0, MinHeight = 0, Padding = new Thickness(3) };
        AutomationProperties.SetName(customColor, L.Get("Icon_CustomColor"));
        AutomationProperties.SetAutomationId(customColor, "IconCustomColor");
        ToolTipService.SetToolTip(customColor, L.Get("Icon_CustomColor"));
        palette.Children.Add(customColor);
        UpdateCatalogColor();

        string[] icons;
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "icon-packs", "Boxicons");
            icons = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.png").OrderBy(Path.GetFileName).Select(path => new Uri(path).AbsoluteUri).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { icons = []; }
        Icons.ItemsSource = icons;
        AutomationProperties.SetAutomationId(Icons, "IconCatalog");
        AutomationProperties.SetName(Search, L.Get("Icon_Search"));
        AutomationProperties.SetAutomationId(Search, "IconSearch");
        Search.TextChanged += (_, _) =>
        {
            Icons.ItemsSource = icons.Where(path => Path.GetFileNameWithoutExtension(new Uri(path).LocalPath).Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            RestoreCatalogSelection();
        };
        Icons.SelectionChanged += (_, _) =>
        {
            if (Icons.SelectedItem is not string uri) return;
            previewNode.IconPath = Path.GetRelativePath(AppContext.BaseDirectory, new Uri(uri).LocalPath);
            FileSelected = false; UpdatePreview();
        };
        RestoreCatalogSelection();

        var content = new Grid { Height = Math.Clamp(height, 280, 580), RowSpacing = 10 };
        void Row(FrameworkElement element, bool stretch = false)
        {
            content.RowDefinitions.Add(new RowDefinition { Height = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow(element, content.RowDefinitions.Count - 1); content.Children.Add(element);
        }
        Row(previewRow);
        Row(Search);
        Row(palette);
        Row(icons.Length == 0 ? new TextBlock { Text = L.Get("Icon_PackMissing"), TextWrapping = TextWrapping.Wrap } : Icons, stretch: true);
        Row(error);
        Dialog.Content = content;
        UpdatePreview();
    }

    internal void SetColor(string value)
    {
        colorChanged = true;
        color = value;
        UpdateCatalogColor(); UpdatePreview();
    }

    internal void SelectFile(string path)
    {
        previewNode.IconPath = path; FileSelected = true;
        Icons.SelectedItem = null; UpdatePreview();
    }

    internal void SelectExplorer()
    {
        previewNode.IconPath = ""; FileSelected = false;
        Icons.SelectedItem = null; UpdatePreview();
    }

    internal void ShowError(string message) { error.Message = message; error.IsOpen = true; }

    private void RestoreCatalogSelection()
    {
        Icons.SelectedItem = Icons.Items.OfType<string>().FirstOrDefault(uri =>
            string.Equals(Path.GetRelativePath(AppContext.BaseDirectory, new Uri(uri).LocalPath), SelectedPath, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateCatalogColor()
    {
        Icons.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><BitmapIcon UriSource='{{Binding}}' ShowAsMonochrome='True' Foreground='{color}' Width='40' Height='40' ToolTipService.ToolTip='{{Binding}}'/></DataTemplate>");
        foreach (var swatch in swatches) swatch.Button.BorderThickness = new Thickness(string.Equals(swatch.Color, color, StringComparison.OrdinalIgnoreCase) ? 2 : 0);
    }

    private void UpdatePreview()
    {
        previewNode.IconColor = SelectedColor;
        preview.Content = createIcon(previewNode, 32);
        var description = SelectedPath.Length == 0 ? L.Get("Editor_UseExplorerIcon_838f6d") : Path.GetFileNameWithoutExtension(SelectedPath);
        if (IsBundled) description += " · " + color;
        ToolTipService.SetToolTip(preview, description);
        AutomationProperties.SetName(preview, description);
        foreach (var control in palette.Children.OfType<Control>()) control.IsEnabled = IsBundled;
        error.IsOpen = false;
    }

    private static Windows.UI.Color ParseColor(string value)
    {
        var rgb = Convert.ToUInt32(value[1..], 16);
        return Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
