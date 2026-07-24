using Godot;
using Godot.Collections;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Runtime;

/// <summary>
/// Game-side autoload. Polls user://mcp_game_request each frame and runs commands
/// against the live scene tree. State machine for async ops (capture/monitor/
/// recording/move-to/watch-signals). Writes user://mcp_game_response.
/// Port of addons/godot_mcp/mcp_game_inspector_service.gd.
/// </summary>
public partial class McpGameInspectorService : Node
{
    private const string RequestPath = "user://mcp_game_request";
    private const string ResponsePath = "user://mcp_game_response";
    private const string DebuggerContinueFlag = "user://mcp_debugger_continue";

    private enum State { Idle, CapturingFrames, Monitoring, Recording, MovingTo, WatchingSignals }

    private State _state = State.Idle;
    private bool _pendingCommand;

    private int _captureFramesRemaining;
    private int _captureFrameInterval = 1;
    private int _captureFrameCounter;
    private bool _captureHalfRes = true;
    private readonly Godot.Collections.Array _capturedImages = new();
    private string _captureNodePath = "";
    private Godot.Collections.Array _captureNodeProps = new();
    private readonly Godot.Collections.Array _captureFrameData = new();

    private readonly Godot.Collections.Array _recordingEvents = new();
    private ulong _recordingStartMsec;

    private string _monitorNodePath = "";
    private Godot.Collections.Array _monitorProperties = new();
    private int _monitorFramesRemaining;
    private int _monitorFrameInterval = 1;
    private int _monitorFrameCounter;
    private readonly Godot.Collections.Array _monitorTimeline = new();

    private Godot.Collections.Array _watchNodes = new();
    private Godot.Collections.Array _watchSignalFilter = new();
    private readonly Godot.Collections.Array _watchLog = new();
    private ulong _watchStartMsec;
    private int _watchDurationMs = 5000;
    private readonly Godot.Collections.Array _watchConnections = new();

    private Vector3 _movetoTarget = Vector3.Zero;
    private Node3D? _movetoPlayer;
    private Node3D? _movetoCameraPivot;
    private float _movetoArrivalRadius = 1.5f;
    private float _movetoTimeout = 15.0f;
    private float _movetoElapsed;
    private bool _movetoRun;
    private bool _movetoLookAt = true;
    private readonly List<Key> _movetoKeysHeld = new();

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Process(double delta)
    {
        if (_pendingCommand
            && !Godot.FileAccess.FileExists(RequestPath)
            && !Godot.FileAccess.FileExists(ResponsePath))
        {
            GD.PushWarning("[MCP] Recovered from crashed command — writing error response");
            _pendingCommand = false;
            _state = State.Idle;
            using (var flag = Godot.FileAccess.Open(DebuggerContinueFlag, Godot.FileAccess.ModeFlags.Write))
            {
                // flag file presence is the signal; nothing to write
            }
            WriteResponse(new Dictionary { ["error"] = "Command crashed (runtime error). Check Godot debugger." });
            return;
        }

        switch (_state)
        {
            case State.Idle:
                if (Godot.FileAccess.FileExists(RequestPath))
                    HandleRequest();
                break;
            case State.CapturingFrames:
                ProcessCapture();
                break;
            case State.Monitoring:
                ProcessMonitor();
                break;
            case State.Recording:
                if (Godot.FileAccess.FileExists(RequestPath))
                    HandleRequest();
                break;
            case State.MovingTo:
                ProcessMoveTo((float)delta);
                break;
            case State.WatchingSignals:
                ProcessWatchSignals();
                break;
        }
    }

    // ── Request handling ──────────────────────────────────────────────────────

    private void HandleRequest()
    {
        string text;
        using (var file = Godot.FileAccess.Open(RequestPath, Godot.FileAccess.ModeFlags.Read))
        {
            if (file == null) return;
            text = file.GetAsText();
        }
        DirAccess.RemoveAbsolute(RequestPath);

        var parsed = Json.ParseString(text);
        if (parsed.VariantType != Variant.Type.Dictionary)
        {
            WriteResponse(new Dictionary { ["error"] = "Invalid request JSON" });
            return;
        }

        _state = State.Idle;
        _pendingCommand = true;

        var msg = parsed.AsGodotDictionary();
        var command = msg.TryGetValue("command", out var c) ? c.AsString() : "";
        var @params = msg.TryGetValue("params", out var p) && p.VariantType == Variant.Type.Dictionary
            ? p.AsGodotDictionary()
            : new Dictionary();

        switch (command)
        {
            case "get_scene_tree": CmdGetSceneTree(@params); break;
            case "get_node_properties": CmdGetNodeProperties(@params); break;
            case "set_node_property": CmdSetNodeProperty(@params); break;
            case "capture_frames": CmdCaptureFrames(@params); break;
            case "monitor_properties": CmdMonitorProperties(@params); break;
            case "execute_script": CmdExecuteScript(@params); break;
            case "start_recording": CmdStartRecording(@params); break;
            case "stop_recording": CmdStopRecording(@params); break;
            case "replay_recording": _ = CmdReplayRecording(@params); break;
            case "find_nodes_by_script": CmdFindNodesByScript(@params); break;
            case "get_autoload": CmdGetAutoload(@params); break;
            case "batch_get_properties": CmdBatchGetProperties(@params); break;
            case "find_ui_elements": CmdFindUiElements(@params); break;
            case "click_button_by_text": CmdClickButtonByText(@params); break;
            case "wait_for_node": _ = CmdWaitForNode(@params); break;
            case "find_nearby_nodes": CmdFindNearbyNodes(@params); break;
            case "navigate_to": CmdNavigateTo(@params); break;
            case "move_to": CmdMoveTo(@params); break;
            case "watch_signals": CmdWatchSignals(@params); break;
            case "assert_node_state": CmdAssertNodeState(@params); break;
            case "get_performance_monitors": CmdGetPerformanceMonitors(@params); break;
            default: WriteResponse(new Dictionary { ["error"] = $"Unknown command: {command}" }); break;
        }
    }

    // ── get_scene_tree ────────────────────────────────────────────────────────

    private void CmdGetSceneTree(Dictionary @params)
    {
        var root = GetTree().CurrentScene;
        if (root == null) { WriteResponse(new Dictionary { ["error"] = "No current scene" }); return; }

        var maxDepth = @params.TryGetValue("max_depth", out var md) ? md.AsInt32() : -1;
        var scriptFilter = @params.TryGetValue("script_filter", out var sf) ? sf.AsString() : "";
        var typeFilter = @params.TryGetValue("type_filter", out var tf) ? tf.AsString() : "";
        var namedOnly = @params.TryGetValue("named_only", out var no) && no.AsBool();

        var hasFilter = !string.IsNullOrEmpty(scriptFilter) || !string.IsNullOrEmpty(typeFilter) || namedOnly;
        if (hasFilter)
        {
            var tree = BuildFilteredNodeTree(root, maxDepth, scriptFilter, typeFilter, namedOnly);
            if (tree.VariantType == Variant.Type.Nil)
                WriteResponse(new Dictionary { ["tree"] = default, ["message"] = "No nodes matched the filter" });
            else
                WriteResponse(new Dictionary { ["tree"] = tree });
        }
        else
        {
            WriteResponse(new Dictionary { ["tree"] = BuildNodeTree(root, maxDepth) });
        }
    }

    private static Dictionary BuildNodeTree(Node node, int maxDepth, int currentDepth = 0)
    {
        var result = new Dictionary
        {
            ["name"] = node.Name.ToString(),
            ["type"] = node.GetClass(),
            ["path"] = node.GetPath().ToString(),
        };
        if (node.GetScript().As<Script>() is { } script && !string.IsNullOrEmpty(script.ResourcePath))
            result["script"] = script.ResourcePath;

        if (maxDepth == -1 || currentDepth < maxDepth)
        {
            var children = new Godot.Collections.Array();
            foreach (var child in node.GetChildren())
                children.Add(BuildNodeTree(child, maxDepth, currentDepth + 1));
            if (children.Count > 0)
                result["children"] = children;
        }
        return result;
    }

