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
        var store = new ConfigStore(path, Path.Combine(directory, "active-config.txt"), directory);
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
            var invalidPath = Path.Combine(directory, "invalid.json");
            File.WriteAllText(invalidPath, "{\"SchemaVersion\":3,\"Name\":\"Invalid\",\"Settings\":{},\"Items\":null}");
            try { await pipe.ExecuteAsync("test", "loadConfig", new JsonObject { ["path"] = invalidPath }); throw new Exception("invalid config accepted"); }
            catch (AgentPipe.ProtocolException) { }
            await pipe.ExecuteAsync("test", "update", JsonNode.Parse("""{"id":"group","node":{"Name":"Still present","Kind":"Group"}}""")!.AsObject());
            File.Delete(invalidPath);
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
            var saveAs = await pipe.ExecuteAsync("test", "saveAs", new JsonObject { ["name"] = "Copy" });
            Check(saveAs["config"]?["Name"]?.GetValue<string>() == "Copy" && store.FilePath == path,
                "save as creates a named draft without activating it");
            Check((await pipe.ExecuteAsync("test", "apply"))["Success"]!.GetValue<bool>(), "copy applies through pipe");
            var reopened = await pipe.ExecuteAsync("test", "begin");
            Check(reopened["configPath"]!.GetValue<string>() == store.FilePath && store.FilePath != path && File.Exists(store.FilePath),
                "applying copy reports new active file");
            Check(new ConfigStore(store.FilePath).Current.Items.Single().Children.Single().Steps.Count == 2,
                "copy preserves workflow");
            await pipe.ExecuteAsync("mcp-independent", "begin");
            await pipe.ExecuteAsync("mcp-independent", "update", JsonNode.Parse("""{"id":"group","node":{"Name":"Independent draft","Kind":"Group"}}""")!.AsObject());
            var delete = await pipe.ExecuteAsync("test", "deleteActiveConfig", new JsonObject { ["version"] = reopened["version"]!.GetValue<long>() });
            Check(delete["configPath"]!.GetValue<string>() == path, "delete opens next active configuration in editor session");
            Check((await pipe.ExecuteAsync("mcp-independent", "validate"))["IsValid"]!.GetValue<bool>(), "independent draft survives active deletion");
            await Reject(() => pipe.ExecuteAsync("mcp-independent", "apply"), "independent draft becomes stale after deletion");
            await pipe.ExecuteAsync("mcp-independent", "update", JsonNode.Parse("""{"id":"group","node":{"Name":"Preserved after failed apply","Kind":"Group"}}""")!.AsObject());
            await pipe.ExecuteAsync("mcp-independent", "discard");
            await RejectCode(() => pipe.ExecuteAsync("session-without-begin", "validate"), "draft_not_started", "missing draft has stable error code");
        }
        var limitedPipe = pipe;
        for (var index = 0; index < 33; index++)
            await limitedPipe.ExecuteAsync("editor-" + index, "begin");
        for (var index = 0; index < 32; index++)
            await limitedPipe.ExecuteAsync("mcp-" + index, "begin");
        await RejectCode(() => limitedPipe.ExecuteAsync("mcp-over-limit", "begin"), "draft_limit", "MCP draft limit enforced");
        await limitedPipe.ExecuteAsync("editor-with-full-mcp-pool", "begin");
        await limitedPipe.ExecuteAsync("mcp-0", "discard");
        await RejectCode(() => limitedPipe.ExecuteAsync("mcp-0", "validate"), "draft_not_started", "discard releases the draft");
        await limitedPipe.ExecuteAsync("mcp-over-limit", "begin");
        var activity = (Dictionary<string, DateTime>)typeof(AgentPipe).GetField("_draftActivity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(limitedPipe)!;
        activity["mcp-1"] = DateTime.UtcNow.AddMinutes(-31);
        await limitedPipe.ExecuteAsync("mcp-after-expiry", "begin");
        await RejectCode(() => limitedPipe.ExecuteAsync("mcp-1", "validate"), "draft_not_started", "expired MCP draft released");
        await limitedPipe.ExecuteAsync("mcp-2", "validate");
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
