using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Faithful port of addons/godot_mcp/commands/shader_commands.gd.
/// Exposes shader file management (create/read/edit) and ShaderMaterial
/// assignment/parameter tools, including the v1.14.0 text-resource write guard.
/// </summary>
[Tool]
public partial class ShaderCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "create_shader",
            Description = "Create a new .gdshader file, optionally from a shader_type-specific default template.",
            Category = "shader",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Path for the new shader file (res://...)."),
                ["content"] = Schema.Str("Full shader source. When omitted, a default template for shader_type is generated."),
                ["shader_type"] = Schema.Enum("Shader type used for the generated template.", "spatial", "canvas_item", "particles", "sky"),
                ["force"] = Schema.Bool("Overwrite even if the target is open in the script editor.", false),
            }, "path"),
            Handler = p => Task.FromResult(CreateShader(p)),
        },
        new()
        {
            Name = "read_shader",
            Description = "Read the full text content of a shader file.",
            Category = "shader",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Path to the shader file (res://...)."),
            }, "path"),
            Handler = p => Task.FromResult(ReadShader(p)),
        },
        new()
        {
            Name = "edit_shader",
            Description = "Edit an existing shader via full-content overwrite or ordered search/replace entries.",
            Category = "shader",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Path to the shader file (res://...)."),
                ["content"] = Schema.Str("New full shader source (takes priority over replacements)."),
                ["replacements"] = Schema.Arr(
                    "List of {search, replace} objects applied in order.",
                    Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                    {
                        ["search"] = Schema.Str("Text to search for."),
                        ["replace"] = Schema.Str("Replacement text."),
                    })),
                ["force"] = Schema.Bool("Overwrite even if the target is open in the script editor.", false),
            }, "path"),
            Handler = p => Task.FromResult(EditShader(p)),
        },
        new()
        {
            Name = "assign_shader_material",
            Description = "Create a ShaderMaterial from a shader file and assign it to a node (undo-tracked).",
            Category = "shader",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the target node in the edited scene."),
                ["shader_path"] = Schema.Str("Path to the shader resource to assign (res://...)."),
            }, "node_path", "shader_path"),
            Handler = p => Task.FromResult(AssignShaderMaterial(p)),
        },
        new()
        {
            Name = "set_shader_param",
            Description = "Set a shader parameter (uniform) on a node's ShaderMaterial. String values are smart-parsed as expressions when possible.",
            Category = "shader",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the target node in the edited scene."),
                ["param"] = Schema.Str("Shader uniform/parameter name."),
                ["value"] = Schema.Any("Value to assign to the parameter."),
            }, "node_path", "param"),
            Handler = p => Task.FromResult(SetShaderParam(p)),
        },
        new()
        {
            Name = "get_shader_params",
            Description = "Get all shader parameters (uniforms) currently set on a node's ShaderMaterial.",
            Category = "shader",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the target node in the edited scene."),
            }, "node_path"),
            Handler = p => Task.FromResult(GetShaderParams(p)),
        },
    };

    // ── create_shader ────────────────────────────────────────────────────────

    private static Dictionary CreateShader(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        var content = OptionalString(@params, "content", "");
        var shaderType = OptionalString(@params, "shader_type", "spatial");
        var force = OptionalBool(@params, "force", false);

        var guard = GuardTextResourceWrite(path, force);
        if (guard.Count > 0)
            return guard;

        if (string.IsNullOrEmpty(content))
        {
            content = shaderType switch
            {
                "spatial" => "shader_type spatial;\n\nvoid vertex() {\n\t// Called for every vertex\n}\n\nvoid fragment() {\n\t// Called for every pixel\n\tALBEDO = vec3(1.0);\n}\n",
                "canvas_item" => "shader_type canvas_item;\n\nvoid vertex() {\n\t// Called for every vertex\n}\n\nvoid fragment() {\n\t// Called for every pixel\n\tCOLOR = vec4(1.0);\n}\n",
                "particles" => "shader_type particles;\n\nvoid start() {\n\t// Called when particle spawns\n}\n\nvoid process() {\n\t// Called every frame per particle\n}\n",
                "sky" => "shader_type sky;\n\nvoid sky() {\n\tCOLOR = vec3(0.3, 0.5, 0.8);\n}\n",
                _ => content,
            };
        }

        var dirErr = EnsureParentDir(path);
        if (dirErr.Count > 0)
            return dirErr;

        using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
        {
            if (file == null)
                return ErrorInternal($"Cannot create shader: {Godot.FileAccess.GetOpenError()}");
            file.StoreString(content);
        }

        RefreshLoadedShader(path, content);

        return Success(new Dictionary { ["path"] = path, ["shader_type"] = shaderType, ["created"] = true });
    }

    // ── read_shader ──────────────────────────────────────────────────────────

    private static Dictionary ReadShader(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Shader '{path}'");

        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
            return ErrorInternal($"Cannot read shader: {Godot.FileAccess.GetOpenError()}");

        var content = file.GetAsText();

        return Success(new Dictionary { ["path"] = path, ["content"] = content, ["size"] = content.Length });
    }

    // ── shared reload helper ─────────────────────────────────────────────────

    private static void RefreshLoadedShader(string path, string content)
    {
        var normalized = NormalizeProjectPath(path);
        if (string.IsNullOrEmpty(normalized))
            return;
        if (ResourceLoader.HasCached(normalized))
        {
            var shader = new Shader { Code = content };
            shader.TakeOverPath(normalized);
            shader.EmitChanged();
        }
        EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(normalized);
    }

    // ── edit_shader ──────────────────────────────────────────────────────────

    private static Dictionary EditShader(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Shader '{path}'");

        var force = OptionalBool(@params, "force", false);
        var guard = GuardTextResourceWrite(path, force);
        if (guard.Count > 0)
            return guard;

        var changesMade = 0;
        var content = "";

        if (@params.ContainsKey("content"))
        {
            content = @params["content"].AsString();
            changesMade = 1;
        }
        else if (@params.TryGetValue("replacements", out var replacementsVariant)
                 && replacementsVariant.VariantType == Variant.Type.Array)
        {
            using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read))
            {
                if (file == null)
                    return ErrorInternal("Cannot read shader");
                content = file.GetAsText();
            }

            foreach (var replacementVariant in replacementsVariant.AsGodotArray())
            {
                if (replacementVariant.VariantType != Variant.Type.Dictionary)
                    continue;
                var replacement = replacementVariant.AsGodotDictionary();
                var search = replacement.TryGetValue("search", out var searchV) ? searchV.AsString() : "";
                var replace = replacement.TryGetValue("replace", out var replaceV) ? replaceV.AsString() : "";
                if (!string.IsNullOrEmpty(search) && content.Contains(search))
                {
                    content = content.Replace(search, replace);
                    changesMade += 1;
                }
            }
        }

        if (changesMade > 0)
        {
            using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
            {
                if (file == null)
                    return ErrorInternal($"Cannot write shader: {Godot.FileAccess.GetOpenError()}");
                file.StoreString(content);
            }
            RefreshLoadedShader(path, content);
        }

        return Success(new Dictionary { ["path"] = path, ["changes_made"] = changesMade });
    }

    // ── assign_shader_material ───────────────────────────────────────────────

    private Dictionary AssignShaderMaterial(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (shaderPath, err2) = RequireString(@params, "shader_path");
        if (err2 != null)
            return err2;

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node at '{nodePath}'");

        if (!ResourceLoader.Exists(shaderPath))
            return ErrorNotFound($"Shader '{shaderPath}'");

        if (ResourceLoader.Load(shaderPath) is not Shader shader)
            return ErrorInternal("Failed to load shader");

        var material = new ShaderMaterial { Shader = shader };

        if (node is CanvasItem)
        {
            SetPropertyWithUndo(node, "material", material, "MCP: Assign shader material");
        }
        else if (node is MeshInstance3D)
        {
            SetPropertyWithUndo(node, "material_override", material, "MCP: Assign shader material");
        }
        else if (NodeHasProperty(node, "material"))
        {
            SetPropertyWithUndo(node, "material", material, "MCP: Assign shader material");
        }
        else
        {
            return ErrorInvalidParams($"Node '{nodePath}' ({node.GetClass()}) does not support materials");
        }

        return Success(new Dictionary { ["node_path"] = nodePath, ["shader_path"] = shaderPath, ["assigned"] = true });
    }

    // ── set_shader_param ──────────────────────────────────────────────────────

    private static Dictionary SetShaderParam(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (paramName, err2) = RequireString(@params, "param");
        if (err2 != null)
            return err2;

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node at '{nodePath}'");

        var material = FindShaderMaterial(node);
        if (material == null)
            return Error(CodeGeneric, "Node has no ShaderMaterial");

        Variant value = @params.TryGetValue("value", out var v) ? v : default;
        if (value.VariantType == Variant.Type.String)
        {
            var s = value.AsString();
            var expr = new Expression();
            if (expr.Parse(s) == Godot.Error.Ok)
            {
                var parsed = expr.Execute();
                if (parsed.VariantType != Variant.Type.Nil)
                    value = parsed;
            }
        }

        material.SetShaderParameter(paramName, value);

        return Success(new Dictionary { ["node_path"] = nodePath, ["param"] = paramName, ["value"] = value.ToString() });
    }

    // ── get_shader_params ─────────────────────────────────────────────────────

    private static Dictionary GetShaderParams(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node at '{nodePath}'");

        var material = FindShaderMaterial(node);
        if (material == null)
            return Error(CodeGeneric, "Node has no ShaderMaterial");

        var shaderParams = new Dictionary();
        foreach (var prop in material.GetPropertyList())
        {
            var pname = prop["name"].AsString();
            if (pname.StartsWith("shader_parameter/"))
            {
                var key = pname[17..];
                shaderParams[key] = material.Get(pname).ToString();
            }
        }

        return Success(new Dictionary { ["node_path"] = nodePath, ["params"] = shaderParams });
    }

    // ── shared helpers ─────────────────────────────────────────────────────────

    private static ShaderMaterial? FindShaderMaterial(Node node)
    {
        if (node is CanvasItem canvasItem && canvasItem.Material is ShaderMaterial canvasMaterial)
            return canvasMaterial;
        if (node is MeshInstance3D meshInstance && meshInstance.MaterialOverride is ShaderMaterial meshMaterial)
            return meshMaterial;
        return null;
    }

    private static bool NodeHasProperty(GodotObject obj, string propertyName)
    {
        foreach (var prop in obj.GetPropertyList())
        {
            if (prop["name"].AsString() == propertyName)
                return true;
        }
        return false;
    }
}