    private static Variant BuildFilteredNodeTree(Node node, int maxDepth, string scriptFilter, string typeFilter, bool namedOnly, int currentDepth = 0)
    {
        var nodeMatches = NodeMatchesFilter(node, scriptFilter, typeFilter, namedOnly);

        var matchedChildren = new Godot.Collections.Array();
        if (maxDepth == -1 || currentDepth < maxDepth)
        {
            foreach (var child in node.GetChildren())
            {
                var childTree = BuildFilteredNodeTree(child, maxDepth, scriptFilter, typeFilter, namedOnly, currentDepth + 1);
                if (childTree.VariantType != Variant.Type.Nil)
                    matchedChildren.Add(childTree);
            }
        }

        if (!nodeMatches && matchedChildren.Count == 0)
            return default;

        var result = new Dictionary
        {
            ["name"] = node.Name.ToString(),
            ["type"] = node.GetClass(),
            ["path"] = node.GetPath().ToString(),
        };
        if (node.GetScript().As<Script>() is { } script && !string.IsNullOrEmpty(script.ResourcePath))
            result["script"] = script.ResourcePath;
        if (matchedChildren.Count > 0)
            result["children"] = matchedChildren;
        return result;
    }

    private static bool NodeMatchesFilter(Node node, string scriptFilter, string typeFilter, bool namedOnly)
    {
        if (namedOnly && node.Name.ToString().StartsWith("@"))
            return false;
        if (!string.IsNullOrEmpty(typeFilter) && !node.IsClass(typeFilter))
            return false;
        if (!string.IsNullOrEmpty(scriptFilter))
        {
            if (node.GetScript().As<Script>() is not { } script)
                return false;
            if (!script.ResourcePath.ToLowerInvariant().Contains(scriptFilter.ToLowerInvariant()))
                return false;
        }
        return true;
    }

    // ── get_node_properties ───────────────────────────────────────────────────

    private void CmdGetNodeProperties(Dictionary @params)
    {
        var nodePath = @params.TryGetValue("node_path", out var np) ? np.AsString() : "";
        if (string.IsNullOrEmpty(nodePath)) { WriteResponse(new Dictionary { ["error"] = "node_path is required" }); return; }

        var node = GetNodeOrNull(new NodePath(nodePath));
        if (node == null) { WriteResponse(new Dictionary { ["error"] = $"Node not found: {nodePath}" }); return; }

        var filter = @params.TryGetValue("properties", out var f) && f.VariantType == Variant.Type.Array
            ? f.AsGodotArray() : null;
        var props = FilterNodeProperties(node, filter);

        WriteResponse(new Dictionary
        {
            ["node_path"] = node.GetPath().ToString(),
            ["type"] = node.GetClass(),
            ["properties"] = props,
        });
    }

    private static Dictionary FilterNodeProperties(Node node, Godot.Collections.Array? filter)
    {
        var props = new Dictionary();
        if (filter == null || filter.Count == 0)
        {
            foreach (var propInfo in node.GetPropertyList())
            {
                var propName = propInfo["name"].AsString();
                var usage = propInfo["usage"].AsInt32();
                if ((usage & (long)PropertyUsageFlags.Editor) == 0) continue;
                if (propName.StartsWith("_") || propName == "script") continue;
                props[propName] = PropertyParser.SerializeValue(node.Get(propName));
            }
        }
        else
        {
            foreach (var propName in filter)
                props[propName.AsString()] = PropertyParser.SerializeValue(node.Get(propName.AsString()));
        }
        return props;
    }

    // ── capture_frames ────────────────────────────────────────────────────────

    private void CmdCaptureFrames(Dictionary @params)
    {
        _pendingCommand = false;
        var count = Mathf.Clamp(@params.TryGetValue("count", out var c) ? c.AsInt32() : 5, 1, 30);
        _captureFrameInterval = Mathf.Max(@params.TryGetValue("frame_interval", out var fi) ? fi.AsInt32() : 10, 1);
        _captureHalfRes = @params.TryGetValue("half_resolution", out var hr) ? hr.AsBool() : true;

        _captureNodePath = "";
        _captureNodeProps = new Godot.Collections.Array();
        _captureFrameData.Clear();
        if (@params.TryGetValue("node_data", out var nd) && nd.VariantType == Variant.Type.Dictionary)
        {
            var nodeData = nd.AsGodotDictionary();
            _captureNodePath = nodeData.TryGetValue("node_path", out var np) ? np.AsString() : "";
            _captureNodeProps = nodeData.TryGetValue("properties", out var props) && props.VariantType == Variant.Type.Array
                ? props.AsGodotArray() : new Godot.Collections.Array();
        }

        _capturedImages.Clear();
        _captureFramesRemaining = count;
        _captureFrameCounter = 0;
        _state = State.CapturingFrames;
        CaptureOneFrame();
    }

    private void ProcessCapture()
    {
        if (Godot.FileAccess.FileExists(RequestPath))
        {
            _state = State.Idle;
            HandleRequest();
            return;
        }
        if (++_captureFrameCounter >= _captureFrameInterval)
        {
            _captureFrameCounter = 0;
            CaptureOneFrame();
        }
    }

    private void CaptureOneFrame()
    {
        var viewport = GetViewport();
        if (viewport == null) { FinishCapture(); return; }

        var image = viewport.GetTexture().GetImage();
        if (image == null) { FinishCapture(); return; }

        if (_captureHalfRes)
        {
            var newSize = image.GetSize() / 2;
            image.Resize((int)newSize.X, (int)newSize.Y, Image.Interpolation.Bilinear);
        }

        var pngBuffer = image.SavePngToBuffer();
        _capturedImages.Add(Marshalls.RawToBase64(pngBuffer));

        if (!string.IsNullOrEmpty(_captureNodePath) && _captureNodeProps.Count > 0)
        {
            var snap = new Dictionary();
            if (GetTree().Root.GetNodeOrNull(new NodePath(_captureNodePath)) is { } node)
            {
                foreach (var propName in _captureNodeProps)
                    snap[propName.AsString()] = PropertyParser.SerializeValue(node.Get(propName.AsString()));
            }
            _captureFrameData.Add(snap);
        }

        if (--_captureFramesRemaining <= 0)
            FinishCapture();
    }

    private void FinishCapture()
    {
        _state = State.Idle;
        var viewport = GetViewport();
        var w = 0;
        var h = 0;
        if (viewport != null)
        {
            var size = viewport.GetVisibleRect().Size;
            if (_captureHalfRes) size /= 2;
            w = (int)size.X;
            h = (int)size.Y;
        }

        var response = new Dictionary
        {
            ["frames"] = _capturedImages,
            ["count"] = _capturedImages.Count,
            ["width"] = w,
            ["height"] = h,
            ["half_resolution"] = _captureHalfRes,
        };
        if (_captureFrameData.Count > 0)
            response["frame_data"] = _captureFrameData;
        WriteResponse(response);
        _capturedImages.Clear();
        _captureFrameData.Clear();
    }

    // ── monitor_properties ────────────────────────────────────────────────────

    private void CmdMonitorProperties(Dictionary @params)
    {
        _pendingCommand = false;
        _monitorNodePath = @params.TryGetValue("node_path", out var np) ? np.AsString() : "";
        _monitorProperties = @params.TryGetValue("properties", out var props) && props.VariantType == Variant.Type.Array
            ? props.AsGodotArray() : new Godot.Collections.Array();
        if (string.IsNullOrEmpty(_monitorNodePath) || _monitorProperties.Count == 0)
        {
            WriteResponse(new Dictionary { ["error"] = "node_path and properties are required" });
            return;
        }

        var frameCount = Mathf.Clamp(@params.TryGetValue("frame_count", out var fc) ? fc.AsInt32() : 60, 1, 600);
        _monitorFrameInterval = Mathf.Max(@params.TryGetValue("frame_interval", out var fi) ? fi.AsInt32() : 1, 1);

        _monitorTimeline.Clear();
        _monitorFramesRemaining = frameCount;
        _monitorFrameCounter = 0;
        _state = State.Monitoring;
        SampleOneFrame();
    }

    private void ProcessMonitor()
    {
        if (Godot.FileAccess.FileExists(RequestPath))
        {
            _state = State.Idle;
            HandleRequest();
            return;
        }
        if (++_monitorFrameCounter >= _monitorFrameInterval)
        {
            _monitorFrameCounter = 0;
            SampleOneFrame();
        }
    }

    private void SampleOneFrame()
    {
        var sample = new Dictionary();
        var node = GetNodeOrNull(new NodePath(_monitorNodePath));
        if (node == null)
        {
            foreach (var propName in _monitorProperties)
                sample[propName.AsString()] = default;
        }
        else
        {
            foreach (var propName in _monitorProperties)
                sample[propName.AsString()] = PropertyParser.SerializeValue(node.Get(propName.AsString()));
        }
        _monitorTimeline.Add(sample);
        if (--_monitorFramesRemaining <= 0)
            FinishMonitor();
    }

    private void FinishMonitor()
    {
        _state = State.Idle;
        WriteResponse(new Dictionary
        {
            ["node_path"] = _monitorNodePath,
            ["properties"] = _monitorProperties,
            ["samples"] = _monitorTimeline,
            ["sample_count"] = _monitorTimeline.Count,
            ["frame_interval"] = _monitorFrameInterval,
        });
        _monitorTimeline.Clear();
    }

