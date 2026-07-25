using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Advanced node setup tools: AnimatedSprite2D with SpriteFrames, VehicleBody3D
/// with VehicleWheel3D children, runtime navigation-path and physics-point
/// queries (via file IPC to MCPGameInspector), and Sky configuration on a
/// WorldEnvironment. Scene mutations go through EditorUndoRedoManager.
/// </summary>
[Tool]
public partial class AdvancedNodesCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "setup_animated_sprite",
            Description = "Create an AnimatedSprite2D node with a SpriteFrames resource populated from " +
                "animation definitions (name, fps, loop, frames of texture_path+duration). Undo-tracked.",
            Category = "advanced_nodes",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new AnimatedSprite2D node", "AnimatedSprite2D"),
                ["animations"] = Schema.Arr(
                    "Animation definitions: [{name, fps, loop, frames:[{texture_path, duration}]}].",
                    Schema.Obj("Animation definition.")),
                ["autoplay"] = Schema.Str("Animation name to autoplay on scene start"),
            }, "animations"),
            Handler = SetupAnimatedSprite,
        },
        new()
        {
            Name = "setup_vehicle",
            Description = "Create a VehicleBody3D with VehicleWheel3D children. Each wheel has an x/z " +
                "offset, radius, suspension tuning, and steering/traction/brake flags. Undo-tracked.",
            Category = "advanced_nodes",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new VehicleBody3D node", "VehicleBody3D"),
                ["engine_force"] = Schema.Num("Vehicle engine_force", 0.0),
                ["brake"] = Schema.Num("Vehicle brake", 0.0),
                ["steering"] = Schema.Num("Vehicle steering (radians)", 0.0),
                ["mass"] = Schema.Num("RigidBody mass", 40.0),
                ["wheels"] = Schema.Arr(
                    "Wheel definitions: [{x_offset, z_offset, radius, suspension_travel, " +
                    "suspension_stiffness, damping_compression, damping_relaxation, steering, " +
                    "use_steering, use_engine, use_brake}].",
                    Schema.Obj("Wheel definition.")),
            }),
            Handler = SetupVehicle,
        },
        new()
        {
            Name = "query_navigation_path",
            Description = "Query a navigation path between two positions in the running game. " +
                "Requires play_scene; the MCPGameInspector autoload resolves the path against the " +
                "active NavigationServer map. Returns path points, total distance, and is_empty.",
            Category = "advanced_nodes",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["start"] = Schema.Obj("Start position as {x, y, z} (or {x, y} for 2D). Mutually exclusive with start_node_path."),
                ["start_node_path"] = Schema.Str("Path to a node whose global position is the start."),
                ["end"] = Schema.Obj("End position as {x, y, z} (or {x, y} for 2D). Mutually exclusive with end_node_path."),
                ["end_node_path"] = Schema.Str("Path to a node whose global position is the end."),
                ["agent_rid"] = Schema.Str("Optional NavigationAgent RID to query with a specific navigation map."),
            }),
            Handler = QueryNavigationPath,
        },
        new()
        {
            Name = "physics_query_point",
            Description = "Run a physics intersection query at a point in the running game. " +
                "Requires play_scene. Supports sphere/box shapes (with radius or size) or a plain " +
                "point query. Returns colliders [{node_path, type, position}].",
            Category = "advanced_nodes",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["point"] = Schema.Obj("Query point as {x, y, z} (3D) or {x, y} (2D)."),
                ["shape"] = Schema.Enum("Query shape", "sphere", "box", "point"),
                ["radius"] = Schema.Num("Sphere radius (shape=sphere)"),
                ["size"] = Schema.Num("Box half-extent (shape=box); scalar applied to all axes"),
                ["collision_mask"] = Schema.Int("Collision layer bitmask to test against", 1),
            }, "point"),
            Handler = PhysicsQueryPoint,
        },
        new()
        {
            Name = "setup_sky",
            Description = "Create or configure a Sky resource on a WorldEnvironment. Supports " +
                "procedural (ProceduralSkyMaterial) and panorama (PanoramaSkyMaterial) sky types, " +
                "with sun, color, curve, and radiance_size overrides. Undo-tracked.",
            Category = "advanced_nodes",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of an existing WorldEnvironment to configure"),
                ["parent_path"] = Schema.Str("Scene-relative parent for a new WorldEnvironment (used when node_path is empty)", "."),
                ["name"] = Schema.Str("Name for a new WorldEnvironment node", "WorldEnvironment"),
                ["sky_type"] = Schema.Enum("Sky material type", "procedural", "panorama"),
                ["panorama_path"] = Schema.Str("res:// path to a panorama texture (sky_type=panorama)"),
                ["sun_latitude"] = Schema.Num("Sun latitude in degrees (applied when supported by the sky material)"),
                ["sun_longitude"] = Schema.Num("Sun longitude in degrees (applied when supported by the sky material)"),
                ["sky_top_color"] = Schema.Any("Sky top color as Color(...) string, #hex, or {r,g,b,a} dict"),
                ["sky_horizon_color"] = Schema.Any("Sky horizon color"),
                ["sky_curve"] = Schema.Num("Sky curve (procedural)", 0.15),
                ["ground_bottom_color"] = Schema.Any("Ground bottom color"),
                ["ground_horizon_color"] = Schema.Any("Ground horizon color"),
                ["ground_curve"] = Schema.Num("Ground curve (procedural)", 0.02),
                ["radiance_size"] = Schema.Int("Sky radiance size: 32/64/128/256/512/1024/2048 or raw enum 0-6", 256),
            }),
            Handler = SetupSky,
        },
    };

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool HasProperty(GodotObject obj, string propertyName)
    {
        foreach (var prop in obj.GetPropertyList())
        {
            if (prop["name"].AsString() == propertyName)
                return true;
        }
        return false;
    }

    private static Color ParseColorParam(Dictionary @params, string key, Color @default)
    {
        if (!@params.TryGetValue(key, out var val))
            return @default;
        if (val.VariantType == Variant.Type.Color)
            return val.AsColor();
        if (val.VariantType == Variant.Type.String)
        {
            var s = val.AsString();
            if (Color.HtmlIsValid(s))
                return Color.FromHtml(s);
            return Utils.PropertyParser.ParseValue(val, Variant.Type.Color).AsColor();
        }
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

    // ── 1. setup_animated_sprite ───────────────────────────────────────────────

    private Task<Dictionary> SetupAnimatedSprite(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'",
                "Use get_scene_tree to see available nodes"));

        var animations = OptionalArray(@params, "animations");
        if (animations == null || animations.Count == 0)
            return Task.FromResult(ErrorInvalidParams(
                "Missing required parameter: animations (must be a non-empty array)"));

        var nodeName = OptionalString(@params, "name", "AnimatedSprite2D");
        var sprite = new AnimatedSprite2D { Name = nodeName };
        var frames = new SpriteFrames();

        // Strip the auto-added "default" animation so we control the full list.
        if (frames.HasAnimation("default"))
            frames.RemoveAnimation("default");

        var animSummary = new Godot.Collections.Array();
        foreach (var animVar in animations)
        {
            if (animVar.VariantType != Variant.Type.Dictionary)
                continue;
            var animDef = animVar.AsGodotDictionary();
            var animName = OptionalString(animDef, "name", "");
            if (string.IsNullOrEmpty(animName))
                return Task.FromResult(ErrorInvalidParams("Each animation requires a non-empty 'name'"));

            if (!frames.HasAnimation(animName))
                frames.AddAnimation(animName);
            frames.SetAnimationSpeed(animName, OptionalFloat(animDef, "fps", 5.0));
            frames.SetAnimationLoopMode(animName,
                OptionalBool(animDef, "loop", true) ? SpriteFrames.LoopMode.Linear : SpriteFrames.LoopMode.None);

            var frameCount = 0;
            var frameList = OptionalArray(animDef, "frames");
            if (frameList != null)
            {
                foreach (var frameVar in frameList)
                {
                    if (frameVar.VariantType != Variant.Type.Dictionary)
                        continue;
                    var frameDef = frameVar.AsGodotDictionary();
                    var texPath = OptionalString(frameDef, "texture_path", "");
                    if (string.IsNullOrEmpty(texPath))
                        continue;
                    var duration = (float)OptionalFloat(frameDef, "duration", 1.0);
                    var tex = ResourceLoader.Load<Texture2D>(texPath);
                    if (tex == null)
                        return Task.FromResult(ErrorNotFound($"Texture '{texPath}'",
                            "Verify the path with search_files (it must be an imported Texture2D resource)"));
                    frames.AddFrame(animName, tex, duration);
                    frameCount++;
                }
            }
            animSummary.Add(new Dictionary
            {
                ["name"] = animName,
                ["fps"] = frames.GetAnimationSpeed(animName),
                ["loop"] = frames.GetAnimationLoopMode(animName) != SpriteFrames.LoopMode.None,
                ["frame_count"] = frameCount,
            });
        }

        if (animSummary.Count == 0)
            return Task.FromResult(ErrorInvalidParams("No valid animations were defined"));

        sprite.SpriteFrames = frames;

        var autoplay = OptionalString(@params, "autoplay", "");
        if (!string.IsNullOrEmpty(autoplay))
        {
            if (!frames.HasAnimation(autoplay))
                return Task.FromResult(ErrorInvalidParams(
                    $"autoplay '{autoplay}' does not match any defined animation"));
            sprite.Autoplay = autoplay;
        }

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Add AnimatedSprite2D '{nodeName}' to {parent.Name}");
        undoRedo.AddDoMethod(parent, Node.MethodName.AddChild, sprite);
        undoRedo.AddDoMethod(sprite, Node.MethodName.SetOwner, root);
        undoRedo.AddDoReference(sprite);
        undoRedo.AddDoReference(frames);
        undoRedo.AddUndoMethod(parent, Node.MethodName.RemoveChild, sprite);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(sprite).ToString(),
            ["type"] = "AnimatedSprite2D",
            ["animations"] = animSummary,
            ["autoplay"] = autoplay,
        }));
    }

    // ── 2. setup_vehicle ───────────────────────────────────────────────────────

    private Task<Dictionary> SetupVehicle(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'",
                "Use get_scene_tree to see available nodes"));

        var nodeName = OptionalString(@params, "name", "VehicleBody3D");
        var vehicle = new VehicleBody3D
        {
            Name = nodeName,
            EngineForce = (float)OptionalFloat(@params, "engine_force", 0.0),
            Brake = (float)OptionalFloat(@params, "brake", 0.0),
            Steering = (float)OptionalFloat(@params, "steering", 0.0),
            Mass = (float)OptionalFloat(@params, "mass", 40.0),
        };

        var wheelsData = OptionalArray(@params, "wheels") ?? new Godot.Collections.Array();
        var wheels = new List<VehicleWheel3D>();
        var wheelSummaries = new Godot.Collections.Array();

        foreach (var wheelVar in wheelsData)
        {
            if (wheelVar.VariantType != Variant.Type.Dictionary)
                continue;
            var w = wheelVar.AsGodotDictionary();

            var wheelIndex = wheels.Count;
            var wheel = new VehicleWheel3D
            {
                Name = $"Wheel{wheelIndex + 1}",
                WheelRadius = (float)OptionalFloat(w, "radius", 0.4),
                SuspensionTravel = (float)OptionalFloat(w, "suspension_travel", 0.2),
                SuspensionStiffness = (float)OptionalFloat(w, "suspension_stiffness", 5.88),
                DampingCompression = (float)OptionalFloat(w, "damping_compression", 0.83),
                DampingRelaxation = (float)OptionalFloat(w, "damping_relaxation", 0.88),
                Steering = (float)OptionalFloat(w, "steering", 0.0),
                UseAsSteering = OptionalBool(w, "use_steering", false),
                UseAsTraction = OptionalBool(w, "use_engine", false),
            };

            var xOff = (float)OptionalFloat(w, "x_offset", 0.0);
            var zOff = (float)OptionalFloat(w, "z_offset", 0.0);
            wheel.Position = new Vector3(xOff, 0f, zOff);

            wheels.Add(wheel);
            wheelSummaries.Add(new Dictionary
            {
                ["name"] = wheel.Name.ToString(),
                ["x_offset"] = xOff,
                ["z_offset"] = zOff,
                ["radius"] = wheel.WheelRadius,
                ["use_steering"] = wheel.UseAsSteering,
                ["use_engine"] = wheel.UseAsTraction,
                ["use_brake"] = OptionalBool(w, "use_brake", true),
            });
        }

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Add VehicleBody3D '{nodeName}' to {parent.Name}");
        undoRedo.AddDoMethod(parent, Node.MethodName.AddChild, vehicle);
        undoRedo.AddDoMethod(vehicle, Node.MethodName.SetOwner, root);
        foreach (var wheel in wheels)
        {
            undoRedo.AddDoMethod(vehicle, Node.MethodName.AddChild, wheel);
            undoRedo.AddDoMethod(wheel, Node.MethodName.SetOwner, root);
        }
        undoRedo.AddDoReference(vehicle);
        foreach (var wheel in wheels)
            undoRedo.AddDoReference(wheel);
        for (var i = wheels.Count - 1; i >= 0; i--)
            undoRedo.AddUndoMethod(vehicle, Node.MethodName.RemoveChild, wheels[i]);
        undoRedo.AddUndoMethod(parent, Node.MethodName.RemoveChild, vehicle);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(vehicle).ToString(),
            ["type"] = "VehicleBody3D",
            ["engine_force"] = vehicle.EngineForce,
            ["brake"] = vehicle.Brake,
            ["steering"] = vehicle.Steering,
            ["mass"] = vehicle.Mass,
            ["wheels"] = wheelSummaries,
            ["wheel_count"] = wheels.Count,
        }));
    }

    // ── 3. query_navigation_path (runtime game IPC) ────────────────────────────

    private async Task<Dictionary> QueryNavigationPath(Dictionary @params)
    {
        var cmdParams = new Dictionary();

        var hasStart = false;
        if (@params.TryGetValue("start", out var startVal) && startVal.VariantType != Variant.Type.Nil)
        {
            cmdParams["start"] = startVal;
            hasStart = true;
        }
        else if (@params.TryGetValue("start_node_path", out var startPathVal)
                 && startPathVal.VariantType == Variant.Type.String
                 && !string.IsNullOrEmpty(startPathVal.AsString()))
        {
            cmdParams["start_node_path"] = startPathVal.AsString();
            hasStart = true;
        }
        if (!hasStart)
            return ErrorInvalidParams("Missing required parameter: start or start_node_path");

        var hasEnd = false;
        if (@params.TryGetValue("end", out var endVal) && endVal.VariantType != Variant.Type.Nil)
        {
            cmdParams["end"] = endVal;
            hasEnd = true;
        }
        else if (@params.TryGetValue("end_node_path", out var endPathVal)
                 && endPathVal.VariantType == Variant.Type.String
                 && !string.IsNullOrEmpty(endPathVal.AsString()))
        {
            cmdParams["end_node_path"] = endPathVal.AsString();
            hasEnd = true;
        }
        if (!hasEnd)
            return ErrorInvalidParams("Missing required parameter: end or end_node_path");

        var agentRid = OptionalString(@params, "agent_rid", "");
        if (!string.IsNullOrEmpty(agentRid))
            cmdParams["agent_rid"] = agentRid;

        var result = await SendGameCommand("query_navigation_path", cmdParams);
        return IsError(result) ? result : Success(UnwrapGameResult(result));
    }

    // ── 4. physics_query_point (runtime game IPC) ──────────────────────────────

    private async Task<Dictionary> PhysicsQueryPoint(Dictionary @params)
    {
        if (!@params.TryGetValue("point", out var pointVal) || pointVal.VariantType == Variant.Type.Nil)
            return ErrorInvalidParams("Missing required parameter: point");

        var cmdParams = new Dictionary
        {
            ["point"] = pointVal,
            ["shape"] = OptionalString(@params, "shape", "point"),
            ["collision_mask"] = OptionalInt(@params, "collision_mask", 1),
        };

        if (@params.TryGetValue("radius", out var radiusVal) && radiusVal.VariantType != Variant.Type.Nil)
            cmdParams["radius"] = radiusVal;
        if (@params.TryGetValue("size", out var sizeVal) && sizeVal.VariantType != Variant.Type.Nil)
            cmdParams["size"] = sizeVal;

        var result = await SendGameCommand("physics_query_point", cmdParams);
        return IsError(result) ? result : Success(UnwrapGameResult(result));
    }

    // ── 5. setup_sky ───────────────────────────────────────────────────────────

    private Task<Dictionary> SetupSky(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var nodePath = OptionalString(@params, "node_path", "");
        WorldEnvironment? worldEnv = null;
        Node? targetParent = null;
        var isExisting = false;

        if (!string.IsNullOrEmpty(nodePath))
        {
            var existing = FindNodeByPath(nodePath);
            if (existing == null)
                return Task.FromResult(ErrorNotFound($"Node '{nodePath}'",
                    "Use get_scene_tree to see available nodes"));
            if (existing is not WorldEnvironment we)
                return Task.FromResult(ErrorInvalidParams(
                    $"Node '{nodePath}' is not a WorldEnvironment (is {existing.GetClass()})"));
            worldEnv = we;
            isExisting = true;
        }
        else
        {
            var parentPath = OptionalString(@params, "parent_path", ".");
            targetParent = FindNodeByPath(parentPath);
            if (targetParent == null)
                return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'",
                    "Use get_scene_tree to see available nodes"));

            // Reuse the first WorldEnvironment child of the parent if one exists.
            foreach (var child in targetParent.GetChildren())
            {
                if (child is WorldEnvironment existingWe)
                {
                    worldEnv = existingWe;
                    isExisting = true;
                    break;
                }
            }

            worldEnv ??= new WorldEnvironment { Name = OptionalString(@params, "name", "WorldEnvironment") };
        }

        var skyType = OptionalString(@params, "sky_type", "procedural").ToLowerInvariant();
        var env = worldEnv.Environment ?? new Godot.Environment();
        var sky = env.Sky ?? new Sky();

        switch (skyType)
        {
            case "procedural":
            {
                var skyMat = new ProceduralSkyMaterial();
                if (@params.ContainsKey("sky_top_color"))
                    skyMat.SkyTopColor = ParseColorParam(@params, "sky_top_color", skyMat.SkyTopColor);
                if (@params.ContainsKey("sky_horizon_color"))
                    skyMat.SkyHorizonColor = ParseColorParam(@params, "sky_horizon_color", skyMat.SkyHorizonColor);
                if (@params.ContainsKey("sky_curve"))
                    skyMat.SkyCurve = (float)OptionalFloat(@params, "sky_curve", 0.15);
                if (@params.ContainsKey("ground_bottom_color"))
                    skyMat.GroundBottomColor = ParseColorParam(@params, "ground_bottom_color", skyMat.GroundBottomColor);
                if (@params.ContainsKey("ground_horizon_color"))
                    skyMat.GroundHorizonColor = ParseColorParam(@params, "ground_horizon_color", skyMat.GroundHorizonColor);
                if (@params.ContainsKey("ground_curve"))
                    skyMat.GroundCurve = (float)OptionalFloat(@params, "ground_curve", 0.02);

                // ProceduralSkyMaterial does not expose sun_latitude/sun_longitude in
                // Godot 4.7 — apply only when a future engine version adds them.
                if (@params.ContainsKey("sun_latitude") && HasProperty(skyMat, "sun_latitude"))
                    skyMat.Set("sun_latitude", OptionalFloat(@params, "sun_latitude", 0.0));
                if (@params.ContainsKey("sun_longitude") && HasProperty(skyMat, "sun_longitude"))
                    skyMat.Set("sun_longitude", OptionalFloat(@params, "sun_longitude", 0.0));

                sky.SkyMaterial = skyMat;
                break;
            }
            case "panorama":
            {
                var skyMat = new PanoramaSkyMaterial();
                var panoramaPath = OptionalString(@params, "panorama_path", "");
                if (!string.IsNullOrEmpty(panoramaPath))
                {
                    var tex = ResourceLoader.Load<Texture2D>(panoramaPath);
                    if (tex == null)
                        return Task.FromResult(ErrorNotFound($"Panorama texture '{panoramaPath}'",
                            "Verify the path with search_files (it must be an imported Texture2D resource)"));
                    skyMat.Panorama = tex;
                }
                sky.SkyMaterial = skyMat;
                break;
            }
            default:
                return Task.FromResult(ErrorInvalidParams(
                    $"Unknown sky_type: '{skyType}'. Supported: procedural, panorama"));
        }

        // Radiance size: accept either raw enum (0-6) or pixel size (32-2048).
        if (@params.ContainsKey("radiance_size"))
        {
            var sizeVal = OptionalInt(@params, "radiance_size", 256);
            var enumIdx = sizeVal switch
            {
                32 => 0,
                64 => 1,
                128 => 2,
                256 => 3,
                512 => 4,
                1024 => 5,
                2048 => 6,
                >= 0 and <= 6 => sizeVal,
                _ => 3,
            };
            sky.RadianceSize = (Sky.RadianceSizeEnum)enumIdx;
        }

        env.Sky = sky;
        env.BackgroundMode = Godot.Environment.BGMode.Sky;
        worldEnv.Environment = env;

        if (!isExisting)
        {
            var parent = targetParent ?? root;
            AddChildWithUndo(parent, worldEnv, root, $"MCP: Add WorldEnvironment '{worldEnv.Name}' with Sky");
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(worldEnv).ToString(),
            ["name"] = worldEnv.Name.ToString(),
            ["sky_type"] = skyType,
            ["background_mode"] = "sky",
            ["is_existing"] = isExisting,
        }));
    }
}
