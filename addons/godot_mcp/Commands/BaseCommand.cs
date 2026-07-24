using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Base class for all MCP command modules.
/// Faithful port of addons/godot_mcp/commands/base_command.gd, including the
/// v1.14.0 file-conflict safety guards and game file-IPC bridge.
///
/// Envelope contract (parsed by MCP clients — do NOT change key names):
///   success => {"result": {...}}
///   error   => {"error": {"code": int, "message": str, "data": {...optional}}}
/// </summary>
[Tool]
public abstract partial class BaseCommand : Node
{
    public EditorPlugin? Plugin { get; set; }

    /// <summary>Override in subclasses: return all tool definitions this module provides.</summary>
    public abstract IReadOnlyList<ToolDefinition> GetTools();

    // ── Error codes ────────────────────────────────────────────────────────────

    public const int CodeGeneric = -32000;        // generic / no-scene / no-game
    public const int CodeNotFound = -32001;
    public const int CodeScriptCompile = -32002;
    public const int CodeConflict = -32009;
    public const int CodeMethodNotFound = -32601;
    public const int CodeInvalidParams = -32602;
    public const int CodeInternal = -32603;

    // ── Result envelopes ───────────────────────────────────────────────────────

    public static Dictionary Success(Dictionary? data = null) =>
        new() { ["result"] = data ?? new Dictionary() };

    public static Dictionary Error(int code, string message, Dictionary? data = null)
    {
        var err = new Dictionary { ["code"] = code, ["message"] = message };
        if (data is { Count: > 0 })
            err["data"] = data;
        return new Dictionary { ["error"] = err };
    }

    public static Dictionary ErrorNotFound(string what, string suggestion = "")
    {
        var data = new Dictionary();
        if (!string.IsNullOrEmpty(suggestion))
            data["suggestion"] = suggestion;
        return Error(CodeNotFound, $"{what} not found", data);
    }

    public static Dictionary ErrorInvalidParams(string message) =>
        Error(CodeInvalidParams, message);

    public static Dictionary ErrorNoScene() =>
        Error(CodeGeneric, "No scene is currently open",
            new Dictionary { ["suggestion"] = "Use open_scene to open a scene first" });

    public static Dictionary ErrorInternal(string message) =>
        Error(CodeInternal, $"Internal error: {message}");

    public static Dictionary ErrorConflict(string message, Dictionary? data = null) =>
        Error(CodeConflict, message, data);

    public static bool IsError(Dictionary result) => result.ContainsKey("error");

    // ── Param coercion ─────────────────────────────────────────────────────────

    /// <summary>Get a required non-empty string param. Error is non-null when missing.</summary>
    public static (string Value, Dictionary? Error) RequireString(Dictionary @params, string key)
    {
        if (!@params.TryGetValue(key, out var v)
            || v.VariantType != Variant.Type.String
            || string.IsNullOrEmpty(v.AsString()))
            return ("", ErrorInvalidParams($"Missing required parameter: {key}"));
        return (v.AsString(), null);
    }

    public static string OptionalString(Dictionary @params, string key, string @default = "")
    {
        if (@params.TryGetValue(key, out var v) && v.VariantType == Variant.Type.String)
            return v.AsString();
        return @default;
    }

    public static bool OptionalBool(Dictionary @params, string key, bool @default = false)
    {
        if (@params.TryGetValue(key, out var v) && v.VariantType == Variant.Type.Bool)
            return v.AsBool();
        return @default;
    }

    /// <summary>Coerces numeric strings too, matching GDScript's int(params[key]).</summary>
    public static int OptionalInt(Dictionary @params, string key, int @default = 0)
    {
        if (!@params.TryGetValue(key, out var v))
            return @default;
        return v.VariantType switch
        {
            Variant.Type.Int => (int)v.AsInt64(),
            Variant.Type.Float => (int)v.AsDouble(),
            Variant.Type.Bool => v.AsBool() ? 1 : 0,
            Variant.Type.String => (int)v.AsString().ToFloat(),
            _ => @default,
        };
    }

