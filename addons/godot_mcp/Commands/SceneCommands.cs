using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// Faithful port of addons/godot_mcp/commands/scene_commands.gd.
/// Scene lifecycle tools: inspect the edited scene tree, read/create/open/
/// delete/save .tscn files, instance sub-scenes, play/stop the game, and
/// list exported script variables of a scene.
/// </summary>
[Tool]
public partial class SceneCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "get_scene_tree",
            Description = "Get the node hierarchy of the currently edited scene as a nested tree " +
                "(name, type, scene-relative path, attached script).",
            Category = "scene",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["max_depth"] = Schema.Int("Maximum tree depth to descend (-1 for unlimited).", -1),
            }),
            Handler = p => Task.FromResult(GetSceneTree(p)),
        },
        new()
        {
            Name = "get_scene_file_content",
            Description = "Read the raw text content of a scene file (.tscn).",
            Category = "scene",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the scene file to read."),
            }, "path"),
            Handler = p => Task.FromResult(GetSceneFileContent(p)),
        },
        new()
        {
            Name = "create_scene",
            Description = "Create a new .tscn scene file with a single root node of the given type.",
            Category = "scene",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path where the new scene file will be saved."),
                ["root_type"] = Schema.Str("Node class name for the scene root (e.g. 'Node2D', 'Node3D', 'Control').", "Node2D"),
                ["root_name"] = Schema.Str("Name for the root node. Defaults to the file basename.", ""),
            }, "path"),
            Handler = p => Task.FromResult(CreateScene(p)),
        },
        new()
        {
            Name = "open_scene",
            Description = "Open a scene file in the editor, making it the edited scene.",
            Category = "scene",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the scene file to open."),
            }, "path"),
            Handler = p => Task.FromResult(OpenScene(p)),
        },
        new()
        {
            Name = "delete_scene",
            Description = "Delete a scene file from the project (including its .import file if present).",
            Category = "scene",
            Destructive = true,
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the scene file to delete."),
            }, "path"),
            Handler = p => Task.FromResult(DeleteScene(p)),
        },
        new()
        {
            Name = "add_scene_instance",
            Description = "Instance another scene file as a child node in the currently edited scene (with undo support).",
            Category = "scene",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["scene_path"] = Schema.Str("res:// path of the scene file to instance."),
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node.", "."),
                ["name"] = Schema.Str("Name for the new instance node (optional)."),
            }, "scene_path"),
            Handler = p => Task.FromResult(AddSceneInstance(p)),
        },
        new()
        {
            Name = "play_scene",
            Description = "Run the project from the editor: the main scene, the currently edited scene, or a specific scene file.",
            Category = "scene",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["mode"] = Schema.Str("'main' to play the main scene, 'current' to play the edited scene, or a res:// scene path to play.", "main"),
            }),
            Handler = p => Task.FromResult(PlayScene(p)),
        },
        new()
        {
            Name = "stop_scene",
            Description = "Stop the currently running game started from the editor and clean up MCP IPC temp files.",
            Category = "scene",
            InputSchema = Schema.Object(),
            Handler = p => Task.FromResult(StopScene(p)),
        },
        new()
        {
            Name = "save_scene",
            Description = "Save the currently edited scene, optionally to a new path (Save As).",
            Category = "scene",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Optional res:// path to save to. Defaults to the scene's existing path."),
            }),
            Handler = p => Task.FromResult(SaveScene(p)),
        },
        new()
        {
            Name = "get_scene_exports",
            Description = "List all exported script variables (@export) of every scripted node in a scene file, with current values.",
            Category = "scene",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the scene file to inspect."),
            }, "path"),
            Handler = p => Task.FromResult(GetSceneExports(p)),
        },
    };

    // ── get_scene_tree ──────────────────────────────────────────────────────────

    private static Dictionary GetSceneTree(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        var maxDepth = OptionalInt(@params, "max_depth", -1);
        var tree = NodeUtils.GetNodeTree(root, maxDepth: maxDepth);
        return Success(new Dictionary { ["scene_path"] = root.SceneFilePath, ["tree"] = tree });
    }

    // ── get_scene_file_content ──────────────────────────────────────────────────

    private static Dictionary GetSceneFileContent(Dictionary @params)
    {
        var (path, requireErr) = RequireString(@params, "path");
        if (requireErr != null)
            return requireErr;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Scene file '{path}'");

        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
            return ErrorInternal($"Cannot read file: {Godot.FileAccess.GetOpenError()}");

        var content = file.GetAsText();
        return Success(new Dictionary { ["path"] = path, ["content"] = content, ["size"] = content.Length });
    }

    // ── create_scene ────────────────────────────────────────────────────────────

    private static Dictionary CreateScene(Dictionary @params)
    {
        var (path, requireErr) = RequireString(@params, "path");
        if (requireErr != null)
            return requireErr;

        var guard = GuardOfflineSceneSave(path);
        if (guard.Count > 0)
            return guard;

        var rootType = OptionalString(@params, "root_type", "Node2D");
        var rootName = OptionalString(@params, "root_name", "");

        // Validate root type exists
        if (!ClassDB.ClassExists(rootType))
            return ErrorInvalidParams($"Unknown node type: {rootType}");

        // Create the scene
        if (ClassDB.Instantiate(rootType).AsGodotObject() is not Node root)
            return ErrorInvalidParams($"Unknown node type: {rootType}");
        if (string.IsNullOrEmpty(rootName))
            rootName = path.GetFile().GetBaseName();
        root.Name = rootName;

        var scene = new PackedScene();
        var err = scene.Pack(root);
        root.QueueFree();

        if (err != Godot.Error.Ok)
            return ErrorInternal($"Failed to pack scene: {err}");

        // Ensure directory exists
        var dirPath = path.GetBaseDir();
        if (!DirAccess.DirExistsAbsolute(dirPath))
            DirAccess.MakeDirRecursiveAbsolute(dirPath);

        err = ResourceSaver.Save(scene, path);
        if (err != Godot.Error.Ok)
            return ErrorInternal($"Failed to save scene: {err}");

        // Refresh filesystem
        GetEditor().GetResourceFilesystem().Scan();

        return Success(new Dictionary { ["path"] = path, ["root_type"] = rootType, ["root_name"] = rootName });
    }

    // ── open_scene ──────────────────────────────────────────────────────────────

    private static Dictionary OpenScene(Dictionary @params)
    {
        var (path, requireErr) = RequireString(@params, "path");
        if (requireErr != null)
            return requireErr;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Scene file '{path}'");

        GetEditor().OpenSceneFromPath(path);
        return Success(new Dictionary { ["path"] = path, ["opened"] = true });
    }

    // ── delete_scene ────────────────────────────────────────────────────────────

    private static Dictionary DeleteScene(Dictionary @params)
    {
        var (path, requireErr) = RequireString(@params, "path");
        if (requireErr != null)
            return requireErr;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Scene file '{path}'");

        var err = DirAccess.RemoveAbsolute(path);
        if (err != Godot.Error.Ok)
            return ErrorInternal($"Failed to delete scene: {err}");

        // Also remove .import file if exists
        var importPath = path + ".import";
        if (Godot.FileAccess.FileExists(importPath))
            DirAccess.RemoveAbsolute(importPath);

        GetEditor().GetResourceFilesystem().Scan();
        return Success(new Dictionary { ["path"] = path, ["deleted"] = true });
    }

    // ── add_scene_instance ──────────────────────────────────────────────────────

    private Dictionary AddSceneInstance(Dictionary @params)
    {
        var (scenePath, requireErr) = RequireString(@params, "scene_path");
        if (requireErr != null)
            return requireErr;

        var parentPath = OptionalString(@params, "parent_path", ".");
        var instanceName = OptionalString(@params, "name", "");

        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        if (!Godot.FileAccess.FileExists(scenePath))
            return ErrorNotFound($"Scene file '{scenePath}'");

        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return ErrorNotFound($"Parent node '{parentPath}'", "Use get_scene_tree to see available nodes");

        var packed = ResourceLoader.Load(scenePath) as PackedScene;
        if (packed == null)
            return ErrorInternal($"Failed to load scene: {scenePath}");

        var instance = packed.Instantiate();
        if (!string.IsNullOrEmpty(instanceName))
            instance.Name = instanceName;

        AddChildWithUndo(parent, instance, root, "MCP: Add scene instance");

        NodeUtils.SetOwnerRecursive(instance, root);

        return Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(instance).ToString(),
            ["scene_path"] = scenePath,
            ["name"] = instance.Name.ToString(),
        });
    }

    // ── play_scene ──────────────────────────────────────────────────────────────

    private static Dictionary PlayScene(Dictionary @params)
    {
        var mode = OptionalString(@params, "mode", "main"); // "main", "current", or path

        switch (mode)
        {
            case "main":
                GetEditor().PlayMainScene();
                break;
            case "current":
                GetEditor().PlayCurrentScene();
                break;
            default:
                // Treat as scene path
                if (!Godot.FileAccess.FileExists(mode))
                    return ErrorNotFound($"Scene file '{mode}'");
                GetEditor().PlayCustomScene(mode);
                break;
        }

        return Success(new Dictionary { ["playing"] = true, ["mode"] = mode });
    }

    // ── stop_scene ──────────────────────────────────────────────────────────────

    private static Dictionary StopScene(Dictionary @params)
    {
        if (!GetEditor().IsPlayingScene())
            return Success(new Dictionary { ["stopped"] = false, ["message"] = "No scene is currently playing" });

        GetEditor().StopPlayingScene();

        // Clean up temp files
        CleanupScreenshotFiles();
        CleanupInputFiles();
        CleanupInspectorFiles();

        return Success(new Dictionary { ["stopped"] = true });
    }

    // ── save_scene ──────────────────────────────────────────────────────────────

    private static Dictionary SaveScene(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        var path = OptionalString(@params, "path", "");
        if (string.IsNullOrEmpty(path))
            path = root.SceneFilePath;

        if (string.IsNullOrEmpty(path))
            return ErrorInvalidParams("No save path specified and scene has no existing path");

        var normalizedPath = NormalizeProjectPath(path);
        if (IsScenePathOpen(normalizedPath) && !IsActiveScenePath(normalizedPath))
        {
            var openScenes = new Godot.Collections.Array();
            foreach (var p in GetOpenScenePaths()) openScenes.Add(p);
            return ErrorConflict(
                $"Refusing to save inactive open scene '{normalizedPath}' from the active editor scene",
                new Dictionary
                {
                    ["path"] = normalizedPath,
                    ["active_scene"] = NormalizeProjectPath(root.SceneFilePath),
                    ["open_scenes"] = openScenes,
                    ["suggestion"] = "Open the target scene tab before saving it, or close it before offline edits.",
                });
        }

        var dirPath = normalizedPath.GetBaseDir();
        if (!DirAccess.DirExistsAbsolute(dirPath))
            DirAccess.MakeDirRecursiveAbsolute(dirPath);

        Godot.Error err;
        string saveMethod;
        if (string.IsNullOrEmpty(root.SceneFilePath) || NormalizeProjectPath(root.SceneFilePath) != normalizedPath)
        {
            GetEditor().SaveSceneAs(normalizedPath);
            err = Godot.Error.Ok;
            saveMethod = "EditorInterface.save_scene_as";
        }
        else
        {
            err = GetEditor().SaveScene();
            saveMethod = "EditorInterface.save_scene";
        }
        if (err != Godot.Error.Ok)
            return ErrorInternal($"Failed to save scene via {saveMethod}: {err}");

        return Success(new Dictionary { ["path"] = normalizedPath, ["saved"] = true, ["method"] = saveMethod });
    }

    // ── get_scene_exports ───────────────────────────────────────────────────────

    private static Dictionary GetSceneExports(Dictionary @params)
    {
        var (path, requireErr) = RequireString(@params, "path");
        if (requireErr != null)
            return requireErr;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Scene file '{path}'");

        var packed = ResourceLoader.Load(path) as PackedScene;
        if (packed == null)
            return ErrorInternal($"Failed to load scene: {path}");

        var instance = packed.Instantiate();
        if (instance == null)
            return ErrorInternal($"Failed to instantiate scene: {path}");

        var nodesData = new Godot.Collections.Array();
        CollectExportsRecursive(instance, instance, nodesData);

        instance.QueueFree();

        return Success(new Dictionary
        {
            ["path"] = path,
            ["nodes"] = nodesData,
            ["count"] = nodesData.Count,
        });
    }

    private static void CollectExportsRecursive(Node node, Node root, Godot.Collections.Array nodesData)
    {
        if (node.GetScript().As<Script>() is { } script)
        {
            var exports = new Dictionary();
            foreach (Dictionary propInfo in script.GetScriptPropertyList())
            {
                var usage = propInfo["usage"].AsInt64();
                if ((usage & (long)PropertyUsageFlags.Editor) != 0
                    && (usage & (long)PropertyUsageFlags.ScriptVariable) != 0)
                {
                    var propName = propInfo["name"].AsString();
                    exports[propName] = new Dictionary
                    {
                        ["value"] = PropertyParser.SerializeValue(node.Get(propName)),
                        ["type"] = propInfo["type"],
                        ["hint"] = propInfo.TryGetValue("hint", out var hint) ? hint : 0,
                        ["hint_string"] = propInfo.TryGetValue("hint_string", out var hintString) ? hintString : "",
                    };
                }
            }
            if (exports.Count > 0)
            {
                var nodePath = node == root ? "." : root.GetPathTo(node).ToString();
                nodesData.Add(new Dictionary
                {
                    ["node_path"] = nodePath,
                    ["node_name"] = node.Name.ToString(),
                    ["node_type"] = node.GetClass(),
                    ["script_path"] = script.ResourcePath,
                    ["exports"] = exports,
                });
            }
        }

        foreach (var child in node.GetChildren())
            CollectExportsRecursive(child, root, nodesData);
    }

    // ── temp-file cleanup (used by stop_scene) ─────────────────────────────────

    private static void CleanupScreenshotFiles()
    {
        var userDir = GetGameUserDir();
        var requestPath = userDir + "/mcp_screenshot_request";
        var screenshotPath = userDir + "/mcp_screenshot.png";
        if (Godot.FileAccess.FileExists(requestPath))
            DirAccess.RemoveAbsolute(requestPath);
        if (Godot.FileAccess.FileExists(screenshotPath))
            DirAccess.RemoveAbsolute(screenshotPath);
    }

    private static void CleanupInputFiles()
    {
        var userDir = GetGameUserDir();
        var commandsPath = userDir + "/mcp_input_commands";
        if (Godot.FileAccess.FileExists(commandsPath))
            DirAccess.RemoveAbsolute(commandsPath);
    }

    private static void CleanupInspectorFiles()
    {
        var userDir = GetGameUserDir();
        var requestPath = userDir + "/mcp_game_request";
        var responsePath = userDir + "/mcp_game_response";
        if (Godot.FileAccess.FileExists(requestPath))
            DirAccess.RemoveAbsolute(requestPath);
        if (Godot.FileAccess.FileExists(responsePath))
            DirAccess.RemoveAbsolute(responsePath);
    }
}
