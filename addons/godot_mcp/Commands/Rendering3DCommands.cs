using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// Advanced 3D rendering tools: decals, fog volumes, reflection probes,
/// VoxelGI, and LightmapGI. All node creation goes through
/// EditorUndoRedoManager so changes participate in the editor's undo history.
/// </summary>
[Tool]
public partial class Rendering3DCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "create_decal",
            Description = "Create a Decal node projecting a texture onto geometry (albedo texture, size, " +
                "position/rotation, albedo_mix, emission_energy, modulation). Undo-tracked.",
            Category = "rendering_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new Decal node", "Decal"),
                ["texture"] = Schema.Str("res:// path to the albedo texture projected by the decal"),
                ["size"] = Schema.Any("Decal box size as Vector3(x,y,z) string or {x,y,z} dict"),
                ["position"] = Schema.Any("Position as Vector3(x,y,z) string or {x,y,z} dict"),
                ["rotation"] = Schema.Any("Rotation in degrees as Vector3(x,y,z) string or {x,y,z} dict"),
                ["albedo_mix"] = Schema.Num("How strongly the decal blends with the underlying albedo (0-1)", 1.0),
                ["emission_energy"] = Schema.Num("Emission energy multiplier"),
                ["modulation"] = Schema.Any("Color modulation as Color(...) string, #hex, or {r,g,b,a} dict"),
            }),
            Handler = CreateDecal,
        },
        new()
        {
            Name = "create_fog_volume",
            Description = "Create a FogVolume node (volumetric fog) with a FogMaterial: shape, size, density, " +
                "albedo, and emission. Undo-tracked.",
            Category = "rendering_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new FogVolume node", "FogVolume"),
                ["size"] = Schema.Any("Fog volume size as Vector3(x,y,z) string or {x,y,z} dict"),
                ["shape"] = Schema.Enum("Fog volume shape", "box", "sphere", "ellipsoid", "cone", "cylinder", "capsule"),
                ["density"] = Schema.Num("Fog density (higher = thicker)", 1.0),
                ["albedo"] = Schema.Any("Fog albedo color as Color(...) string, #hex, or {r,g,b,a} dict"),
                ["emission"] = Schema.Any("Fog emission color as Color(...) string, #hex, or {r,g,b,a} dict"),
                ["position"] = Schema.Any("Position as Vector3(x,y,z) string or {x,y,z} dict"),
            }),
            Handler = CreateFogVolume,
        },
        new()
        {
            Name = "create_reflection_probe",
            Description = "Create a ReflectionProbe node: size, intensity, max_distance, box_projection, " +
                "enable_shadows, and resolution. Note: Godot 4 controls probe resolution project-wide; when " +
                "'resolution' is provided it is applied to ProjectSettings " +
                "rendering/reflections/reflection_atlas/reflection_size. Undo-tracked.",
            Category = "rendering_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new ReflectionProbe node", "ReflectionProbe"),
                ["size"] = Schema.Any("Probe box extents as Vector3(x,y,z) string or {x,y,z} dict"),
                ["intensity"] = Schema.Num("Reflection intensity multiplier", 1.0),
                ["max_distance"] = Schema.Num("Max distance the probe influences", 10.0),
                ["box_projection"] = Schema.Bool("Enable box projection (parallax correction)", false),
                ["enable_shadows"] = Schema.Bool("Enable shadows in the probe render", false),
                ["resolution"] = Schema.Int("Project-wide reflection atlas size in pixels (applied to ProjectSettings)"),
                ["position"] = Schema.Any("Position as Vector3(x,y,z) string or {x,y,z} dict"),
            }),
            Handler = CreateReflectionProbe,
        },
        new()
        {
            Name = "setup_voxel_gi",
            Description = "Create a VoxelGI node (real-time baked global illumination), configure subdivision " +
                "and use_two_bounces, then bake it. Bake quality (low/medium/high/ultra) is applied via " +
                "ProjectSettings rendering/global_illumination/voxel_gi/quality during the bake and restored " +
                "afterwards. Undo-tracked.",
            Category = "rendering_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new VoxelGI node", "VoxelGI"),
                ["size"] = Schema.Any("VoxelGI capture volume size as Vector3(x,y,z) string or {x,y,z} dict"),
                ["subdivision"] = Schema.Any("Voxel subdivision level: 64, 128, 256, or 512 (higher = more detail)"),
                ["quality"] = Schema.Enum("Bake quality", "low", "medium", "high", "ultra"),
                ["use_two_bounces"] = Schema.Bool("Compute two indirect light bounces during bake", false),
            }),
            Handler = SetupVoxelGI,
        },
        new()
        {
            Name = "setup_lightmap_gi",
            Description = "Create a LightmapGI node (precomputed lightmaps), configure bake quality, bounces, " +
                "and denoiser, then optionally bake it (bake=true by default). Undo-tracked.",
            Category = "rendering_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new LightmapGI node", "LightmapGI"),
                ["quality"] = Schema.Enum("Bake quality", "low", "medium", "high"),
                ["bounces"] = Schema.Int("Number of indirect light bounces", 3),
                ["denoiser"] = Schema.Bool("Denoise the baked lightmaps", true),
                ["bake"] = Schema.Bool("Run the lightmap bake after creation", true),
            }),
            Handler = SetupLightmapGI,
        },
    };

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Color ParseColorParam(Dictionary @params, string key, Color @default)
    {
        if (!@params.TryGetValue(key, out var val))
            return @default;
        if (val.VariantType == Variant.Type.String)
            return PropertyParser.ParseValue(val, Variant.Type.Color).AsColor();
        if (val.VariantType == Variant.Type.Dictionary)
        {
            var d = val.AsGodotDictionary();
            return new Color(
                (float)GetNum(d, "r", @default.R),
                (float)GetNum(d, "g", @default.G),
                (float)GetNum(d, "b", @default.B),
                (float)GetNum(d, "a", @default.A));
        }
        return @default;
    }

    private static Vector3 ParseVector3Param(Dictionary @params, string key, Vector3 @default)
    {
        if (!@params.TryGetValue(key, out var val))
            return @default;
        if (val.VariantType == Variant.Type.String)
            return PropertyParser.ParseValue(val, Variant.Type.Vector3).AsVector3();
        if (val.VariantType == Variant.Type.Dictionary)
        {
            var d = val.AsGodotDictionary();
            return new Vector3(
                (float)GetNum(d, "x", @default.X),
                (float)GetNum(d, "y", @default.Y),
                (float)GetNum(d, "z", @default.Z));
        }
        if (val.VariantType == Variant.Type.Array)
        {
            var arr = val.AsGodotArray();
            if (arr.Count >= 3)
                return new Vector3((float)arr[0].AsDouble(), (float)arr[1].AsDouble(), (float)arr[2].AsDouble());
        }
        return @default;
    }

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

    // ── 1. create_decal ───────────────────────────────────────────────────────

    private Task<Dictionary> CreateDecal(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        var decal = new Decal { Name = OptionalString(@params, "name", "Decal") };

        decal.Size = ParseVector3Param(@params, "size", new Vector3(2, 2, 2));
        decal.Position = ParseVector3Param(@params, "position", Vector3.Zero);
        decal.RotationDegrees = ParseVector3Param(@params, "rotation", Vector3.Zero);
        decal.AlbedoMix = (float)OptionalFloat(@params, "albedo_mix", 1.0);
        decal.EmissionEnergy = (float)OptionalFloat(@params, "emission_energy", 1.0);
        if (@params.ContainsKey("modulation"))
            decal.Modulate = ParseColorParam(@params, "modulation", Colors.White);

        var texturePath = OptionalString(@params, "texture", "");
        if (!string.IsNullOrEmpty(texturePath))
        {
            if (!ResourceLoader.Exists(texturePath))
            {
                decal.QueueFree();
                return Task.FromResult(ErrorNotFound($"Texture '{texturePath}'",
                    "Provide a valid res:// path to an image texture"));
            }
            if (ResourceLoader.Load(texturePath) is Texture2D tex)
            {
                decal.TextureAlbedo = tex;
            }
            else
            {
                decal.QueueFree();
                return Task.FromResult(ErrorInvalidParams($"'{texturePath}' is not a Texture2D"));
            }
        }

        AddChildWithUndo(parent, decal, root, "MCP: Add Decal");

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(decal).ToString(),
            ["name"] = decal.Name.ToString(),
            ["size"] = decal.Size.ToString(),
            ["has_texture"] = decal.TextureAlbedo != null,
            ["texture"] = texturePath,
        }));
    }

    // ── 2. create_fog_volume ──────────────────────────────────────────────────

    private Task<Dictionary> CreateFogVolume(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        var fogVolume = new FogVolume { Name = OptionalString(@params, "name", "FogVolume") };

        // Godot has no dedicated sphere/capsule fog shapes; ellipsoid covers both
        // (scale uniformly for sphere, elongate one axis for capsule).
        var shapeStr = OptionalString(@params, "shape", "box").ToLowerInvariant();
        switch (shapeStr)
        {
            case "box":
                fogVolume.Shape = RenderingServer.FogVolumeShape.Box;
                break;
            case "sphere":
            case "ellipsoid":
            case "capsule":
                fogVolume.Shape = RenderingServer.FogVolumeShape.Ellipsoid;
                break;
            case "cone":
                fogVolume.Shape = RenderingServer.FogVolumeShape.Cone;
                break;
            case "cylinder":
                fogVolume.Shape = RenderingServer.FogVolumeShape.Cylinder;
                break;
            default:
                fogVolume.QueueFree();
                return Task.FromResult(ErrorInvalidParams(
                    $"Unknown shape '{shapeStr}'. Available: box, sphere, ellipsoid, cone, cylinder, capsule"));
        }

        fogVolume.Size = ParseVector3Param(@params, "size", new Vector3(2, 2, 2));
        fogVolume.Position = ParseVector3Param(@params, "position", Vector3.Zero);

        var material = new FogMaterial
        {
            Density = (float)OptionalFloat(@params, "density", 1.0),
        };
        if (@params.ContainsKey("albedo"))
            material.Albedo = ParseColorParam(@params, "albedo", Colors.White);
        if (@params.ContainsKey("emission"))
            material.Emission = ParseColorParam(@params, "emission", new Color(0, 0, 0, 1));
        fogVolume.Material = material;

        AddChildWithUndo(parent, fogVolume, root, "MCP: Add FogVolume");

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(fogVolume).ToString(),
            ["name"] = fogVolume.Name.ToString(),
            ["shape"] = shapeStr,
            ["size"] = fogVolume.Size.ToString(),
            ["density"] = material.Density,
        }));
    }

    // ── 3. create_reflection_probe ────────────────────────────────────────────

    private Task<Dictionary> CreateReflectionProbe(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        var probe = new ReflectionProbe { Name = OptionalString(@params, "name", "ReflectionProbe") };

        probe.Size = ParseVector3Param(@params, "size", new Vector3(10, 10, 10));
        probe.Position = ParseVector3Param(@params, "position", Vector3.Zero);
        probe.Intensity = (float)OptionalFloat(@params, "intensity", 1.0);
        probe.MaxDistance = (float)OptionalFloat(@params, "max_distance", 10.0);
        probe.BoxProjection = OptionalBool(@params, "box_projection", false);
        probe.EnableShadows = OptionalBool(@params, "enable_shadows", false);

        AddChildWithUndo(parent, probe, root, "MCP: Add ReflectionProbe");

        // Godot 4 has no per-probe resolution; it is project-wide.
        var resolution = OptionalInt(@params, "resolution", 0);
        var resolutionApplied = false;
        const string resolutionSetting = "rendering/reflections/reflection_atlas/reflection_size";
        if (resolution > 0)
        {
            ProjectSettings.SetSetting(resolutionSetting, resolution);
            resolutionApplied = true;
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(probe).ToString(),
            ["name"] = probe.Name.ToString(),
            ["size"] = probe.Size.ToString(),
            ["intensity"] = probe.Intensity,
            ["max_distance"] = probe.MaxDistance,
            ["box_projection"] = probe.BoxProjection,
            ["enable_shadows"] = probe.EnableShadows,
            ["resolution_applied_to"] = resolutionApplied ? resolutionSetting : "",
        }));
    }

    // ── 4. setup_voxel_gi ─────────────────────────────────────────────────────

    private Task<Dictionary> SetupVoxelGI(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        var voxelGI = new VoxelGI { Name = OptionalString(@params, "name", "VoxelGI") };

        voxelGI.Size = ParseVector3Param(@params, "size", new Vector3(20, 8, 20));

        var subdivision = OptionalInt(@params, "subdivision", 128);
        switch (subdivision)
        {
            case 64:
                voxelGI.Subdiv = VoxelGI.SubdivEnum.Subdiv64;
                break;
            case 128:
                voxelGI.Subdiv = VoxelGI.SubdivEnum.Subdiv128;
                break;
            case 256:
                voxelGI.Subdiv = VoxelGI.SubdivEnum.Subdiv256;
                break;
            case 512:
                voxelGI.Subdiv = VoxelGI.SubdivEnum.Subdiv512;
                break;
            default:
                voxelGI.QueueFree();
                return Task.FromResult(ErrorInvalidParams(
                    $"Invalid subdivision '{subdivision}'. Available: 64, 128, 256, 512"));
        }

        var qualityStr = OptionalString(@params, "quality", "low").ToLowerInvariant();
        var qualityInt = qualityStr switch
        {
            "low" => 0,
            "medium" => 1,
            "high" => 2,
            "ultra" => 3,
            _ => -1,
        };
        if (qualityInt < 0)
        {
            voxelGI.QueueFree();
            return Task.FromResult(ErrorInvalidParams(
                $"Unknown quality '{qualityStr}'. Available: low, medium, high, ultra"));
        }

        // The baker reads use_two_bounces from the VoxelGIData present at bake
        // time, so it must be pre-assigned — setting it after baking has no effect.
        var useTwoBounces = OptionalBool(@params, "use_two_bounces", false);
        if (useTwoBounces)
            voxelGI.Data = new VoxelGIData { UseTwoBounces = true };

        AddChildWithUndo(parent, voxelGI, root, "MCP: Add VoxelGI");

        // Bake quality lives only in ProjectSettings; apply it just for this bake.
        // VoxelGI.Bake() returns void — success cannot be reported synchronously.
        const string qualitySetting = "rendering/global_illumination/voxel_gi/quality";
        var oldQuality = ProjectSettings.GetSetting(qualitySetting, 0).AsInt64();
        ProjectSettings.SetSetting(qualitySetting, qualityInt);
        voxelGI.Bake();
        ProjectSettings.SetSetting(qualitySetting, oldQuality);

        var result = new Dictionary
        {
            ["node_path"] = root.GetPathTo(voxelGI).ToString(),
            ["name"] = voxelGI.Name.ToString(),
            ["subdivision"] = subdivision,
            ["quality"] = qualityStr,
            ["use_two_bounces"] = useTwoBounces,
            ["bake_triggered"] = true,
        };

        return Task.FromResult(Success(result));
    }

    // ── 5. setup_lightmap_gi ──────────────────────────────────────────────────

    private Task<Dictionary> SetupLightmapGI(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        var lightmapGI = new LightmapGI { Name = OptionalString(@params, "name", "LightmapGI") };

        var qualityStr = OptionalString(@params, "quality", "medium").ToLowerInvariant();
        switch (qualityStr)
        {
            case "low":
                lightmapGI.Quality = LightmapGI.BakeQuality.Low;
                break;
            case "medium":
                lightmapGI.Quality = LightmapGI.BakeQuality.Medium;
                break;
            case "high":
                lightmapGI.Quality = LightmapGI.BakeQuality.High;
                break;
            case "ultra":
                lightmapGI.Quality = LightmapGI.BakeQuality.Ultra;
                break;
            default:
                lightmapGI.QueueFree();
                return Task.FromResult(ErrorInvalidParams(
                    $"Unknown quality '{qualityStr}'. Available: low, medium, high"));
        }

        lightmapGI.Bounces = OptionalInt(@params, "bounces", 3);
        lightmapGI.UseDenoiser = OptionalBool(@params, "denoiser", true);

        AddChildWithUndo(parent, lightmapGI, root, "MCP: Add LightmapGI");

        var result = new Dictionary
        {
            ["node_path"] = root.GetPathTo(lightmapGI).ToString(),
            ["name"] = lightmapGI.Name.ToString(),
            ["quality"] = qualityStr,
            ["bounces"] = lightmapGI.Bounces,
        };

        if (OptionalBool(@params, "bake", true))
        {
            // LightmapGI.Bake() is editor-only and not exposed in the C# bindings;
            // Call reaches it dynamically inside the editor process. Returns
            // LightmapGI.BakeError as int (0 = Ok).
            var bakeResult = lightmapGI.Call("bake");
            var bakeOk = bakeResult.VariantType == Variant.Type.Int && bakeResult.AsInt64() == 0;
            result["bake_success"] = bakeOk;
            if (!bakeOk)
                result["bake_error"] = bakeResult.ToString();
        }
        else
        {
            result["bake_success"] = false;
            result["bake_skipped"] = true;
        }

        return Task.FromResult(Success(result));
    }
}
