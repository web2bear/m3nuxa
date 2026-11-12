using m3nuxa.App;
using System.Text.Json.Nodes;

internal static class CopyConflictChecks
{
    public static void Run(string root)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try { RunAsync(root).GetAwaiter().GetResult(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "copy-conflicts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        var store = new ConfigStore(path, Path.Combine(directory, "active-config.txt"), directory);
        if (!store.Apply(store.CreateDraft()).Success) throw new Exception("Initial config failed");
        using var pipe = new AgentPipe(store, () => { });

        await pipe.ExecuteAsync("old", "begin");
        var newer = store.CreateDraft();
        newer.Config.Name = "Updated";
        if (!store.Apply(newer).Success) throw new Exception("Newer configuration failed");
        await pipe.ExecuteAsync("old", "saveAs", new JsonObject { ["name"] = "Stale copy" });
        await RejectApply(pipe, "old", "copy must preserve the original base version");

        var source = Path.Combine(directory, "other.json");
        File.WriteAllText(source, File.ReadAllText(path).Replace("Updated", "Other"));
        await pipe.ExecuteAsync("external", "loadConfig", new JsonObject { ["path"] = source });
        await pipe.ExecuteAsync("external", "saveAs", new JsonObject { ["name"] = "External copy" });
        File.AppendAllText(source, " ");
        await RejectApply(pipe, "external", "copy must detect a changed source file");
        if (Directory.EnumerateFiles(directory, "*.json").Count() != 2 || store.FilePath != path)
            throw new Exception("Rejected copies must not create files or change the active configuration");
    }

    private static async Task RejectApply(AgentPipe pipe, string session, string message)
    {
        try { await pipe.ExecuteAsync(session, "apply"); }
        catch (AgentPipe.ProtocolException) { return; }
        throw new Exception(message);
    }
}
