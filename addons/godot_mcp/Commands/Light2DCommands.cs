using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// 2D lighting, light-occluder, and parallax background authoring tools.
/// All node creation goes through EditorUndoRedoManager so the operations
/// participate in the editor's undo history (Ctrl+Z).
/// </summary>
[Tool]
public partial class Light2DCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "setup_light_2d",
            Description = "Create a PointLight2D or DirectionalLight2D node as a child of an existing node (undo-tracked).",
            Category = "light_2d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["light_type"] = Schema.Enum("Type of light to create", "point", "directional"),
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node"),
                ["name"] = Schema.Str("Name for the new light node (defaults to the class name)"),
                ["texture"] = Schema.Str("res:// path to a Texture2D for the PointLight2D (point lights only)"),
                ["color"] = Schema.Str("Light color: hex '#RRGGBB[AA]', named color, or Color(r,g,b,a) expression", "#ffffff"),
                ["energy"] = Schema.Num("Light energy multiplier", 1.0),
                ["texture_scale"] = Schema.Num("Texture scale multiplier (point lights only)", 1.0),
                ["texture_offset"] = Schema.Obj("Texture offset as {x,y} (point lights only)"),
                ["z_range_min"] = Schema.Int("Minimum Z range (-1024 to 1024)", -1024),
                ["z_range_max"] = Schema.Int("Maximum Z range (-1024 to 1024)", 1024),
                ["z_enabled"] = Schema.Bool("Enable Z range filtering (forward-compatible)", true),
                ["shadow_enabled"] = Schema.Bool("Enable shadow rendering for this light", false),
                ["blend_mode"] = Schema.Enum("Light blend mode", "add", "mix", "sub"),
            }, "parent_path", "light_type"),
            Handler = SetupLight2D,
        },
        new()
        {
            Name = "setup_light_occluder_2d",
            Description = "Create a LightOccluder2D node with an OccluderPolygon2D polygon (undo-tracked).",
            Category = "light_2d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node"),
                ["name"] = Schema.Str("Name for the new occluder node", "LightOccluder2D"),
                ["polygon_points"] = Schema.Arr("Polygon outline as [{x,y}, ...]; at least 3 points", Schema.Obj("Point as {x,y}")),
                ["cull_mode"] = Schema.Enum("Backface cull mode for the occluder polygon", "disabled", "counter_clockwise", "clockwise"),
                ["sdf_collision"] = Schema.Bool("Enable signed-distance-field collision for this occluder", true),
            }, "parent_path"),
            Handler = SetupLightOccluder2D,
        },
        new()
        {
            Name = "setup_parallax",
            Description = "Create a ParallaxBackground with a ParallaxLayer child as children of an existing node (undo-tracked).",
            Category = "light_2d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node"),
                ["name"] = Schema.Str("Name for the new ParallaxBackground node", "ParallaxBackground"),
                ["scroll_scale"] = Schema.Obj("Background scroll scale as {x,y}; default {x:1,y:1}"),
                ["scroll_offset"] = Schema.Obj("Background scroll base offset as {x,y}; default {x:0,y:0}"),
                ["autoscroll"] = Schema.Obj("Autoscroll velocity as {x,y} (forward-compatible; no-op on stock ParallaxBackground)"),
                ["repeat_size"] = Schema.Obj("Layer motion-mirroring repeat size as {x,y}; default {x:0,y:0}"),
                ["motion_scale"] = Schema.Obj("Layer motion scale as {x,y}; default {x:1,y:1}"),
            }, "parent_path"),
            Handler = SetupParallax,
        },
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static double GetNum(Dictionary d, string key, double @default)
    {
        if (!d.TryGetValue(key, out var v)) return @default;
        return v.VariantType switch
        {
            Variant.Type.Int => v.AsInt64(),
            Variant.Type.Float => v.AsDouble(),
            Variant.Type.String => v.AsString().ToFloat(),
            Variant.Type.Bool => v.AsBool() ? 1 : 0,
            _ => @default,
        };
    }

    private static Vector2 ReadVector2(Dictionary @params, string key, Vector2 @default)
    {
        if (!@params.TryGetValue(key, out var v) || v.VariantType != Variant.Type.Dictionary)
            return @default;
        var d = v.AsGodotDictionary();
        return new Vector2(
            (float)GetNum(d, "x", @default.X),
            (float)GetNum(d, "y", @default.Y));
    }

    /// <summary>Support hex "#RRGGBB", "#RRGGBBAA", named colors, or a Color(r,g,b,a) expression.</summary>
    private static Color ParseColor(string colorStr)
    {
        if (string.IsNullOrEmpty(colorStr))
            return Colors.White;
        if (colorStr.StartsWith('#'))
            return Color.FromHtml(colorStr);

        switch (colorStr.ToLowerInvariant())
        {
            case "red": return Colors.Red;
            case "green": return Colors.Green;
            case "blue": return Colors.Blue;
            case "white": return Colors.White;
            case "black": return Colors.Black;
            case "yellow": return Colors.Yellow;
            case "orange": return new Color(1.0f, 0.5f, 0.0f);
            case "gray":
            case "grey": return Colors.Gray;
            case "cyan": return Colors.Cyan;
            case "magenta": return Colors.Magenta;
            case "transparent": return new Color(0, 0, 0, 0);
        }

        using var expr = new Expression();
        if (expr.Parse(colorStr) == Godot.Error.Ok)
        {
            var parsed = expr.Execute();
            if (!expr.HasExecuteFailed() && parsed.VariantType == Variant.Type.Color)
                return parsed.AsColor();
        }
        return Colors.White;
    }

    private static Light2D.BlendModeEnum ParseBlendMode(string s) => s.ToLowerInvariant() switch
    {
        "sub" => Light2D.BlendModeEnum.Sub,
        "mix" => Light2D.BlendModeEnum.Mix,
        _ => Light2D.BlendModeEnum.Add,
    };

    private static OccluderPolygon2D.CullModeEnum ParseCullMode(string s) => s.ToLowerInvariant() switch
    {
        "counter_clockwise" => OccluderPolygon2D.CullModeEnum.CounterClockwise,
        "clockwise" => OccluderPolygon2D.CullModeEnum.Clockwise,
        _ => OccluderPolygon2D.CullModeEnum.Disabled,
    };

    // ── 1. setup_light_2d ────────────────────────────────────────────────────

    private Task<Dictionary> SetupLight2D(Dictionary @params)
    {
        var (lightTypeRaw, typeErr) = RequireString(@params, "light_type");
        if (typeErr != null) return Task.FromResult(typeErr);
        var lightType = lightTypeRaw.ToLowerInvariant();
        if (lightType != "point" && lightType != "directional")
            return Task.FromResult(ErrorInvalidParams(
                $"Invalid light_type: '{lightTypeRaw}'. Expected 'point' or 'directional'."));

        var (parentPath, err) = RequireString(@params, "parent_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Node at '{parentPath}'"));

        // Pre-load the texture so we fail fast without leaving a dangling node.
        Texture2D? texture = null;
        var texturePath = OptionalString(@params, "texture", "");
        if (lightType == "point" && !string.IsNullOrEmpty(texturePath))
        {
            texture = ResourceLoader.Load<Texture2D>(texturePath);
            if (texture == null)
                return Task.FromResult(ErrorNotFound($"Texture at '{texturePath}'",
                    "Provide a valid res:// path to a Texture2D resource"));
        }

        var defaultName = lightType == "point" ? "PointLight2D" : "DirectionalLight2D";
        var nodeName = OptionalString(@params, "name", defaultName);
        var colorStr = OptionalString(@params, "color", "#ffffff");
        var color = ParseColor(colorStr);
        var energy = (float)OptionalFloat(@params, "energy", 1.0);
        var zMin = OptionalInt(@params, "z_range_min", -1024);
        var zMax = OptionalInt(@params, "z_range_max", 1024);
        var zEnabled = OptionalBool(@params, "z_enabled", true);
        var shadowEnabled = OptionalBool(@params, "shadow_enabled", false);
        var blendModeStr = OptionalString(@params, "blend_mode", "add");
        var blendMode = ParseBlendMode(blendModeStr);

        Light2D light;
        if (lightType == "point")
        {
            var p = new PointLight2D
            {
                Name = nodeName,
                Color = color,
                Energy = energy,
                RangeZMin = zMin,
                RangeZMax = zMax,
                ShadowEnabled = shadowEnabled,
                BlendMode = blendMode,
                TextureScale = (float)OptionalFloat(@params, "texture_scale", 1.0),
                Offset = ReadVector2(@params, "texture_offset", Vector2.Zero),
            };
            if (texture != null)
                p.Texture = texture;
            light = p;
        }
        else
        {
            light = new DirectionalLight2D
            {
                Name = nodeName,
                Color = color,
                Energy = energy,
                RangeZMin = zMin,
                RangeZMax = zMax,
                ShadowEnabled = shadowEnabled,
                BlendMode = blendMode,
            };
        }

        // Forward-compatible: Light2D has no z_enabled in current Godot; apply via
        // Set() so it works if/when the engine exposes the property, and is a
        // no-op (with an editor log entry) otherwise. Only sent when caller asked.
        if (@params.ContainsKey("z_enabled"))
            light.Set("z_enabled", zEnabled);

        AddChildWithUndo(parent, light, root, "MCP: Setup light 2D");

        return Task.FromResult(Success(new Dictionary
        {
            ["name"] = light.Name.ToString(),
            ["parent"] = parentPath,
            ["light_type"] = lightType,
            ["color"] = color.ToString(),
            ["energy"] = energy,
            ["z_range_min"] = zMin,
            ["z_range_max"] = zMax,
            ["z_enabled"] = zEnabled,
            ["shadow_enabled"] = shadowEnabled,
            ["blend_mode"] = blendModeStr,
            ["texture"] = texturePath,
            ["created"] = true,
        }));
    }

    // ── 2. setup_light_occluder_2d ───────────────────────────────────────────

    private Task<Dictionary> SetupLightOccluder2D(Dictionary @params)
    {
        var (parentPath, err) = RequireString(@params, "parent_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Node at '{parentPath}'"));

        var points = new List<Vector2>();
        if (@params.TryGetValue("polygon_points", out var ppVal) && ppVal.VariantType == Variant.Type.Array)
        {
            foreach (var item in ppVal.AsGodotArray())
            {
                if (item.VariantType != Variant.Type.Dictionary)
                    continue;
                var d = item.AsGodotDictionary();
                points.Add(new Vector2(
                    (float)GetNum(d, "x", 0),
                    (float)GetNum(d, "y", 0)));
            }
        }
        if (points.Count < 3)
            return Task.FromResult(ErrorInvalidParams(
                "polygon_points must contain at least 3 {x,y} points"));

        var nodeName = OptionalString(@params, "name", "LightOccluder2D");
        var cullModeStr = OptionalString(@params, "cull_mode", "disabled");
        var cullMode = ParseCullMode(cullModeStr);
        var sdfCollision = OptionalBool(@params, "sdf_collision", true);

        var polygon = new OccluderPolygon2D
        {
            Polygon = points.ToArray(),
            Closed = true,
            CullMode = cullMode,
        };

        var occluder = new LightOccluder2D
        {
            Name = nodeName,
            Occluder = polygon,
            SdfCollision = sdfCollision,
        };

        AddChildWithUndo(parent, occluder, root, "MCP: Setup light occluder 2D");

        return Task.FromResult(Success(new Dictionary
        {
            ["name"] = occluder.Name.ToString(),
            ["parent"] = parentPath,
            ["point_count"] = points.Count,
            ["cull_mode"] = cullModeStr,
            ["sdf_collision"] = sdfCollision,
            ["created"] = true,
        }));
    }

    // ── 3. setup_parallax ────────────────────────────────────────────────────

    private Task<Dictionary> SetupParallax(Dictionary @params)
    {
        var (parentPath, err) = RequireString(@params, "parent_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Node at '{parentPath}'"));

        var nodeName = OptionalString(@params, "name", "ParallaxBackground");
        var scrollScale = ReadVector2(@params, "scroll_scale", new Vector2(1, 1));
        var scrollOffset = ReadVector2(@params, "scroll_offset", Vector2.Zero);
        var motionScale = ReadVector2(@params, "motion_scale", new Vector2(1, 1));
        var repeatSize = ReadVector2(@params, "repeat_size", Vector2.Zero);
        var autoscroll = ReadVector2(@params, "autoscroll", Vector2.Zero);

        var bg = new ParallaxBackground
        {
            Name = nodeName,
            ScrollBaseOffset = scrollOffset,
        };

        var layer = new ParallaxLayer
        {
            Name = "ParallaxLayer",
            MotionScale = motionScale,
            MotionMirroring = repeatSize,
        };

        // Autoscroll is a Parallax2D property; forward via Set() so this stays
        // forwards-compatible if the engine exposes it on ParallaxBackground.
        if (@params.ContainsKey("autoscroll"))
            bg.Set("autoscroll", autoscroll);

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Setup parallax");
        undoRedo.AddDoMethod(parent, Node.MethodName.AddChild, bg);
        undoRedo.AddDoMethod(bg, Node.MethodName.SetOwner, root);
        undoRedo.AddDoMethod(bg, Node.MethodName.AddChild, layer);
        undoRedo.AddDoMethod(layer, Node.MethodName.SetOwner, root);
        undoRedo.AddDoReference(bg);
        undoRedo.AddDoReference(layer);
        undoRedo.AddUndoMethod(bg, Node.MethodName.RemoveChild, layer);
        undoRedo.AddUndoMethod(parent, Node.MethodName.RemoveChild, bg);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["name"] = bg.Name.ToString(),
            ["parent"] = parentPath,
            ["layer_name"] = layer.Name.ToString(),
            ["scroll_scale"] = scrollScale.ToString(),
            ["scroll_offset"] = scrollOffset.ToString(),
            ["motion_scale"] = motionScale.ToString(),
            ["repeat_size"] = repeatSize.ToString(),
            ["autoscroll"] = autoscroll.ToString(),
            ["created"] = true,
        }));
    }
}
