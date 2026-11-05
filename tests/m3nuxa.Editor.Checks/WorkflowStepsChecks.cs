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
        draftState.SetBaseline();
        RefreshTree(node.Id);
        await Task.Delay(100);
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