    // ── watch_signals ─────────────────────────────────────────────────────────

    private void CmdWatchSignals(Dictionary @params)
    {
        _pendingCommand = false;
        if (!@params.TryGetValue("node_paths", out var nps) || nps.VariantType != Variant.Type.Array)
        {
            WriteResponse(new Dictionary { ["error"] = "node_paths array is required" });
            return;
        }
        var nodePaths = nps.AsGodotArray();
        if (nodePaths.Count == 0)
        {
            WriteResponse(new Dictionary { ["error"] = "node_paths array is empty" });
            return;
        }

        _watchSignalFilter = @params.TryGetValue("signal_filter", out var sf) && sf.VariantType == Variant.Type.Array
            ? sf.AsGodotArray() : new Godot.Collections.Array();
        _watchDurationMs = Mathf.Clamp(@params.TryGetValue("duration_ms", out var dm) ? dm.AsInt32() : 5000, 500, 30000);
        _watchLog.Clear();
        _watchConnections.Clear();
        _watchNodes = nodePaths;

        var connectedCount = 0;
        foreach (var npsVar in nodePaths)
        {
            var nodePathStr = npsVar.AsString();
            var node = GetNodeOrNull(new NodePath(nodePathStr));
            if (node == null)
            {
                _watchLog.Add(new Dictionary { ["warning"] = $"Node not found: {nodePathStr}" });
                continue;
            }
            foreach (var sigInfo in node.GetSignalList())
            {
                var sigName = sigInfo["name"].AsString();
                if (_watchSignalFilter.Count > 0)
                {
                    var matchFound = false;
                    foreach (var fs in _watchSignalFilter)
                    {
                        if (sigName.Contains(fs.AsString())) { matchFound = true; break; }
                    }
                    if (!matchFound) continue;
                }

                // Use a signal-arg-count-agnostic callback via CallDeferred("OnSignalFired", ...).
                // The args themselves are recorded by a thin bridge method captured with binds.
                var binds = new Godot.Collections.Array { nodePathStr, sigName };
                var callable = Callable.From(() =>
                {
                    // We can't recover args this way; record occurrence only.
                    OnSignalFired(nodePathStr, sigName, new Godot.Collections.Array());
                });

                if (node.IsConnected(sigName, callable))
                    continue;
                node.Connect(sigName, callable);
                _watchConnections.Add(new Dictionary { ["node"] = node, ["signal"] = sigName, ["callable"] = callable });
                connectedCount++;
            }
        }

        if (connectedCount == 0 && _watchLog.Count == 0)
        {
            WriteResponse(new Dictionary { ["error"] = "No signals connected. Check node_paths and signal_filter." });
            return;
        }

        _watchStartMsec = Time.GetTicksMsec();
        _state = State.WatchingSignals;
    }

    private void OnSignalFired(string nodePathStr, string sigName, Godot.Collections.Array args)
    {
        var elapsed = Time.GetTicksMsec() - _watchStartMsec;
        var entry = new Dictionary
        {
            ["time_ms"] = elapsed,
            ["node"] = nodePathStr,
            ["signal"] = sigName,
        };
        if (args.Count > 0)
        {
            var serialized = new Godot.Collections.Array();
            foreach (var a in args)
                serialized.Add(PropertyParser.SerializeValue(a));
            entry["args"] = serialized;
        }
        _watchLog.Add(entry);
    }

    private void ProcessWatchSignals()
    {
        if (Godot.FileAccess.FileExists(RequestPath))
        {
            FinishWatchSignals();
            _state = State.Idle;
            HandleRequest();
            return;
        }
        var elapsed = Time.GetTicksMsec() - _watchStartMsec;
        if ((long)elapsed >= _watchDurationMs)
            FinishWatchSignals();
    }

    private void FinishWatchSignals()
    {
        foreach (var conn in _watchConnections)
        {
            var connDict = conn.AsGodotDictionary();
            if (connDict["node"].AsGodotObject() is Node node && GodotObject.IsInstanceValid(node))
            {
                var sigName = connDict["signal"].AsString();
                var cb = connDict["callable"].AsCallable();
                if (node.IsConnected(sigName, cb))
                    node.Disconnect(sigName, cb);
            }
        }
        _watchConnections.Clear();
        _state = State.Idle;
        WriteResponse(new Dictionary
        {
            ["node_paths"] = _watchNodes,
            ["signal_filter"] = _watchSignalFilter,
            ["duration_ms"] = _watchDurationMs,
            ["events"] = _watchLog,
            ["event_count"] = _watchLog.Count,
        });
        _watchLog.Clear();
    }

    // ── set_node_property ─────────────────────────────────────────────────────

    private void CmdSetNodeProperty(Dictionary @params)
    {
        var nodePath = @params.TryGetValue("node_path", out var np) ? np.AsString() : "";
        if (string.IsNullOrEmpty(nodePath)) { WriteResponse(new Dictionary { ["error"] = "node_path is required" }); return; }
        var property = @params.TryGetValue("property", out var prop) ? prop.AsString() : "";
        if (string.IsNullOrEmpty(property)) { WriteResponse(new Dictionary { ["error"] = "property is required" }); return; }
        if (!@params.ContainsKey("value")) { WriteResponse(new Dictionary { ["error"] = "value is required" }); return; }

        var node = GetNodeOrNull(new NodePath(nodePath));
        if (node == null) { WriteResponse(new Dictionary { ["error"] = $"Node not found: {nodePath}" }); return; }

        var oldValue = node.Get(property);
        var rawValue = @params["value"];
        var parsedValue = ParseValueForType(rawValue, oldValue.VariantType);

        node.Set(property, parsedValue);
        var newValue = node.Get(property);

        WriteResponse(new Dictionary
        {
            ["node_path"] = node.GetPath().ToString(),
            ["property"] = property,
            ["old_value"] = PropertyParser.SerializeValue(oldValue),
            ["new_value"] = PropertyParser.SerializeValue(newValue),
        });
    }

    private static Variant ParseValueForType(Variant raw, Variant.Type targetType)
    {
        if (raw.VariantType == targetType) return raw;

        if (raw.VariantType == Variant.Type.Dictionary)
        {
            var dict = raw.AsGodotDictionary();
            switch (targetType)
            {
                case Variant.Type.Vector3:
                    return new Vector3(dict.GetFloat("x"), dict.GetFloat("y"), dict.GetFloat("z"));
                case Variant.Type.Vector3I:
                    return new Vector3I((int)dict.GetFloat("x"), (int)dict.GetFloat("y"), (int)dict.GetFloat("z"));
                case Variant.Type.Vector2:
                    return new Vector2(dict.GetFloat("x"), dict.GetFloat("y"));
                case Variant.Type.Vector2I:
                    return new Vector2I((int)dict.GetFloat("x"), (int)dict.GetFloat("y"));
                case Variant.Type.Color:
                    return new Color(dict.GetFloat("r"), dict.GetFloat("g"), dict.GetFloat("b"), dict.GetFloat("a", 1f));
            }
            return raw;
        }

        if (raw.VariantType == Variant.Type.String)
        {
            var rawStr = raw.AsString();
            if (rawStr.StartsWith("#")) return Color.FromHtml(rawStr);
            var expr = new Expression();
            if (expr.Parse(rawStr) == Godot.Error.Ok)
            {
                var result = expr.Execute();
                if (!expr.HasExecuteFailed()) return result;
            }
            return rawStr;
        }

        if (raw.VariantType == Variant.Type.Float && targetType == Variant.Type.Int)
            return (long)raw.AsDouble();
        if (raw.VariantType == Variant.Type.Int && targetType == Variant.Type.Float)
            return raw.AsInt64();

        return raw;
    }

    // ── execute_script ────────────────────────────────────────────────────────

