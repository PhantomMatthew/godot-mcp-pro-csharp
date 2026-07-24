using System;
using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Editor-side commands for runtime game inspection.
/// Communicates with the MCPGameInspector autoload via file-based IPC
/// (see <see cref="BaseCommand.SendGameCommand"/>).
/// Faithful port of addons/godot_mcp/commands/runtime_commands.gd.
/// </summary>
[Tool]
public partial class RuntimeCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "get_game_scene_tree",
            Description = "Get the running game's scene tree, optionally filtered by depth, script path, " +
                "or node type, or restricted to named (non-generated) nodes.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["max_depth"] = Schema.Int("Maximum depth to traverse; -1 for unlimited.", -1),
                ["script_filter"] = Schema.Str("Only include nodes whose attached script path contains this substring."),
                ["type_filter"] = Schema.Str("Only include nodes whose class matches this type."),
                ["named_only"] = Schema.Bool("Only include nodes with a non-generated name.", false),
            }),
            Category = "runtime",
            Handler = GetGameSceneTree,
        },
        new()
        {
            Name = "get_game_node_properties",
            Description = "Get properties of a node in the running game, optionally restricted to a property list.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the node in the running game's scene tree."),
                ["properties"] = Schema.Arr("Optional list of property names to fetch; omit for all.", Schema.Str("Property name.")),
            }, "node_path"),
            Category = "runtime",
            Handler = GetGameNodeProperties,
        },
        new()
        {
            Name = "set_game_node_property",
            Description = "Set a property on a node in the running game.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the node in the running game's scene tree."),
                ["property"] = Schema.Str("Name of the property to set."),
                ["value"] = Schema.Any("New value for the property."),
            }, "node_path", "property", "value"),
            Category = "runtime",
            Handler = SetGameNodeProperty,
        },
        new()
        {
            Name = "capture_frames",
            Description = "Capture a sequence of frames from the running game as images.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["count"] = Schema.Int("Number of frames to capture.", 5),
                ["frame_interval"] = Schema.Int("Frames to skip between captures.", 10),
                ["half_resolution"] = Schema.Bool("Capture at half the viewport resolution.", true),
            }),
            Category = "runtime",
            Handler = CaptureFrames,
        },
        new()
        {
            Name = "monitor_properties",
            Description = "Sample one or more node properties over a number of frames in the running game.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the node in the running game's scene tree."),
                ["properties"] = Schema.Arr("List of property names to sample.", Schema.Str("Property name.")),
                ["frame_count"] = Schema.Int("Number of frames to sample.", 60),
                ["frame_interval"] = Schema.Int("Frames to skip between samples.", 1),
            }, "node_path", "properties"),
            Category = "runtime",
            Handler = MonitorProperties,
        },
        new()
        {
            Name = "execute_game_script",
            Description = "Execute an arbitrary GDScript expression/snippet inside the running game process.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["code"] = Schema.Str("GDScript source code to execute in the running game."),
            }, "code"),
            Category = "runtime",
            Handler = ExecuteGameScript,
        },
        new()
        {
            Name = "start_recording",
            Description = "Start recording input events in the running game for later replay.",
            InputSchema = Schema.Object(),
            Category = "runtime",
            Handler = StartRecording,
        },
        new()
        {
            Name = "stop_recording",
            Description = "Stop the active input recording in the running game and return the captured events.",
            InputSchema = Schema.Object(),
            Category = "runtime",
            Handler = StopRecording,
        },
        new()
        {
            Name = "replay_recording",
            Description = "Replay a previously captured sequence of input events in the running game.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["events"] = Schema.Arr("Recorded events (each with a time_ms key) to replay.", Schema.Obj("Recorded event.")),
                ["speed"] = Schema.Num("Playback speed multiplier.", 1.0),
            }, "events"),
            Category = "runtime",
            Handler = ReplayRecording,
        },
        new()
        {
            Name = "find_nodes_by_script",
            Description = "Find all nodes in the running game whose attached script matches the given path.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["script"] = Schema.Str("Script path (or substring) to match against attached node scripts."),
                ["properties"] = Schema.Arr("Optional list of property names to include for each match.", Schema.Str("Property name.")),
            }, "script"),
            Category = "runtime",
            Handler = FindNodesByScript,
        },
        new()
        {
            Name = "get_autoload",
            Description = "Get an autoload singleton's properties from the running game.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["name"] = Schema.Str("Name of the autoload singleton."),
                ["properties"] = Schema.Arr("Optional list of property names to fetch; omit for all.", Schema.Str("Property name.")),
            }, "name"),
            Category = "runtime",
            Handler = GetAutoload,
        },
        new()
        {
            Name = "batch_get_properties",
            Description = "Get properties from multiple nodes in the running game in a single call.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["nodes"] = Schema.Arr(
                    "List of requests, each with a node_path and optional properties list.",
                    Schema.Obj("Node property request.")),
            }, "nodes"),
            Category = "runtime",
            Handler = BatchGetProperties,
        },
        new()
        {
            Name = "find_ui_elements",
            Description = "Find UI (Control) elements currently present in the running game, optionally filtered by type.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["type_filter"] = Schema.Str("Only include Control nodes whose class matches this type."),
            }),
            Category = "runtime",
            Handler = FindUiElements,
        },
        new()
        {
            Name = "click_button_by_text",
            Description = "Find and click a button in the running game by its visible text.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["text"] = Schema.Str("Text to match against button labels."),
                ["partial"] = Schema.Bool("Allow partial (substring) text matches.", true),
            }, "text"),
            Category = "runtime",
            Handler = ClickButtonByText,
        },
        new()
        {
            Name = "wait_for_node",
            Description = "Poll the running game until a node at the given path appears, or time out.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the node to wait for in the running game's scene tree."),
                ["timeout"] = Schema.Num("Maximum seconds to wait.", 5.0),
                ["poll_frames"] = Schema.Int("Frames to wait between polls.", 5),
            }, "node_path"),
            Category = "runtime",
            Handler = WaitForNode,
        },
        new()
        {
            Name = "find_nearby_nodes",
            Description = "Find nodes near a given position in the running game, optionally filtered by type, " +
                "group, or radius.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["position"] = Schema.Any("Center position (Vector2/Vector3-shaped value) to search from."),
                ["radius"] = Schema.Num("Search radius around the position."),
                ["type_filter"] = Schema.Str("Only include nodes whose class matches this type."),
                ["group_filter"] = Schema.Str("Only include nodes that belong to this group."),
                ["max_results"] = Schema.Int("Maximum number of results to return."),
            }, "position"),
            Category = "runtime",
            Handler = FindNearbyNodes,
        },
        new()
        {
            Name = "navigate_to",
            Description = "Instantly move/orient the player (or camera) toward a target in the running game.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["target"] = Schema.Any("Target position, or a node path, to navigate to."),
                ["player_path"] = Schema.Str("Path to the player node; defaults to the game's convention."),
                ["camera_path"] = Schema.Str("Path to the camera node; defaults to the game's convention."),
                ["move_speed"] = Schema.Num("Movement speed used for the navigation."),
            }, "target"),
            Category = "runtime",
            Handler = NavigateTo,
        },
        new()
        {
            Name = "move_to",
            Description = "Move the player toward a target over time in the running game, waiting for arrival " +
                "or a game-side timeout.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["target"] = Schema.Any("Target position, or a node path, to move toward."),
                ["player_path"] = Schema.Str("Path to the player node; defaults to the game's convention."),
                ["camera_path"] = Schema.Str("Path to the camera node; defaults to the game's convention."),
                ["arrival_radius"] = Schema.Num("Distance from the target considered as arrival."),
                ["timeout"] = Schema.Num("Maximum seconds the game will spend moving before giving up.", 15.0),
                ["run"] = Schema.Bool("Whether to run instead of walk."),
                ["look_at_target"] = Schema.Bool("Whether to orient the player toward the target while moving."),
            }, "target"),
            Category = "runtime",
            Handler = MoveTo,
        },
        new()
        {
            Name = "watch_signals",
            Description = "Watch one or more nodes in the running game for emitted signals over a duration.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_paths"] = Schema.Arr("Paths to the nodes to watch.", Schema.Str("Node path.")),
                ["signal_filter"] = Schema.Arr("Optional list of signal names to restrict watching to.", Schema.Str("Signal name.")),
                ["duration_ms"] = Schema.Int("Duration to watch for, in milliseconds.", 5000),
            }, "node_paths"),
            Category = "runtime",
            Handler = WatchSignals,
        },
    };

    // ── get_game_scene_tree ─────────────────────────────────────────────────

    private async Task<Dictionary> GetGameSceneTree(Dictionary @params)
    {
        var maxDepth = OptionalInt(@params, "max_depth", -1);
        var cmdParams = new Dictionary { ["max_depth"] = maxDepth };

        var scriptFilter = OptionalString(@params, "script_filter");
        if (!string.IsNullOrEmpty(scriptFilter))
            cmdParams["script_filter"] = scriptFilter;

        var typeFilter = OptionalString(@params, "type_filter");
        if (!string.IsNullOrEmpty(typeFilter))
            cmdParams["type_filter"] = typeFilter;

        var namedOnly = OptionalBool(@params, "named_only", false);
        if (namedOnly)
            cmdParams["named_only"] = true;

        return await RunGameCommand("get_scene_tree", cmdParams);
    }

    // ── get_game_node_properties ────────────────────────────────────────────

    private async Task<Dictionary> GetGameNodeProperties(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var cmdParams = new Dictionary { ["node_path"] = nodePath };
        var properties = OptionalArray(@params, "properties");
        if (properties != null)
            cmdParams["properties"] = properties;

        return await RunGameCommand("get_node_properties", cmdParams);
    }

    // ── set_game_node_property ──────────────────────────────────────────────

    private async Task<Dictionary> SetGameNodeProperty(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var (property, propErr) = RequireString(@params, "property");
        if (propErr != null)
            return propErr;

        if (!@params.TryGetValue("value", out var value))
            return ErrorInvalidParams("Missing required parameter: value");

        var cmdParams = new Dictionary
        {
            ["node_path"] = nodePath,
            ["property"] = property,
            ["value"] = value,
        };
        return await RunGameCommand("set_node_property", cmdParams);
    }

    // ── execute_game_script ─────────────────────────────────────────────────

    private async Task<Dictionary> ExecuteGameScript(Dictionary @params)
    {
        var (code, err) = RequireString(@params, "code");
        if (err != null)
            return err;

        return await RunGameCommand("execute_script", new Dictionary { ["code"] = code }, 10.0);
    }

    // ── capture_frames ───────────────────────────────────────────────────────

    private async Task<Dictionary> CaptureFrames(Dictionary @params)
    {
        var count = OptionalInt(@params, "count", 5);
        var frameInterval = OptionalInt(@params, "frame_interval", 10);
        var halfResolution = OptionalBool(@params, "half_resolution", true);

        // Dynamic timeout: allow enough time for frame capture.
        // At 60fps, 30 frames * 10 interval = 300 frames = 5 seconds + overhead.
        var estimatedSeconds = (count * frameInterval) / 60.0 + 2.0;
        var timeout = Math.Min(estimatedSeconds, 25.0);

        var cmdParams = new Dictionary
        {
            ["count"] = count,
            ["frame_interval"] = frameInterval,
            ["half_resolution"] = halfResolution,
        };
        return await RunGameCommand("capture_frames", cmdParams, timeout);
    }

    // ── monitor_properties ──────────────────────────────────────────────────

    private async Task<Dictionary> MonitorProperties(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var (properties, propsErr) = RequireArray(@params, "properties", "'properties' array is required");
        if (propsErr != null)
            return propsErr;

        var frameCount = OptionalInt(@params, "frame_count", 60);
        var frameInterval = OptionalInt(@params, "frame_interval", 1);

        // Dynamic timeout.
        var estimatedSeconds = (frameCount * frameInterval) / 60.0 + 2.0;
        var timeout = Math.Min(estimatedSeconds, 25.0);

        var cmdParams = new Dictionary
        {
            ["node_path"] = nodePath,
            ["properties"] = properties,
            ["frame_count"] = frameCount,
            ["frame_interval"] = frameInterval,
        };
        return await RunGameCommand("monitor_properties", cmdParams, timeout);
    }

    // ── start_recording / stop_recording ────────────────────────────────────

    private async Task<Dictionary> StartRecording(Dictionary @params) =>
        await RunGameCommand("start_recording", new Dictionary());

    private async Task<Dictionary> StopRecording(Dictionary @params) =>
        await RunGameCommand("stop_recording", new Dictionary(), 5.0);

    // ── replay_recording ─────────────────────────────────────────────────────

    private async Task<Dictionary> ReplayRecording(Dictionary @params)
    {
        var (events, err) = RequireArray(@params, "events", "'events' array is required");
        if (err != null)
            return err;

        var speed = OptionalFloat(@params, "speed", 1.0);

        // Calculate timeout based on event duration.
        var maxTimeMs = 0;
        foreach (var eventVariant in events)
        {
            if (eventVariant.VariantType != Variant.Type.Dictionary)
                continue;
            var t = OptionalInt(eventVariant.AsGodotDictionary(), "time_ms", 0);
            if (t > maxTimeMs)
                maxTimeMs = t;
        }
        var timeout = Math.Min((maxTimeMs / 1000.0 / speed) + 5.0, 120.0);

        var cmdParams = new Dictionary { ["events"] = events, ["speed"] = speed };
        return await RunGameCommand("replay_recording", cmdParams, timeout);
    }

    // ── find_nodes_by_script ─────────────────────────────────────────────────

    private async Task<Dictionary> FindNodesByScript(Dictionary @params)
    {
        var (script, err) = RequireString(@params, "script");
        if (err != null)
            return err;

        var cmdParams = new Dictionary { ["script"] = script };
        var properties = OptionalArray(@params, "properties");
        if (properties != null)
            cmdParams["properties"] = properties;

        return await RunGameCommand("find_nodes_by_script", cmdParams);
    }

    // ── get_autoload ─────────────────────────────────────────────────────────

    private async Task<Dictionary> GetAutoload(Dictionary @params)
    {
        var (name, err) = RequireString(@params, "name");
        if (err != null)
            return err;

        var cmdParams = new Dictionary { ["name"] = name };
        var properties = OptionalArray(@params, "properties");
        if (properties != null)
            cmdParams["properties"] = properties;

        return await RunGameCommand("get_autoload", cmdParams);
    }

    // ── batch_get_properties ─────────────────────────────────────────────────

    private async Task<Dictionary> BatchGetProperties(Dictionary @params)
    {
        var (nodes, err) = RequireArray(@params, "nodes", "'nodes' array is required");
        if (err != null)
            return err;

        return await RunGameCommand("batch_get_properties", new Dictionary { ["nodes"] = nodes });
    }

    // ── find_ui_elements ─────────────────────────────────────────────────────

    private async Task<Dictionary> FindUiElements(Dictionary @params)
    {
        var cmdParams = new Dictionary();
        var typeFilter = OptionalString(@params, "type_filter");
        if (!string.IsNullOrEmpty(typeFilter))
            cmdParams["type_filter"] = typeFilter;

        return await RunGameCommand("find_ui_elements", cmdParams);
    }

    // ── click_button_by_text ─────────────────────────────────────────────────

    private async Task<Dictionary> ClickButtonByText(Dictionary @params)
    {
        var (text, err) = RequireString(@params, "text");
        if (err != null)
            return err;

        var partial = OptionalBool(@params, "partial", true);
        var cmdParams = new Dictionary { ["text"] = text, ["partial"] = partial };

        return await RunGameCommand("click_button_by_text", cmdParams);
    }

    // ── wait_for_node ────────────────────────────────────────────────────────

    private async Task<Dictionary> WaitForNode(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var timeout = OptionalFloat(@params, "timeout", 5.0);
        var pollFrames = OptionalInt(@params, "poll_frames", 5);

        var cmdParams = new Dictionary
        {
            ["node_path"] = nodePath,
            ["timeout"] = timeout,
            ["poll_frames"] = pollFrames,
        };
        return await RunGameCommand("wait_for_node", cmdParams, timeout + 2.0);
    }

    // ── find_nearby_nodes ────────────────────────────────────────────────────

    private async Task<Dictionary> FindNearbyNodes(Dictionary @params)
    {
        if (!@params.TryGetValue("position", out var position))
            return ErrorInvalidParams("Missing required parameter: position");

        var cmdParams = new Dictionary { ["position"] = position };
        if (@params.ContainsKey("radius"))
            cmdParams["radius"] = OptionalFloat(@params, "radius");

        var typeFilter = OptionalString(@params, "type_filter");
        if (!string.IsNullOrEmpty(typeFilter))
            cmdParams["type_filter"] = typeFilter;

        var groupFilter = OptionalString(@params, "group_filter");
        if (!string.IsNullOrEmpty(groupFilter))
            cmdParams["group_filter"] = groupFilter;

        if (@params.ContainsKey("max_results"))
            cmdParams["max_results"] = OptionalInt(@params, "max_results");

        return await RunGameCommand("find_nearby_nodes", cmdParams);
    }

    // ── navigate_to ──────────────────────────────────────────────────────────

    private async Task<Dictionary> NavigateTo(Dictionary @params)
    {
        if (!@params.TryGetValue("target", out var target))
            return ErrorInvalidParams("Missing required parameter: target");

        var cmdParams = new Dictionary { ["target"] = target };

        var playerPath = OptionalString(@params, "player_path");
        if (!string.IsNullOrEmpty(playerPath))
            cmdParams["player_path"] = playerPath;

        var cameraPath = OptionalString(@params, "camera_path");
        if (!string.IsNullOrEmpty(cameraPath))
            cmdParams["camera_path"] = cameraPath;

        if (@params.ContainsKey("move_speed"))
            cmdParams["move_speed"] = OptionalFloat(@params, "move_speed");

        return await RunGameCommand("navigate_to", cmdParams);
    }

    // ── move_to ──────────────────────────────────────────────────────────────

    private async Task<Dictionary> MoveTo(Dictionary @params)
    {
        if (!@params.TryGetValue("target", out var target))
            return ErrorInvalidParams("Missing required parameter: target");

        var cmdParams = new Dictionary { ["target"] = target };

        var playerPath = OptionalString(@params, "player_path");
        if (!string.IsNullOrEmpty(playerPath))
            cmdParams["player_path"] = playerPath;

        var cameraPath = OptionalString(@params, "camera_path");
        if (!string.IsNullOrEmpty(cameraPath))
            cmdParams["camera_path"] = cameraPath;

        if (@params.ContainsKey("arrival_radius"))
            cmdParams["arrival_radius"] = OptionalFloat(@params, "arrival_radius");

        if (@params.ContainsKey("timeout"))
            cmdParams["timeout"] = OptionalFloat(@params, "timeout");

        if (@params.ContainsKey("run"))
            cmdParams["run"] = OptionalBool(@params, "run");

        if (@params.ContainsKey("look_at_target"))
            cmdParams["look_at_target"] = OptionalBool(@params, "look_at_target");

        // Dynamic timeout: game-side timeout + overhead for IPC polling.
        var gameTimeout = OptionalFloat(@params, "timeout", 15.0);
        var ipcTimeout = gameTimeout + 5.0;

        return await RunGameCommand("move_to", cmdParams, ipcTimeout);
    }

    // ── watch_signals ────────────────────────────────────────────────────────

    private async Task<Dictionary> WatchSignals(Dictionary @params)
    {
        var (nodePaths, err) = RequireArray(@params, "node_paths", "Missing required parameter: node_paths (Array)");
        if (err != null)
            return err;

        var cmdParams = new Dictionary { ["node_paths"] = nodePaths };

        var signalFilter = OptionalArray(@params, "signal_filter");
        if (signalFilter != null)
            cmdParams["signal_filter"] = signalFilter;

        var durationMs = OptionalInt(@params, "duration_ms", 5000);
        cmdParams["duration_ms"] = durationMs;

        // Dynamic timeout: duration + overhead.
        var timeoutSec = (durationMs / 1000.0) + 5.0;

        return await RunGameCommand("watch_signals", cmdParams, timeoutSec);
    }

    // ── shared helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Send a command to the game process and normalize the reply: the game
    /// writes its own {"result": ...} envelope which the file-IPC transport
    /// wraps again, so unwrap defensively before re-wrapping for the MCP client.
    /// </summary>
    private async Task<Dictionary> RunGameCommand(string command, Dictionary cmdParams, double timeoutSec = 5.0)
    {
        var result = await SendGameCommand(command, cmdParams, timeoutSec);
        return IsError(result) ? result : Success(UnwrapGameResult(result));
    }

    /// <summary>Get a required array param. Error is non-null when missing or not an array.</summary>
    private static (Godot.Collections.Array Value, Dictionary? Error) RequireArray(
        Dictionary @params, string key, string errorMessage)
    {
        if (!@params.TryGetValue(key, out var v) || v.VariantType != Variant.Type.Array)
            return (new Godot.Collections.Array(), ErrorInvalidParams(errorMessage));
        return (v.AsGodotArray(), null);
    }
}
