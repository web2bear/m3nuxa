using m3nuxa.App;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage
{
    internal async Task CheckWorkflowStepsAsync()
    {
        var node = new MenuNode { Name = "Workflow", Kind = MenuNodeKind.Action,
            Steps = [new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 100 }, new MenuAction { Kind = ActionKind.Delay, DelayMilliseconds = 200 }] };
        config = new AppConfig { Items = [node] };
        node.Steps[1].Name = L.Get("Workflow_Delay");
        if (StepDisplayName(node, 0) == StepDisplayName(node, 1)) throw new Exception("Unnamed and named steps must have distinct automatic labels");
        node.Steps[1].Name = "";
        draftState.SetBaseline();
        RefreshTree(node.Id);
        DetailsTabs.SelectedIndex = 1;
        await Task.Delay(100);
        var list = stepList ?? throw new Exception("Step list missing");
        list.UpdateLayout();
        var row = (ListViewItem)list.Items[0];
        var point = row.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
        var x = (int)(point.X + row.ActualWidth / 2);
        var y = (int)(point.Y + row.ActualHeight / 4);
        if (!TryGetStepInsertion(list, x, y, out var target, out var lineY) || target != 0) throw new Exception("First insertion target incorrect");
        UpdateStepDropIndicator(x, y);
        if (stepDropIndicator?.Visibility != Microsoft.UI.Xaml.Visibility.Visible || Math.Abs(Canvas.GetTop(stepDropIndicator) - Math.Max(0, lineY - 1.5)) > 1)
            throw new Exception("Insertion indicator does not match target");
        UpdateStepDropIndicator(-10000, -10000);
        if (stepDropIndicator.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed) throw new Exception("Insertion indicator remains visible outside list");
        void Invoke(string key)
        {
            var bar = PropertyElements().OfType<CommandBar>().Single();
            var button = bar.PrimaryCommands.OfType<AppBarButton>().Single(x => x.Label == L.Get(key));
            if (!button.IsEnabled) throw new Exception($"{key} unexpectedly disabled");
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
            if (peer?.GetPattern(PatternInterface.Invoke) is not IInvokeProvider provider) throw new Exception($"{key} not invokable");
            provider.Invoke();
        }
        Invoke("Workflow_MoveDown");
        if (config.Items[0].Steps.Select(x => x.DelayMilliseconds).SequenceEqual([200,100]) == false) throw new Exception("Move down failed");
        UndoClicked(this, null!);
        if (config.Items[0].Steps.Select(x => x.DelayMilliseconds).SequenceEqual([100,200]) == false) throw new Exception("Undo step move failed");
        RedoClicked(this, null!);
        if (config.Items[0].Steps.Select(x => x.DelayMilliseconds).SequenceEqual([200,100]) == false) throw new Exception("Redo step move failed");
        Invoke("Workflow_Duplicate");
        if (config.Items[0].Steps.Select(x => x.DelayMilliseconds).SequenceEqual([200,100,100]) == false) throw new Exception("Duplicate step failed");
        await Task.CompletedTask;
    }
}
