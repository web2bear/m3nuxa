using m3nuxa.App;
using System.Text.Json.Nodes;

internal static class AgentPipeWorkflowChecks
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
        var directory = Path.Combine(root, "pipe-workflow");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        var store = new ConfigStore(path);
        using var pipe = new AgentPipe(store, () => { });
        {
            var successEnvelope = JsonNode.Parse(AgentPipe.SerializeSuccessForChecks(new JsonObject { ["probe"] = true }))!.AsObject();
            Check(successEnvelope["protocolVersion"]?.GetValue<int>() == AgentPipeProtocol.Version &&
                successEnvelope["success"]?.GetValue<bool>() == true && successEnvelope["data"]?["probe"]?.GetValue<bool>() == true,
                "successful pipe response uses the versioned envelope");
            var oldVersion = await pipe.DispatchForChecks("""{"protocolVersion":99,"session":"test","operation":"read","arguments":{}}""");
            var oldVersionEnvelope = JsonNode.Parse(oldVersion)!.AsObject();
            Check(oldVersionEnvelope["success"]?.GetValue<bool>() == false &&
                oldVersionEnvelope["error"]?["code"]?.GetValue<string>() == "unsupported_protocol_version",
                "unsupported protocol version returns a stable error code");
            var malformed = await pipe.DispatchForChecks("{");
            var malformedEnvelope = JsonNode.Parse(malformed)!.AsObject();
            Check(malformedEnvelope["success"]?.GetValue<bool>() == false &&
                malformedEnvelope["error"]?["code"]?.GetValue<string>() == "request_failed",
                "malformed request returns the explicit error envelope");
            var initial = await pipe.ExecuteAsync("test", "begin");
            Check(initial["configPath"] is null, "unsaved configuration has no filename");
            await pipe.ExecuteAsync("test", "create", JsonNode.Parse("""
            {"node":{"Id":"group","Name":"Group","Kind":"Group","Children":[{"Id":"action","Name":"Action","Kind":"Action","Steps":[{"Kind":"InsertText","Text":"first"},{"Kind":"Delay","DelayMilliseconds":5}]}]}}
            """)!.AsObject());
            await pipe.ExecuteAsync("test", "update", JsonNode.Parse("""
            {"id":"group","node":{"Name":"Renamed","Kind":"Group"}}
            """)!.AsObject());
            await pipe.ExecuteAsync("test", "update", JsonNode.Parse("""
            {"id":"action","node":{"Name":"Changed","Kind":"Action"}}
            """)!.AsObject());
            await RejectCode(() => pipe.ExecuteAsync("test", "update", JsonNode.Parse("""
            {"id":"action","node":{"Kind":"Action","Steps":[{"Kind":"Delay"},{"Kind":"Delay"}]}}
            """)!.AsObject()), "steps_require_set_steps", "update cannot replace equally many steps");
            await Reject(() => pipe.ExecuteAsync("test", "setSteps", JsonNode.Parse("""{"id":"action","steps":[]}""")!.AsObject()), "empty sequence rejected");
            await Reject(() => pipe.ExecuteAsync("test", "setSteps", JsonNode.Parse("""{"id":"action","steps":[null]}""")!.AsObject()), "null step rejected");
            var unchanged = await pipe.ExecuteAsync("test", "validate");
            Check(unchanged["IsValid"]!.GetValue<bool>(), "property updates preserve children and steps");
            await pipe.ExecuteAsync("test", "setSteps", JsonNode.Parse("""
            {"id":"action","steps":[{"Kind":"Delay","DelayMilliseconds":25},{"Kind":"InsertText","Text":"second"}]}
            """)!.AsObject());
            var applied = await pipe.ExecuteAsync("test", "apply");
            Check(applied["Success"]!.GetValue<bool>(), "sequence applies");
            var saved = await pipe.ExecuteAsync("test", "begin");
            Check(saved["configPath"]?.GetValue<string>() == Path.GetFullPath(path), "saved configuration path returned to editor");
            var node = new ConfigStore(path).Current.Items.Single().Children.Single();
            Check(node.Name == "Changed" && node.Steps.Count == 2 && node.Steps[0].DelayMilliseconds == 25 && node.Steps[1].Text == "second", "updated ordered sequence survives restart");
            var otherPath = Path.Combine(directory, "saved-as.json");
            var saveAs = await pipe.ExecuteAsync("test", "saveAs", new JsonObject { ["path"] = otherPath });
            Check(saveAs["Success"]!.GetValue<bool>(), "save as through agent pipe succeeds");
            var reopened = await pipe.ExecuteAsync("test", "begin");
            Check(reopened["configPath"]!.GetValue<string>() == otherPath && File.Exists(otherPath), "save as reports new active file");
            await RejectCode(() => pipe.ExecuteAsync("session-without-begin", "validate"), "draft_not_started", "missing draft has stable error code");
        }
    }

    private static async Task RejectCode(Func<Task<JsonNode>> operation, string expectedCode, string name)
    {
        try { await operation(); }
        catch (AgentPipe.ProtocolException ex) when (ex.Code == expectedCode) { return; }
        throw new Exception(name + ": expected code " + expectedCode);
    }

    private static async Task Reject(Func<Task<JsonNode>> operation, string name)
    {
        try { await operation(); }
        catch (InvalidOperationException) { return; }
        throw new Exception(name);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }
}
