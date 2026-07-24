using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Read and persist Godot InputMap actions (project.godot "input/*" settings).
/// Faithful port of addons/godot_mcp/commands/input_map_commands.gd.
/// </summary>
[Tool]
public partial class InputMapCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "get_input_actions",
            Description = "List InputMap actions configured in the project, with their deadzone and " +
                "serialized event bindings. Built-in 'ui_*' and editor-only actions are excluded by default.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["filter"] = Schema.Str("Only return actions whose name contains this substring.", ""),
                ["include_builtin"] = Schema.Bool(
                    "Include built-in 'ui_*' actions and editor-only actions not defined in project.godot.", false),
            }),
            Category = "input_map",
            Handler = p => Task.FromResult(GetInputActions(p)),
        },
        new()
        {
            Name = "set_input_action",
            Description = "Create or replace an InputMap action's deadzone and event bindings, persisting " +
                "the change to project.godot and updating the live InputMap.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["action"] = Schema.Str("Name of the input action to create or replace."),
                ["events"] = Schema.Arr(
                    "Array of event definitions. Each has a 'type' of 'key', 'mouse_button', 'joypad_button' " +
                    "or 'joypad_motion', plus type-specific keys (keycode/physical_keycode/ctrl/shift/alt/meta " +
                    "for 'key'; button_index for 'mouse_button'/'joypad_button'; axis/axis_value for 'joypad_motion').",
                    Schema.Obj("Event definition dictionary.")),
                ["deadzone"] = Schema.Num("Deadzone for analog events on this action.", 0.5),
            }, "action", "events"),
            Category = "input_map",
            Handler = p => Task.FromResult(SetInputAction(p)),
        },
    };

    // ── get_input_actions ────────────────────────────────────────────────────

    private static Dictionary GetInputActions(Dictionary @params)
    {
        var filter = OptionalString(@params, "filter", "");
        var includeBuiltin = OptionalBool(@params, "include_builtin", false);

        var actions = new Dictionary();
        foreach (var action in InputMap.GetActions())
        {
            var actionStr = action.ToString();
            if (!includeBuiltin)
            {
                // Skip built-in UI actions
                if (actionStr.StartsWith("ui_"))
                    continue;
                // Skip editor-registered actions (spatial_editor/*, etc.) — the addon
                // runs in the editor process, so InputMap also carries the editor's
                // own actions, which are not part of the project's InputMap
                if (!ProjectSettings.HasSetting("input/" + actionStr))
                    continue;
            }
            // Apply filter
            if (!string.IsNullOrEmpty(filter) && !actionStr.Contains(filter))
                continue;

            var events = new Godot.Collections.Array();
            foreach (var evt in InputMap.ActionGetEvents(action))
                events.Add(SerializeEvent(evt));

            actions[actionStr] = new Dictionary
            {
                ["deadzone"] = InputMap.ActionGetDeadzone(action),
                ["events"] = events,
            };
        }

        return Success(new Dictionary { ["actions"] = actions, ["count"] = actions.Count });
    }

    // ── set_input_action ─────────────────────────────────────────────────────

    private static Dictionary SetInputAction(Dictionary @params)
    {
        var (actionName, err) = RequireString(@params, "action");
        if (err != null)
            return err;

        if (!@params.TryGetValue("events", out var eventsVariant) || eventsVariant.VariantType != Variant.Type.Array)
            return ErrorInvalidParams("'events' array is required");
        var eventDefs = eventsVariant.AsGodotArray();

        var deadzone = OptionalFloat(@params, "deadzone", 0.5);

        // Build the events array
        var events = new Godot.Collections.Array();
        foreach (var eventDefVariant in eventDefs)
        {
            if (eventDefVariant.VariantType != Variant.Type.Dictionary)
                continue;
            var evt = ParseEvent(eventDefVariant.AsGodotDictionary());
            if (evt != null)
                events.Add(evt);
        }

        // Save to ProjectSettings
        var settingValue = new Dictionary
        {
            ["deadzone"] = deadzone,
            ["events"] = events,
        };

        ProjectSettings.SetSetting("input/" + actionName, settingValue);
        var saveErr = ProjectSettings.Save();
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save project settings: {saveErr}");

        // Also update the runtime InputMap
        if (!InputMap.HasAction(actionName))
        {
            InputMap.AddAction(actionName, (float)deadzone);
        }
        else
        {
            InputMap.ActionSetDeadzone(actionName, (float)deadzone);
            InputMap.ActionEraseEvents(actionName);
        }
        foreach (var evt in events)
            InputMap.ActionAddEvent(actionName, (InputEvent)evt.AsGodotObject());

        return Success(new Dictionary
        {
            ["action"] = actionName,
            ["deadzone"] = deadzone,
            ["events_count"] = events.Count,
            ["saved"] = true,
        });
    }

    // ── event (de)serialization ──────────────────────────────────────────────

    private static Dictionary SerializeEvent(InputEvent evt)
    {
        switch (evt)
        {
            case InputEventKey keyEvent:
            {
                var info = new Dictionary
                {
                    ["type"] = "key",
                    ["keycode"] = keyEvent.Keycode != Key.None ? OS.GetKeycodeString(keyEvent.Keycode) : "",
                    ["physical_keycode"] = keyEvent.PhysicalKeycode != Key.None
                        ? OS.GetKeycodeString(keyEvent.PhysicalKeycode)
                        : "",
                };
                if (keyEvent.CtrlPressed) info["ctrl"] = true;
                if (keyEvent.ShiftPressed) info["shift"] = true;
                if (keyEvent.AltPressed) info["alt"] = true;
                if (keyEvent.MetaPressed) info["meta"] = true;
                return info;
            }
            case InputEventMouseButton mbEvent:
                return new Dictionary
                {
                    ["type"] = "mouse_button",
                    ["button_index"] = (int)mbEvent.ButtonIndex,
                };
            case InputEventJoypadButton jbEvent:
                return new Dictionary
                {
                    ["type"] = "joypad_button",
                    ["button_index"] = (int)jbEvent.ButtonIndex,
                };
            case InputEventJoypadMotion jmEvent:
                return new Dictionary
                {
                    ["type"] = "joypad_motion",
                    ["axis"] = (int)jmEvent.Axis,
                    ["axis_value"] = jmEvent.AxisValue,
                };
            default:
                return new Dictionary { ["type"] = evt.GetClass() };
        }
    }

    private static InputEvent? ParseEvent(Dictionary def)
    {
        var type = OptionalString(def, "type", "");
        switch (type)
        {
            case "key":
            {
                var evt = new InputEventKey();
                var keycodeStr = OptionalString(def, "keycode", "");
                if (!string.IsNullOrEmpty(keycodeStr))
                    evt.Keycode = OS.FindKeycodeFromString(keycodeStr);
                var physStr = OptionalString(def, "physical_keycode", "");
                if (!string.IsNullOrEmpty(physStr))
                    evt.PhysicalKeycode = OS.FindKeycodeFromString(physStr);
                evt.CtrlPressed = OptionalBool(def, "ctrl", false);
                evt.ShiftPressed = OptionalBool(def, "shift", false);
                evt.AltPressed = OptionalBool(def, "alt", false);
                evt.MetaPressed = OptionalBool(def, "meta", false);
                return evt;
            }
            case "mouse_button":
            {
                var evt = new InputEventMouseButton
                {
                    ButtonIndex = (MouseButton)OptionalInt(def, "button_index", 1),
                };
                return evt;
            }
            case "joypad_button":
            {
                var evt = new InputEventJoypadButton
                {
                    ButtonIndex = (JoyButton)OptionalInt(def, "button_index", 0),
                };
                return evt;
            }
            case "joypad_motion":
            {
                var evt = new InputEventJoypadMotion
                {
                    Axis = (JoyAxis)OptionalInt(def, "axis", 0),
                    AxisValue = (float)OptionalFloat(def, "axis_value", 1.0),
                };
                return evt;
            }
            default:
                return null;
        }
    }
}
