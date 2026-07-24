using System.Linq;
using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// Extended editor tools: undo/redo, save all, breakpoints, editor settings, addon management.
/// </summary>
[Tool]
public partial class EditorExtensionCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools()
    {
        return new List<ToolDefinition>
        {
            new()
            {
                Name = "editor_undo",
                Description = "Undo the last editor action (equivalent to Ctrl+Z).",
                Category = "editor",
                InputSchema = Schema.Object(),
                Handler = p => Task.FromResult(EditorUndo(p)),
            },
            new()
            {
                Name = "editor_redo",
                Description = "Redo the last undone editor action (equivalent to Ctrl+Shift+Z).",
                Category = "editor",
                InputSchema = Schema.Object(),
                Handler = p => Task.FromResult(EditorRedo(p)),
            },
            new()
            {
                Name = "editor_save_all",
                Description = "Save all open scenes in the editor.",
                Category = "editor",
                InputSchema = Schema.Object(),
                Handler = p => Task.FromResult(EditorSaveAll(p)),
            },
            new()
            {
                Name = "set_breakpoint",
                Description = "Set or remove a breakpoint at a line in a script.",
                Category = "editor",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["script_path"] = Schema.Str("res:// path to the script file."),
                    ["line"] = Schema.Int("Line number (1-based)."),
                    ["enabled"] = Schema.Bool("true to set, false to remove.", true),
                }, "script_path", "line"),
                Handler = p => Task.FromResult(SetBreakpoint(p)),
            },
            new()
            {
                Name = "get_breakpoints",
                Description = "List all active breakpoints across the project.",
                Category = "editor",
                InputSchema = Schema.Object(),
                Handler = p => Task.FromResult(GetBreakpoints(p)),
            },
            new()
            {
                Name = "clear_breakpoints",
                Description = "Remove all breakpoints. Destructive.",
                Category = "editor",
                Destructive = true,
                InputSchema = Schema.Object(),
                Handler = p => Task.FromResult(ClearBreakpoints(p)),
            },
            new()
            {
                Name = "get_editor_setting",
                Description = "Read a single editor preference setting (e.g. 'editors/3d/grid_size', 'text_editor/theme/color_theme').",
                Category = "editor",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["key"] = Schema.Str("Setting key, e.g. 'editors/3d/fov'."),
                }, "key"),
                Handler = p => Task.FromResult(GetEditorSetting(p)),
            },
            new()
            {
                Name = "set_editor_setting",
                Description = "Write an editor preference setting and persist it.",
                Category = "editor",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["key"] = Schema.Str("Setting key."),
                    ["value"] = Schema.Any("Setting value (string, int, float, bool, or color string)."),
                }, "key", "value"),
                Handler = p => Task.FromResult(SetEditorSetting(p)),
            },
            new()
            {
                Name = "list_addons",
                Description = "List all installed editor plugins (addons) with their enabled/disabled status.",
                Category = "project",
                InputSchema = Schema.Object(),
                Handler = p => Task.FromResult(ListAddons(p)),
            },
            new()
            {
                Name = "set_addon_enabled",
                Description = "Enable or disable an editor plugin (addon). The change is persisted to project.godot.",
                Category = "project",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["plugin_path"] = Schema.Str("Addon identifier, e.g. 'godot_mcp' (the addon directory name under addons/)."),
                    ["enabled"] = Schema.Bool("true to enable, false to disable."),
                }, "plugin_path", "enabled"),
                Handler = p => Task.FromResult(SetAddonEnabled(p)),
            },
        };
    }

    // ── Undo / Redo ───────────────────────────────────────────────────────────

    private static Dictionary EditorUndo(Dictionary _)
    {
        var undoRedo = EditorInterface.Singleton.GetEditorUndoRedo();
        if (undoRedo == null)
            return Error(CodeInternal, "Editor UndoRedo manager is not available.");
        var hasUndo = (bool)undoRedo.Call("has_undo");
        if (!hasUndo)
            return Success(new Dictionary { ["undone"] = false, ["message"] = "Nothing to undo." });
        undoRedo.Call("undo");
        return Success(new Dictionary { ["undone"] = true });
    }

    private static Dictionary EditorRedo(Dictionary _)
    {
        var undoRedo = EditorInterface.Singleton.GetEditorUndoRedo();
        if (undoRedo == null)
            return Error(CodeInternal, "Editor UndoRedo manager is not available.");
        var hasRedo = (bool)undoRedo.Call("has_redo");
        if (!hasRedo)
            return Success(new Dictionary { ["redone"] = false, ["message"] = "Nothing to redo." });
        undoRedo.Call("redo");
        return Success(new Dictionary { ["redone"] = true });
    }

    // ── Save All ──────────────────────────────────────────────────────────────

    private static Dictionary EditorSaveAll(Dictionary _)
    {
        var ei = EditorInterface.Singleton;
        var openScenes = ei.GetOpenScenes();
        var saved = new Godot.Collections.Array();

        foreach (string scenePath in ei.GetOpenScenes())
        {
            if (string.IsNullOrEmpty(scenePath))
                continue;
            ei.OpenSceneFromPath(scenePath);
            var err = ei.SaveScene();
            if (err == Godot.Error.Ok)
                saved.Add(scenePath);
        }

        return Success(new Dictionary
        {
            ["saved"] = saved,
            ["count"] = saved.Count,
        });
    }

    // ── Breakpoints ───────────────────────────────────────────────────────────

    private static Node? FindDebugger()
    {
        var baseControl = EditorInterface.Singleton.GetBaseControl();
        if (baseControl == null) return null;
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

    private Dictionary SetBreakpoint(Dictionary @params)
    {
        var (scriptPath, e1) = RequireString(@params, "script_path");
        if (e1 != null) return e1;
        var line = OptionalInt(@params, "line", 0);
        if (line <= 0)
            return ErrorInvalidParams("line must be a positive integer");
        var enabled = OptionalBool(@params, "enabled", true);

        var dbg = FindDebugger();
        if (dbg == null)
            return ErrorInternal("Script editor debugger not found. Open a script in the editor first.");

        var normalizedPath = NormalizeProjectPath(scriptPath);
        dbg.Call("set_breakpoint", normalizedPath, line, enabled);

        return Success(new Dictionary
        {
            ["script_path"] = normalizedPath,
            ["line"] = line,
            ["enabled"] = enabled,
        });
    }

    private Dictionary GetBreakpoints(Dictionary _)
    {
        var dbg = FindDebugger();
        if (dbg == null)
            return Success(new Dictionary { ["breakpoints"] = new Godot.Collections.Array(), ["count"] = 0 });

        var rawBreakpoints = dbg.Call("get_breakpoints");
        var breakpoints = new Godot.Collections.Array();

        if (rawBreakpoints.VariantType == Variant.Type.Dictionary)
        {
            var bpDict = rawBreakpoints.AsGodotDictionary();
            foreach (var key in bpDict.Keys)
            {
                var path = key.AsString();
                var linesVar = bpDict[key];
                if (linesVar.VariantType == Variant.Type.Array)
                {
                    foreach (var lineVar in linesVar.AsGodotArray())
                        breakpoints.Add(new Dictionary { ["script_path"] = path, ["line"] = lineVar.AsInt32() });
                }
            }
        }

        return Success(new Dictionary
        {
            ["breakpoints"] = breakpoints,
            ["count"] = breakpoints.Count,
        });
    }

    private Dictionary ClearBreakpoints(Dictionary _)
    {
        var dbg = FindDebugger();
        if (dbg == null)
            return Success(new Dictionary { ["cleared"] = true, ["count"] = 0 });

        var rawBreakpoints = dbg.Call("get_breakpoints");
        var count = 0;

        if (rawBreakpoints.VariantType == Variant.Type.Dictionary)
        {
            var bpDict = rawBreakpoints.AsGodotDictionary();
            foreach (var key in bpDict.Keys)
            {
                var path = key.AsString();
                var linesVar = bpDict[key];
                if (linesVar.VariantType == Variant.Type.Array)
                {
                    foreach (var lineVar in linesVar.AsGodotArray())
                    {
                        dbg.Call("set_breakpoint", path, lineVar.AsInt32(), false);
                        count++;
                    }
                }
            }
        }

        return Success(new Dictionary { ["cleared"] = true, ["count"] = count });
    }

    // ── Editor Settings ───────────────────────────────────────────────────────

    private static Dictionary GetEditorSetting(Dictionary @params)
    {
        var (key, err) = RequireString(@params, "key");
        if (err != null) return err;

        var settings = EditorInterface.Singleton.GetEditorSettings();
        if (settings == null)
            return ErrorInternal("Editor settings not available.");

        if (!settings.HasSetting(key))
            return ErrorNotFound($"Editor setting '{key}'",
                "Use get_editor_setting with a valid key. Browse available keys via list all with key prefix.");

        var value = settings.GetSetting(key);
        return Success(new Dictionary
        {
            ["key"] = key,
            ["value"] = PropertyParser.SerializeValue(value),
            ["type"] = value.VariantType.ToString(),
        });
    }

    private static Dictionary SetEditorSetting(Dictionary @params)
    {
        var (key, e1) = RequireString(@params, "key");
        if (e1 != null) return e1;
        if (!@params.ContainsKey("value"))
            return ErrorInvalidParams("Missing required parameter: value");

        var settings = EditorInterface.Singleton.GetEditorSettings();
        if (settings == null)
            return ErrorInternal("Editor settings not available.");

        var existingValue = settings.HasSetting(key) ? settings.GetSetting(key) : default;
        var newValue = PropertyParser.ParseValue(@params["value"], existingValue.VariantType);

        settings.SetSetting(key, newValue);
        settings.Call("save");

        return Success(new Dictionary
        {
            ["key"] = key,
            ["saved"] = true,
        });
    }

    // ── Addon Management ──────────────────────────────────────────────────────

    private static Dictionary ListAddons(Dictionary _)
    {
        var addons = new Godot.Collections.Array();

        var enabledPlugins = ProjectSettings.GetSetting("editor_plugins/enabled", new Godot.Collections.Array())
            .AsGodotArray();
        var enabledSet = new System.Collections.Generic.HashSet<string>();
        foreach (var p in enabledPlugins)
            enabledSet.Add(p.AsString());

        var addonsDir = "res://addons";
        if (!DirAccess.DirExistsAbsolute(addonsDir))
            return Success(new Dictionary { ["addons"] = addons, ["count"] = 0 });

        using var dir = DirAccess.Open(addonsDir);
        if (dir == null)
            return Success(new Dictionary { ["addons"] = addons, ["count"] = 0 });

        dir.ListDirBegin();
        var itemName = dir.GetNext();
        while (!string.IsNullOrEmpty(itemName))
        {
            if (dir.CurrentIsDir() && !itemName.StartsWith("."))
            {
                var cfgPath = $"{addonsDir}/{itemName}/plugin.cfg";
                if (Godot.FileAccess.FileExists(cfgPath))
                {
                    var cfg = new ConfigFile();
                    var info = new Dictionary { ["name"] = itemName };
                    if (cfg.Load(cfgPath) == Godot.Error.Ok && cfg.HasSection("plugin"))
                    {
                        info["display_name"] = cfg.GetValue("plugin", "name", itemName).AsString();
                        info["description"] = cfg.GetValue("plugin", "description", "").AsString();
                        info["author"] = cfg.GetValue("plugin", "author", "").AsString();
                        info["version"] = cfg.GetValue("plugin", "version", "").AsString();
                    }
                    var resPath = $"res://addons/{itemName}/plugin.cfg";
                    info["enabled"] = enabledSet.Contains(resPath) || enabledSet.Contains(itemName);
                    addons.Add(info);
                }
            }
            itemName = dir.GetNext();
        }
        dir.ListDirEnd();

        return Success(new Dictionary
        {
            ["addons"] = addons,
            ["count"] = addons.Count,
        });
    }

    private static Dictionary SetAddonEnabled(Dictionary @params)
    {
        var (pluginName, e1) = RequireString(@params, "plugin_path");
        if (e1 != null) return e1;
        var enabled = OptionalBool(@params, "enabled", true);

        var cfgPath = $"res://addons/{pluginName}/plugin.cfg";
        if (!Godot.FileAccess.FileExists(cfgPath))
            return ErrorNotFound($"Addon '{pluginName}'",
                $"No plugin.cfg found at {cfgPath}. Check the addon directory name.");

        var enabledPlugins = ProjectSettings.GetSetting("editor_plugins/enabled", new Godot.Collections.Array())
            .AsGodotArray();
        var resPath = $"res://addons/{pluginName}/plugin.cfg";

        var currentlyEnabled = false;
        var updatedList = new Godot.Collections.Array();
        foreach (var p in enabledPlugins)
        {
            var pathStr = p.AsString();
            if (pathStr == resPath || pathStr == pluginName)
                currentlyEnabled = true;
            else
                updatedList.Add(pathStr);
        }

        if (enabled && !currentlyEnabled)
            updatedList.Add(resPath);
        else if (!enabled && !currentlyEnabled)
            return Success(new Dictionary
            {
                ["plugin_path"] = pluginName,
                ["enabled"] = false,
                ["message"] = "Addon was already disabled.",
            });

        ProjectSettings.SetSetting("editor_plugins/enabled", updatedList);
        ProjectSettings.Save();

        return Success(new Dictionary
        {
            ["plugin_path"] = pluginName,
            ["enabled"] = enabled,
            ["message"] = enabled
                ? "Addon enabled. Reload the project for changes to take effect."
                : "Addon disabled. Reload the project for changes to take effect.",
        });
    }
}