    public static double OptionalFloat(Dictionary @params, string key, double @default = 0.0)
    {
        if (!@params.TryGetValue(key, out var v))
            return @default;
        return v.VariantType switch
        {
            Variant.Type.Int => v.AsInt64(),
            Variant.Type.Float => v.AsDouble(),
            Variant.Type.Bool => v.AsBool() ? 1 : 0,
            Variant.Type.String => v.AsString().ToFloat(),
            _ => @default,
        };
    }

    public static Godot.Collections.Array? OptionalArray(Dictionary @params, string key)
    {
        if (@params.TryGetValue(key, out var v) && v.VariantType == Variant.Type.Array)
            return v.AsGodotArray();
        return null;
    }

    public static Dictionary? OptionalDict(Dictionary @params, string key)
    {
        if (@params.TryGetValue(key, out var v) && v.VariantType == Variant.Type.Dictionary)
            return v.AsGodotDictionary();
        return null;
    }

    /// <summary>GDScript-compatible bool coercion: "true"/"1"/"yes" (case-insensitive) → true.</summary>
    protected static bool CoerceBool(Variant v) => v.VariantType switch
    {
        Variant.Type.Bool => v.AsBool(),
        Variant.Type.String => v.AsString().ToLowerInvariant() is "true" or "1" or "yes",
        Variant.Type.Int => v.AsInt64() != 0,
        Variant.Type.Float => v.AsDouble() != 0,
        _ => false,
    };

    /// <summary>GDScript-compatible int coercion: parses strings like "10" → 10.</summary>
    protected static int CoerceInt(Variant v) => v.VariantType switch
    {
        Variant.Type.Int => (int)v.AsInt64(),
        Variant.Type.Float => (int)v.AsDouble(),
        Variant.Type.String => (int)v.AsString().ToFloat(),
        Variant.Type.Bool => v.AsBool() ? 1 : 0,
        _ => 0,
    };

    // ── Editor access ──────────────────────────────────────────────────────────

    public static EditorInterface GetEditor() => EditorInterface.Singleton;

    public static Node? GetEditedRoot() => EditorInterface.Singleton.GetEditedSceneRoot();

    public EditorUndoRedoManager GetUndoRedo() => Plugin!.GetUndoRedo();

    // ── Path helpers & write guards (v1.14.0 safety model) ────────────────────

