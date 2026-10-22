using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace ContexWin.App;

internal sealed class McpHttpServer
{
    public const int Port = 58342;
    public const string Endpoint = "http://127.0.0.1:58342/mcp";
    private WebApplication? app;

    public bool IsRunning => app is not null;

    public async Task StartAsync(AgentPipe pipe)
    {
        if (app is not null) return;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        builder.Services.AddSingleton(pipe);
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<McpHttpTools>();

        var candidate = builder.Build();
        candidate.Use(async (context, next) =>
        {
            if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != Port ||
                (context.Request.Headers.Origin.Count > 0 &&
                 context.Request.Headers.Origin.ToString() != $"http://127.0.0.1:{Port}"))
            {
                context.Response.StatusCode = 403;
                return;
            }
            await next();
        });
        candidate.MapMcp("/mcp");
        try
        {
            await candidate.StartAsync();
            app = candidate;
            DevLog.Write("mcp-http", $"Listening at {Endpoint}");
        }
        catch
        {
            await candidate.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (app is null) return;
        var running = app;
        await running.StopAsync();
        await running.DisposeAsync();
        app = null;
        DevLog.Write("mcp-http", "Stopped");
    }
}

[McpServerToolType]
internal sealed class McpHttpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool(Name = "read"), Description("Read the active menu configuration and version.")]
    public static async Task<string> Read(AgentPipe pipe) =>
        (await pipe.ExecuteAsync("", "read")).ToJsonString();

    [McpServerTool(Name = "begin"), Description("Start an independent draft and return its draftId. Pass draftId to later editing tools.")]
    public static async Task<string> Begin(AgentPipe pipe)
    {
        var draftId = Guid.NewGuid().ToString("N");
        var response = (await pipe.ExecuteAsync(draftId, "begin")).AsObject();
        response["draftId"] = draftId;
        return response.ToJsonString();
    }

    [McpServerTool(Name = "create"), Description("Add a menu item to a draft. parentId is a group ID; omit it for the root.")]
    public static async Task<string> Create(AgentPipe pipe, string draftId, MenuNode node, string? parentId = null, int index = -1) =>
        (await pipe.ExecuteAsync(draftId, "create", new JsonObject
        {
            ["node"] = JsonSerializer.SerializeToNode(node, JsonOptions), ["parentId"] = parentId, ["index"] = index
        })).ToJsonString();

    [McpServerTool(Name = "update"), Description("Replace an item in a draft, retaining its ID.")]
    public static async Task<string> Update(AgentPipe pipe, string draftId, string id, MenuNode node) =>
        (await pipe.ExecuteAsync(draftId, "update", new JsonObject
        {
            ["id"] = id, ["node"] = JsonSerializer.SerializeToNode(node, JsonOptions)
        })).ToJsonString();

    [McpServerTool(Name = "move"), Description("Move an item within a draft. Omit parentId to move it to the root.")]
    public static async Task<string> Move(AgentPipe pipe, string draftId, string id, string? parentId = null, int index = -1) =>
        (await pipe.ExecuteAsync(draftId, "move", new JsonObject
        {
            ["id"] = id, ["parentId"] = parentId, ["index"] = index
        })).ToJsonString();

    [McpServerTool(Name = "delete"), Description("Remove an item from a draft.")]
    public static async Task<string> Delete(AgentPipe pipe, string draftId, string id) =>
        (await pipe.ExecuteAsync(draftId, "delete", new JsonObject { ["id"] = id })).ToJsonString();

    [McpServerTool(Name = "validate"), Description("Validate a draft without saving it.")]
    public static async Task<string> Validate(AgentPipe pipe, string draftId) =>
        (await pipe.ExecuteAsync(draftId, "validate")).ToJsonString();

    [McpServerTool(Name = "apply"), Description("Validate and save a draft, with a backup of the previous configuration.")]
    public static async Task<string> Apply(AgentPipe pipe, string draftId)
    {
        var response = await pipe.ExecuteAsync(draftId, "apply");
        if (response["Success"]?.GetValue<bool>() == false)
            throw new InvalidOperationException(response["Error"]?.GetValue<string>() ?? "Не удалось применить черновик.");
        return response.ToJsonString();
    }
}
