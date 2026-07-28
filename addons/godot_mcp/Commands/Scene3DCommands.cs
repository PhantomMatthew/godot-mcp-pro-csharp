using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// 3D scene authoring tools: mesh instances, lighting, materials, environment,
/// cameras, and gridmaps. Faithful port of
/// addons/godot_mcp/commands/scene_3d_commands.gd. All mutations go through
/// EditorUndoRedoManager so they participate in the editor's undo history.
/// </summary>
[Tool]
public partial class Scene3DCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "add_mesh_instance",
            Description = "Add a MeshInstance3D as a child of an existing node, using either a primitive " +
                "mesh_type (BoxMesh, SphereMesh, CylinderMesh, CapsuleMesh, PlaneMesh, PrismMesh, TorusMesh, " +
                "QuadMesh) or a mesh_file (.glb/.gltf/.obj).",
            Category = "scene_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new node", "MeshInstance3D"),
                ["mesh_type"] = Schema.Enum("Primitive mesh class to create",
                    "BoxMesh", "SphereMesh", "CylinderMesh", "CapsuleMesh", "PlaneMesh", "PrismMesh", "TorusMesh", "QuadMesh"),
                ["mesh_file"] = Schema.Str("res:// path to a .glb, .gltf, or .obj mesh file"),
                ["mesh_properties"] = Schema.Obj("Property overrides applied to the created primitive mesh"),
                ["position"] = Schema.Vec3("Position {x,y,z} (or \"Vector3(x,y,z)\" string)"),
                ["rotation"] = Schema.Vec3("Rotation in degrees {x,y,z} (or \"Vector3(x,y,z)\" string)"),
                ["scale"] = Schema.Vec3("Scale {x,y,z} (or \"Vector3(x,y,z)\" string)"),
            }),
            Handler = AddMeshInstance,
        },
        new()
        {
            Name = "setup_lighting",
            Description = "Add a Light3D (DirectionalLight3D, OmniLight3D, or SpotLight3D) as a child of an " +
                "existing node, or apply a named preset (sun, indoor, dramatic).",
            Category = "scene_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new light node"),
                ["light_type"] = Schema.Enum("Light node class to create", "DirectionalLight3D", "OmniLight3D", "SpotLight3D"),
                ["preset"] = Schema.Enum("Lighting preset (overrides light_type defaults)", "sun", "indoor", "dramatic"),
                ["color"] = Schema.Any("Light color as Color(...) string, #hex, or {r,g,b,a} dict"),
                ["energy"] = Schema.Num("Light energy multiplier"),
                ["shadows"] = Schema.Bool("Enable shadow casting"),
                ["range"] = Schema.Num("Omni/spot light range"),
                ["attenuation"] = Schema.Num("Omni/spot light attenuation"),
                ["spot_angle"] = Schema.Num("Spot light cone angle in degrees"),
                ["spot_angle_attenuation"] = Schema.Num("Spot light angle attenuation"),
                ["position"] = Schema.Vec3("Position {x,y,z} (or \"Vector3(x,y,z)\" string)"),
                ["rotation"] = Schema.Vec3("Rotation in degrees {x,y,z} (or \"Vector3(x,y,z)\" string)"),
            }),
            Handler = SetupLighting,
        },
        new()
        {
            Name = "set_material_3d",
            Description = "Create a StandardMaterial3D and assign it as a surface override material on a " +
                "MeshInstance3D (undo-tracked).",
            Category = "scene_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the MeshInstance3D"),
                ["surface_index"] = Schema.Int("Surface index to override", 0),
                ["albedo_color"] = Schema.Any("Albedo color as Color(...) string, #hex, or {r,g,b,a} dict"),
                ["albedo_texture"] = Schema.Str("res:// path to the albedo texture"),
                ["metallic"] = Schema.Num("Metallic factor (0-1)"),
                ["roughness"] = Schema.Num("Roughness factor (0-1)"),
                ["metallic_texture"] = Schema.Str("res:// path to the metallic texture"),
                ["roughness_texture"] = Schema.Str("res:// path to the roughness texture"),
                ["normal_texture"] = Schema.Str("res:// path to the normal map texture"),
                ["emission"] = Schema.Any("Emission color as Color(...) string, #hex, or {r,g,b,a} dict"),
                ["emission_color"] = Schema.Any("Alias for 'emission'"),
                ["emission_energy"] = Schema.Num("Emission energy multiplier"),
                ["emission_texture"] = Schema.Str("res:// path to the emission texture"),
                ["transparency"] = Schema.Any("Transparency mode name or index (DISABLED, ALPHA, ALPHA_SCISSOR, ALPHA_HASH, ALPHA_DEPTH_PRE_PASS)"),
                ["cull_mode"] = Schema.Any("Cull mode name or index (BACK, FRONT, DISABLED)"),
            }, "node_path"),
            Handler = SetMaterial3D,
        },
        new()
        {
            Name = "setup_environment",
            Description = "Create or reconfigure a WorldEnvironment node: background mode, procedural sky, " +
                "ambient light, tonemap, fog, glow, SSAO, SSR, and SDFGI.",
            Category = "scene_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node (used when creating a new WorldEnvironment)", "."),
                ["name"] = Schema.Str("Name for the new WorldEnvironment node", "WorldEnvironment"),
                ["node_path"] = Schema.Str("Scene-relative path of an existing WorldEnvironment to reconfigure"),
                ["background_mode"] = Schema.Enum("Background mode", "sky", "color", "canvas", "clear_color"),
                ["background_color"] = Schema.Any("Background color when background_mode is 'color'"),
                ["sky"] = Schema.Obj("Procedural sky parameters: sky_top_color, sky_horizon_color, ground_bottom_color, ground_horizon_color, sun_angle_max, sky_curve"),
                ["ambient_light_color"] = Schema.Any("Ambient light color"),
                ["ambient_light_energy"] = Schema.Num("Ambient light energy multiplier"),
                ["ambient_light_source"] = Schema.Any("Ambient light source (BACKGROUND, DISABLED, COLOR, SKY)"),
                ["tonemap_mode"] = Schema.Any("Tonemap mode (LINEAR, REINHARDT, FILMIC, ACES, AGX)"),
                ["tonemap_exposure"] = Schema.Num("Tonemap exposure"),
                ["tonemap_white"] = Schema.Num("Tonemap white reference value"),
                ["fog_enabled"] = Schema.Bool("Enable fog"),
                ["fog_light_color"] = Schema.Any("Fog color"),
                ["fog_density"] = Schema.Num("Fog density"),
                ["fog_light_energy"] = Schema.Num("Fog light energy"),
                ["glow_enabled"] = Schema.Bool("Enable glow"),
                ["glow_intensity"] = Schema.Num("Glow intensity"),
                ["glow_strength"] = Schema.Num("Glow strength"),
                ["glow_bloom"] = Schema.Num("Glow bloom"),
                ["ssao_enabled"] = Schema.Bool("Enable screen-space ambient occlusion"),
                ["ssao_radius"] = Schema.Num("SSAO radius"),
                ["ssao_intensity"] = Schema.Num("SSAO intensity"),
                ["ssr_enabled"] = Schema.Bool("Enable screen-space reflections"),
                ["ssr_max_steps"] = Schema.Int("SSR max steps"),
                ["ssr_fade_in"] = Schema.Num("SSR fade in"),
                ["ssr_fade_out"] = Schema.Num("SSR fade out"),
                ["sdfgi_enabled"] = Schema.Bool("Enable SDFGI"),
            }),
            Handler = SetupEnvironment,
        },
        new()
        {
            Name = "setup_camera_3d",
            Description = "Create or reconfigure a Camera3D: projection, fov/size/near/far, cull mask, current, " +
                "transform, look_at, and environment override.",
            Category = "scene_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node (used when creating a new Camera3D)", "."),
                ["name"] = Schema.Str("Name for the new Camera3D node", "Camera3D"),
                ["node_path"] = Schema.Str("Scene-relative path of an existing Camera3D to reconfigure"),
                ["projection"] = Schema.Enum("Camera projection mode", "perspective", "orthogonal", "frustum"),
                ["fov"] = Schema.Num("Field of view in degrees (perspective)"),
                ["size"] = Schema.Num("Size in world units (orthogonal/frustum)"),
                ["near"] = Schema.Num("Near clip distance"),
                ["far"] = Schema.Num("Far clip distance"),
                ["cull_mask"] = Schema.Int("Camera visibility layers bitmask"),
                ["current"] = Schema.Bool("Make this the active camera", false),
                ["position"] = Schema.Vec3("Position {x,y,z} (or \"Vector3(x,y,z)\" string)"),
                ["rotation"] = Schema.Vec3("Rotation in degrees {x,y,z} (or \"Vector3(x,y,z)\" string)"),
                ["look_at"] = Schema.Vec3("Point to look at {x,y,z} (or \"Vector3(x,y,z)\" string)"),
                ["environment_path"] = Schema.Str("res:// path to an Environment resource to assign"),
            }),
            Handler = SetupCamera3D,
        },
        new()
        {
            Name = "add_gridmap",
            Description = "Create or reconfigure a GridMap: mesh library, cell size, position, and cell contents.",
            Category = "scene_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node (used when creating a new GridMap)", "."),
                ["name"] = Schema.Str("Name for the new GridMap node", "GridMap"),
                ["node_path"] = Schema.Str("Scene-relative path of an existing GridMap to reconfigure"),
                ["mesh_library_path"] = Schema.Str("res:// path to a .meshlib or .tres MeshLibrary resource"),
                ["cell_size"] = Schema.Vec3("Cell size {x,y,z} (or \"Vector3(x,y,z)\" string)"),
                ["position"] = Schema.Vec3("Position {x,y,z} (or \"Vector3(x,y,z)\" string)"),
                ["cells"] = Schema.Arr("Cells to set", Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["x"] = Schema.Int("Cell X coordinate"),
                    ["y"] = Schema.Int("Cell Y coordinate"),
                    ["z"] = Schema.Int("Cell Z coordinate"),
                    ["item"] = Schema.Int("MeshLibrary item index"),
                    ["orientation"] = Schema.Int("Cell orientation (basis index)"),
                })),
            }),
            Handler = AddGridmap,
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

    private static bool HasProperty(GodotObject obj, string propertyName)
    {
        foreach (var prop in obj.GetPropertyList())
        {
            if (prop["name"].AsString() == propertyName)
                return true;
        }
        return false;
    }

    // ── 1. add_mesh_instance ─────────────────────────────────────────────────

    private Task<Dictionary> AddMeshInstance(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        var nodeName = OptionalString(@params, "name", "MeshInstance3D");
        var meshType = OptionalString(@params, "mesh_type", "");
        var meshFile = OptionalString(@params, "mesh_file", "");

        if (string.IsNullOrEmpty(meshType) && string.IsNullOrEmpty(meshFile))
            return Task.FromResult(ErrorInvalidParams("Either 'mesh_type' or 'mesh_file' is required"));

        var meshInstance = new MeshInstance3D { Name = nodeName };

        if (!string.IsNullOrEmpty(meshFile))
        {
            // Load .glb / .gltf / .obj
            if (!ResourceLoader.Exists(meshFile))
            {
                meshInstance.QueueFree();
                return Task.FromResult(ErrorNotFound($"Mesh file '{meshFile}'", "Provide a valid res:// path to .glb, .gltf, or .obj"));
            }
            var loaded = ResourceLoader.Load(meshFile);
            if (loaded is Mesh mesh)
            {
                meshInstance.Mesh = mesh;
            }
            else if (loaded is PackedScene packedScene)
            {
                // For .glb/.gltf we instantiate and steal the first MeshInstance3D's mesh
                var sceneInstance = packedScene.Instantiate();
                Mesh? foundMesh = null;
                var searchNodes = new Queue<Node>();
                searchNodes.Enqueue(sceneInstance);
                while (searchNodes.Count > 0)
                {
                    var n = searchNodes.Dequeue();
                    if (n is MeshInstance3D mi && mi.Mesh != null)
                    {
                        foundMesh = mi.Mesh;
                        break;
                    }
                    foreach (var child in n.GetChildren())
                        searchNodes.Enqueue(child);
                }
                sceneInstance.QueueFree();
                if (foundMesh == null)
                {
                    meshInstance.QueueFree();
                    return Task.FromResult(ErrorInvalidParams($"No mesh found in '{meshFile}'"));
                }
                meshInstance.Mesh = foundMesh;
            }
            else
            {
                meshInstance.QueueFree();
                return Task.FromResult(ErrorInvalidParams($"'{meshFile}' is not a Mesh or PackedScene"));
            }
        }
        else
        {
            var meshClasses = new System.Collections.Generic.Dictionary<string, Func<Mesh>>
            {
                ["BoxMesh"] = () => new BoxMesh(),
                ["SphereMesh"] = () => new SphereMesh(),
                ["CylinderMesh"] = () => new CylinderMesh(),
                ["CapsuleMesh"] = () => new CapsuleMesh(),
                ["PlaneMesh"] = () => new PlaneMesh(),
                ["PrismMesh"] = () => new PrismMesh(),
                ["TorusMesh"] = () => new TorusMesh(),
                ["QuadMesh"] = () => new QuadMesh(),
            };
            if (!meshClasses.TryGetValue(meshType, out var factory))
            {
                meshInstance.QueueFree();
                return Task.FromResult(ErrorInvalidParams(
                    $"Unknown mesh_type '{meshType}'. Available: {string.Join(", ", meshClasses.Keys)}"));
            }
            var meshRes = factory();
            // Apply mesh properties if provided
            var meshProperties = OptionalDict(@params, "mesh_properties") ?? new Dictionary();
            foreach (var propName in meshProperties.Keys)
            {
                var propNameStr = propName.AsString();
                if (!HasProperty(meshRes, propNameStr))
                    continue;
                var current = meshRes.Get(propNameStr);
                meshRes.Set(propNameStr, PropertyParser.ParseValue(meshProperties[propName], current.VariantType));
            }
            meshInstance.Mesh = meshRes;
        }

        // Transform
        var position = ParseVector3Param(@params, "position", Vector3.Zero);
        var rotationDeg = ParseVector3Param(@params, "rotation", Vector3.Zero);
        var scaleVec = ParseVector3Param(@params, "scale", Vector3.One);

        meshInstance.Position = position;
        meshInstance.RotationDegrees = rotationDeg;
        meshInstance.Scale = scaleVec;

        AddChildWithUndo(parent, meshInstance, root, "MCP: Add MeshInstance3D");

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(meshInstance).ToString(),
            ["name"] = meshInstance.Name.ToString(),
            ["mesh_type"] = string.IsNullOrEmpty(meshFile) ? meshType : meshFile,
        }));
    }

    // ── 2. setup_lighting ────────────────────────────────────────────────────

    private Task<Dictionary> SetupLighting(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        var lightType = OptionalString(@params, "light_type", "");
        var preset = OptionalString(@params, "preset", "");
        var nodeName = OptionalString(@params, "name", "");

        // Preset configurations
        if (!string.IsNullOrEmpty(preset))
        {
            switch (preset)
            {
                case "sun":
                    lightType = "DirectionalLight3D";
                    if (string.IsNullOrEmpty(nodeName)) nodeName = "SunLight";
                    break;
                case "indoor":
                    lightType = "OmniLight3D";
                    if (string.IsNullOrEmpty(nodeName)) nodeName = "IndoorLight";
                    break;
                case "dramatic":
                    lightType = "SpotLight3D";
                    if (string.IsNullOrEmpty(nodeName)) nodeName = "DramaticLight";
                    break;
                default:
                    return Task.FromResult(ErrorInvalidParams($"Unknown preset '{preset}'. Available: sun, indoor, dramatic"));
            }
        }

        if (string.IsNullOrEmpty(lightType))
            return Task.FromResult(ErrorInvalidParams("Either 'light_type' or 'preset' is required"));

        Light3D light;
        switch (lightType)
        {
            case "DirectionalLight3D":
                light = new DirectionalLight3D();
                break;
            case "OmniLight3D":
                light = new OmniLight3D();
                break;
            case "SpotLight3D":
                light = new SpotLight3D();
                break;
            default:
                return Task.FromResult(ErrorInvalidParams(
                    $"Unknown light_type '{lightType}'. Available: DirectionalLight3D, OmniLight3D, SpotLight3D"));
        }

        if (string.IsNullOrEmpty(nodeName)) nodeName = lightType;
        light.Name = nodeName;

        // Common properties
        light.LightColor = ParseColorParam(@params, "color", Colors.White);
        light.LightEnergy = (float)OptionalFloat(@params, "energy", 1.0);
        light.ShadowEnabled = OptionalBool(@params, "shadows", false);

        // Type-specific properties
        if (light is OmniLight3D omni)
        {
            omni.OmniRange = (float)OptionalFloat(@params, "range", 5.0);
            omni.OmniAttenuation = (float)OptionalFloat(@params, "attenuation", 1.0);
        }
        else if (light is SpotLight3D spot)
        {
            spot.SpotRange = (float)OptionalFloat(@params, "range", 5.0);
            spot.SpotAttenuation = (float)OptionalFloat(@params, "attenuation", 1.0);
            spot.SpotAngle = (float)OptionalFloat(@params, "spot_angle", 45.0);
            spot.SpotAngleAttenuation = (float)OptionalFloat(@params, "spot_angle_attenuation", 1.0);
        }

        // Apply preset defaults after type creation
        if (!string.IsNullOrEmpty(preset))
        {
            switch (preset)
            {
                case "sun":
                    light.LightEnergy = (float)OptionalFloat(@params, "energy", 1.0);
                    light.ShadowEnabled = OptionalBool(@params, "shadows", true);
                    light.RotationDegrees = ParseVector3Param(@params, "rotation", new Vector3(-45, -30, 0));
                    break;
                case "indoor":
                    light.LightEnergy = (float)OptionalFloat(@params, "energy", 0.8);
                    light.LightColor = ParseColorParam(@params, "color", new Color(1.0f, 0.95f, 0.85f));
                    if (light is OmniLight3D omniPreset)
                        omniPreset.OmniRange = (float)OptionalFloat(@params, "range", 8.0);
                    break;
                case "dramatic":
                    light.LightEnergy = (float)OptionalFloat(@params, "energy", 2.0);
                    light.ShadowEnabled = OptionalBool(@params, "shadows", true);
                    if (light is SpotLight3D spotPreset)
                    {
                        spotPreset.SpotAngle = (float)OptionalFloat(@params, "spot_angle", 25.0);
                        spotPreset.SpotRange = (float)OptionalFloat(@params, "range", 10.0);
                    }
                    break;
            }
        }

        // Position / rotation
        light.Position = ParseVector3Param(@params, "position", Vector3.Zero);
        if (@params.ContainsKey("rotation"))
            light.RotationDegrees = ParseVector3Param(@params, "rotation", light.RotationDegrees);

        AddChildWithUndo(parent, light, root, $"MCP: Add {lightType}");

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(light).ToString(),
            ["name"] = light.Name.ToString(),
            ["light_type"] = lightType,
            ["preset"] = preset,
        }));
    }

    // ── 3. set_material_3d ───────────────────────────────────────────────────

    private Task<Dictionary> SetMaterial3D(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'"));

        if (node is not MeshInstance3D meshInst)
            return Task.FromResult(ErrorInvalidParams($"Node '{nodePath}' is not a MeshInstance3D (is {node.GetClass()})"));

        var surfaceIndex = OptionalInt(@params, "surface_index", 0);

        var mat = new StandardMaterial3D();

        // Albedo
        mat.AlbedoColor = ParseColorParam(@params, "albedo_color", Colors.White);
        if (@params.TryGetValue("albedo_texture", out var albedoTexVal))
        {
            var texPath = albedoTexVal.AsString();
            if (ResourceLoader.Exists(texPath))
                mat.AlbedoTexture = ResourceLoader.Load<Texture2D>(texPath);
        }

        // PBR
        mat.Metallic = (float)OptionalFloat(@params, "metallic", 0.0);
        mat.Roughness = (float)OptionalFloat(@params, "roughness", 1.0);
        if (@params.TryGetValue("metallic_texture", out var metallicTexVal))
        {
            var texPath = metallicTexVal.AsString();
            if (ResourceLoader.Exists(texPath))
                mat.MetallicTexture = ResourceLoader.Load<Texture2D>(texPath);
        }
        if (@params.TryGetValue("roughness_texture", out var roughnessTexVal))
        {
            var texPath = roughnessTexVal.AsString();
            if (ResourceLoader.Exists(texPath))
                mat.RoughnessTexture = ResourceLoader.Load<Texture2D>(texPath);
        }
        if (@params.TryGetValue("normal_texture", out var normalTexVal))
        {
            mat.NormalEnabled = true;
            var texPath = normalTexVal.AsString();
            if (ResourceLoader.Exists(texPath))
                mat.NormalTexture = ResourceLoader.Load<Texture2D>(texPath);
        }

        // Emission
        if (@params.ContainsKey("emission") || @params.ContainsKey("emission_color"))
        {
            mat.EmissionEnabled = true;
            var defaultEmission = ParseColorParam(@params, "emission_color", Colors.Black);
            mat.Emission = ParseColorParam(@params, "emission", defaultEmission);
            mat.EmissionEnergyMultiplier = (float)OptionalFloat(@params, "emission_energy", 1.0);
        }
        if (@params.TryGetValue("emission_texture", out var emissionTexVal))
        {
            mat.EmissionEnabled = true;
            var texPath = emissionTexVal.AsString();
            if (ResourceLoader.Exists(texPath))
                mat.EmissionTexture = ResourceLoader.Load<Texture2D>(texPath);
        }

        // Transparency
        if (@params.TryGetValue("transparency", out var transparencyVal))
        {
            var transparencyStr = transparencyVal.ToString().ToUpperInvariant();
            mat.Transparency = transparencyStr switch
            {
                "DISABLED" or "0" => BaseMaterial3D.TransparencyEnum.Disabled,
                "ALPHA" or "1" => BaseMaterial3D.TransparencyEnum.Alpha,
                "ALPHA_SCISSOR" or "2" => BaseMaterial3D.TransparencyEnum.AlphaScissor,
                "ALPHA_HASH" or "3" => BaseMaterial3D.TransparencyEnum.AlphaHash,
                "ALPHA_DEPTH_PRE_PASS" or "4" => BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass,
                _ => mat.Transparency,
            };
        }

        // Cull mode
        if (@params.TryGetValue("cull_mode", out var cullVal))
        {
            var cullStr = cullVal.ToString().ToUpperInvariant();
            mat.CullMode = cullStr switch
            {
                "BACK" or "0" => BaseMaterial3D.CullModeEnum.Back,
                "FRONT" or "1" => BaseMaterial3D.CullModeEnum.Front,
                "DISABLED" or "2" => BaseMaterial3D.CullModeEnum.Disabled,
                _ => mat.CullMode,
            };
        }

        // Apply
        var oldMat = meshInst.GetSurfaceOverrideMaterial(surfaceIndex);
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Set material on {meshInst.Name}");
        undoRedo.AddDoMethod(meshInst, MeshInstance3D.MethodName.SetSurfaceOverrideMaterial, surfaceIndex, mat);
        undoRedo.AddUndoMethod(meshInst, MeshInstance3D.MethodName.SetSurfaceOverrideMaterial, surfaceIndex, oldMat);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(meshInst).ToString(),
            ["surface_index"] = surfaceIndex,
            ["albedo_color"] = mat.AlbedoColor.ToString(),
            ["metallic"] = mat.Metallic,
            ["roughness"] = mat.Roughness,
        }));
    }

    // ── 4. setup_environment ─────────────────────────────────────────────────

    private Task<Dictionary> SetupEnvironment(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        var nodeName = OptionalString(@params, "name", "WorldEnvironment");

        // Check if a WorldEnvironment already exists at the target
        var nodePath = OptionalString(@params, "node_path", "");
        WorldEnvironment? worldEnv = null;
        var isExisting = false;

        if (!string.IsNullOrEmpty(nodePath))
        {
            var existing = FindNodeByPath(nodePath);
            if (existing is WorldEnvironment existingEnv)
            {
                worldEnv = existingEnv;
                isExisting = true;
            }
        }

        worldEnv ??= new WorldEnvironment { Name = nodeName };

        var env = worldEnv.Environment ?? new Godot.Environment();

        // Background / Sky
        var bgMode = OptionalString(@params, "background_mode", "sky");
        switch (bgMode.ToLowerInvariant())
        {
            case "sky":
                env.BackgroundMode = Godot.Environment.BGMode.Sky;
                break;
            case "color":
                env.BackgroundMode = Godot.Environment.BGMode.Color;
                env.BackgroundColor = ParseColorParam(@params, "background_color", new Color(0.3f, 0.3f, 0.3f));
                break;
            case "canvas":
                env.BackgroundMode = Godot.Environment.BGMode.Canvas;
                break;
            case "clear_color":
                env.BackgroundMode = Godot.Environment.BGMode.ClearColor;
                break;
        }

        // Procedural sky
        if (@params.TryGetValue("sky", out var skyVal) && skyVal.VariantType == Variant.Type.Dictionary)
        {
            var skyParams = skyVal.AsGodotDictionary();
            var skyMat = new ProceduralSkyMaterial
            {
                SkyTopColor = ParseColorParam(skyParams, "sky_top_color", new Color(0.385f, 0.454f, 0.55f)),
                SkyHorizonColor = ParseColorParam(skyParams, "sky_horizon_color", new Color(0.646f, 0.654f, 0.67f)),
                GroundBottomColor = ParseColorParam(skyParams, "ground_bottom_color", new Color(0.2f, 0.169f, 0.133f)),
                GroundHorizonColor = ParseColorParam(skyParams, "ground_horizon_color", new Color(0.646f, 0.654f, 0.67f)),
                SunAngleMax = (float)OptionalFloat(skyParams, "sun_angle_max", 30.0),
                SkyCurve = (float)OptionalFloat(skyParams, "sky_curve", 0.15),
            };

            var sky = new Sky { SkyMaterial = skyMat };
            env.Sky = sky;
            env.BackgroundMode = Godot.Environment.BGMode.Sky;
        }

        // Ambient light
        if (@params.ContainsKey("ambient_light_color"))
            env.AmbientLightColor = ParseColorParam(@params, "ambient_light_color", Colors.White);
        if (@params.ContainsKey("ambient_light_energy"))
            env.AmbientLightEnergy = (float)OptionalFloat(@params, "ambient_light_energy", 1.0);
        if (@params.TryGetValue("ambient_light_source", out var ambientSrcVal))
        {
            var src = ambientSrcVal.ToString().ToUpperInvariant();
            env.AmbientLightSource = src switch
            {
                "BACKGROUND" or "0" => Godot.Environment.AmbientSource.Bg,
                "DISABLED" or "1" => Godot.Environment.AmbientSource.Disabled,
                "COLOR" or "2" => Godot.Environment.AmbientSource.Color,
                "SKY" or "3" => Godot.Environment.AmbientSource.Sky,
                _ => env.AmbientLightSource,
            };
        }

        // Tonemap
        if (@params.TryGetValue("tonemap_mode", out var tonemapVal))
        {
            var tm = tonemapVal.ToString().ToUpperInvariant();
            switch (tm)
            {
                case "LINEAR": case "0":
                    env.TonemapMode = Godot.Environment.ToneMapper.Linear;
                    break;
                case "REINHARDT": case "1":
                    env.TonemapMode = Godot.Environment.ToneMapper.Reinhardt;
                    break;
                case "FILMIC": case "2":
                    env.TonemapMode = Godot.Environment.ToneMapper.Filmic;
                    break;
                case "ACES": case "3":
                    env.TonemapMode = Godot.Environment.ToneMapper.Aces;
                    break;
                case "AGX": case "4":
                    // Godot 4.3 compat: AGX was not yet a named enum member, so the
                    // original GDScript sets it by raw integer value. Keep that
                    // trick here even though 4.4+ exposes Environment.ToneMapper.Agx.
                    env.TonemapMode = (Godot.Environment.ToneMapper)4;
                    break;
            }
        }
        if (@params.ContainsKey("tonemap_exposure"))
            env.TonemapExposure = (float)OptionalFloat(@params, "tonemap_exposure", 1.0);
        if (@params.ContainsKey("tonemap_white"))
            env.TonemapWhite = (float)OptionalFloat(@params, "tonemap_white", 1.0);

        // Fog
        if (@params.ContainsKey("fog_enabled"))
            env.FogEnabled = OptionalBool(@params, "fog_enabled", false);
        if (env.FogEnabled || @params.ContainsKey("fog_light_color"))
        {
            env.FogLightColor = ParseColorParam(@params, "fog_light_color", new Color(0.518f, 0.553f, 0.608f));
            if (@params.ContainsKey("fog_density"))
                env.FogDensity = (float)OptionalFloat(@params, "fog_density", 0.01);
            if (@params.ContainsKey("fog_light_energy"))
                env.FogLightEnergy = (float)OptionalFloat(@params, "fog_light_energy", 1.0);
        }

        // Glow
        if (@params.ContainsKey("glow_enabled"))
            env.GlowEnabled = OptionalBool(@params, "glow_enabled", false);
        if (env.GlowEnabled)
        {
            if (@params.ContainsKey("glow_intensity"))
                env.GlowIntensity = (float)OptionalFloat(@params, "glow_intensity", 0.8);
            if (@params.ContainsKey("glow_strength"))
                env.GlowStrength = (float)OptionalFloat(@params, "glow_strength", 1.0);
            if (@params.ContainsKey("glow_bloom"))
                env.GlowBloom = (float)OptionalFloat(@params, "glow_bloom", 0.0);
        }

        // SSAO
        if (@params.ContainsKey("ssao_enabled"))
            env.SsaoEnabled = OptionalBool(@params, "ssao_enabled", false);
        if (env.SsaoEnabled)
        {
            if (@params.ContainsKey("ssao_radius"))
                env.SsaoRadius = (float)OptionalFloat(@params, "ssao_radius", 1.0);
            if (@params.ContainsKey("ssao_intensity"))
                env.SsaoIntensity = (float)OptionalFloat(@params, "ssao_intensity", 2.0);
        }

        // SSR
        if (@params.ContainsKey("ssr_enabled"))
            env.SsrEnabled = OptionalBool(@params, "ssr_enabled", false);
        if (env.SsrEnabled)
        {
            if (@params.ContainsKey("ssr_max_steps"))
                env.SsrMaxSteps = OptionalInt(@params, "ssr_max_steps", 64);
            if (@params.ContainsKey("ssr_fade_in"))
                env.SsrFadeIn = (float)OptionalFloat(@params, "ssr_fade_in", 0.15);
            if (@params.ContainsKey("ssr_fade_out"))
                env.SsrFadeOut = (float)OptionalFloat(@params, "ssr_fade_out", 2.0);
        }

        // SDFGI
        if (@params.ContainsKey("sdfgi_enabled"))
            env.SdfgiEnabled = OptionalBool(@params, "sdfgi_enabled", false);

        worldEnv.Environment = env;

        if (!isExisting)
            AddChildWithUndo(parent, worldEnv, root, "MCP: Add WorldEnvironment");

        var features = new Godot.Collections.Array();
        if (env.FogEnabled) features.Add("fog");
        if (env.GlowEnabled) features.Add("glow");
        if (env.SsaoEnabled) features.Add("ssao");
        if (env.SsrEnabled) features.Add("ssr");
        if (env.SdfgiEnabled) features.Add("sdfgi");

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(worldEnv).ToString(),
            ["name"] = worldEnv.Name.ToString(),
            ["background_mode"] = bgMode,
            ["features"] = features,
            ["is_existing"] = isExisting,
        }));
    }

    // ── 5. setup_camera_3d ───────────────────────────────────────────────────

    private Task<Dictionary> SetupCamera3D(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        // Check if we're configuring an existing camera
        var nodePath = OptionalString(@params, "node_path", "");
        Camera3D? camera = null;
        var isExisting = false;

        if (!string.IsNullOrEmpty(nodePath))
        {
            var existing = FindNodeByPath(nodePath);
            if (existing is Camera3D existingCamera)
            {
                camera = existingCamera;
                isExisting = true;
            }
            else if (existing != null)
            {
                return Task.FromResult(ErrorInvalidParams($"Node '{nodePath}' is not a Camera3D (is {existing.GetClass()})"));
            }
        }

        camera ??= new Camera3D { Name = OptionalString(@params, "name", "Camera3D") };

        // Projection
        var projectionStr = OptionalString(@params, "projection", "");
        if (!string.IsNullOrEmpty(projectionStr))
        {
            switch (projectionStr.ToLowerInvariant())
            {
                case "perspective": case "0":
                    camera.Projection = Camera3D.ProjectionType.Perspective;
                    break;
                case "orthogonal": case "orthographic": case "1":
                    camera.Projection = Camera3D.ProjectionType.Orthogonal;
                    break;
                case "frustum": case "2":
                    camera.Projection = Camera3D.ProjectionType.Frustum;
                    break;
            }
        }

        // Properties
        if (@params.ContainsKey("fov"))
            camera.Fov = (float)OptionalFloat(@params, "fov", 75.0);
        if (@params.ContainsKey("size"))
            camera.Size = (float)OptionalFloat(@params, "size", 1.0);
        if (@params.ContainsKey("near"))
            camera.Near = (float)OptionalFloat(@params, "near", 0.05);
        if (@params.ContainsKey("far"))
            camera.Far = (float)OptionalFloat(@params, "far", 4000.0);
        if (@params.ContainsKey("cull_mask"))
            camera.CullMask = (uint)OptionalInt(@params, "cull_mask", 1048575);

        // Make current
        camera.Current = OptionalBool(@params, "current", false);

        // Transform
        camera.Position = ParseVector3Param(@params, "position", isExisting ? camera.Position : new Vector3(0, 1, 3));
        if (@params.ContainsKey("rotation"))
            camera.RotationDegrees = ParseVector3Param(@params, "rotation", camera.RotationDegrees);
        if (@params.ContainsKey("look_at"))
        {
            var target = ParseVector3Param(@params, "look_at", Vector3.Zero);
            // We need to set position first, then use look_at
            camera.LookAt(target);
        }

        // Environment override
        if (@params.TryGetValue("environment_path", out var envPathVal))
        {
            var envPath = envPathVal.AsString();
            if (ResourceLoader.Exists(envPath))
            {
                var envRes = ResourceLoader.Load(envPath);
                if (envRes is Godot.Environment environmentRes)
                    camera.Environment = environmentRes;
            }
        }

        if (!isExisting)
            AddChildWithUndo(parent, camera, root, "MCP: Add Camera3D");

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(camera).ToString(),
            ["name"] = camera.Name.ToString(),
            ["projection"] = camera.Projection == Camera3D.ProjectionType.Perspective ? "perspective" : "orthogonal",
            ["fov"] = camera.Fov,
            ["position"] = camera.Position.ToString(),
            ["is_existing"] = isExisting,
        }));
    }

    // ── 6. add_gridmap ───────────────────────────────────────────────────────

    private Task<Dictionary> AddGridmap(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        var nodeName = OptionalString(@params, "name", "GridMap");

        // Check for existing GridMap to configure
        var nodePath = OptionalString(@params, "node_path", "");
        GridMap? gridmap = null;
        var isExisting = false;

        if (!string.IsNullOrEmpty(nodePath))
        {
            var existing = FindNodeByPath(nodePath);
            if (existing is GridMap existingGridmap)
            {
                gridmap = existingGridmap;
                isExisting = true;
            }
            else if (existing != null)
            {
                return Task.FromResult(ErrorInvalidParams($"Node '{nodePath}' is not a GridMap (is {existing.GetClass()})"));
            }
        }

        gridmap ??= new GridMap { Name = nodeName };

        // Mesh library
        if (@params.TryGetValue("mesh_library_path", out var libPathVal))
        {
            var libPath = libPathVal.AsString();
            if (!ResourceLoader.Exists(libPath))
            {
                if (!isExisting)
                    gridmap.QueueFree();
                return Task.FromResult(ErrorNotFound($"MeshLibrary '{libPath}'", "Provide a valid res:// path to a .meshlib or .tres file"));
            }
            var lib = ResourceLoader.Load(libPath);
            if (lib is MeshLibrary meshLibrary)
            {
                gridmap.MeshLibrary = meshLibrary;
            }
            else
            {
                if (!isExisting)
                    gridmap.QueueFree();
                return Task.FromResult(ErrorInvalidParams($"'{libPath}' is not a MeshLibrary"));
            }
        }

        // Cell size
        if (@params.ContainsKey("cell_size"))
            gridmap.CellSize = ParseVector3Param(@params, "cell_size", new Vector3(2, 2, 2));

        // Position
        gridmap.Position = ParseVector3Param(@params, "position", isExisting ? gridmap.Position : Vector3.Zero);

        if (!isExisting)
            AddChildWithUndo(parent, gridmap, root, "MCP: Add GridMap");

        // Set cells
        var cells = OptionalArray(@params, "cells") ?? new Godot.Collections.Array();
        var cellsSet = 0;
        foreach (var cell in cells)
        {
            if (cell.VariantType != Variant.Type.Dictionary)
                continue;
            var cellDict = cell.AsGodotDictionary();
            var x = (int)GetNum(cellDict, "x", 0);
            var y = (int)GetNum(cellDict, "y", 0);
            var z = (int)GetNum(cellDict, "z", 0);
            var item = (int)GetNum(cellDict, "item", 0);
            var orientation = (int)GetNum(cellDict, "orientation", 0);
            gridmap.SetCellItem(new Vector3I(x, y, z), item, orientation);
            cellsSet++;
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(gridmap).ToString(),
            ["name"] = gridmap.Name.ToString(),
            ["cells_set"] = cellsSet,
            ["is_existing"] = isExisting,
            ["has_mesh_library"] = gridmap.MeshLibrary != null,
        }));
    }
}