    public static string NormalizeProjectPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        if (path.StartsWith("res://") || path.StartsWith("user://"))
            return path.SimplifyPath();
        return ProjectSettings.LocalizePath(path).SimplifyPath();
    }

    public static bool IsSceneResourcePath(string path)
    {
        var ext = path.GetExtension().ToLowerInvariant();
        return ext is "tscn" or "scn";
    }

    public static List<string> GetOpenScenePaths()
    {
        var paths = new List<string>();
        foreach (var scenePath in EditorInterface.Singleton.GetOpenScenes())
        {
            var normalized = NormalizeProjectPath(scenePath);
            if (!string.IsNullOrEmpty(normalized) && !paths.Contains(normalized))
                paths.Add(normalized);
        }
        var root = GetEditedRoot();
        if (root != null && !string.IsNullOrEmpty(root.SceneFilePath))
        {
            var activePath = NormalizeProjectPath(root.SceneFilePath);
            if (!paths.Contains(activePath))
                paths.Add(activePath);
        }
        return paths;
    }

    public static bool IsScenePathOpen(string path)
    {
        var normalized = NormalizeProjectPath(path);
        return !string.IsNullOrEmpty(normalized) && GetOpenScenePaths().Contains(normalized);
    }

    public static bool IsActiveScenePath(string path)
    {
        var root = GetEditedRoot();
        if (root == null)
            return false;
        return NormalizeProjectPath(root.SceneFilePath) == NormalizeProjectPath(path);
    }

    /// <summary>
    /// Refuse to write scene files that are currently open in the editor.
    /// Returns an empty dictionary when the write is allowed.
    /// </summary>
    public static Dictionary GuardOfflineSceneSave(string path)
    {
        if (IsSceneResourcePath(path) && IsScenePathOpen(path))
        {
            var openScenes = new Godot.Collections.Array();
            foreach (var p in GetOpenScenePaths()) openScenes.Add(p);
            return ErrorConflict(
                $"Refusing to save open scene '{NormalizeProjectPath(path)}' outside the Godot editor state",
                new Dictionary
                {
                    ["path"] = NormalizeProjectPath(path),
                    ["open_scenes"] = openScenes,
                    ["suggestion"] = "Use live editor changes plus save_scene, or close the scene before offline edits.",
                });
        }
        return new Dictionary();
    }

    public static bool IsShaderResourcePath(string path)
    {
        var ext = path.GetExtension().ToLowerInvariant();
        return ext is "gdshader" or "gdshaderinc" or "shader";
    }

    public static bool IsTextResourceOpenInScriptEditor(string path)
    {
        var target = NormalizeProjectPath(path);
        if (string.IsNullOrEmpty(target))
            return false;
        if (IsShaderResourcePath(target) && ResourceLoader.HasCached(target))
            return true;
        var scriptEditor = EditorInterface.Singleton.GetScriptEditor();
        if (scriptEditor == null)
            return false;
        foreach (Script openScript in scriptEditor.GetOpenScripts())
        {
            if (openScript != null && NormalizeProjectPath(openScript.ResourcePath) == target)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Refuse to write text resources open in the script editor unless force=true.
    /// Returns an empty dictionary when the write is allowed.
    /// </summary>
    public static Dictionary GuardTextResourceWrite(string path, bool force)
    {
        if (!force && IsTextResourceOpenInScriptEditor(path))
        {
            return ErrorConflict(
                $"Refusing to write open text resource '{NormalizeProjectPath(path)}' outside the script editor state",
                new Dictionary
                {
                    ["path"] = NormalizeProjectPath(path),
                    ["suggestion"] = "Close the file in Godot's script editor or pass force=true to overwrite it deliberately.",
                });
        }
        return new Dictionary();
    }

    /// <summary>Create the parent directory of a res:// path if missing. Returns {} on success.</summary>
    public static Dictionary EnsureParentDir(string path)
    {
        var dir = path.GetBaseDir();
        if (string.IsNullOrEmpty(dir) || DirAccess.DirExistsAbsolute(dir))
            return new Dictionary();
        var derr = DirAccess.MakeDirRecursiveAbsolute(dir);
        if (derr != Godot.Error.Ok)
            return ErrorInternal($"Cannot create directory '{dir}': {derr}");
        return new Dictionary();
    }

    public static void MarkCurrentSceneUnsaved()
    {
        EditorInterface.Singleton.MarkSceneAsUnsaved();
    }

    // ── UndoRedo helpers ───────────────────────────────────────────────────────

    public void AddChildWithUndo(Node parent, Node child, Node root, string actionName)
    {
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction(actionName);
        undoRedo.AddDoMethod(parent, Node.MethodName.AddChild, child);
        undoRedo.AddDoMethod(child, Node.MethodName.SetOwner, root);
        undoRedo.AddDoReference(child);
        undoRedo.AddUndoMethod(parent, Node.MethodName.RemoveChild, child);
        undoRedo.CommitAction();
    }

    public void SetPropertyWithUndo(GodotObject target, string property, Variant newValue, string actionName)
    {
        var oldValue = target.Get(property);
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction(actionName);
        undoRedo.AddDoProperty(target, property, newValue);
        if (newValue.VariantType == Variant.Type.Object && newValue.AsGodotObject() is Resource newRes)
            undoRedo.AddDoReference(newRes);
        undoRedo.AddUndoProperty(target, property, oldValue);
        if (oldValue.VariantType == Variant.Type.Object && oldValue.AsGodotObject() is Resource oldRes)
            undoRedo.AddUndoReference(oldRes);
        undoRedo.CommitAction();
    }

    // ── Node resolution ────────────────────────────────────────────────────────

    /// <summary>Find node by path in the edited scene. "." or root-name resolves to the root.</summary>
    public static Node? FindNodeByPath(string nodePath)
    {
        var root = GetEditedRoot();
        if (root == null)
            return null;
        if (nodePath == "." || nodePath == root.Name)
            return root;
        if (root.HasNode(nodePath))
            return root.GetNode(nodePath);
        // Try with root name prefix stripped
        var prefix = root.Name + "/";
        if (nodePath.StartsWith(prefix))
        {
            var rel = nodePath[prefix.Length..];
            if (root.HasNode(rel))
                return root.GetNode(rel);
        }
        return null;
    }

    // ── Game file-IPC bridge ──────────────────────────────────────────────────

    /// <summary>
    /// Get the game process's user data directory.
    /// OS.GetUserDataDir() is cached at editor startup and won't reflect project
    /// name changes made to project.godot while the editor is running. The game
    /// process reads the name from disk, so we must do the same.
    /// </summary>
    public static string GetGameUserDir()
    {
        var cachedDir = OS.GetUserDataDir();
        var cfg = new ConfigFile();
        var err = cfg.Load(ProjectSettings.GlobalizePath("res://project.godot"));
        if (err != Godot.Error.Ok)
            return cachedDir;
        // When use_custom_user_dir=true, editor and game share the same dir.
        if (cfg.GetValue("application", "config/use_custom_user_dir", false).AsBool())
            return cachedDir;
        var diskName = cfg.GetValue("application", "config/name", "");
        if (diskName.VariantType != Variant.Type.String || string.IsNullOrEmpty(diskName.AsString()))
            return cachedDir;
        // Sanitize exactly like Godot does when computing the default user dir
        // (core/config/project_settings.cpp ProjectSettings::_init).
        var sanitized = ValidateFilename(diskName.AsString().XMLUnescape() ?? "").Replace(".", "_");
        if (string.IsNullOrEmpty(sanitized))
            return cachedDir;
        var gameDir = cachedDir.GetBaseDir().PathJoin(sanitized);
        if (!DirAccess.DirExistsAbsolute(gameDir))
            DirAccess.MakeDirRecursiveAbsolute(gameDir);
        return gameDir;
    }

    /// <summary>
    /// Replicates GDScript's String.validate_filename(): characters not allowed
    /// in file names are replaced with underscores.
    /// </summary>
    private static string ValidateFilename(string name)
    {
        var invalid = new[] { ':', '/', '\\', '?', '*', '"', '|', '%', '<', '>' };
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(System.Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    /// <summary>
    /// Unwrap the (possibly multi-)wrapped {"result": ...} envelope returned by
    /// the game IPC channel. The game writes its own {"result": ...} envelope and
    /// the transport wraps it again, so consumers must unwrap defensively.
    /// </summary>
    public static Dictionary UnwrapGameResult(Dictionary result)
    {
        Variant payload = result;
        while (payload.VariantType == Variant.Type.Dictionary
               && payload.AsGodotDictionary().TryGetValue("result", out var inner)
               && inner.VariantType == Variant.Type.Dictionary)
            payload = inner;
        return payload.VariantType == Variant.Type.Dictionary
            ? payload.AsGodotDictionary()
            : new Dictionary();
    }

    /// <summary>Awaitable frame-safe delay driven by the scene tree (keeps main-thread semantics).</summary>
    public async Task DelaySeconds(double seconds)
    {
        var timer = GetTree().CreateTimer(seconds);
        await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
    }

    /// <summary>Send a command to the running game via file IPC and await its response.</summary>
    public async Task<Dictionary> SendGameCommand(string command, Dictionary? @params = null, double timeoutSec = 5.0)
    {
        var ei = GetEditor();
        if (!ei.IsPlayingScene())
            return Error(CodeGeneric, "No scene is currently playing",
                new Dictionary { ["suggestion"] = "Use play_scene first" });

        var userDir = GetGameUserDir();
        var requestPath = userDir + "/mcp_game_request";
        var responsePath = userDir + "/mcp_game_response";

        // Clean stale response
        if (Godot.FileAccess.FileExists(responsePath))
            DirAccess.RemoveAbsolute(responsePath);

        // Write request
        var requestData = Json.Stringify(new Dictionary
        {
            ["command"] = command,
            ["params"] = @params ?? new Dictionary(),
        });
        using (var req = Godot.FileAccess.Open(requestPath, Godot.FileAccess.ModeFlags.Write))
        {
            if (req == null)
                return ErrorInternal("Could not create game request file");
            req.StoreString(requestData);
        }

        // Poll for response
        var attempts = (int)(timeoutSec / 0.1);
        while (attempts > 0)
        {
            await DelaySeconds(0.1);
            if (Godot.FileAccess.FileExists(responsePath))
                break;
            if (!ei.IsPlayingScene())
            {
                if (Godot.FileAccess.FileExists(requestPath))
                    DirAccess.RemoveAbsolute(requestPath);
                return Error(CodeGeneric, "Game stopped during command execution");
            }
            attempts--;
        }

        if (!Godot.FileAccess.FileExists(responsePath))
        {
            // Try to auto-resume the debugger (runtime error may have paused the game)
            if (ei.IsPlayingScene())
            {
                TryDebuggerContinue();
                for (var retry = 0; retry < 20; retry++)
                {
                    await DelaySeconds(0.1);
                    if (Godot.FileAccess.FileExists(responsePath))
                        break;
                }
            }
        }

        if (!Godot.FileAccess.FileExists(responsePath))
        {
            if (Godot.FileAccess.FileExists(requestPath))
                DirAccess.RemoveAbsolute(requestPath);
            return BuildTimeoutError(timeoutSec);
        }

        // Read response
        string text;
        using (var file = Godot.FileAccess.Open(responsePath, Godot.FileAccess.ModeFlags.Read))
        {
            if (file == null)
                return ErrorInternal("Could not read game response file");
            text = file.GetAsText();
        }
        DirAccess.RemoveAbsolute(responsePath);

        var parsed = Json.ParseString(text);
        if (parsed.VariantType != Variant.Type.Dictionary)
            return ErrorInternal("Invalid response JSON from game");
        var parsedDict = parsed.AsGodotDictionary();

        if (parsedDict.ContainsKey("error"))
            return Error(CodeGeneric, parsedDict["error"].ToString());

        return Success(parsedDict);
    }

    // ── Game-command timeout diagnostics ──────────────────────────────────────
    // Never tell the agent "the game isn't running / autoload missing" when the
    // game IS running and merely paused by a runtime error.

    /// <summary>Locate the editor's ScriptEditorDebugger node (BFS from base control).</summary>
    protected static Node? FindScriptEditorDebugger()
    {
        var baseControl = EditorInterface.Singleton.GetBaseControl();
        if (baseControl == null)
            return null;
        var queue = new Queue<Node>();
        queue.Enqueue(baseControl);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.GetClass() == "ScriptEditorDebugger")
                return node;
            foreach (var child in node.GetChildren())
                queue.Enqueue(child);
        }
        return null;
    }

    /// <summary>Look up an editor theme icon by name (locale-independent), or null.</summary>
    protected static Texture2D? GetEditorIcon(string iconName)
    {
        var baseControl = EditorInterface.Singleton.GetBaseControl();
        if (baseControl != null && baseControl.HasThemeIcon(iconName, "EditorIcons"))
            return baseControl.GetThemeIcon(iconName, "EditorIcons");
        return null;
    }

    /// <summary>
    /// Find the debugger "Continue" button without relying on UI text.
    /// The editor is translated, so matching tooltip/label text breaks for
    /// non-English editors. Match by the editor theme icon "DebugContinue"
    /// first, falling back to the English tooltip only if the icon can't be resolved.
    /// </summary>
    protected static Button? FindDebuggerContinueButton()
    {
        var dbg = FindScriptEditorDebugger();
        if (dbg == null)
            return null;
        var continueIcon = GetEditorIcon("DebugContinue");
        Button? fallback = null;
        var inner = new Queue<Node>();
        inner.Enqueue(dbg);
        while (inner.Count > 0)
        {
            var n = inner.Dequeue();
            if (n is Button b)
            {
                if (continueIcon != null && b.Icon == continueIcon)
                    return b;
                if (b.TooltipText == "Continue")
                    fallback = b;
            }
            foreach (var c in n.GetChildren())
                inner.Enqueue(c);
        }
        return fallback;
    }

    /// <summary>True when the running game is halted at a breakpoint or runtime error.</summary>
    public static bool IsDebuggerPaused()
    {
        var btn = FindDebuggerContinueButton();
        return btn != null && !btn.Disabled;
    }

    /// <summary>
    /// Read recent runtime errors from the debugger's "Errors" tab tree, so a
    /// timeout caused by a script error can report the actual cause inline.
    /// </summary>
    public static Godot.Collections.Array CollectDebuggerErrors(int maxErrors = 10)
    {
        var @out = new Godot.Collections.Array();
        var dbg = FindScriptEditorDebugger();
        if (dbg == null)
            return @out;
        foreach (var child in dbg.GetChildren())
        {
            if (child is not TabContainer tabContainer)
                continue;
            for (var tabIdx = 0; tabIdx < tabContainer.GetTabCount(); tabIdx++)
            {
                var tabControl = tabContainer.GetTabControl(tabIdx);
                if (tabControl is VBoxContainer && tabControl.Name.ToString().StartsWith("Errors"))
                {
                    foreach (var vchild in tabControl.GetChildren())
                    {
                        if (vchild is not Tree tree)
                            continue;
                        var rootItem = tree.GetRoot();
                        if (rootItem == null)
                            continue;
                        var item = rootItem.GetFirstChild();
                        while (item != null && @out.Count < maxErrors)
                        {
                            var col0 = item.GetText(0).StripEdges();
                            var col1 = item.GetText(1).StripEdges();
                            var msg = col0;
                            if (!string.IsNullOrEmpty(col1))
                                msg = string.IsNullOrEmpty(msg) ? col1 : msg + " " + col1;
                            if (!string.IsNullOrEmpty(msg))
                                @out.Add(msg);
                            item = item.GetNext();
                        }
                    }
                    break;
                }
            }
            break;
        }
        return @out;
    }

    /// <summary>Press the debugger "Continue" button to resume a paused game process.</summary>
    public static void TryDebuggerContinue()
    {
        var btn = FindDebuggerContinueButton();
        if (btn != null && !btn.Disabled)
        {
            btn.EmitSignal(BaseButton.SignalName.Pressed);
            GD.PushWarning("[MCP] Auto-resumed debugger after runtime error");
        }
    }

    /// <summary>
    /// Build an accurate error for a file-IPC game-command timeout.
    /// Distinguishes "game not running" from "game running but unresponsive
    /// (likely paused by a runtime error / breakpoint)".
    /// </summary>
    public static Dictionary BuildTimeoutError(double timeoutSec)
    {
        if (!EditorInterface.Singleton.IsPlayingScene())
        {
            return Error(CodeGeneric,
                $"Game command timed out after {timeoutSec:0.0}s and the game process is no longer running.",
                new Dictionary
                {
                    ["game_running"] = false,
                    ["suggestion"] = "The scene stopped. Call play_scene to start it again before sending runtime commands.",
                });
        }

        var paused = IsDebuggerPaused();
        var runtimeErrors = CollectDebuggerErrors(10);
        var data = new Dictionary
        {
            ["game_running"] = true,
            ["debugger_paused"] = paused,
        };
        if (runtimeErrors.Count > 0)
            data["runtime_errors"] = runtimeErrors;

        string msg;
        if (paused || runtimeErrors.Count > 0)
        {
            msg = $"Game command timed out after {timeoutSec:0.0}s, but the game IS running. "
                + "A runtime/script error paused the scene, so it could not respond to the command.";
            data["suggestion"] = "This is NOT a connection or autoload problem. Fix the error in 'runtime_errors' "
                + "(or call get_editor_errors for the full list), then retry. The debugger was auto-resumed; "
                + "if errors persist, call stop_scene then play_scene to restart cleanly.";
        }
        else
        {
            msg = $"Game command timed out after {timeoutSec:0.0}s. The game is running but did not respond in time.";
            data["suggestion"] = "The MCP server connection is fine and the game is running. The command may be slow "
                + "or the game may be busy/blocked. Retry with a longer timeout, and call get_editor_errors to check "
                + "for runtime errors. In rare cases (custom projects) verify the MCPGameInspector autoload is active.";
        }
        return Error(CodeGeneric, msg, data);
    }
}
