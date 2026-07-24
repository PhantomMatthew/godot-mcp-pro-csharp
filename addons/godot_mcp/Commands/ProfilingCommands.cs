using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Performance monitoring tools for the running game and the editor process.
/// Faithful port of addons/godot_mcp/commands/profiling_commands.gd.
/// </summary>
[Tool]
public partial class ProfilingCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "get_performance_monitors",
            Description = "Get performance monitors from the running game process via the file-IPC bridge " +
                "(Performance is a per-process singleton, so this must route through the game rather than " +
                "reading the editor's own metrics). Optionally filter by a category name prefix.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["category"] = Schema.Str(
                    "When set, only monitors whose key starts with this prefix are returned (e.g. 'render', 'memory').",
                    ""),
            }),
            Category = "profiling",
            Handler = GetPerformanceMonitors,
        },
        new()
        {
            Name = "get_editor_performance",
            Description = "Get a quick summary of the editor process's own performance monitors (FPS, frame " +
                "time, draw calls, object/node counts, memory usage). For the running game's metrics, use " +
                "get_performance_monitors instead.",
            InputSchema = Schema.Object(),
            Category = "profiling",
            Handler = p => Task.FromResult(GetEditorPerformance(p)),
        },
    };

    private async Task<Dictionary> GetPerformanceMonitors(Dictionary @params)
    {
        // Performance is a per-process singleton: reading it here would report the
        // EDITOR's metrics, not the game's. Route through the game IPC channel.
        if (GetEditor().IsPlayingScene())
        {
            var gameResult = await SendGameCommand("get_performance_monitors", new Dictionary(), 5.0);
            if (gameResult.ContainsKey("error"))
                return gameResult;

            var payload = UnwrapGameResult(gameResult);
            var gameMonitors = payload.TryGetValue("monitors", out var monitorsVariant)
                && monitorsVariant.VariantType == Variant.Type.Dictionary
                ? monitorsVariant.AsGodotDictionary()
                : new Dictionary();

            var category = OptionalString(@params, "category", "");
            if (!string.IsNullOrEmpty(category))
            {
                var filtered = new Dictionary();
                foreach (var key in gameMonitors.Keys)
                {
                    var keyStr = key.AsString();
                    if (keyStr.StartsWith(category))
                        filtered[key] = gameMonitors[key];
                }
                return Success(new Dictionary { ["monitors"] = filtered, ["category"] = category, ["process"] = "game" });
            }
            return Success(new Dictionary { ["monitors"] = gameMonitors, ["process"] = "game" });
        }

        return Error(CodeGeneric, "No scene is currently playing",
            new Dictionary { ["suggestion"] = "Use play_scene first. For editor-process metrics, use get_editor_performance." });
    }

    private static Dictionary GetEditorPerformance(Dictionary @params)
    {
        // Quick summary for common use
        var summary = new Dictionary
        {
            ["fps"] = Performance.GetMonitor(Performance.Monitor.TimeFps),
            ["frame_time_msec"] = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0,
            ["draw_calls"] = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            ["objects_in_frame"] = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
            ["node_count"] = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
            ["orphan_nodes"] = Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount),
            ["memory_static_mb"] = Performance.GetMonitor(Performance.Monitor.MemoryStatic) / (1024.0 * 1024.0),
            ["video_mem_mb"] = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0),
        };
        return Success(summary);
    }
}
