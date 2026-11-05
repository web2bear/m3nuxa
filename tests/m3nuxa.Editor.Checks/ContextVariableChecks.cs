using m3nuxa.App;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage
{
    internal async Task CheckContextVariablesAsync()
    {
        var action = new MenuAction { Kind = ActionKind.RunProgram, Path = "abcd", Arguments = "abcd", WorkingDirectory = "abcd" };
        var node = new MenuNode { Name = "Variables", Kind = MenuNodeKind.Action, Action = action };
        config = new AppConfig { Items = [node] };
        draftState.SetBaseline();
        RefreshTree(node.Id);
        await Task.Delay(100);
        var expected = new[] { "{selectedPaths}", "{selectedPath}", "{fileName}", "{extension}", "{parentFolder}", "{currentFolder}", "{clipboardText}", "{env:PATH}", "{env:TEMP}", "{knownFolder:Desktop}", "{knownFolder:Documents}", "{knownFolder:Downloads}", "{knownFolder:Pictures}", "{knownFolder:Music}", "{knownFolder:Videos}", "{knownFolder:Profile}", "{knownFolder:Temp}" };
        var argumentsLabel = L.Get("Editor_Arguments_3c3a07");
        foreach (var label in new[] { L.Get("Editor_Path_1b46c6"), argumentsLabel, L.Get("Editor_WorkingDirectory_72e5f4") })
        {
            var row = PropertyElements().OfType<Grid>().Single(grid => grid.Children.OfType<TextBox>().Any(t => (string)t.Header == label));
            if (!CurrentPropertyElements().Contains(row)) throw new Exception(label + ": variable field detached from the current panel.");
            var variableButton = row.Children.OfType<Button>().Single(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(b).StartsWith(L.Get("Editor_InsertContextVariable_cdf409"), StringComparison.Ordinal));
            if (label != argumentsLabel)
            {
                var browse = row.Children.OfType<Button>().Single(b => b != variableButton);
                if (Grid.GetColumn(browse) >= Grid.GetColumn(variableButton)) throw new Exception(label + ": browse button must precede variables.");
            }
        }
        var (dialog, list) = CreateVariableDialog();
        var options = list.Items.OfType<ListViewItem>().Where(item => item.Tag is string).ToArray();
        if (!options.Select(item => (string)item.Tag).SequenceEqual(expected)) throw new Exception("Variable options changed.");
        if (options.Any(item => item.Content is not Grid grid || grid.Children.OfType<TextBlock>().Count() != 2 || string.IsNullOrWhiteSpace(grid.Children.OfType<TextBlock>().Last().Text))) throw new Exception("Variable descriptions missing.");
        if (dialog.IsPrimaryButtonEnabled) throw new Exception("Insert must be disabled before selection.");
        list.SelectedItem = options[0];
        if (!dialog.IsPrimaryButtonEnabled) throw new Exception("Selecting a variable must enable insertion.");
        await Task.CompletedTask;
    }
}