    private void CmdExecuteScript(Dictionary @params)
    {
        var code = @params.TryGetValue("code", out var cv) ? cv.AsString() : "";
        if (string.IsNullOrEmpty(code)) { WriteResponse(new Dictionary { ["error"] = "code is required" }); return; }

        // Normalize indentation: convert leading spaces to tabs
        var rawLines = code.Split('\n');
        var indentSize = 0;
        foreach (var rawLine in rawLines)
        {
            var spaces = 0;
            while (spaces < rawLine.Length && rawLine[spaces] == ' ') spaces++;
            if (spaces > 0 && (indentSize == 0 || spaces < indentSize))
                indentSize = spaces;
        }
        if (indentSize > 0)
        {
            var spaceUnit = new string(' ', indentSize);
            for (var idx = 0; idx < rawLines.Length; idx++)
            {
                var rl = rawLines[idx];
                var tabs = "";
                while (rl.StartsWith(spaceUnit)) { tabs += "\t"; rl = rl[indentSize..]; }
                rawLines[idx] = tabs + rl;
            }
        }

        // Separate top-level func definitions (place at class level, not inside run())
        var classFuncs = new List<string>();
        var bodyLines = new List<string>();
        var i = 0;
        while (i < rawLines.Length)
        {
            var line = rawLines[i];
            if (!line.StartsWith("\t") && !line.StartsWith(" ") && line.StartsWith("func "))
            {
                classFuncs.Add(line);
                i++;
                while (i < rawLines.Length)
                {
                    var nextLine = rawLines[i];
                    if (string.IsNullOrEmpty(nextLine) || nextLine.StartsWith("\t"))
                    {
                        classFuncs.Add(nextLine);
                        i++;
                    }
                    else break;
                }
            }
            else
            {
                bodyLines.Add(line);
                i++;
            }
        }

        var wrapped = "extends Node\n\nvar _mcp_output: Array = []\nvar _mcp_error: String = \"\"\n\n"
            + "func _mcp_print(value: Variant) -> void:\n\t_mcp_output.append(str(value))\n\n"
            + "func _safe_get(node: Node, prop: String, default: Variant = null) -> Variant:\n"
            + "\tif node == null:\n\t\treturn default\n\treturn node.get(prop) if prop in node else default\n\n";
        foreach (var funcLine in classFuncs) wrapped += funcLine + "\n";
        if (classFuncs.Count > 0) wrapped += "\n";
        wrapped += "func run() -> Variant:\n";
        foreach (var line in bodyLines) wrapped += "\t" + line + "\n";
        wrapped += "\treturn _mcp_output\n";

        var script = new GDScript();
        script.SourceCode = wrapped;
        var err = script.Reload();
        if (err != Godot.Error.Ok)
        {
            WriteResponse(new Dictionary { ["error"] = $"Script compilation failed: {err}" });
            return;
        }

        var tempNode = new Node();
        tempNode.SetScript(script);
        GetTree().CurrentScene.AddChild(tempNode);

        Variant output = default;
        if (tempNode.HasMethod("run"))
            output = tempNode.Call("run");

        var mcpOutput = tempNode.Get("_mcp_output");
        var outputArr = mcpOutput.VariantType == Variant.Type.Array ? mcpOutput.AsGodotArray() : new Godot.Collections.Array();
        tempNode.QueueFree();

        WriteResponse(new Dictionary
        {
            ["output"] = outputArr,
            ["return_value"] = output.VariantType == Variant.Type.Nil ? default : output.ToString(),
        });
    }

    // ── find_nodes_by_script ──────────────────────────────────────────────────

    private void CmdFindNodesByScript(Dictionary @params)
    {
        var scriptName = @params.TryGetValue("script", out var sn) ? sn.AsString() : "";
        if (string.IsNullOrEmpty(scriptName)) { WriteResponse(new Dictionary { ["error"] = "'script' is required" }); return; }

        var root = GetTree().CurrentScene;
        if (root == null) { WriteResponse(new Dictionary { ["error"] = "No current scene" }); return; }

        var propFilter = @params.TryGetValue("properties", out var pf) && pf.VariantType == Variant.Type.Array
            ? pf.AsGodotArray() : null;
        var matches = new Godot.Collections.Array();
        FindNodesByScriptRecursive(root, scriptName.ToLowerInvariant(), propFilter, matches);
        WriteResponse(new Dictionary { ["nodes"] = matches, ["count"] = matches.Count });
    }

    private static void FindNodesByScriptRecursive(Node node, string scriptFilter, Godot.Collections.Array? propFilter, Godot.Collections.Array results)
    {
        if (node.GetScript().As<Script>() is { } script && script.ResourcePath.ToLowerInvariant().Contains(scriptFilter))
        {
            var entry = new Dictionary
            {
                ["name"] = node.Name.ToString(),
                ["path"] = node.GetPath().ToString(),
                ["type"] = node.GetClass(),
                ["script"] = script.ResourcePath,
                ["properties"] = FilterNodeProperties(node, propFilter),
            };
            results.Add(entry);
        }
        foreach (var child in node.GetChildren())
            FindNodesByScriptRecursive(child, scriptFilter, propFilter, results);
    }

    // ── get_autoload ──────────────────────────────────────────────────────────

    private void CmdGetAutoload(Dictionary @params)
    {
        var autoloadName = @params.TryGetValue("name", out var n) ? n.AsString() : "";
        if (string.IsNullOrEmpty(autoloadName)) { WriteResponse(new Dictionary { ["error"] = "'name' is required" }); return; }

        var node = GetNodeOrNull(new NodePath("/root/" + autoloadName));
        if (node == null) { WriteResponse(new Dictionary { ["error"] = $"Autoload not found: {autoloadName}" }); return; }

        var propFilter = @params.TryGetValue("properties", out var pf) && pf.VariantType == Variant.Type.Array
            ? pf.AsGodotArray() : null;
        var props = FilterNodeProperties(node, propFilter);

        var result = new Dictionary
        {
            ["name"] = autoloadName,
            ["path"] = node.GetPath().ToString(),
            ["type"] = node.GetClass(),
            ["properties"] = props,
        };
        if (node.GetScript().As<Script>() is { } script && !string.IsNullOrEmpty(script.ResourcePath))
            result["script"] = script.ResourcePath;
        WriteResponse(result);
    }

    // ── batch_get_properties ──────────────────────────────────────────────────

    private void CmdBatchGetProperties(Dictionary @params)
    {
        if (!@params.TryGetValue("nodes", out var nodesVar) || nodesVar.VariantType != Variant.Type.Array)
        {
            WriteResponse(new Dictionary { ["error"] = "'nodes' array is required" });
            return;
        }
        var nodes = nodesVar.AsGodotArray();
        if (nodes.Count == 0) { WriteResponse(new Dictionary { ["error"] = "'nodes' array is required" }); return; }

        var results = new Godot.Collections.Array();
        foreach (var entryVar in nodes)
        {
            if (entryVar.VariantType != Variant.Type.Dictionary) continue;
            var entry = entryVar.AsGodotDictionary();
            var nodePath = entry.TryGetValue("path", out var np) ? np.AsString() : "";
            var propFilter = entry.TryGetValue("properties", out var pf) && pf.VariantType == Variant.Type.Array
                ? pf.AsGodotArray() : null;

            if (string.IsNullOrEmpty(nodePath))
            {
                results.Add(new Dictionary { ["path"] = "", ["properties"] = new Dictionary(), ["error"] = "Empty path" });
                continue;
            }
            var node = GetNodeOrNull(new NodePath(nodePath));
            if (node == null)
            {
                results.Add(new Dictionary { ["path"] = nodePath, ["properties"] = new Dictionary(), ["error"] = "Node not found" });
                continue;
            }
            results.Add(new Dictionary
            {
                ["path"] = nodePath,
                ["properties"] = FilterNodeProperties(node, propFilter),
            });
        }
        WriteResponse(new Dictionary { ["nodes"] = results, ["count"] = results.Count });
    }

    // ── find_ui_elements ──────────────────────────────────────────────────────

    private void CmdFindUiElements(Dictionary @params)
    {
        var root = GetTree().CurrentScene;
        if (root == null) { WriteResponse(new Dictionary { ["error"] = "No current scene" }); return; }

        var typeFilter = @params.TryGetValue("type_filter", out var tf) ? tf.AsString() : "";
        var elements = new Godot.Collections.Array();
        FindUiRecursive(root, typeFilter, elements);
        WriteResponse(new Dictionary { ["elements"] = elements, ["count"] = elements.Count });
    }

