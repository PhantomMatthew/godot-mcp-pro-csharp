using Godot;
using Godot.Collections;
using GodotMcpPro.Commands;

namespace GodotMcpPro.Server;

/// <summary>
/// MCP (Model Context Protocol) JSON-RPC 2.0 message handler.
/// Implements: initialize, notifications/initialized, ping, tools/list, tools/call.
/// Runs entirely on the Godot main thread.
/// </summary>
public sealed class McpProtocolHandler
{
    public const string ServerName = "godot-mcp-pro-csharp";
    public const string ServerVersion = "0.1.0";
    private static readonly string[] SupportedProtocolVersions =
        { "2025-06-18", "2025-03-26", "2024-11-05" };

    private readonly CommandRouter _router;

    public McpProtocolHandler(CommandRouter router) => _router = router;

    /// <summary>
    /// Handle one raw JSON-RPC message. Returns the response JSON string,
    /// or null when no response should be sent (notifications).
    /// </summary>
    public async Task<string?> HandleMessage(string json)
    {
        var parsed = Json.ParseString(json);
        if (parsed.VariantType != Variant.Type.Dictionary)
            return Stringify(JsonRpcError(default, -32700, "Parse error: invalid JSON"));

        var msg = parsed.AsGodotDictionary();
        var hasId = msg.ContainsKey("id");
        var id = hasId ? msg["id"] : default;
        var method = msg.TryGetValue("method", out var m) ? m.AsString() : "";
        var @params = msg.TryGetValue("params", out var p) && p.VariantType == Variant.Type.Dictionary
            ? p.AsGodotDictionary()
            : new Dictionary();

        // Notifications never get a response.
        if (!hasId)
        {
            // notifications/initialized, notifications/cancelled, etc. — nothing to do.
            return null;
        }

        switch (method)
        {
            case "initialize":
                return Stringify(JsonRpcResult(id, HandleInitialize(@params)));

            case "ping":
                return Stringify(JsonRpcResult(id, new Dictionary()));

            case "tools/list":
                return Stringify(JsonRpcResult(id, HandleToolsList()));

            case "tools/call":
                return Stringify(await HandleToolsCall(id, @params));

            default:
                return Stringify(JsonRpcError(id, -32601, $"Method not found: {method}"));
        }
    }

    private static Dictionary HandleInitialize(Dictionary @params)
    {
        var requested = @params.TryGetValue("protocolVersion", out var pv) ? pv.AsString() : "";
        var negotiated = System.Array.IndexOf(SupportedProtocolVersions, requested) >= 0
            ? requested
            : SupportedProtocolVersions[0];

        return new Dictionary
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new Dictionary
            {
                ["tools"] = new Dictionary { ["listChanged"] = false },
            },
            ["serverInfo"] = new Dictionary
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
            ["instructions"] =
                "MCP server running inside the Godot editor. Tools manipulate the currently "
                + "open project: scenes, nodes, scripts, resources, and the running game. "
                + "All mutations are undoable via the editor's UndoRedo history.",
        };
    }

    private Dictionary HandleToolsList()
    {
        var tools = new Godot.Collections.Array();
        foreach (var tool in _router.Tools)
        {
            if (!_router.IsToolEnabled(tool.Name))
                continue;
            tools.Add(new Dictionary
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema,
            });
        }
        return new Dictionary { ["tools"] = tools };
    }

    private async Task<Dictionary> HandleToolsCall(Variant id, Dictionary @params)
    {
        var name = @params.TryGetValue("name", out var n) ? n.AsString() : "";
        var arguments = @params.TryGetValue("arguments", out var a) && a.VariantType == Variant.Type.Dictionary
            ? a.AsGodotDictionary()
            : new Dictionary();

        if (string.IsNullOrEmpty(name))
            return JsonRpcError(id, -32602, "Missing tool name");

        var envelope = await _router.Execute(name, arguments);

        // Unknown tool is a protocol-level error per the MCP spec.
        if (envelope.TryGetValue("error", out var errVariant))
        {
            var err = errVariant.AsGodotDictionary();
            var code = err.TryGetValue("code", out var c) ? (int)c.AsInt64() : BaseCommand.CodeInternal;
            if (code == BaseCommand.CodeMethodNotFound)
                return JsonRpcError(id, -32602, $"Unknown tool: {name}");

            // Tool execution errors are reported inside the result with isError=true.
            return JsonRpcResult(id, new Dictionary
            {
                ["content"] = new Godot.Collections.Array
                {
                    new Dictionary { ["type"] = "text", ["text"] = Json.Stringify(err) },
                },
                ["isError"] = true,
            });
        }

        var data = envelope.TryGetValue("result", out var r) ? r : new Dictionary();
        return JsonRpcResult(id, new Dictionary
        {
            ["content"] = new Godot.Collections.Array
            {
                new Dictionary { ["type"] = "text", ["text"] = Json.Stringify(data) },
            },
            ["isError"] = false,
        });
    }

    // ── JSON-RPC envelopes ─────────────────────────────────────────────────────

    private static Dictionary JsonRpcResult(Variant id, Dictionary result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static Dictionary JsonRpcError(Variant id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new Dictionary { ["code"] = code, ["message"] = message },
        };

    private static string Stringify(Dictionary d) => Json.Stringify(d);
}
