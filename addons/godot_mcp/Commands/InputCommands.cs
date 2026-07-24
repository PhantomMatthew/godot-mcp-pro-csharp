using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Simulate keyboard/mouse/action input events in the currently running game
/// via a file-based IPC bridge (the game reads and replays "mcp_input_commands").
/// Faithful port of addons/godot_mcp/commands/input_commands.gd.
/// </summary>
[Tool]
public partial class InputCommands : BaseCommand
{
    private const string CommandsFileName = "mcp_input_commands";

    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "simulate_key",
            Description = "Simulate a keyboard key press/release in the running game by writing an " +
                "input event to the game's file-IPC command channel.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["keycode"] = Schema.Str(
                    "Key name string recognized by Godot's OS.find_keycode_from_string (e.g. 'A', 'Space', 'Escape')."),
                ["pressed"] = Schema.Bool("Whether the key is pressed (true) or released (false).", true),
                ["shift"] = Schema.Bool("Whether Shift is held.", false),
                ["ctrl"] = Schema.Bool("Whether Ctrl is held.", false),
                ["alt"] = Schema.Bool("Whether Alt is held.", false),
            }, "keycode"),
            Category = "input",
            Handler = p => Task.FromResult(SimulateKey(p)),
        },
        new()
        {
            Name = "simulate_mouse_click",
            Description = "Simulate a mouse button click at a given viewport position in the running game. " +
                "By default automatically sends a press followed by a release so UI buttons actually fire.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["button"] = Schema.Int("Mouse button index (1=left, 2=right, 3=middle).", 1),
                ["pressed"] = Schema.Bool("Whether the button is pressed (true) or released (false).", true),
                ["double_click"] = Schema.Bool("Whether this is a double-click event.", false),
                ["auto_release"] = Schema.Bool(
                    "When pressed=true, automatically send a matching release event one frame later.", true),
                ["x"] = Schema.Num("Viewport X position of the click.", 0),
                ["y"] = Schema.Num("Viewport Y position of the click.", 0),
            }),
            Category = "input",
            Handler = p => Task.FromResult(SimulateMouseClick(p)),
        },
        new()
        {
            Name = "simulate_mouse_move",
            Description = "Simulate a mouse motion event in the running game, optionally with a relative " +
                "delta and pressed-button mask (for drag gestures).",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["x"] = Schema.Num("Viewport X position of the motion.", 0),
                ["y"] = Schema.Num("Viewport Y position of the motion.", 0),
                ["relative_x"] = Schema.Num("Relative X delta since the previous motion event.", 0),
                ["relative_y"] = Schema.Num("Relative Y delta since the previous motion event.", 0),
                ["button_mask"] = Schema.Int("Bitmask of mouse buttons currently held during the motion.", 0),
                ["unhandled"] = Schema.Bool(
                    "Force-mark the event as unhandled input. If omitted, this is auto-enabled when " +
                    "button_mask > 0 (drag/camera-pan use case); pass explicitly to override that default."),
            }),
            Category = "input",
            Handler = p => Task.FromResult(SimulateMouseMove(p)),
        },
        new()
        {
            Name = "simulate_action",
            Description = "Simulate a Godot input action (as registered in the InputMap) being pressed or " +
                "released in the running game, with an optional analog strength.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["action"] = Schema.Str("Name of the input action to simulate, as registered in the InputMap."),
                ["pressed"] = Schema.Bool("Whether the action is pressed (true) or released (false).", true),
                ["strength"] = Schema.Num("Analog strength of the action event, from 0.0 to 1.0.", 1.0),
            }, "action"),
            Category = "input",
            Handler = p => Task.FromResult(SimulateAction(p)),
        },
        new()
        {
            Name = "simulate_sequence",
            Description = "Simulate a sequence of input events in the running game. When frame_delay <= 0, " +
                "all events are dispatched within the same frame; otherwise the game paces them out with " +
                "the given number of frames between each event.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["events"] = Schema.Arr(
                    "Array of event dictionaries (each with at least a 'type' key), in the same shape " +
                    "produced by simulate_key/simulate_mouse_click/simulate_mouse_move/simulate_action.",
                    Schema.Obj("Input event definition.")),
                ["frame_delay"] = Schema.Int("Frames to wait between each event in the sequence.", 1),
            }, "events"),
            Category = "input",
            Handler = p => Task.FromResult(SimulateSequence(p)),
        },
    };

    // ── simulate_key ────────────────────────────────────────────────────────

    private static Dictionary SimulateKey(Dictionary @params)
    {
        var (keycode, err) = RequireString(@params, "keycode");
        if (err != null)
            return err;

        var pressed = OptionalBool(@params, "pressed", true);
        var shift = OptionalBool(@params, "shift", false);
        var ctrl = OptionalBool(@params, "ctrl", false);
        var alt = OptionalBool(@params, "alt", false);

        var evt = new Dictionary
        {
            ["type"] = "key",
            ["keycode"] = keycode,
            ["pressed"] = pressed,
            ["shift"] = shift,
            ["ctrl"] = ctrl,
            ["alt"] = alt,
        };
        WriteCommands(new Godot.Collections.Array { evt });
        return Success(new Dictionary { ["sent"] = true, ["event"] = evt });
    }

    // ── simulate_mouse_click ────────────────────────────────────────────────

    private static Dictionary SimulateMouseClick(Dictionary @params)
    {
        var button = OptionalInt(@params, "button", 1); // MOUSE_BUTTON_LEFT
        var pressed = OptionalBool(@params, "pressed", true);
        var doubleClick = OptionalBool(@params, "double_click", false);
        var autoRelease = OptionalBool(@params, "auto_release", true);
        var x = OptionalFloat(@params, "x", 0);
        var y = OptionalFloat(@params, "y", 0);

        var pressEvent = new Dictionary
        {
            ["type"] = "mouse_button",
            ["button"] = button,
            ["pressed"] = pressed,
            ["double_click"] = doubleClick,
            ["position"] = new Dictionary { ["x"] = x, ["y"] = y },
        };

        // Auto-release: send press + release in sequence so UI buttons actually fire
        if (pressed && autoRelease)
        {
            var releaseEvent = pressEvent.Duplicate(true);
            releaseEvent["pressed"] = false;
            var sequenceData = new Dictionary
            {
                ["sequence_events"] = new Godot.Collections.Array { pressEvent, releaseEvent },
                ["frame_delay"] = 1,
            };
            var writeErr = WriteCommandsFile(sequenceData);
            if (writeErr != null)
                return writeErr;
            return Success(new Dictionary { ["sent"] = true, ["event"] = pressEvent, ["auto_release"] = true });
        }

        WriteCommands(new Godot.Collections.Array { pressEvent });
        return Success(new Dictionary { ["sent"] = true, ["event"] = pressEvent });
    }

    // ── simulate_mouse_move ─────────────────────────────────────────────────

    private static Dictionary SimulateMouseMove(Dictionary @params)
    {
        var x = OptionalFloat(@params, "x", 0);
        var y = OptionalFloat(@params, "y", 0);
        var relX = OptionalFloat(@params, "relative_x", 0);
        var relY = OptionalFloat(@params, "relative_y", 0);
        var buttonMask = OptionalInt(@params, "button_mask", 0);
        var unhandledExplicit = @params.ContainsKey("unhandled");
        var unhandled = OptionalBool(@params, "unhandled", false);

        var evt = new Dictionary
        {
            ["type"] = "mouse_motion",
            ["position"] = new Dictionary { ["x"] = x, ["y"] = y },
            ["relative"] = new Dictionary { ["x"] = relX, ["y"] = relY },
            ["button_mask"] = buttonMask,
        };
        // Auto-enable unhandled for drag motions (camera-pan use case) ONLY when
        // the caller did NOT explicitly pass an "unhandled" key. If they passed
        // one — true or false — honor it. This lets UI drag-and-drop tests opt
        // back into normal GUI dispatch by passing unhandled: false explicitly.
        if (unhandledExplicit)
            evt["unhandled"] = unhandled;
        else if (buttonMask > 0)
            evt["unhandled"] = true;

        WriteCommands(new Godot.Collections.Array { evt });
        return Success(new Dictionary { ["sent"] = true, ["event"] = evt });
    }

    // ── simulate_action ─────────────────────────────────────────────────────

    private static Dictionary SimulateAction(Dictionary @params)
    {
        var (actionName, err) = RequireString(@params, "action");
        if (err != null)
            return err;

        var pressed = OptionalBool(@params, "pressed", true);
        var strength = OptionalFloat(@params, "strength", 1.0);

        var evt = new Dictionary
        {
            ["type"] = "action",
            ["action"] = actionName,
            ["pressed"] = pressed,
            ["strength"] = strength,
        };
        WriteCommands(new Godot.Collections.Array { evt });
        return Success(new Dictionary { ["sent"] = true, ["event"] = evt });
    }

    // ── simulate_sequence ────────────────────────────────────────────────────

    private static Dictionary SimulateSequence(Dictionary @params)
    {
        if (!@params.TryGetValue("events", out var eventsVariant) || eventsVariant.VariantType != Variant.Type.Array)
            return ErrorInvalidParams("Missing required parameter: events (Array)");

        var events = eventsVariant.AsGodotArray();
        if (events.Count == 0)
            return ErrorInvalidParams("Events array is empty");

        var frameDelay = OptionalInt(@params, "frame_delay", 1);

        foreach (var eventDataVariant in events)
        {
            if (eventDataVariant.VariantType != Variant.Type.Dictionary)
                return ErrorInvalidParams($"Invalid event in sequence: {eventDataVariant}");
            var eventData = eventDataVariant.AsGodotDictionary();
            if (!eventData.TryGetValue("type", out var typeVariant)
                || typeVariant.VariantType != Variant.Type.String
                || string.IsNullOrEmpty(typeVariant.AsString()))
                return ErrorInvalidParams($"Invalid event in sequence: {eventDataVariant}");
        }

        if (frameDelay <= 0)
        {
            // All events in one frame - write as plain array
            WriteCommands(events);
        }
        else
        {
            // Sequence with frame delay - game side handles timing
            var sequenceData = new Dictionary
            {
                ["sequence_events"] = events,
                ["frame_delay"] = frameDelay,
            };
            var writeErr = WriteCommandsFile(sequenceData);
            if (writeErr != null)
                return writeErr;
        }

        return Success(new Dictionary { ["sent"] = true, ["event_count"] = events.Count, ["frame_delay"] = frameDelay });
    }

    // ── file-IPC helpers ─────────────────────────────────────────────────────

    private static string GetCommandsPath() => GetGameUserDir() + "/" + CommandsFileName;

    /// <summary>Fire-and-forget write used by simple single/multi-event commands (matches _write_commands).</summary>
    private static void WriteCommands(Godot.Collections.Array events)
    {
        var json = Json.Stringify(events);
        using var file = Godot.FileAccess.Open(GetCommandsPath(), Godot.FileAccess.ModeFlags.Write);
        if (file == null)
        {
            GD.PushError($"[MCP Input] Failed to write commands: {Godot.FileAccess.GetOpenError()}");
            return;
        }
        file.StoreString(json);
    }

    /// <summary>Write used by callers that need to surface a write failure as an error result.</summary>
    private static Dictionary? WriteCommandsFile(Dictionary payload)
    {
        var json = Json.Stringify(payload);
        using var file = Godot.FileAccess.Open(GetCommandsPath(), Godot.FileAccess.ModeFlags.Write);
        if (file == null)
            return ErrorInternal($"Failed to write commands: {Godot.FileAccess.GetOpenError()}");
        file.StoreString(json);
        return null;
    }
}