    private static void FindUiRecursive(Node node, string typeFilter, Godot.Collections.Array results)
    {
        if (node is Control { Visible: true } ctrl)
        {
            var entry = new Dictionary();
            bool include = false;
            switch (ctrl)
            {
                case OptionButton ob:
                    entry["type"] = "OptionButton";
                    entry["text"] = ob.Text;
                    entry["selected"] = ob.Selected;
                    include = true;
                    break;
                case CheckBox cb:
                    entry["type"] = "CheckBox";
                    entry["text"] = cb.Text;
                    entry["checked"] = cb.ButtonPressed;
                    include = true;
                    break;
                case Button btn:
                    entry["type"] = "Button";
                    entry["text"] = btn.Text;
                    entry["disabled"] = btn.Disabled;
                    include = true;
                    break;
                case Label lbl:
                    entry["type"] = "Label";
                    entry["text"] = lbl.Text;
                    include = true;
                    break;
                case LineEdit le:
                    entry["type"] = "LineEdit";
                    entry["text"] = le.Text;
                    entry["placeholder"] = le.PlaceholderText;
                    include = true;
                    break;
                case TextEdit te:
                    entry["type"] = "TextEdit";
                    entry["text"] = te.Text.Length > 200 ? te.Text[..200] : te.Text;
                    include = true;
                    break;
                case HSlider:
                case VSlider:
                {
                    var sl = (Godot.Range)ctrl;
                    entry["type"] = ctrl is HSlider ? "HSlider" : "VSlider";
                    entry["value"] = sl.Value;
                    entry["min"] = sl.MinValue;
                    entry["max"] = sl.MaxValue;
                    include = true;
                    break;
                }
            }

            if (include)
            {
                var entryType = entry.TryGetValue("type", out var et) ? et.AsString() : "";
                if (string.IsNullOrEmpty(typeFilter) || entryType == typeFilter)
                {
                    var rect = ctrl.GetGlobalRect();
                    entry["name"] = ctrl.Name.ToString();
                    entry["path"] = ctrl.GetPath().ToString();
                    entry["rect"] = new Dictionary
                    {
                        ["x"] = rect.Position.X,
                        ["y"] = rect.Position.Y,
                        ["width"] = rect.Size.X,
                        ["height"] = rect.Size.Y,
                    };
                    entry["center"] = new Dictionary
                    {
                        ["x"] = rect.Position.X + rect.Size.X / 2.0f,
                        ["y"] = rect.Position.Y + rect.Size.Y / 2.0f,
                    };
                    results.Add(entry);
                }
            }
        }

        foreach (var child in node.GetChildren())
            FindUiRecursive(child, typeFilter, results);
    }

    // ── click_button_by_text ──────────────────────────────────────────────────

    private void CmdClickButtonByText(Dictionary @params)
    {
        var text = @params.TryGetValue("text", out var tv) ? tv.AsString() : "";
        var partial = @params.TryGetValue("partial", out var pv) ? pv.AsBool() : true;
        if (string.IsNullOrEmpty(text)) { WriteResponse(new Dictionary { ["error"] = "'text' is required" }); return; }

        var root = GetTree().CurrentScene;
        if (root == null) { WriteResponse(new Dictionary { ["error"] = "No current scene" }); return; }

        var btn = FindButtonByText(root, text, partial);
        if (btn == null) { WriteResponse(new Dictionary { ["error"] = $"No visible button found with text: '{text}'" }); return; }

        var rect = btn.GetGlobalRect();
        var center = rect.GetCenter();
        var btnTextValue = btn.Text;
        var btnPath = btn.IsInsideTree() ? btn.GetPath().ToString() : "";

        btn.EmitSignal(BaseButton.SignalName.Pressed);

        if (!GodotObject.IsInstanceValid(btn) || !btn.IsInsideTree())
        {
            WriteResponse(new Dictionary
            {
                ["clicked"] = true,
                ["button_text"] = btnTextValue,
                ["button_path"] = btnPath,
                ["position"] = new Dictionary { ["x"] = center.X, ["y"] = center.Y },
                ["note"] = "Button was removed from scene tree after click (likely a scene transition)",
            });
            return;
        }

        WriteResponse(new Dictionary
        {
            ["clicked"] = true,
            ["button_text"] = btn.Text,
            ["button_path"] = btn.GetPath().ToString(),
            ["position"] = new Dictionary { ["x"] = center.X, ["y"] = center.Y },
        });
    }

    private static Button? FindButtonByText(Node node, string text, bool partial)
    {
        if (node is Button { Visible: true } btn)
        {
            var btnText = btn.Text.ToLowerInvariant().StripEdges();
            var searchText = text.ToLowerInvariant().StripEdges();
            if (partial && btnText.Contains(searchText)) return btn;
            if (!partial && btnText == searchText) return btn;
        }
        foreach (var child in node.GetChildren())
        {
            var found = FindButtonByText(child, text, partial);
            if (found != null) return found;
        }
        return null;
    }

    // ── wait_for_node ─────────────────────────────────────────────────────────

