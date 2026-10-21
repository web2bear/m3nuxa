using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

var session = Guid.NewGuid().ToString("N");
var operations = new[] { "read", "begin", "create", "update", "move", "delete", "validate", "apply" };
while (await Console.In.ReadLineAsync() is { } line)
{
    JsonNode? id = null;
    try
    {
        var request = JsonNode.Parse(line)!.AsObject();
        id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        if (id is null) continue;
        object result = method switch
        {
            "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "ContexWin", version = "0.1.0" } },
            "ping" => new { },
            "tools/list" => new { tools = operations.Select(name => new
            {
                name,
                description = name switch
                {
                    "read" => "Read the active menu configuration",
                    "begin" => "Start an independent menu configuration draft",
                    "apply" => "Validate and apply the draft with a backup",
                    _ => $"{name} an item in the current draft"
                },
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["id"] = new { type = "string" },
                        ["parentId"] = new { type = "string" },
                        ["index"] = new { type = "integer" },
                        ["node"] = new { type = "object" }
                    }
                }
            }).ToArray() },
            "tools/call" => await CallTool(request["params"]?.AsObject() ?? throw new ArgumentException("Missing params")),
            _ => throw new ArgumentException($"Unknown method: {method}")
        };
        Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
    }
    catch (Exception ex)
    {
        if (id is not null) Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32000, message = ex.Message } }));
    }
}

async Task<object> CallTool(JsonObject parameters)
{
    var operation = parameters["name"]?.GetValue<string>() ?? throw new ArgumentException("Missing tool name");
    if (!operations.Contains(operation)) throw new ArgumentException($"Unknown tool: {operation}");
    var process = Process.GetProcessesByName("ContexWin.App").FirstOrDefault() ??
                  throw new InvalidOperationException("Start ContexWin.App before connecting MCP.");
    using var pipe = new NamedPipeClientStream(".", $"ContexWin-{Environment.UserName}-{process.Id}", PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(3000);
    using var reader = new StreamReader(pipe, leaveOpen: true);
    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    await writer.WriteLineAsync(new JsonObject
    {
        ["session"] = session,
        ["operation"] = operation,
        ["arguments"] = parameters["arguments"]?.DeepClone() ?? new JsonObject()
    }.ToJsonString());
    var response = await reader.ReadLineAsync() ?? throw new IOException("Tray process disconnected.");
    var parsed = JsonNode.Parse(response)!;
    return new { content = new[] { new { type = "text", text = parsed.ToJsonString() } }, isError = parsed["error"] is not null };
}
