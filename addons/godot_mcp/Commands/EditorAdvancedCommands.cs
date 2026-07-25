using System.Text.RegularExpressions;
using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Advanced editor configuration tools: Camera3D attribute setup (DOF/exposure),
/// collision/render layer naming in ProjectSettings, and a project-wide lint
/// pass for common authoring issues (missing script/resource references,
/// dangling ExtResource ids, empty scenes, orphan nodes).
/// </summary>
[Tool]
public partial class EditorAdvancedCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "setup_camera_attributes",
            Description = "Create or configure a CameraAttributesPractical resource on a Camera3D node (depth-of-field blur and exposure settings). Only parameters explicitly passed are changed. Undo-tracked.",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the Camera3D node in the edited scene."),
                ["dof_blur_far_enabled"] = Schema.Bool("Enable far depth-of-field blur."),
                ["dof_blur_far_distance"] = Schema.Num("Distance from the camera at which the far blur starts."),
                ["dof_blur_far_transition"] = Schema.Num("Length of the transition between the in-focus and far-blurred regions."),
                ["dof_blur_near_enabled"] = Schema.Bool("Enable near depth-of-field blur."),
                ["dof_blur_near_distance"] = Schema.Num("Distance from the camera at which the near blur starts."),
                ["dof_blur_near_transition"] = Schema.Num("Length of the transition between the in-focus and near-blurred regions."),
                ["exposure_multiplier"] = Schema.Num("Exposure multiplier (EV)."),
                ["exposure_sensitivity"] = Schema.Num("Exposure sensitivity (ISO)."),
                ["exposure_auto_exposure_enabled"] = Schema.Bool("Enable auto exposure."),
                ["exposure_auto_exposure_min"] = Schema.Num("Minimum auto-exposure sensitivity."),
                ["exposure_auto_exposure_max"] = Schema.Num("Maximum auto-exposure sensitivity."),
            }, "node_path"),
            Handler = p => Task.FromResult(SetupCameraAttributes(p)),
        },
        new()
        {
            Name = "set_collision_layer_names",
            Description = "Set human-readable names for physics/render layers (2d_physics, 3d_physics, 2d_render, 3d_render) in ProjectSettings and persist them to project.godot.",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["dimension"] = Schema.Enum("Layer dimension.", "2d", "3d"),
                ["type"] = Schema.Enum("Layer type.", "physics", "render"),
                ["names"] = Schema.Arr("Layer names: index 0 is layer 1, index 1 is layer 2, etc. Up to 20 entries (32 supported for physics). Empty strings clear the name."),
            }, "dimension", "type", "names"),
            Handler = p => Task.FromResult(SetCollisionLayerNames(p)),
        },
        new()
        {
            Name = "lint_project",
            Description = "Scan the project's .tscn scene files for common issues: missing script references, missing resources, dangling ExtResource ids, empty scenes, and orphan nodes. Returns issues[{severity, message, path}].",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["include_addons"] = Schema.Bool("Also scan files under res://addons/.", false),
            }),
            Handler = p => Task.FromResult(LintProject(p)),
        },
    };

    // ── setup_camera_attributes ───────────────────────────────────────────

    // (param name, godot property name, isBool)
    private static readonly (string Param, string Prop, bool IsBool)[] CameraAttrMap =
    {
        ("dof_blur_far_enabled", "dof_blur_far_enabled", true),
        ("dof_blur_far_distance", "dof_blur_far_distance", false),
        ("dof_blur_far_transition", "dof_blur_far_transition", false),
        ("dof_blur_near_enabled", "dof_blur_near_enabled", true),
        ("dof_blur_near_distance", "dof_blur_near_distance", false),
        ("dof_blur_near_transition", "dof_blur_near_transition", false),
        ("exposure_multiplier", "exposure_multiplier", false),
        ("exposure_sensitivity", "exposure_sensitivity", false),
        ("exposure_auto_exposure_enabled", "auto_exposure_enabled", true),
        ("exposure_auto_exposure_min", "auto_exposure_min_sensitivity", false),
        ("exposure_auto_exposure_max", "auto_exposure_max_sensitivity", false),
    };

    private Dictionary SetupCameraAttributes(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return err;

        var root = GetEditedRoot();
        if (root == null) return ErrorNoScene();

        var node = FindNodeByPath(nodePath);
        if (node is not Camera3D camera)
            return ErrorNotFound($"Camera3D '{nodePath}'", "Pass node_path of an existing Camera3D in the edited scene");

        var attrs = camera.Attributes as CameraAttributesPractical;
        var created = false;
        if (attrs == null)
        {
            attrs = new CameraAttributesPractical();
            created = true;
        }

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Setup Camera Attributes");

        if (created)
        {
            var oldAttrs = camera.Attributes;
            undoRedo.AddDoProperty(camera, "attributes", attrs);
            undoRedo.AddDoReference(attrs);
            undoRedo.AddUndoProperty(camera, "attributes", oldAttrs);
            if (oldAttrs != null)
                undoRedo.AddUndoReference(oldAttrs);
        }

        var applied = new Godot.Collections.Array();
        foreach (var (param, prop, isBool) in CameraAttrMap)
        {
            if (!@params.ContainsKey(param))
                continue;

            Variant newValue = isBool
                ? Variant.From(CoerceBool(@params[param]))
                : Variant.From((float)OptionalFloat(@params, param, 0.0));

            var oldValue = attrs.Get(prop);
            undoRedo.AddDoProperty(attrs, prop, newValue);
            undoRedo.AddUndoProperty(attrs, prop, oldValue);
            applied.Add(prop);
        }

        undoRedo.CommitAction();

        return Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(camera).ToString(),
            ["created"] = created,
            ["applied"] = applied,
        });
    }

    // ── set_collision_layer_names ─────────────────────────────────────────

    private static Dictionary SetCollisionLayerNames(Dictionary @params)
    {
        var (dimension, err) = RequireString(@params, "dimension");
        if (err != null) return err;
        if (dimension is not ("2d" or "3d"))
            return ErrorInvalidParams("dimension must be '2d' or '3d'");

        var (type, err2) = RequireString(@params, "type");
        if (err2 != null) return err2;
        if (type is not ("physics" or "render"))
            return ErrorInvalidParams("type must be 'physics' or 'render'");

        var names = OptionalArray(@params, "names");
        if (names == null)
            return ErrorInvalidParams("Missing required parameter: names");
        if (names.Count > 32)
            return ErrorInvalidParams("names supports at most 32 entries");

        var prefix = $"layer_names/{dimension}_{type}/layer_";
        var applied = new Godot.Collections.Array();
        for (var i = 0; i < names.Count; i++)
        {
            var key = prefix + (i + 1);
            var value = names[i].VariantType == Variant.Type.String ? names[i].AsString() : names[i].ToString();
            ProjectSettings.SetSetting(key, value);
            applied.Add(new Dictionary
            {
                ["layer"] = i + 1,
                ["name"] = value,
            });
        }

        var saveErr = ProjectSettings.Save();
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save project settings: {saveErr}");

        return Success(new Dictionary
        {
            ["dimension"] = dimension,
            ["type"] = type,
            ["prefix"] = prefix,
            ["applied"] = applied,
            ["saved"] = true,
        });
    }

    // ── lint_project ──────────────────────────────────────────────────────

    private static readonly Regex HeaderAttrRegex =
        new("(\\w+)\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);

    private static Dictionary LintProject(Dictionary @params)
    {
        var includeAddons = OptionalBool(@params, "include_addons", false);

        var sceneFiles = new List<string>();
        CollectSceneFiles("res://", includeAddons, sceneFiles);

        var issues = new Godot.Collections.Array();
        foreach (var path in sceneFiles)
            LintSceneFile(path, issues);

        return Success(new Dictionary
        {
            ["issues"] = issues,
            ["issue_count"] = issues.Count,
            ["scanned_scenes"] = sceneFiles.Count,
        });
    }

    private static void CollectSceneFiles(string dir, bool includeAddons, List<string> output)
    {
        using var d = DirAccess.Open(dir);
        if (d == null)
            return;
        d.ListDirBegin();
        var name = d.GetNext();
        while (!string.IsNullOrEmpty(name))
        {
            if (d.CurrentIsDir())
            {
                if (!name.StartsWith(".") && (includeAddons || name != "addons"))
                    CollectSceneFiles(dir.PathJoin(name), includeAddons, output);
            }
            else if (name.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(dir.PathJoin(name));
            }
            name = d.GetNext();
        }
        d.ListDirEnd();
    }

    private static void AddIssue(Godot.Collections.Array issues, string severity, string message, string path)
    {
        issues.Add(new Dictionary
        {
            ["severity"] = severity,
            ["message"] = message,
            ["path"] = path,
        });
    }

    private static System.Collections.Generic.Dictionary<string, string> ParseHeaderAttrs(string headerLine)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>();
        foreach (Match m in HeaderAttrRegex.Matches(headerLine))
            result[m.Groups[1].Value] = m.Groups[2].Value;
        return result;
    }

    private static void LintSceneFile(string path, Godot.Collections.Array issues)
    {
        string text;
        using (var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read))
        {
            if (f == null)
            {
                AddIssue(issues, "error", "Cannot open scene file", path);
                return;
            }
            text = f.GetAsText();
        }

        var extResources = new System.Collections.Generic.Dictionary<string, (string Type, string ResPath)>();
        var nodePaths = new HashSet<string>();
        var nodes = new List<(string Name, string Parent)>();
        var extResourceUsages = new List<string>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[ext_resource"))
            {
                var attrs = ParseHeaderAttrs(line);
                if (attrs.TryGetValue("id", out var id))
                {
                    attrs.TryGetValue("type", out var t);
                    attrs.TryGetValue("path", out var p);
                    extResources[id] = (t ?? "", p ?? "");
                }
            }
            else if (line.StartsWith("[node "))
            {
                var attrs = ParseHeaderAttrs(line);
                attrs.TryGetValue("name", out var nodeName);
                attrs.TryGetValue("parent", out var parent);
                nodeName ??= "";
                parent ??= "";
                if (string.IsNullOrEmpty(parent))
                {
                    // Root node of the scene
                    nodePaths.Add("");
                }
                else
                {
                    var full = parent == "." ? nodeName : parent + "/" + nodeName;
                    nodes.Add((nodeName, parent));
                    nodePaths.Add(full);
                }
            }
            else
            {
                // Property lines may reference ExtResource("id")
                var idx = 0;
                const string marker = "ExtResource(\"";
                while ((idx = line.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0)
                {
                    var start = idx + marker.Length;
                    var end = line.IndexOf('"', start);
                    if (end > start)
                    {
                        extResourceUsages.Add(line[start..end]);
                        idx = end + 1;
                    }
                    else
                    {
                        idx = start;
                    }
                }
            }
        }

        // Missing script/resource references
        foreach (var (id, res) in extResources)
        {
            if (string.IsNullOrEmpty(res.ResPath))
                continue;
            if (!Godot.FileAccess.FileExists(res.ResPath))
            {
                var what = res.Type == "Script" ? "Missing script reference" : "Missing resource";
                AddIssue(issues, "error",
                    $"{what}: '{res.ResPath}' (ext_resource id '{id}') does not exist", path);
            }
        }

        // Dangling ExtResource usages (referenced id never declared)
        foreach (var id in extResourceUsages)
        {
            if (!extResources.ContainsKey(id))
                AddIssue(issues, "error",
                    $"Dangling ExtResource reference '{id}' (no matching [ext_resource] declaration)", path);
        }

        // Empty scene: only a root node
        if (nodes.Count == 0)
            AddIssue(issues, "warning", "Scene is empty (contains only a root node)", path);

        // Orphan nodes: parent path does not resolve within the scene
        foreach (var (nodeName, parent) in nodes)
        {
            if (parent == ".")
                continue;
            if (!nodePaths.Contains(parent))
                AddIssue(issues, "warning",
                    $"Orphan node '{nodeName}': parent path '{parent}' does not exist in the scene", path);
        }
    }
}
