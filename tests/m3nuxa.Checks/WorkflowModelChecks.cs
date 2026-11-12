using m3nuxa.App;
using System.Text.Json;

internal static class WorkflowModelChecks
{
    public static void Run(string root)
    {
        root = Path.Combine(root, "workflow-model");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "config.json");
        const string legacy = """
        {"SchemaVersion":1,"Items":[{"Id":"old","Name":"Legacy","Kind":"Action","Hotkey":"Ctrl+Alt+F5","IconPath":"icon.png","Action":{"Kind":"InsertText","Text":"old text"},"Children":[]}]}
        """;
        File.WriteAllText(path, legacy);
        var store = new ConfigStore(path);
        var node = store.Current.Items.Single();
        Check(store.Current.SchemaVersion == 3 && node.Steps.Count == 1 && node.Steps[0].Text == "old text", "legacy action migrates");
        Check(node.Hotkey == "Ctrl+Alt+F5" && node.IconPath == "icon.png", "legacy item metadata migrates");
        var draft = store.CreateDraft();
        draft.Config.Items[0].Steps.Add(new MenuAction { Name = "Wait for editor", Kind = ActionKind.Delay, DelayMilliseconds = 25 });
        Check(store.Apply(draft).Success, "migrated sequence applies");
        var backup = Directory.GetFiles(Path.Combine(root, "backups"), "*.json").Single();
        Check(File.ReadAllText(backup) == legacy, "original configuration backed up before migration");
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var item = json.RootElement.GetProperty("Items")[0];
            Check(json.RootElement.GetProperty("SchemaVersion").GetInt32() == 3 && item.GetProperty("Steps").GetArrayLength() == 2, "new schema stores ordered steps");
            Check(!item.TryGetProperty("Action", out _), "new schema omits legacy action");
        }
        var restored = new ConfigStore(path).Current.Items.Single();
        Check(restored.Steps.Count == 2 && restored.Steps[1].DelayMilliseconds == 25 && restored.Steps[1].Name == "Wait for editor", "steps survive restart");
        draft = store.CreateDraft();
        draft.Config.Items[0].Steps[1].DelayMilliseconds = -1;
        Check(store.Validate(draft.Config).IsValid, "invalid subsequent step can be saved");
        draft.Config.Items[0].Steps.Clear();
        Check(store.Apply(draft).Success && new ConfigStore(path).Current.Items[0].Steps.Count == 0, "empty sequence saves and reloads");
        draft.Config.Items[0].Steps.Add(new MenuAction { Kind = ActionKind.RunProgram, Path = "test.exe", WaitForExit = true, TimeoutMilliseconds = 0 });
        Check(store.Validate(draft.Config).IsValid && store.Validate(draft.Config).Warnings.Count == 0, "incomplete step can be saved without path warnings");
        draft.Config.Items[0].Steps[0].Kind = (ActionKind)999;
        Check(!store.Validate(draft.Config).IsValid, "unknown step kind remains a structural error");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