    private async Task CmdWaitForNode(Dictionary @params)
    {
        _pendingCommand = false;
        var nodePath = @params.TryGetValue("node_path", out var np) ? np.AsString() : "";
        if (string.IsNullOrEmpty(nodePath)) { WriteResponse(new Dictionary { ["error"] = "'node_path' is required" }); return; }

        var timeoutSec = @params.TryGetValue("timeout", out var to) ? to.AsSingle() : 5.0f;
        var pollInterval = Mathf.Max(@params.TryGetValue("poll_frames", out var pf) ? pf.AsInt32() : 5, 1);

        var attempts = (int)(timeoutSec / (pollInterval / 60.0));
        for (var i = 0; i < attempts; i++)
        {
            var node = GetNodeOrNull(new NodePath(nodePath));
            if (node != null)
            {
                var result = new Dictionary
                {
                    ["found"] = true,
                    ["node_path"] = node.GetPath().ToString(),
                    ["type"] = node.GetClass(),
                    ["name"] = node.Name.ToString(),
                };
                if (node.GetScript().As<Script>() is { } script && !string.IsNullOrEmpty(script.ResourcePath))
                    result["script"] = script.ResourcePath;
                WriteResponse(result);
                return;
            }
            for (var f = 0; f < pollInterval; f++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        WriteResponse(new Dictionary
        {
            ["found"] = false,
            ["node_path"] = nodePath,
            ["error"] = $"Node not found after {timeoutSec:0.0}s",
        });
    }

    // ── find_nearby_nodes ─────────────────────────────────────────────────────

    private void CmdFindNearbyNodes(Dictionary @params)
    {
        var radius = @params.GetFloat("radius", 20.0f);
        var maxResults = @params.GetInt("max_results", 10);
        var typeFilter = @params.TryGetValue("type_filter", out var tf) ? tf.AsString() : "";
        var groupFilter = @params.TryGetValue("group_filter", out var gf) ? gf.AsString() : "";

        var origin = Vector3.Zero;
        if (@params.TryGetValue("position", out var positionParam))
        {
            if (positionParam.VariantType == Variant.Type.String)
            {
                var originNode = GetNodeOrNull(new NodePath(positionParam.AsString()));
                if (originNode == null) { WriteResponse(new Dictionary { ["error"] = $"Origin node not found: {positionParam.AsString()}" }); return; }
                if (originNode is Node3D n3d) origin = n3d.GlobalPosition;
                else if (originNode is Node2D n2d) origin = new Vector3(n2d.GlobalPosition.X, n2d.GlobalPosition.Y, 0);
                else { WriteResponse(new Dictionary { ["error"] = $"Origin node is not Node2D or Node3D: {positionParam.AsString()}" }); return; }
            }
            else if (positionParam.VariantType == Variant.Type.Dictionary)
            {
                var dict = positionParam.AsGodotDictionary();
                origin = new Vector3(dict.GetFloat("x"), dict.GetFloat("y"), dict.GetFloat("z"));
            }
            else
            {
                WriteResponse(new Dictionary { ["error"] = "'position' is required (node_path string or {x,y,z} object)" });
                return;
            }
        }
        else
        {
            WriteResponse(new Dictionary { ["error"] = "'position' is required (node_path string or {x,y,z} object)" });
            return;
        }

        var root = GetTree().CurrentScene;
        if (root == null) { WriteResponse(new Dictionary { ["error"] = "No current scene" }); return; }

        var candidates = new List<Dictionary>();
        FindNearbyRecursive(root, origin, radius, typeFilter, groupFilter, candidates);
        candidates.Sort((a, b) => a.GetFloat("distance").CompareTo(b.GetFloat("distance")));
        if (candidates.Count > maxResults) candidates.RemoveRange(maxResults, candidates.Count - maxResults);

        var candidatesArr = new Godot.Collections.Array();
        foreach (var d in candidates) candidatesArr.Add(d);

        WriteResponse(new Dictionary
        {
            ["origin"] = new Dictionary { ["x"] = origin.X, ["y"] = origin.Y, ["z"] = origin.Z },
            ["radius"] = radius,
            ["nodes"] = candidatesArr,
            ["count"] = candidatesArr.Count,
        });
    }

    private static void FindNearbyRecursive(Node node, Vector3 origin, float radius, string typeFilter, string groupFilter, List<Dictionary> results)
    {
        Vector3 pos = Vector3.Zero;
        var isSpatial = false;
        if (node is Node3D n3d) { pos = n3d.GlobalPosition; isSpatial = true; }
        else if (node is Node2D n2d) { pos = new Vector3(n2d.GlobalPosition.X, n2d.GlobalPosition.Y, 0); isSpatial = true; }

        if (isSpatial)
        {
            var diff = pos - origin;
            var dist = diff.Length();
            if (dist <= radius)
            {
                var passes = true;
                if (!string.IsNullOrEmpty(typeFilter) && !node.IsClass(typeFilter)) passes = false;
                if (!string.IsNullOrEmpty(groupFilter) && !node.IsInGroup(groupFilter)) passes = false;

                if (passes)
                {
                    var entry = new Dictionary
                    {
                        ["node_path"] = node.GetPath().ToString(),
                        ["name"] = node.Name.ToString(),
                        ["type"] = node.GetClass(),
                        ["distance"] = Mathf.Snapped(dist, 0.01f),
                        ["global_position"] = new Dictionary { ["x"] = Mathf.Snapped(pos.X, 0.01f), ["y"] = Mathf.Snapped(pos.Y, 0.01f), ["z"] = Mathf.Snapped(pos.Z, 0.01f) },
                        ["direction"] = new Dictionary { ["x"] = Mathf.Snapped(diff.X, 0.01f), ["y"] = Mathf.Snapped(diff.Y, 0.01f), ["z"] = Mathf.Snapped(diff.Z, 0.01f) },
                    };
                    if (node.GetScript().As<Script>() is { } script && !string.IsNullOrEmpty(script.ResourcePath))
                        entry["script"] = script.ResourcePath;
                    results.Add(entry);
                }
            }
        }

        foreach (var child in node.GetChildren())
            FindNearbyRecursive(child, origin, radius, typeFilter, groupFilter, results);
    }

    // ── navigate_to ───────────────────────────────────────────────────────────

    private void CmdNavigateTo(Dictionary @params)
    {
        var playerPath = @params.TryGetValue("player_path", out var pp) ? pp.AsString() : "/root/Main/Player";
        var player = GetNodeOrNull(new NodePath(playerPath));
        if (player == null) { WriteResponse(new Dictionary { ["error"] = $"Player not found: {playerPath}" }); return; }

        Vector3 playerPos;
        if (player is Node3D p3d) playerPos = p3d.GlobalPosition;
        else { WriteResponse(new Dictionary { ["error"] = $"Player is not Node3D: {playerPath}" }); return; }

        Vector3 targetPos;
        if (@params.TryGetValue("target", out var targetParam))
        {
            if (targetParam.VariantType == Variant.Type.String)
            {
                var targetNode = GetNodeOrNull(new NodePath(targetParam.AsString()));
                if (targetNode == null) { WriteResponse(new Dictionary { ["error"] = $"Target node not found: {targetParam.AsString()}" }); return; }
                if (targetNode is Node3D tn3d) targetPos = tn3d.GlobalPosition;
                else { WriteResponse(new Dictionary { ["error"] = $"Target is not Node3D: {targetParam.AsString()}" }); return; }
            }
            else if (targetParam.VariantType == Variant.Type.Dictionary)
            {
                var dict = targetParam.AsGodotDictionary();
                targetPos = new Vector3(dict.GetFloat("x"), dict.GetFloat("y"), dict.GetFloat("z"));
            }
            else { WriteResponse(new Dictionary { ["error"] = "'target' is required (node_path string or {x,y,z} object)" }); return; }
        }
        else { WriteResponse(new Dictionary { ["error"] = "'target' is required (node_path string or {x,y,z} object)" }); return; }

        var worldDir = targetPos - playerPos;
        var distance = worldDir.Length();
        var flatDir = new Vector3(worldDir.X, 0, worldDir.Z).Normalized();

        var cameraPath = @params.TryGetValue("camera_path", out var cp) ? cp.AsString() : "";
        Camera3D? camera = null;
        if (!string.IsNullOrEmpty(cameraPath))
        {
            if (GetNodeOrNull(new NodePath(cameraPath)) is Camera3D cam) camera = cam;
        }
        else camera = GetViewport().GetCamera3D();

        var suggestedKeys = new Godot.Collections.Array();
        float cameraYawDelta = 0f;
        var cameraForward = Vector3.Zero;

        if (camera != null)
        {
            cameraForward = -camera.GlobalBasis.Z;
            var camFlat = new Vector3(cameraForward.X, 0, cameraForward.Z).Normalized();
            var camRight = new Vector3(cameraForward.Z, 0, -cameraForward.X).Normalized();

            if (flatDir.Length() > 0.01f)
            {
                var forwardDot = flatDir.Dot(camFlat);
                var rightDot = flatDir.Dot(camRight);
                if (forwardDot > 0.3f) suggestedKeys.Add("KEY_W");
                else if (forwardDot < -0.3f) suggestedKeys.Add("KEY_S");
                if (rightDot > 0.3f) suggestedKeys.Add("KEY_D");
                else if (rightDot < -0.3f) suggestedKeys.Add("KEY_A");

                var angleToTarget = Mathf.Atan2(flatDir.X, flatDir.Z);
                var camYaw = Mathf.Atan2(camFlat.X, camFlat.Z);
                cameraYawDelta = angleToTarget - camYaw;
                while (cameraYawDelta > Mathf.Pi) cameraYawDelta -= Mathf.Tau;
                while (cameraYawDelta < -Mathf.Pi) cameraYawDelta += Mathf.Tau;
            }
        }

        var moveSpeed = @params.GetFloat("move_speed", 5.0f);
        var estimatedDuration = moveSpeed > 0 ? distance / moveSpeed : 0;
        var mouseSensitivityScale = 400.0f / Mathf.Pi;
        var suggestedMouseX = -cameraYawDelta * mouseSensitivityScale;

        WriteResponse(new Dictionary
        {
            ["distance"] = Mathf.Snapped(distance, 0.01f),
            ["world_direction"] = new Dictionary { ["x"] = Mathf.Snapped(worldDir.X, 0.01f), ["y"] = Mathf.Snapped(worldDir.Y, 0.01f), ["z"] = Mathf.Snapped(worldDir.Z, 0.01f) },
            ["flat_direction"] = new Dictionary { ["x"] = Mathf.Snapped(flatDir.X, 0.01f), ["z"] = Mathf.Snapped(flatDir.Z, 0.01f) },
            ["suggested_keys"] = suggestedKeys,
            ["camera_rotation_delta"] = new Dictionary
            {
                ["yaw_radians"] = Mathf.Snapped(cameraYawDelta, 0.001f),
                ["suggested_mouse_relative_x"] = Mathf.Snapped(suggestedMouseX, 1.0f),
            },
            ["estimated_duration"] = Mathf.Snapped(estimatedDuration, 0.1f),
            ["player_position"] = new Dictionary { ["x"] = Mathf.Snapped(playerPos.X, 0.01f), ["y"] = Mathf.Snapped(playerPos.Y, 0.01f), ["z"] = Mathf.Snapped(playerPos.Z, 0.01f) },
            ["target_position"] = new Dictionary { ["x"] = Mathf.Snapped(targetPos.X, 0.01f), ["y"] = Mathf.Snapped(targetPos.Y, 0.01f), ["z"] = Mathf.Snapped(targetPos.Z, 0.01f) },
        });
    }

    // ── move_to ───────────────────────────────────────────────────────────────

    private void CmdMoveTo(Dictionary @params)
    {
        var playerPath = @params.TryGetValue("player_path", out var pp) ? pp.AsString() : "/root/Main/Player";
        var player = GetNodeOrNull(new NodePath(playerPath));
        if (player == null || player is not Node3D p3d)
        {
            WriteResponse(new Dictionary { ["error"] = $"Player not found or not Node3D: {playerPath}" });
            return;
        }
        _movetoPlayer = p3d;

        if (@params.TryGetValue("target", out var targetParam))
        {
            if (targetParam.VariantType == Variant.Type.String)
            {
                var targetNode = GetNodeOrNull(new NodePath(targetParam.AsString()));
                if (targetNode == null) { WriteResponse(new Dictionary { ["error"] = $"Target node not found: {targetParam.AsString()}" }); return; }
                if (targetNode is Node3D tn3d) _movetoTarget = tn3d.GlobalPosition;
                else { WriteResponse(new Dictionary { ["error"] = $"Target is not Node3D: {targetParam.AsString()}" }); return; }
            }
            else if (targetParam.VariantType == Variant.Type.Dictionary)
            {
                var dict = targetParam.AsGodotDictionary();
                _movetoTarget = new Vector3(dict.GetFloat("x"), dict.GetFloat("y"), dict.GetFloat("z"));
            }
            else { WriteResponse(new Dictionary { ["error"] = "'target' is required (node_path string or {x,y,z} object)" }); return; }
        }
        else { WriteResponse(new Dictionary { ["error"] = "'target' is required (node_path string or {x,y,z} object)" }); return; }

        _movetoCameraPivot = null;
        var cameraPath = @params.TryGetValue("camera_path", out var cp) ? cp.AsString() : "";
        if (!string.IsNullOrEmpty(cameraPath))
        {
            if (GetNodeOrNull(new NodePath(cameraPath)) is Node3D camNode) _movetoCameraPivot = camNode;
        }
        else
        {
            foreach (var child in _movetoPlayer.GetChildren())
                if (child is SpringArm3D) { _movetoCameraPivot = child as Node3D; break; }
            if (_movetoCameraPivot == null)
            {
                var cam = GetViewport().GetCamera3D();
                if (cam != null && cam.GetParent() is Node3D parent3d && cam.GetParent() != GetTree().Root)
                    _movetoCameraPivot = parent3d;
            }
        }

        _movetoArrivalRadius = @params.GetFloat("arrival_radius", 1.5f);
        _movetoTimeout = @params.GetFloat("timeout", 15.0f);
        _movetoRun = @params.TryGetValue("run", out var rv) ? rv.AsBool() : false;
        _movetoLookAt = @params.TryGetValue("look_at_target", out var lav) ? lav.AsBool() : true;
        _movetoElapsed = 0f;
        _movetoKeysHeld.Clear();

        var dist = _movetoPlayer.GlobalPosition.DistanceTo(_movetoTarget);
        if (dist <= _movetoArrivalRadius)
        {
            WriteResponse(new Dictionary
            {
                ["success"] = true,
                ["arrived"] = true,
                ["final_distance"] = Mathf.Snapped(dist, 0.01f),
                ["final_position"] = PropertyParser.SerializeValue(_movetoPlayer.GlobalPosition),
                ["target_position"] = PropertyParser.SerializeValue(_movetoTarget),
                ["elapsed_time"] = 0.0f,
            });
            return;
        }

        _pendingCommand = false;
        _state = State.MovingTo;

        InjectKey(Key.W, true);
        if (_movetoRun) InjectKey(Key.Shift, true);
    }

    private void ProcessMoveTo(float delta)
    {
        if (Godot.FileAccess.FileExists(RequestPath))
        {
            FinishMoveTo(false, "Aborted by new command");
            _state = State.Idle;
            HandleRequest();
            return;
        }

        _movetoElapsed += delta;

        if (_movetoElapsed >= _movetoTimeout)
        {
            FinishMoveTo(false, $"Timeout after {_movetoTimeout:0.0}s");
            return;
        }

        if (_movetoPlayer == null || !GodotObject.IsInstanceValid(_movetoPlayer))
        {
            FinishMoveTo(false, "Player node was freed");
            return;
        }

        var playerPos = _movetoPlayer.GlobalPosition;
        var flatTarget = new Vector3(_movetoTarget.X, playerPos.Y, _movetoTarget.Z);
        var dist = playerPos.DistanceTo(flatTarget);

        if (dist <= _movetoArrivalRadius)
        {
            FinishMoveTo(true, "Arrived");
            return;
        }

        if (_movetoLookAt && _movetoCameraPivot != null && GodotObject.IsInstanceValid(_movetoCameraPivot))
        {
            var dir = flatTarget - playerPos;
            if (dir.LengthSquared() > 0.01f)
            {
                var targetYaw = Mathf.Atan2(-dir.X, -dir.Z);
                var currentYaw = _movetoCameraPivot.Rotation.Y;
                var yawDiff = targetYaw - currentYaw;
                while (yawDiff > Mathf.Pi) yawDiff -= Mathf.Tau;
                while (yawDiff < -Mathf.Pi) yawDiff += Mathf.Tau;
                var maxStep = 10.0f * delta;
                var step = Mathf.Clamp(yawDiff, -maxStep, maxStep);
                _movetoCameraPivot.Rotation = new Vector3(
                    _movetoCameraPivot.Rotation.X,
                    _movetoCameraPivot.Rotation.Y + step,
                    _movetoCameraPivot.Rotation.Z);
            }
        }
    }

    private void FinishMoveTo(bool success, string message)
    {
        ReleaseAllKeys();
        _state = State.Idle;

        var finalPos = Vector3.Zero;
        var finalDist = 0f;
        if (_movetoPlayer != null && GodotObject.IsInstanceValid(_movetoPlayer))
        {
            finalPos = _movetoPlayer.GlobalPosition;
            finalDist = finalPos.DistanceTo(_movetoTarget);
        }

        WriteResponse(new Dictionary
        {
            ["success"] = success,
            ["arrived"] = success,
            ["message"] = message,
            ["final_distance"] = Mathf.Snapped(finalDist, 0.01f),
            ["final_position"] = PropertyParser.SerializeValue(finalPos),
            ["target_position"] = PropertyParser.SerializeValue(_movetoTarget),
            ["elapsed_time"] = Mathf.Snapped(_movetoElapsed, 0.01f),
        });
    }

    private void InjectKey(Key keycode, bool pressed)
    {
        var evt = new InputEventKey { Keycode = keycode, Pressed = pressed };
        Input.ParseInputEvent(evt);
        if (pressed) _movetoKeysHeld.Add(keycode);
        else _movetoKeysHeld.Remove(keycode);
    }

    private void ReleaseAllKeys()
    {
        foreach (var keycode in _movetoKeysHeld.ToList())
        {
            var evt = new InputEventKey { Keycode = keycode, Pressed = false };
            Input.ParseInputEvent(evt);
        }
        _movetoKeysHeld.Clear();
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    private void CmdStartRecording(Dictionary @params)
    {
        _recordingEvents.Clear();
        _recordingStartMsec = Time.GetTicksMsec();
        _state = State.Recording;
        SetProcessInput(true);
        WriteResponse(new Dictionary { ["recording"] = true, ["message"] = "Recording started" });
    }

    private void CmdStopRecording(Dictionary @params)
    {
        SetProcessInput(false);
        _state = State.Idle;
        var events = _recordingEvents.Duplicate();
        var durationMs = Time.GetTicksMsec() - _recordingStartMsec;
        WriteResponse(new Dictionary
        {
            ["recording"] = false,
            ["events"] = events,
            ["event_count"] = events.Count,
            ["duration_ms"] = durationMs,
        });
    }

    private async Task CmdReplayRecording(Dictionary @params)
    {
        _pendingCommand = false;
        if (!@params.TryGetValue("events", out var eventsVar) || eventsVar.VariantType != Variant.Type.Array)
        {
            WriteResponse(new Dictionary { ["error"] = "No events to replay" });
            return;
        }
        var events = eventsVar.AsGodotArray();
        if (events.Count == 0) { WriteResponse(new Dictionary { ["error"] = "No events to replay" }); return; }

        var speed = @params.TryGetValue("speed", out var sv) ? sv.AsSingle() : 1.0f;

        var startMsec = Time.GetTicksMsec();
        foreach (var eventData in events)
        {
            var ed = eventData.AsGodotDictionary();
            var delayMs = ed.GetInt("time_ms", 0);
            var adjustedDelay = (int)(delayMs / speed);

            while ((long)(Time.GetTicksMsec() - startMsec) < adjustedDelay)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var evt = ReconstructEvent(ed);
            if (evt != null) Input.ParseInputEvent(evt);
        }

        WriteResponse(new Dictionary
        {
            ["replayed"] = true,
            ["event_count"] = events.Count,
            ["speed"] = speed,
        });
    }

    public override void _Input(InputEvent @event)
    {
        if (_state != State.Recording) return;

        var timeMs = Time.GetTicksMsec() - _recordingStartMsec;
        var data = new Dictionary { ["time_ms"] = timeMs };

        switch (@event)
        {
            case InputEventKey key:
            {
                data["type"] = "key";
                data["keycode"] = key.Keycode != Key.None ? OS.GetKeycodeString(key.Keycode) : "";
                data["physical_keycode"] = key.PhysicalKeycode != Key.None ? OS.GetKeycodeString(key.PhysicalKeycode) : "";
                data["pressed"] = key.Pressed;
                data["shift"] = key.ShiftPressed;
                data["ctrl"] = key.CtrlPressed;
                data["alt"] = key.AltPressed;
                break;
            }
            case InputEventMouseButton mb:
            {
                data["type"] = "mouse_button";
                data["button"] = (long)mb.ButtonIndex;
                data["pressed"] = mb.Pressed;
                data["position"] = new Dictionary { ["x"] = mb.Position.X, ["y"] = mb.Position.Y };
                data["double_click"] = mb.DoubleClick;
                break;
            }
            case InputEventMouseMotion mm:
            {
                data["type"] = "mouse_motion";
                data["position"] = new Dictionary { ["x"] = mm.Position.X, ["y"] = mm.Position.Y };
                data["relative"] = new Dictionary { ["x"] = mm.Relative.X, ["y"] = mm.Relative.Y };
                break;
            }
            case InputEventAction act:
            {
                data["type"] = "action";
                data["action"] = act.Action;
                data["pressed"] = act.Pressed;
                data["strength"] = act.Strength;
                break;
            }
            default: return;
        }

        _recordingEvents.Add(data);
    }

    private static InputEvent? ReconstructEvent(Dictionary data)
    {
        var type = data.TryGetValue("type", out var tv) ? tv.AsString() : "";
        switch (type)
        {
            case "key":
            {
                var evt = new InputEventKey();
                var keycodeStr = data.TryGetValue("keycode", out var kc) ? kc.AsString() : "";
                if (!string.IsNullOrEmpty(keycodeStr)) evt.Keycode = OS.FindKeycodeFromString(keycodeStr);
                evt.Pressed = data.TryGetValue("pressed", out var pr) ? pr.AsBool() : true;
                evt.ShiftPressed = data.TryGetValue("shift", out var sh) ? sh.AsBool() : false;
                evt.CtrlPressed = data.TryGetValue("ctrl", out var ct) ? ct.AsBool() : false;
                evt.AltPressed = data.TryGetValue("alt", out var al) ? al.AsBool() : false;
                return evt;
            }
            case "mouse_button":
            {
                var evt = new InputEventMouseButton();
                evt.ButtonIndex = data.TryGetValue("button", out var b) ? (MouseButton)b.AsInt32() : MouseButton.Left;
                evt.Pressed = data.TryGetValue("pressed", out var pr) ? pr.AsBool() : true;
                evt.DoubleClick = data.TryGetValue("double_click", out var dc) ? dc.AsBool() : false;
                var pos = data.TryGetValue("position", out var posVar) && posVar.VariantType == Variant.Type.Dictionary
                    ? posVar.AsGodotDictionary() : new Dictionary();
                evt.Position = new Vector2(pos.GetFloat("x"), pos.GetFloat("y"));
                evt.GlobalPosition = evt.Position;
                return evt;
            }
            case "mouse_motion":
            {
                var evt = new InputEventMouseMotion();
                var pos = data.TryGetValue("position", out var posVar) && posVar.VariantType == Variant.Type.Dictionary
                    ? posVar.AsGodotDictionary() : new Dictionary();
                evt.Position = new Vector2(pos.GetFloat("x"), pos.GetFloat("y"));
                evt.GlobalPosition = evt.Position;
                var rel = data.TryGetValue("relative", out var relVar) && relVar.VariantType == Variant.Type.Dictionary
                    ? relVar.AsGodotDictionary() : new Dictionary();
                evt.Relative = new Vector2(rel.GetFloat("x"), rel.GetFloat("y"));
                return evt;
            }
            case "action":
            {
                var evt = new InputEventAction();
                evt.Action = data.TryGetValue("action", out var ac) ? ac.AsString() : "";
                evt.Pressed = data.TryGetValue("pressed", out var pr) ? pr.AsBool() : true;
                evt.Strength = data.TryGetValue("strength", out var st) ? st.AsSingle() : 1.0f;
                return evt;
            }
            default: return null;
        }
    }

    // ── assert_node_state ─────────────────────────────────────────────────────

    private void CmdAssertNodeState(Dictionary @params)
    {
        var nodePath = @params.TryGetValue("node_path", out var np) ? np.AsString() : "";
        if (string.IsNullOrEmpty(nodePath)) { WriteResponse(new Dictionary { ["error"] = "node_path is required" }); return; }
        var property = @params.TryGetValue("property", out var prop) ? prop.AsString() : "";
        if (string.IsNullOrEmpty(property)) { WriteResponse(new Dictionary { ["error"] = "property is required" }); return; }

        var node = GetNodeOrNull(new NodePath(nodePath));
        if (node == null) { WriteResponse(new Dictionary { ["error"] = $"Node not found: {nodePath}" }); return; }

        var actual = property.Contains(':')
            ? node.GetIndexed(new NodePath(property))
            : node.Get(property);
        var expected = @params.TryGetValue("expected", out var ev) ? ev : default;
        var op = @params.TryGetValue("operator", out var ov) ? ov.AsString() : "eq";
        bool passed;
        switch (op)
        {
            case "eq": passed = actual.ToString() == expected.ToString() || actual.Equals(expected); break;
            case "neq": passed = !actual.Equals(expected) && actual.ToString() != expected.ToString(); break;
            case "gt": passed = actual.AsDouble() > expected.AsDouble(); break;
            case "lt": passed = actual.AsDouble() < expected.AsDouble(); break;
            case "gte": passed = actual.AsDouble() >= expected.AsDouble(); break;
            case "lte": passed = actual.AsDouble() <= expected.AsDouble(); break;
            case "contains": passed = actual.ToString().Contains(expected.ToString()); break;
            case "type_is": passed = (long)actual.VariantType == expected.AsInt64() || actual.VariantType.ToString() == expected.ToString(); break;
            default: WriteResponse(new Dictionary { ["error"] = $"Unknown operator: {op}" }); return;
        }

        WriteResponse(new Dictionary
        {
            ["result"] = new Dictionary
            {
                ["assertion"] = "node_state",
                ["node_path"] = nodePath,
                ["property"] = property,
                ["operator"] = op,
                ["expected"] = PropertyParser.SerializeValue(expected),
                ["actual"] = PropertyParser.SerializeValue(actual),
                ["passed"] = passed,
            }
        });
    }

    // ── get_performance_monitors ──────────────────────────────────────────────

    private void CmdGetPerformanceMonitors(Dictionary _)
    {
        var m = new Dictionary
        {
            ["fps"] = Performance.GetMonitor(Performance.Monitor.TimeFps),
            ["frame_time_msec"] = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0,
            ["physics_frame_time_msec"] = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0,
            ["navigation_process_msec"] = Performance.GetMonitor(Performance.Monitor.TimeNavigationProcess) * 1000.0,

            ["memory_static"] = Performance.GetMonitor(Performance.Monitor.MemoryStatic),
            ["memory_static_max"] = Performance.GetMonitor(Performance.Monitor.MemoryStaticMax),

            ["object_count"] = Performance.GetMonitor(Performance.Monitor.ObjectCount),
            ["object_resource_count"] = Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),
            ["object_node_count"] = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
            ["object_orphan_node_count"] = Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount),

            ["render_total_objects_in_frame"] = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
            ["render_total_primitives_in_frame"] = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
            ["render_total_draw_calls_in_frame"] = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            ["render_video_mem_used"] = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed),

            ["physics_2d_active_objects"] = Performance.GetMonitor(Performance.Monitor.Physics2DActiveObjects),
            ["physics_2d_collision_pairs"] = Performance.GetMonitor(Performance.Monitor.Physics2DCollisionPairs),
            ["physics_2d_island_count"] = Performance.GetMonitor(Performance.Monitor.Physics2DIslandCount),

            ["physics_3d_active_objects"] = Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects),
            ["physics_3d_collision_pairs"] = Performance.GetMonitor(Performance.Monitor.Physics3DCollisionPairs),
            ["physics_3d_island_count"] = Performance.GetMonitor(Performance.Monitor.Physics3DIslandCount),

            ["navigation_active_maps"] = Performance.GetMonitor(Performance.Monitor.NavigationActiveMaps),
            ["navigation_region_count"] = Performance.GetMonitor(Performance.Monitor.NavigationRegionCount),
            ["navigation_agent_count"] = Performance.GetMonitor(Performance.Monitor.NavigationAgentCount),
        };

        WriteResponse(new Dictionary { ["result"] = new Dictionary { ["monitors"] = m, ["process"] = "game" } });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void WriteResponse(Dictionary data)
    {
        _pendingCommand = false;
        var json = Json.Stringify(data);
        using var file = Godot.FileAccess.Open(ResponsePath, Godot.FileAccess.ModeFlags.Write);
        if (file != null) file.StoreString(json);
    }
}

internal static class DictionaryExtensions
{
    public static float GetFloat(this Godot.Collections.Dictionary d, string key, float @default = 0f) =>
        d.TryGetValue(key, out var v) ? v.AsSingle() : @default;

    public static float GetFloat(this Godot.Collections.Dictionary d, string key) =>
        d.TryGetValue(key, out var v) ? (float)v.AsDouble() : 0f;

    public static int GetInt(this Godot.Collections.Dictionary d, string key, int @default = 0) =>
        d.TryGetValue(key, out var v) ? (int)v.AsInt64() : @default;
}
