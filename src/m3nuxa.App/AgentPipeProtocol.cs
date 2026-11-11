using System.Text.Json.Nodes;

namespace m3nuxa.App;

internal static class AgentPipeProtocol
{
    public const int Version = 1;

    public static JsonObject CreateRequest(string session, string operation, JsonObject? arguments = null) => new()
    {
        ["protocolVersion"] = Version,
        ["session"] = session,
        ["operation"] = operation,
        ["arguments"] = arguments ?? new JsonObject()
    };
}
