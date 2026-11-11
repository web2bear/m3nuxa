using m3nuxa.App;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage
{
    internal async Task CheckIconSelectionAsync()
    {
        var icon = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "icon-packs", "Boxicons"), "*.png").First();
        var path = Path.GetRelativePath(AppContext.BaseDirectory, icon);
        var node = new MenuNode { Name = "Icon selection", Kind = MenuNodeKind.Group, IconPath = path, IconColor = "#60A5FA" };
        config = new AppConfig { Items = [node] };
        draftState.SetBaseline(); RefreshTree(node.Id); UpdateApplyState();

        var selection = CreateIconSelectionDialog(node);
        var pending = selection.Dialog.ShowAsync();
        await Task.Delay(150);
        Require(selection.Icons.SelectedItem is string, "current icon is not selected");
        var preview = Descendants(selection.Dialog).OfType<ContentControl>().Single(control => AutomationProperties.GetAutomationId(control) == "IconPreview");
        Require(preview.Content is FrameworkElement { ActualWidth: > 0 }, "current icon preview is missing");
        for (var attempt = 0; attempt < 40 && !Descendants(preview).OfType<Image>().Any(image => image.Source is not null); attempt++) await Task.Delay(50);
        Require(Descendants(preview).OfType<Image>().Any(image => image.Source is not null), "current icon image did not load");
        selection.SetColor("#EF4444");
        selection.Search.Text = "no-icon-matches-this-query";
        Require(selection.SelectedPath == path && selection.SelectedColor == "#EF4444", "filter lost the current icon or color");
        Require(node.IconColor == "#60A5FA" && !dirty, "preview changed the draft before confirmation");
        InvokeDialogButton(selection.Dialog, "PrimaryButton");
        Require(await pending == ContentDialogResult.Primary, "confirmation result");
        Require(node.IconPath == path && node.IconColor == "#EF4444" && dirty, "color-only confirmation did not commit");
        UndoClicked(this, null!);
        Require(config.Items[0].IconColor == "#60A5FA" && !dirty, "one undo must restore the icon color");
        node = config.Items[0];

        selection = CreateIconSelectionDialog(node);
        pending = selection.Dialog.ShowAsync(); await Task.Delay(100);
        selection.Icons.SelectedItem = selection.Icons.Items.OfType<string>().First(uri => new Uri(uri).LocalPath != icon);
        selection.SetColor("#22C55E");
        Require(!dirty && node.IconPath == path, "catalog selection committed immediately");
        InvokeDialogButton(selection.Dialog, "CloseButton"); await pending;
        Require(!dirty && node.IconPath == path && node.IconColor == "#60A5FA", "cancel changed the draft");

        selection = CreateIconSelectionDialog(node);
        pending = selection.Dialog.ShowAsync(); await Task.Delay(100);
        selection.SelectExplorer();
        Require(!dirty && selection.SelectedPath == "" && selection.SelectedColor == "", "Explorer preview");
        InvokeDialogButton(selection.Dialog, "PrimaryButton"); await pending;
        Require(node.IconPath == "" && node.IconColor == "", "Explorer confirmation");
        UndoClicked(this, null!); node = config.Items[0];

        selection = CreateIconSelectionDialog(node);
        pending = selection.Dialog.ShowAsync(); await Task.Delay(100);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        selection.SelectFile(missing);
        InvokeDialogButton(selection.Dialog, "PrimaryButton"); await Task.Delay(100);
        Require(pending.Status == Windows.Foundation.AsyncStatus.Started && !dirty, "file error closed the dialog or changed the draft");
        Require(Descendants(selection.Dialog).OfType<InfoBar>().Any(bar => bar.IsOpen), "file error is not visible");
        selection.SelectFile(icon);
        InvokeDialogButton(selection.Dialog, "CloseButton"); await pending;
        Require(node.IconPath == path && !dirty, "file cancellation changed the draft");
        Require(draftState.UndoCount == 0, "canceled selection added undo history");

        node.IconColor = ""; draftState.SetBaseline(); UpdateApplyState();
        selection = CreateIconSelectionDialog(node);
        pending = selection.Dialog.ShowAsync(); await Task.Delay(100);
        InvokeDialogButton(selection.Dialog, "PrimaryButton"); await pending;
        Require(!dirty && node.IconColor == "" && draftState.UndoCount == 0, "unchanged legacy icon created a change");

        static void Require(bool value, string stage) { if (!value) throw new Exception("Icon selection: " + stage); }
    }

    private static void InvokeDialogButton(ContentDialog dialog, string name)
    {
        var label = name == "PrimaryButton" ? dialog.PrimaryButtonText : dialog.CloseButtonText;
        var button = Descendants(dialog).OfType<Button>().Single(button => button.Name == name && button.Content as string == label);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
}
