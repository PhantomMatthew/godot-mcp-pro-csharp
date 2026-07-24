using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Miscellaneous editor/game controls that don't warrant a dedicated category:
/// raising the editor's OS window and adjusting Engine.TimeScale at runtime.
/// </summary>
[Tool]
public partial class MiscCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "editor_focus",
            Description = "Bring the Godot editor's OS window to the foreground: requests user attention " +
                "(taskbar/dock flash on platforms with focus-stealing prevention), raises the window to " +
                "the front, and grabs input focus on it.",
            InputSchema = Schema.Object(),
            Category = "misc",
            Handler = p => Task.FromResult(EditorFocus()),
        },
        new()
        {
            Name = "set_time_scale",
            Description = "Set Engine.TimeScale for the running game (0.1 = very slow motion, 1.0 = normal " +
                "speed, up to 10.0 = fast forward). Requires a playing scene; the value applies to the " +
                "game process until changed again or the game stops.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["scale"] = Schema.Num("New time scale, clamped to [0.1, 10.0]. 1.0 is normal speed."),
            }, "scale"),
            Category = "misc",
            Handler = p => Task.FromResult(SetTimeScale(p)),
        },
    };

    // ── editor_focus ─────────────────────────────────────────────────────────

    private Dictionary EditorFocus()
    {
        var windowId = (int)DisplayServer.MainWindowId;

        // Attention request first (flash on dock/taskbar), then raise + focus.
        DisplayServer.Singleton.WindowRequestAttention(windowId);
        DisplayServer.Singleton.WindowMoveToForeground(windowId);
        GetTree().Root.GrabFocus();

        return Success(new Dictionary
        {
            ["focused"] = true,
            ["window_id"] = windowId,
        });
    }

    // ── set_time_scale ───────────────────────────────────────────────────────

    private static Dictionary SetTimeScale(Dictionary @params)
    {
        if (!@params.ContainsKey("scale"))
            return ErrorInvalidParams("Missing required parameter: scale");

        if (!GetEditor().IsPlayingScene())
        {
            return Error(CodeGeneric, "No scene is currently playing",
                new Dictionary
                {
                    ["suggestion"] = "Use play_scene first; Engine.TimeScale only takes effect while a game is running.",
                });
        }

        var previous = Engine.TimeScale;
        var scale = Mathf.Clamp(OptionalFloat(@params, "scale", 1.0), 0.1, 10.0);
        Engine.TimeScale = scale;

        return Success(new Dictionary
        {
            ["time_scale"] = Engine.TimeScale,
            ["previous_time_scale"] = previous,
        });
    }
}
