using Godot;
using Godot.Collections;

namespace GodotMcpPro.Runtime;

/// <summary>
/// Autoload injected by Godot MCP Pro CSharp plugin at runtime.
/// Monitors for input commands from the editor and dispatches them as Input events.
/// Faithful port of addons/godot_mcp/mcp_input_service.gd.
/// </summary>
public partial class McpInputService : Node
{
    private const string CommandsPath = "user://mcp_input_commands";

    private Godot.Collections.Array _sequenceQueue = new();
    private int _sequenceFrameDelay;
    private int _sequenceFramesWaited;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Process(double delta)
    {
        // Process queued sequence events
        if (_sequenceQueue.Count > 0)
            ProcessSequenceTick();

        // Check for new commands from file
        if (Godot.FileAccess.FileExists(CommandsPath))
            ProcessCommands();
    }

    private void ProcessCommands()
    {
        using var file = Godot.FileAccess.Open(CommandsPath, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
            return;
        var text = file.GetAsText();
        file.Close();
        DirAccess.RemoveAbsolute(CommandsPath);

        var parsed = Json.ParseString(text);
        if (parsed.VariantType == Variant.Type.Nil)
        {
            GD.PushWarning("[MCP Input] Failed to parse input commands JSON");
            return;
        }

        // Check if this is a sequence command (dict with "sequence_events")
        if (parsed.VariantType == Variant.Type.Dictionary)
        {
            var dict = parsed.AsGodotDictionary();
            if (dict.ContainsKey("sequence_events"))
            {
                StartSequence(dict);
                return;
            }
        }

        // Otherwise treat as immediate event(s)
        Godot.Collections.Array events = parsed.VariantType == Variant.Type.Array
            ? parsed.AsGodotArray()
            : new Godot.Collections.Array { parsed };

        foreach (var eventDataVariant in events)
        {
            if (eventDataVariant.VariantType != Variant.Type.Dictionary)
                continue;
            var eventData = eventDataVariant.AsGodotDictionary();
            var evt = CreateEvent(eventData);
            if (evt != null)
                DispatchEvent(evt, eventData);
        }
    }

    private void StartSequence(Dictionary data)
    {
        _sequenceQueue = data.TryGetValue("sequence_events", out var seqVariant) && seqVariant.VariantType == Variant.Type.Array
            ? (Godot.Collections.Array)seqVariant.AsGodotArray().Duplicate()
            : new Godot.Collections.Array();
        _sequenceFrameDelay = data.TryGetValue("frame_delay", out var delayVariant) ? delayVariant.AsInt32() : 1;
        _sequenceFramesWaited = 0;
        // Dispatch first event immediately
        if (_sequenceQueue.Count > 0)
            DispatchNextSequenceEvent();
    }

    private void ProcessSequenceTick()
    {
        _sequenceFramesWaited += 1;
        if (_sequenceFramesWaited >= _sequenceFrameDelay)
        {
            _sequenceFramesWaited = 0;
            DispatchNextSequenceEvent();
        }
    }

    private void DispatchNextSequenceEvent()
    {
        if (_sequenceQueue.Count == 0)
            return;
        var eventDataVariant = _sequenceQueue[0];
        _sequenceQueue.RemoveAt(0);
        if (eventDataVariant.VariantType != Variant.Type.Dictionary)
            return;
        var eventData = eventDataVariant.AsGodotDictionary();
        var evt = CreateEvent(eventData);
        if (evt != null)
            DispatchEvent(evt, eventData);
    }

    /// <summary>
    /// Dispatch an input event using the appropriate method.
    /// Mouse drag motions (button_mask > 0) auto-promote to push_input to bypass
    /// GUI consumption and reach _unhandled_input — needed for camera-pan use
    /// cases where UI Controls would otherwise swallow drag events. But for UI
    /// drag-and-drop *testing* we want events to reach the GUI dispatcher so
    /// hit-testing and _get_drag_data / _drop_data fire. So: respect an explicit
    /// "unhandled": false in the event payload — only auto-promote when the
    /// caller did NOT pass an "unhandled" key. Default behavior preserved.
    /// </summary>
    private void DispatchEvent(InputEvent evt, Dictionary eventData)
    {
        bool forceUnhandled;
        if (eventData.ContainsKey("unhandled"))
        {
            forceUnhandled = eventData["unhandled"].AsBool();
        }
        else
        {
            forceUnhandled = evt is InputEventMouseMotion motionEvent && motionEvent.ButtonMask != 0;
        }

        if (forceUnhandled)
        {
            var vp = GetViewport();
            if (vp != null)
                vp.PushInput(evt, true);
            else
                Input.ParseInputEvent(evt);
        }
        else
        {
            Input.ParseInputEvent(evt);
        }
    }

    private InputEvent? CreateEvent(Dictionary data)
    {
        var type = data.TryGetValue("type", out var typeVariant) ? typeVariant.AsString() : "";
        switch (type)
        {
            case "key":
                return CreateKeyEvent(data);
            case "mouse_button":
                return CreateMouseButtonEvent(data);
            case "mouse_motion":
                return CreateMouseMotionEvent(data);
            case "action":
                return CreateActionEvent(data);
            default:
                GD.PushWarning($"[MCP Input] Unknown event type: {type}");
                return null;
        }
    }

    /// <summary>
    /// Convert viewport coordinates to window coordinates for Input.ParseInputEvent().
    /// Godot applies viewport.GetFinalTransform() to mouse events internally,
    /// so we must pass window-space coordinates (pre-transform).
    /// </summary>
    private Vector2 ViewportToWindow(Vector2 viewportPos)
    {
        var vp = GetViewport();
        if (vp == null)
            return viewportPos;
        var xform = vp.GetFinalTransform();
        return xform * viewportPos;
    }

    private InputEventKey CreateKeyEvent(Dictionary data)
    {
        var evt = new InputEventKey();
        var keycodeStr = data.TryGetValue("keycode", out var keycodeVariant) ? keycodeVariant.AsString() : "";
        if (keycodeStr.StartsWith("KEY_"))
        {
            var constantValue = ClassDB.ClassGetIntegerConstant("@GlobalScope", keycodeStr);
            if (constantValue != 0)
                evt.Keycode = (Key)constantValue;
            else
                evt.Keycode = OS.FindKeycodeFromString(keycodeStr.Substring(4));
        }
        else
        {
            evt.Keycode = OS.FindKeycodeFromString(keycodeStr);
        }
        evt.Pressed = data.TryGetValue("pressed", out var pressedVariant) ? pressedVariant.AsBool() : true;
        evt.ShiftPressed = data.TryGetValue("shift", out var shiftVariant) && shiftVariant.AsBool();
        evt.CtrlPressed = data.TryGetValue("ctrl", out var ctrlVariant) && ctrlVariant.AsBool();
        evt.AltPressed = data.TryGetValue("alt", out var altVariant) && altVariant.AsBool();
        return evt;
    }

    private Vector2 ExtractPosition(Dictionary data)
    {
        // Support nested {"position": {"x": ..., "y": ...}} or flat {"x": ..., "y": ...}
        if (data.TryGetValue("position", out var posVariant) && posVariant.VariantType == Variant.Type.Dictionary)
        {
            var pos = posVariant.AsGodotDictionary();
            var x = pos.TryGetValue("x", out var xVariant) ? xVariant.AsSingle() : 0.0f;
            var y = pos.TryGetValue("y", out var yVariant) ? yVariant.AsSingle() : 0.0f;
            return new Vector2(x, y);
        }
        var flatX = data.TryGetValue("x", out var flatXVariant) ? flatXVariant.AsSingle() : 0.0f;
        var flatY = data.TryGetValue("y", out var flatYVariant) ? flatYVariant.AsSingle() : 0.0f;
        return new Vector2(flatX, flatY);
    }

    private InputEventMouseButton CreateMouseButtonEvent(Dictionary data)
    {
        var evt = new InputEventMouseButton();
        var buttonIndex = data.TryGetValue("button", out var buttonVariant) ? buttonVariant.AsInt32() : (int)MouseButton.Left;
        evt.ButtonIndex = (MouseButton)buttonIndex;
        evt.Pressed = data.TryGetValue("pressed", out var pressedVariant) ? pressedVariant.AsBool() : true;
        evt.DoubleClick = data.TryGetValue("double_click", out var dcVariant) && dcVariant.AsBool();
        var windowPos = ViewportToWindow(ExtractPosition(data));
        evt.Position = windowPos;
        evt.GlobalPosition = windowPos;
        return evt;
    }

    private InputEventMouseMotion CreateMouseMotionEvent(Dictionary data)
    {
        var evt = new InputEventMouseMotion();
        var windowPos = ViewportToWindow(ExtractPosition(data));
        evt.Position = windowPos;
        evt.GlobalPosition = windowPos;

        // Support nested {"relative": {"x": ..., "y": ...}} or flat {"relative_x": ..., "relative_y": ...}
        float relX;
        float relY;
        if (data.TryGetValue("relative", out var relVariant) && relVariant.VariantType == Variant.Type.Dictionary)
        {
            var rel = relVariant.AsGodotDictionary();
            relX = rel.TryGetValue("x", out var relXVariant) ? relXVariant.AsSingle() : 0.0f;
            relY = rel.TryGetValue("y", out var relYVariant) ? relYVariant.AsSingle() : 0.0f;
        }
        else
        {
            relX = data.TryGetValue("relative_x", out var flatRelXVariant) ? flatRelXVariant.AsSingle() : 0.0f;
            relY = data.TryGetValue("relative_y", out var flatRelYVariant) ? flatRelYVariant.AsSingle() : 0.0f;
        }

        // Scale relative movement by the same transform (scale only, no offset)
        var vp = GetViewport();
        if (vp != null)
        {
            var scale = vp.GetFinalTransform().Scale;
            evt.Relative = new Vector2(relX, relY) * scale;
        }
        else
        {
            evt.Relative = new Vector2(relX, relY);
        }

        // Set button_mask so drag detection works (e.g. camera pan checks button_mask)
        var buttonMask = data.TryGetValue("button_mask", out var maskVariant) ? maskVariant.AsInt32() : 0;
        evt.ButtonMask = (MouseButtonMask)buttonMask;
        return evt;
    }

    private InputEventAction CreateActionEvent(Dictionary data)
    {
        var evt = new InputEventAction();
        evt.Action = data.TryGetValue("action", out var actionVariant) ? actionVariant.AsString() : "";
        evt.Pressed = data.TryGetValue("pressed", out var pressedVariant) ? pressedVariant.AsBool() : true;
        evt.Strength = data.TryGetValue("strength", out var strengthVariant) ? strengthVariant.AsSingle() : 1.0f;
        return evt;
    }
}
