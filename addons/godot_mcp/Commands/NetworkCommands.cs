using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Network-related node scaffolding tools. These create and configure nodes in
/// the editor (they do NOT perform any runtime networking themselves):
/// a multiplayer manager node carrying ENetMultiplayerPeer configuration, and
/// a pre-configured HTTPRequest node. Node creation is undo-tracked.
/// </summary>
[Tool]
public partial class NetworkCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "setup_multiplayer",
            Description = "Create a multiplayer manager node configured for ENetMultiplayerPeer (server or client). SceneMultiplayer is a RefCounted MultiplayerAPI, not a Node, so a plain Node is created carrying the configuration as metadata (peer_type, server_mode, max_clients, port, address) for a runtime script to consume. Either adds it as a child of parent_path (undo-tracked) or saves it as a new scene and registers it as an autoload.",
            Category = "network",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Path to the parent node in the edited scene. Mutually exclusive with autoload_name."),
                ["autoload_name"] = Schema.Str("Register the node as an autoload singleton with this name (saved as res://<autoload_name>.tscn). Mutually exclusive with parent_path."),
                ["server_mode"] = Schema.Bool("Configure as server (true) or client (false).", false),
                ["max_clients"] = Schema.Int("Maximum number of connected clients (server mode).", 32),
                ["port"] = Schema.Int("Port the ENet peer binds to (server) or connects to (client).", 7777),
                ["address"] = Schema.Str("Remote address to connect to (client mode).", "127.0.0.1"),
            }),
            Handler = p => Task.FromResult(SetupMultiplayer(p)),
        },
        new()
        {
            Name = "setup_http_request",
            Description = "Create an HTTPRequest node under parent_path, configured for a specific URL. The URL/method/headers/body are stored as node metadata (HTTPRequest itself has no URL property — they are consumed by a runtime script calling request()). Does NOT execute any request. Undo-tracked.",
            Category = "network",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Path to the parent node in the edited scene."),
                ["name"] = Schema.Str("Name for the new HTTPRequest node.", "HTTPRequest"),
                ["url"] = Schema.Str("Request URL. If no scheme is present, one is added based on use_https."),
                ["method"] = Schema.Enum("HTTP method.", "get", "post", "put", "delete"),
                ["headers"] = Schema.Arr("Request headers, e.g. [\"Content-Type: application/json\"]."),
                ["body"] = Schema.Str("Request body (for post/put)."),
                ["use_https"] = Schema.Bool("Use HTTPS when the URL has no explicit scheme.", true),
            }, "parent_path", "url"),
            Handler = p => Task.FromResult(SetupHttpRequest(p)),
        },
    };

    // ── setup_multiplayer ─────────────────────────────────────────────────

    private Dictionary SetupMultiplayer(Dictionary @params)
    {
        var parentPath = OptionalString(@params, "parent_path");
        var autoloadName = OptionalString(@params, "autoload_name");

        if (string.IsNullOrEmpty(parentPath) && string.IsNullOrEmpty(autoloadName))
            return ErrorInvalidParams("Must provide either 'parent_path' or 'autoload_name'");
        if (!string.IsNullOrEmpty(parentPath) && !string.IsNullOrEmpty(autoloadName))
            return ErrorInvalidParams("Provide only one of 'parent_path' or 'autoload_name', not both");

        var serverMode = OptionalBool(@params, "server_mode", false);
        var maxClients = OptionalInt(@params, "max_clients", 32);
        var port = OptionalInt(@params, "port", 7777);
        var address = OptionalString(@params, "address", "127.0.0.1");

        if (port < 0 || port > 65535)
            return ErrorInvalidParams("port must be between 0 and 65535");
        if (maxClients < 0 || maxClients > 4095)
            return ErrorInvalidParams("max_clients must be between 0 and 4095");

        var node = new Node
        {
            Name = string.IsNullOrEmpty(autoloadName) ? "MultiplayerManager" : autoloadName,
        };
        node.SetMeta("peer_type", "ENetMultiplayerPeer");
        node.SetMeta("multiplayer_role", serverMode ? "server" : "client");
        node.SetMeta("server_mode", serverMode);
        node.SetMeta("max_clients", maxClients);
        node.SetMeta("port", port);
        node.SetMeta("address", address);

        var config = new Dictionary
        {
            ["peer_type"] = "ENetMultiplayerPeer",
            ["server_mode"] = serverMode,
            ["max_clients"] = maxClients,
            ["port"] = port,
            ["address"] = address,
        };

        if (!string.IsNullOrEmpty(autoloadName))
            return RegisterMultiplayerAutoload(node, autoloadName, config);

        var root = GetEditedRoot();
        if (root == null)
        {
            node.Free();
            return ErrorNoScene();
        }

        var parent = FindNodeByPath(parentPath);
        if (parent == null)
        {
            node.Free();
            return ErrorNotFound($"Node '{parentPath}'", "Use get_scene_tree to see available nodes");
        }

        AddChildWithUndo(parent, node, root, "MCP: Setup Multiplayer");
        MarkCurrentSceneUnsaved();

        return Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["name"] = node.Name.ToString(),
            ["config"] = config,
        });
    }

    private static Dictionary RegisterMultiplayerAutoload(Node node, string autoloadName, Dictionary config)
    {
        var scenePath = $"res://{autoloadName}.tscn";
        if (Godot.FileAccess.FileExists(scenePath))
        {
            node.Free();
            return ErrorConflict($"Scene file '{scenePath}' already exists",
                new Dictionary { ["suggestion"] = "Choose a different autoload_name or remove the existing file" });
        }

        var settingKey = "autoload/" + autoloadName;
        if (ProjectSettings.HasSetting(settingKey))
        {
            node.Free();
            return ErrorConflict($"Autoload '{autoloadName}' already exists",
                new Dictionary { ["suggestion"] = "Use remove_autoload first to replace it" });
        }

        var packed = new PackedScene();
        var packErr = packed.Pack(node);
        if (packErr != Godot.Error.Ok)
        {
            node.Free();
            return ErrorInternal($"Failed to pack multiplayer node: {packErr}");
        }

        var saveSceneErr = ResourceSaver.Save(packed, scenePath);
        if (saveSceneErr != Godot.Error.Ok)
        {
            node.Free();
            return ErrorInternal($"Failed to save '{scenePath}': {saveSceneErr}");
        }

        // Autoload format: "*res://path.tscn" (the * prefix means it's a singleton)
        ProjectSettings.SetSetting(settingKey, "*" + scenePath);
        var saveErr = ProjectSettings.Save();
        if (saveErr != Godot.Error.Ok)
        {
            ProjectSettings.Clear(settingKey);
            ProjectSettings.Save();
            node.Free();
            return ErrorInternal($"Failed to save project settings: {saveErr}");
        }

        return Success(new Dictionary
        {
            ["autoload_name"] = autoloadName,
            ["scene_path"] = scenePath,
            ["config"] = config,
            ["registered"] = true,
        });
    }

    // ── setup_http_request ────────────────────────────────────────────────

    private static readonly System.Collections.Generic.Dictionary<string, int> HttpMethodMap = new()
    {
        ["get"] = (int)Godot.HttpClient.Method.Get,
        ["post"] = (int)Godot.HttpClient.Method.Post,
        ["put"] = (int)Godot.HttpClient.Method.Put,
        ["delete"] = (int)Godot.HttpClient.Method.Delete,
    };

    private Dictionary SetupHttpRequest(Dictionary @params)
    {
        var (parentPath, err) = RequireString(@params, "parent_path");
        if (err != null) return err;

        var (url, err2) = RequireString(@params, "url");
        if (err2 != null) return err2;

        var name = OptionalString(@params, "name", "HTTPRequest");
        var method = OptionalString(@params, "method", "get").ToLowerInvariant();
        var body = OptionalString(@params, "body");
        var useHttps = OptionalBool(@params, "use_https", true);
        var headers = OptionalArray(@params, "headers");

        if (!HttpMethodMap.TryGetValue(method, out var methodEnum))
            return ErrorInvalidParams("method must be one of: get, post, put, delete");

        var root = GetEditedRoot();
        if (root == null) return ErrorNoScene();

        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return ErrorNotFound($"Node '{parentPath}'", "Use get_scene_tree to see available nodes");

        // Normalize URL scheme
        var normalizedUrl = url;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalizedUrl = (useHttps ? "https://" : "http://") + url;
        }

        var headerStrings = new List<string>();
        if (headers != null)
        {
            foreach (var h in headers)
                headerStrings.Add(h.VariantType == Variant.Type.String ? h.AsString() : h.ToString());
        }

        var node = new HttpRequest { Name = name };
        node.SetMeta("url", normalizedUrl);
        node.SetMeta("method", method.ToUpperInvariant());
        node.SetMeta("method_enum", methodEnum);
        node.SetMeta("use_https", useHttps);
        if (!string.IsNullOrEmpty(body))
            node.SetMeta("body", body);
        if (headerStrings.Count > 0)
            node.SetMeta("headers", headerStrings.ToArray());

        AddChildWithUndo(parent, node, root, "MCP: Setup HTTPRequest");
        MarkCurrentSceneUnsaved();

        var result = new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["name"] = node.Name.ToString(),
            ["url"] = normalizedUrl,
            ["method"] = method.ToUpperInvariant(),
            ["method_enum"] = methodEnum,
            ["use_https"] = useHttps,
        };
        if (!string.IsNullOrEmpty(body))
            result["body"] = body;
        if (headerStrings.Count > 0)
        {
            var headerArr = new Godot.Collections.Array();
            foreach (var h in headerStrings) headerArr.Add(h);
            result["headers"] = headerArr;
        }

        return Success(result);
    }
}
