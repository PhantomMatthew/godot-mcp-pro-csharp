using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

[Tool]
public partial class UtilityCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools()
    {
        return new List<ToolDefinition>
        {
            new()
            {
                Name = "create_gradient_resource",
                Description = "Create a Gradient or GradientTexture1D/2D resource file (.tres) with configurable color stops.",
                Category = "resource",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["path"] = Schema.Str("Output res:// path ending in .tres."),
                    ["texture_type"] = Schema.Enum("Resource type.", "gradient", "gradient_texture_1d", "gradient_texture_2d"),
                    ["stops"] = Schema.Arr("Color stops [{offset, color}].", Schema.Obj("Color stop.")),
                    ["width"] = Schema.Int("Texture width (GradientTexture1D/2D).", 256),
                    ["height"] = Schema.Int("Texture height (GradientTexture2D).", 256),
                    ["fill_to"] = Schema.Any("Fill end point {x,y} for GradientTexture2D."),
                    ["fill_from"] = Schema.Any("Fill start point {x,y} for GradientTexture2D."),
                }, "path", "stops"),
                Handler = p => Task.FromResult(CreateGradientResource(p)),
            },
            new()
            {
                Name = "create_noise_texture",
                Description = "Create a FastNoiseLite + NoiseTexture2D resource pair for procedural noise generation.",
                Category = "resource",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["path"] = Schema.Str("Output res:// path ending in .tres for the NoiseTexture2D."),
                    ["noise_type"] = Schema.Enum("FastNoiseLite noise type.", "simplex", "simplex_smooth", "cellular", "perlin", "value", "value_cubic"),
                    ["frequency"] = Schema.Num("Noise frequency.", 0.05),
                    ["fractal_type"] = Schema.Enum("Fractal type.", "none", "fbm", "ridged", "ping_pong"),
                    ["fractal_octaves"] = Schema.Int("Fractal octaves.", 5),
                    ["fractal_lacunarity"] = Schema.Num("Fractal lacunarity.", 2.0),
                    ["fractal_gain"] = Schema.Num("Fractal gain.", 0.5),
                    ["width"] = Schema.Int("Texture width.", 512),
                    ["height"] = Schema.Int("Texture height.", 512),
                    ["seamless"] = Schema.Bool("Generate seamless texture.", false),
                    ["normalize"] = Schema.Bool("Normalize output to 0-1.", true),
                }, "path"),
                Handler = p => Task.FromResult(CreateNoiseTexture(p)),
            },
            new()
            {
                Name = "setup_canvas_layer",
                Description = "Create a CanvasLayer node for UI/screen-space rendering organization.",
                Category = "node",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["parent_path"] = Schema.Str("Parent node path.", "."),
                    ["name"] = Schema.Str("CanvasLayer node name.", "CanvasLayer"),
                    ["layer"] = Schema.Int("Render layer (higher = on top).", 1),
                    ["offset_x"] = Schema.Num("Canvas offset X.", 0),
                    ["offset_y"] = Schema.Num("Canvas offset Y.", 0),
                    ["follow_viewport"] = Schema.Bool("Follow viewport transform.", true),
                }),
                Handler = p => Task.FromResult(SetupCanvasLayer(p)),
            },
            new()
            {
                Name = "duplicate_resource",
                Description = "Duplicate an existing .tres/.res resource file to a new path. The source resource is loaded, copied, and saved.",
                Category = "resource",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["source_path"] = Schema.Str("Source res:// path."),
                    ["dest_path"] = Schema.Str("Destination res:// path."),
                    ["overwrite"] = Schema.Bool("Overwrite if destination exists.", false),
                    ["properties"] = Schema.Obj("Property overrides on the copy."),
                }, "source_path", "dest_path"),
                Handler = p => Task.FromResult(DuplicateResource(p)),
            },
            new()
            {
                Name = "set_rich_text",
                Description = "Set BBCode-formatted text on a RichTextLabel node, with optional auto-fit and scrollbar configuration.",
                Category = "node",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = Schema.Str("Path to a RichTextLabel node."),
                    ["text"] = Schema.Str("BBCode-formatted text content."),
                    ["bbcode_enabled"] = Schema.Bool("Enable BBCode parsing.", true),
                    ["fit_content"] = Schema.Bool("Auto-resize to fit content.", false),
                    ["scroll_active"] = Schema.Bool("Enable scrollbar.", true),
                    ["scroll_following"] = Schema.Bool("Auto-scroll to bottom.", false),
                }, "node_path", "text"),
                Handler = p => Task.FromResult(SetRichText(p)),
            },
        };
    }

    private static Dictionary CreateGradientResource(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null) return err;
        var textureType = OptionalString(@params, "texture_type", "gradient");
        var stopsArr = OptionalArray(@params, "stops");

        var gradient = new Gradient();
        if (stopsArr != null && stopsArr.Count > 0)
        {
            var offsets = new float[stopsArr.Count];
            var colors = new Color[stopsArr.Count];
            for (var i = 0; i < stopsArr.Count; i++)
            {
                var stop = stopsArr[i].AsGodotDictionary();
                offsets[i] = stop.TryGetValue("offset", out var o) ? o.AsSingle() : (float)i / Mathf.Max(stopsArr.Count - 1, 1);
                colors[i] = stop.TryGetValue("color", out var c)
                    ? PropertyParser.ParseColor(c)
                    : Colors.White;
            }
            gradient.SetOffsets(offsets);
            gradient.SetColors(colors);
        }

        Resource targetRes;
        var displayType = textureType;

        switch (textureType)
        {
            case "gradient_texture_1d":
            {
                var tex = new GradientTexture1D { Gradient = gradient };
                tex.Width = OptionalInt(@params, "width", 256);
                targetRes = tex;
                break;
            }
            case "gradient_texture_2d":
            {
                var tex = new GradientTexture2D { Gradient = gradient };
                tex.Width = OptionalInt(@params, "width", 256);
                tex.Height = OptionalInt(@params, "height", 256);
                if (@params.TryGetValue("fill_from", out var ff) && ff.VariantType == Variant.Type.Dictionary)
                    tex.FillFrom = PropertyParser.ParseVector2(ff);
                if (@params.TryGetValue("fill_to", out var ft) && ft.VariantType == Variant.Type.Dictionary)
                    tex.FillTo = PropertyParser.ParseVector2(ft);
                targetRes = tex;
                break;
            }
            default:
                targetRes = gradient;
                displayType = "gradient";
                break;
        }

        var dirGuard = EnsureParentDir(path);
        if (dirGuard.Count > 0) return dirGuard;

        var saveErr = ResourceSaver.Save(targetRes, path);
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save resource: {saveErr}");

        EditorInterface.Singleton.GetResourceFilesystem().Scan();
        return Success(new Dictionary
        {
            ["path"] = path,
            ["type"] = displayType,
            ["stops"] = stopsArr?.Count ?? 0,
        });
    }

    private static Dictionary CreateNoiseTexture(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null) return err;

        var noise = new FastNoiseLite();
        var noiseTypeStr = OptionalString(@params, "noise_type", "simplex");
        noise.NoiseType = noiseTypeStr switch
        {
            "simplex" => FastNoiseLite.NoiseTypeEnum.Simplex,
            "simplex_smooth" => FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            "cellular" => FastNoiseLite.NoiseTypeEnum.Cellular,
            "perlin" => FastNoiseLite.NoiseTypeEnum.Perlin,
            "value" => FastNoiseLite.NoiseTypeEnum.Value,
            "value_cubic" => FastNoiseLite.NoiseTypeEnum.ValueCubic,
            _ => FastNoiseLite.NoiseTypeEnum.Simplex,
        };
        noise.Frequency = (float)OptionalFloat(@params, "frequency", 0.05);

        var fractalTypeStr = OptionalString(@params, "fractal_type", "fbm");
        if (fractalTypeStr != "none")
        {
            noise.FractalType = fractalTypeStr switch
            {
                "fbm" => FastNoiseLite.FractalTypeEnum.Fbm,
                "ridged" => FastNoiseLite.FractalTypeEnum.Ridged,
                "ping_pong" => FastNoiseLite.FractalTypeEnum.PingPong,
                _ => FastNoiseLite.FractalTypeEnum.Fbm,
            };
            noise.FractalOctaves = OptionalInt(@params, "fractal_octaves", 5);
            noise.FractalLacunarity = (float)OptionalFloat(@params, "fractal_lacunarity", 2.0);
            noise.FractalGain = (float)OptionalFloat(@params, "fractal_gain", 0.5);
        }

        var tex = new NoiseTexture2D
        {
            Width = OptionalInt(@params, "width", 512),
            Height = OptionalInt(@params, "height", 512),
            Seamless = OptionalBool(@params, "seamless", false),
            Normalize = OptionalBool(@params, "normalize", true),
            Noise = noise,
        };

        var dirGuard = EnsureParentDir(path);
        if (dirGuard.Count > 0) return dirGuard;

        var saveErr = ResourceSaver.Save(tex, path);
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save noise texture: {saveErr}");

        EditorInterface.Singleton.GetResourceFilesystem().Scan();
        return Success(new Dictionary
        {
            ["path"] = path,
            ["noise_type"] = noiseTypeStr,
            ["width"] = tex.Width,
            ["height"] = tex.Height,
            ["seamless"] = tex.Seamless,
        });
    }

    private Dictionary SetupCanvasLayer(Dictionary @params)
    {
        var parentPath = OptionalString(@params, "parent_path", ".");
        var nodeName = OptionalString(@params, "name", "CanvasLayer");

        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return ErrorNotFound($"Parent node '{parentPath}'");

        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        var layer = new CanvasLayer { Name = nodeName };
        layer.Layer = OptionalInt(@params, "layer", 1);
        layer.Offset = new Vector2(
            (float)OptionalFloat(@params, "offset_x", 0),
            (float)OptionalFloat(@params, "offset_y", 0));
        layer.FollowViewportEnabled = OptionalBool(@params, "follow_viewport", true);

        AddChildWithUndo(parent, layer, root, "MCP: Add CanvasLayer");
        MarkCurrentSceneUnsaved();

        return Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(layer).ToString(),
            ["name"] = nodeName,
            ["layer"] = layer.Layer,
        });
    }

    private static Dictionary DuplicateResource(Dictionary @params)
    {
        var (sourcePath, e1) = RequireString(@params, "source_path");
        if (e1 != null) return e1;
        var (destPath, e2) = RequireString(@params, "dest_path");
        if (e2 != null) return e2;
        var overwrite = OptionalBool(@params, "overwrite", false);
        var overrides = OptionalDict(@params, "properties");

        var normalizedSource = NormalizeProjectPath(sourcePath);
        var normalizedDest = NormalizeProjectPath(destPath);

        if (!ResourceLoader.Exists(normalizedSource))
            return ErrorNotFound($"Resource at '{normalizedSource}'");

        if (!overwrite && Godot.FileAccess.FileExists(normalizedDest))
            return Error(CodeGeneric, $"Destination '{normalizedDest}' already exists. Pass overwrite=true to replace.");

        var res = ResourceLoader.Load(normalizedSource);
        if (res == null)
            return ErrorInternal($"Failed to load source resource '{normalizedSource}'.");

        var dup = (Resource)res.Duplicate();

        if (overrides != null)
        {
            foreach (var key in overrides.Keys)
            {
                var propName = key.AsString();
                dup.Set(propName, PropertyParser.ParseValue(overrides[key]));
            }
        }

        var dirGuard = EnsureParentDir(normalizedDest);
        if (dirGuard.Count > 0) return dirGuard;

        var saveErr = ResourceSaver.Save(dup, normalizedDest);
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save duplicated resource: {saveErr}");

        EditorInterface.Singleton.GetResourceFilesystem().Scan();
        return Success(new Dictionary
        {
            ["source_path"] = normalizedSource,
            ["dest_path"] = normalizedDest,
            ["type"] = dup.GetClass(),
            ["duplicated"] = true,
        });
    }

    private Dictionary SetRichText(Dictionary @params)
    {
        var (nodePath, e1) = RequireString(@params, "node_path");
        if (e1 != null) return e1;
        var (text, e2) = RequireString(@params, "text");
        if (e2 != null) return e2;

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node '{nodePath}'");

        if (node is not RichTextLabel rtl)
            return ErrorInvalidParams($"Node '{nodePath}' is not a RichTextLabel (is {node.GetClass()}).");

        var bbcodeEnabled = OptionalBool(@params, "bbcode_enabled", true);
        var fitContent = OptionalBool(@params, "fit_content", false);
        var scrollActive = OptionalBool(@params, "scroll_active", true);
        var scrollFollowing = OptionalBool(@params, "scroll_following", false);

        SetPropertyWithUndo(rtl, "bbcode_enabled", bbcodeEnabled, "MCP: Set RichTextLabel BBCode");
        SetPropertyWithUndo(rtl, "text", text, "MCP: Set RichTextLabel text");
        SetPropertyWithUndo(rtl, "fit_content", fitContent, "MCP: Set RichTextLabel fit_content");
        SetPropertyWithUndo(rtl, "scroll_active", scrollActive, "MCP: Set RichTextLabel scroll_active");
        SetPropertyWithUndo(rtl, "scroll_following", scrollFollowing, "MCP: Set RichTextLabel scroll_following");
        MarkCurrentSceneUnsaved();

        return Success(new Dictionary
        {
            ["node_path"] = nodePath,
            ["text_length"] = text.Length,
            ["bbcode_enabled"] = bbcodeEnabled,
        });
    }
}
