using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Physics setup tools: collision shapes, physics layers/masks, raycasts, physics
/// body configuration, and collision introspection. Faithful port of
/// addons/godot_mcp/commands/physics_commands.gd. All scene mutations go through
/// EditorUndoRedoManager so they participate in the editor's undo history.
/// </summary>
[Tool]
public partial class PhysicsCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "setup_collision",
            Description = "Add a CollisionShape2D/3D child with a generated shape resource to a physics body or area. " +
                "Dimension is auto-detected from the node's ancestry (Node2D/Control vs Node3D) unless overridden.",
            Category = "physics",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the physics body or area node"),
                ["shape"] = Schema.Str("Shape name: 2D = rectangle/rect, circle, capsule, segment, custom; " +
                    "3D = box/rectangle/rect, sphere/circle, capsule, cylinder, convex/custom"),
                ["dimension"] = Schema.Enum("Force '2d' or '3d' when auto-detection fails (defaults to 2d)", "2d", "3d"),
                ["width"] = Schema.Num("2D rectangle width or 3D box width (default 32 / 1.0)"),
                ["height"] = Schema.Num("Rectangle/box height, or capsule/cylinder height"),
                ["depth"] = Schema.Num("3D box depth (default 1.0)"),
                ["radius"] = Schema.Num("Circle/sphere/capsule/cylinder radius"),
                ["ax"] = Schema.Num("2D segment point A x (default 0.0)"),
                ["ay"] = Schema.Num("2D segment point A y (default 0.0)"),
                ["bx"] = Schema.Num("2D segment point B x (default 32.0)"),
                ["by"] = Schema.Num("2D segment point B y (default 0.0)"),
                ["points"] = Schema.Arr("Array of [x,y] (2D) or [x,y,z] (3D) points for custom/convex shapes"),
                ["disabled"] = Schema.Bool("Create the shape disabled", false),
                ["one_way_collision"] = Schema.Bool("2D only: enable one-way collision", false),
            }, "node_path", "shape"),
            Handler = SetupCollision,
        },
        new()
        {
            Name = "set_physics_layers",
            Description = "Set collision_layer and/or collision_mask on a physics node. Accepts an int bitmask " +
                "or an array of 1-based layer numbers for either property.",
            Category = "physics",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the physics node"),
                ["collision_layer"] = Schema.Any("New collision_layer: int bitmask or array of layer numbers (1-32)"),
                ["collision_mask"] = Schema.Any("New collision_mask: int bitmask or array of layer numbers (1-32)"),
            }, "node_path"),
            Handler = SetPhysicsLayers,
        },
        new()
        {
            Name = "get_physics_layers",
            Description = "Get the collision_layer and collision_mask of a physics node, with named-layer info from ProjectSettings.",
            Category = "physics",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the physics node"),
            }, "node_path"),
            Handler = GetPhysicsLayers,
        },
        new()
        {
            Name = "add_raycast",
            Description = "Add a RayCast2D/3D child to a node. Dimension is auto-detected from the node's ancestry unless overridden.",
            Category = "physics",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the parent node"),
                ["dimension"] = Schema.Enum("Force '2d' or '3d' when auto-detection fails (defaults to 2d)", "2d", "3d"),
                ["name"] = Schema.Str("Name for the new RayCast node", "RayCast"),
                ["enabled"] = Schema.Bool("Whether the raycast is enabled", true),
                ["collision_mask"] = Schema.Int("Collision mask bitmask", 1),
                ["collide_with_areas"] = Schema.Bool("Detect Area2D/3D nodes", false),
                ["collide_with_bodies"] = Schema.Bool("Detect PhysicsBody2D/3D nodes", true),
                ["hit_from_inside"] = Schema.Bool("Detect collisions when the ray starts inside a shape", false),
                ["target_x"] = Schema.Num("target_position.x (default 0.0)"),
                ["target_y"] = Schema.Num("target_position.y (default 50.0 for 2D, -1.0 for 3D)"),
                ["target_z"] = Schema.Num("target_position.z, 3D only (default 0.0)"),
            }, "node_path"),
            Handler = AddRaycast,
        },
        new()
        {
            Name = "setup_physics_body",
            Description = "Apply CharacterBody or RigidBody specific properties to an existing physics body node " +
                "(CharacterBody2D/3D, RigidBody2D/3D, StaticBody2D/3D, AnimatableBody2D/3D).",
            Category = "physics",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the physics body node"),
                ["floor_stop_on_slope"] = Schema.Bool("CharacterBody: stop on slopes"),
                ["floor_max_angle"] = Schema.Num("CharacterBody: max floor angle (radians)"),
                ["floor_snap_length"] = Schema.Num("CharacterBody: floor snap length"),
                ["wall_min_slide_angle"] = Schema.Num("CharacterBody: min wall slide angle (radians)"),
                ["motion_mode"] = Schema.Any("CharacterBody: 'grounded'/'floating' or raw MOTION_MODE int"),
                ["max_slides"] = Schema.Int("CharacterBody: max slide collisions"),
                ["slide_on_ceiling"] = Schema.Bool("CharacterBody: slide on ceiling"),
                ["mass"] = Schema.Num("RigidBody: mass"),
                ["gravity_scale"] = Schema.Num("RigidBody: gravity scale"),
                ["linear_damp"] = Schema.Num("RigidBody: linear damp"),
                ["angular_damp"] = Schema.Num("RigidBody: angular damp"),
                ["freeze"] = Schema.Bool("RigidBody: freeze the body"),
                ["freeze_mode"] = Schema.Any("RigidBody: 'static'/'kinematic' or raw FREEZE_MODE int"),
                ["continuous_cd"] = Schema.Any("RigidBody2D: 'disabled'/'cast_ray'/'cast_shape' or int; RigidBody3D: bool"),
                ["contact_monitor"] = Schema.Bool("RigidBody: enable contact monitoring"),
                ["max_contacts_reported"] = Schema.Int("RigidBody: max reported contacts"),
                ["physics_material_override"] = Schema.Any("Not supported here — use add_resource instead"),
            }, "node_path"),
            Handler = SetupPhysicsBody,
        },
        new()
        {
            Name = "get_collision_info",
            Description = "Get collision layer/mask, body-specific settings, and all collision shapes/raycasts " +
                "on a node and (optionally) its descendants.",
            Category = "physics",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node"),
                ["include_children"] = Schema.Bool("Also collect collision info from descendant nodes", true),
            }, "node_path"),
            Handler = GetCollisionInfo,
        },
    };

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool NodeHasProperty(GodotObject obj, string propertyName)
    {
        foreach (var prop in obj.GetPropertyList())
        {
            if (prop["name"].AsString() == propertyName)
                return true;
        }
        return false;
    }

    /// <summary>Determine if a node (or its ancestors) lives in a 2D or 3D context. Returns "2d", "3d", or "".</summary>
    private static string DetectDimension(Node node)
    {
        if (node is Node2D or Control)
            return "2d";
        if (node is Node3D)
            return "3d";
        var parent = node.GetParent();
        while (parent != null)
        {
            if (parent is Node2D or Control)
                return "2d";
            if (parent is Node3D)
                return "3d";
            parent = parent.GetParent();
        }
        return "";
    }

    private static string GetLayerName(string dim, int layerIndex)
    {
        var settingKey = $"layer_names/{dim}_physics/layer_{layerIndex}";
        if (ProjectSettings.HasSetting(settingKey))
        {
            var nameVal = ProjectSettings.GetSetting(settingKey);
            if (nameVal.VariantType == Variant.Type.String && !string.IsNullOrEmpty(nameVal.AsString()))
                return nameVal.AsString();
        }
        return "";
    }

    private static Godot.Collections.Array LayerBitmaskToInfo(int bitmask, string dim)
    {
        var layers = new Godot.Collections.Array();
        for (var i = 1; i <= 32; i++)
        {
            if ((bitmask & (1 << (i - 1))) != 0)
            {
                var layerName = GetLayerName(dim, i);
                var entry = new Dictionary { ["layer"] = i };
                if (!string.IsNullOrEmpty(layerName))
                    entry["name"] = layerName;
                layers.Add(entry);
            }
        }
        return layers;
    }

    /// <summary>Parse layer value: an int bitmask, or an array of 1-based layer numbers [1, 3, 5].</summary>
    private static int ParseLayerValue(Variant value)
    {
        if (value.VariantType is Variant.Type.Int or Variant.Type.Float)
            return (int)value.AsDouble();
        if (value.VariantType == Variant.Type.Array)
        {
            var bitmask = 0;
            foreach (var layerNum in value.AsGodotArray())
            {
                var n = (int)layerNum.AsDouble();
                if (n is >= 1 and <= 32)
                    bitmask |= 1 << (n - 1);
            }
            return bitmask;
        }
        if (value.VariantType == Variant.Type.String)
            return (int)value.AsString().ToFloat();
        return (int)value.AsDouble();
    }

    // ── Tool handlers ────────────────────────────────────────────────────────────

    private Task<Dictionary> SetupCollision(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var (shapeName, err2) = RequireString(@params, "shape");
        if (err2 != null) return Task.FromResult(err2);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        var dim = DetectDimension(node);
        if (string.IsNullOrEmpty(dim))
            dim = OptionalString(@params, "dimension", "2d");

        var validParents2D = new[] { "PhysicsBody2D", "Area2D", "StaticBody2D", "CharacterBody2D", "RigidBody2D", "AnimatableBody2D" };
        var validParents3D = new[] { "PhysicsBody3D", "Area3D", "StaticBody3D", "CharacterBody3D", "RigidBody3D", "AnimatableBody3D" };

        var isValidParent = false;
        foreach (var vp in dim == "2d" ? validParents2D : validParents3D)
        {
            if (node.IsClass(vp))
            {
                isValidParent = true;
                break;
            }
        }

        if (!isValidParent)
            return Task.FromResult(ErrorInvalidParams(
                $"Node '{nodePath}' ({node.GetClass()}) is not a physics body or area. CollisionShape should be added to a PhysicsBody or Area node."));

        const string childName = "CollisionShape";

        if (dim == "2d")
        {
            Shape2D shape;
            switch (shapeName)
            {
                case "rectangle":
                case "rect":
                {
                    var w = (float)OptionalFloat(@params, "width", 32.0);
                    var h = (float)OptionalFloat(@params, "height", 32.0);
                    shape = new RectangleShape2D { Size = new Vector2(w, h) };
                    break;
                }
                case "circle":
                    shape = new CircleShape2D { Radius = (float)OptionalFloat(@params, "radius", 16.0) };
                    break;
                case "capsule":
                    shape = new CapsuleShape2D
                    {
                        Radius = (float)OptionalFloat(@params, "radius", 16.0),
                        Height = (float)OptionalFloat(@params, "height", 40.0),
                    };
                    break;
                case "segment":
                    shape = new SegmentShape2D
                    {
                        A = new Vector2((float)OptionalFloat(@params, "ax", 0.0), (float)OptionalFloat(@params, "ay", 0.0)),
                        B = new Vector2((float)OptionalFloat(@params, "bx", 32.0), (float)OptionalFloat(@params, "by", 0.0)),
                    };
                    break;
                case "custom":
                {
                    var convex = new ConvexPolygonShape2D();
                    var pointsData = OptionalArray(@params, "points") ?? new Godot.Collections.Array();
                    var pool = new Godot.Collections.Array<Vector2>();
                    foreach (var p in pointsData)
                    {
                        if (p.VariantType == Variant.Type.Array)
                        {
                            var pArr = p.AsGodotArray();
                            if (pArr.Count >= 2)
                                pool.Add(new Vector2((float)pArr[0].AsDouble(), (float)pArr[1].AsDouble()));
                        }
                    }
                    if (pool.Count >= 3)
                    {
                        var packed = new Vector2[pool.Count];
                        for (var i = 0; i < pool.Count; i++) packed[i] = pool[i];
                        convex.Points = packed;
                    }
                    shape = convex;
                    break;
                }
                default:
                    return Task.FromResult(ErrorInvalidParams(
                        $"Unknown 2D shape: '{shapeName}'. Available: rectangle, circle, capsule, segment, custom"));
            }

            var collisionNode = new CollisionShape2D { Shape = shape, Name = childName };
            collisionNode.Disabled = OptionalBool(@params, "disabled", false);
            collisionNode.OneWayCollision = OptionalBool(@params, "one_way_collision", false);

            var undoRedo = GetUndoRedo();
            undoRedo.CreateAction($"MCP: Add CollisionShape2D to {node.Name}");
            undoRedo.AddDoMethod(node, Node.MethodName.AddChild, collisionNode);
            undoRedo.AddDoMethod(collisionNode, Node.MethodName.SetOwner, root);
            undoRedo.AddDoReference(collisionNode);
            undoRedo.AddUndoMethod(node, Node.MethodName.RemoveChild, collisionNode);
            undoRedo.CommitAction();

            return Task.FromResult(Success(new Dictionary
            {
                ["node_path"] = root.GetPathTo(collisionNode).ToString(),
                ["shape_type"] = shape.GetClass(),
                ["dimension"] = "2D",
            }));
        }
        else
        {
            Shape3D shape;
            switch (shapeName)
            {
                case "box":
                case "rectangle":
                case "rect":
                {
                    var sx = (float)OptionalFloat(@params, "width", 1.0);
                    var sy = (float)OptionalFloat(@params, "height", 1.0);
                    var sz = (float)OptionalFloat(@params, "depth", 1.0);
                    shape = new BoxShape3D { Size = new Vector3(sx, sy, sz) };
                    break;
                }
                case "sphere":
                case "circle":
                    shape = new SphereShape3D { Radius = (float)OptionalFloat(@params, "radius", 0.5) };
                    break;
                case "capsule":
                    shape = new CapsuleShape3D
                    {
                        Radius = (float)OptionalFloat(@params, "radius", 0.5),
                        Height = (float)OptionalFloat(@params, "height", 2.0),
                    };
                    break;
                case "cylinder":
                    shape = new CylinderShape3D
                    {
                        Radius = (float)OptionalFloat(@params, "radius", 0.5),
                        Height = (float)OptionalFloat(@params, "height", 2.0),
                    };
                    break;
                case "convex":
                case "custom":
                {
                    var convex = new ConvexPolygonShape3D();
                    var pointsData = OptionalArray(@params, "points") ?? new Godot.Collections.Array();
                    var pool = new Godot.Collections.Array<Vector3>();
                    foreach (var p in pointsData)
                    {
                        if (p.VariantType == Variant.Type.Array)
                        {
                            var pArr = p.AsGodotArray();
                            if (pArr.Count >= 3)
                                pool.Add(new Vector3((float)pArr[0].AsDouble(), (float)pArr[1].AsDouble(), (float)pArr[2].AsDouble()));
                        }
                    }
                    if (pool.Count >= 4)
                    {
                        var packed = new Vector3[pool.Count];
                        for (var i = 0; i < pool.Count; i++) packed[i] = pool[i];
                        convex.Points = packed;
                    }
                    shape = convex;
                    break;
                }
                default:
                    return Task.FromResult(ErrorInvalidParams(
                        $"Unknown 3D shape: '{shapeName}'. Available: box, sphere, capsule, cylinder, convex"));
            }

            var collisionNode = new CollisionShape3D { Shape = shape, Name = childName };
            collisionNode.Disabled = OptionalBool(@params, "disabled", false);

            var undoRedo = GetUndoRedo();
            undoRedo.CreateAction($"MCP: Add CollisionShape3D to {node.Name}");
            undoRedo.AddDoMethod(node, Node.MethodName.AddChild, collisionNode);
            undoRedo.AddDoMethod(collisionNode, Node.MethodName.SetOwner, root);
            undoRedo.AddDoReference(collisionNode);
            undoRedo.AddUndoMethod(node, Node.MethodName.RemoveChild, collisionNode);
            undoRedo.CommitAction();

            return Task.FromResult(Success(new Dictionary
            {
                ["node_path"] = root.GetPathTo(collisionNode).ToString(),
                ["shape_type"] = shape.GetClass(),
                ["dimension"] = "3D",
            }));
        }
    }

    private Task<Dictionary> SetPhysicsLayers(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        if (!NodeHasProperty(node, "collision_layer"))
            return Task.FromResult(ErrorInvalidParams($"Node '{nodePath}' ({node.GetClass()}) does not have collision_layer property"));

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Set physics layers on {node.Name}");

        var changes = new Dictionary();

        if (@params.ContainsKey("collision_layer"))
        {
            var oldLayer = node.Get("collision_layer");
            var newLayer = ParseLayerValue(@params["collision_layer"]);
            undoRedo.AddDoProperty(node, "collision_layer", newLayer);
            undoRedo.AddUndoProperty(node, "collision_layer", oldLayer);
            changes["collision_layer"] = newLayer;
        }

        if (@params.ContainsKey("collision_mask"))
        {
            var oldMask = node.Get("collision_mask");
            var newMask = ParseLayerValue(@params["collision_mask"]);
            undoRedo.AddDoProperty(node, "collision_mask", newMask);
            undoRedo.AddUndoProperty(node, "collision_mask", oldMask);
            changes["collision_mask"] = newMask;
        }

        if (changes.Count == 0)
        {
            undoRedo.CommitAction();
            return Task.FromResult(ErrorInvalidParams("Must provide collision_layer and/or collision_mask"));
        }

        undoRedo.CommitAction();

        var dim = DetectDimension(node);
        if (string.IsNullOrEmpty(dim))
            dim = "2d";

        var resultData = new Dictionary { ["node_path"] = root.GetPathTo(node).ToString() };
        if (changes.ContainsKey("collision_layer"))
        {
            var layerVal = (int)(int)CoerceInt(changes["collision_layer"]);
            resultData["collision_layer"] = layerVal;
            resultData["collision_layer_info"] = LayerBitmaskToInfo(layerVal, dim);
        }
        if (changes.ContainsKey("collision_mask"))
        {
            var maskVal = (int)(int)CoerceInt(changes["collision_mask"]);
            resultData["collision_mask"] = maskVal;
            resultData["collision_mask_info"] = LayerBitmaskToInfo(maskVal, dim);
        }

        return Task.FromResult(Success(resultData));
    }

    private Task<Dictionary> GetPhysicsLayers(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        if (!NodeHasProperty(node, "collision_layer"))
            return Task.FromResult(ErrorInvalidParams($"Node '{nodePath}' ({node.GetClass()}) does not have collision_layer property"));

        var layer = (int)node.Get("collision_layer").AsInt64();
        var mask = (int)node.Get("collision_mask").AsInt64();

        var dim = DetectDimension(node);
        if (string.IsNullOrEmpty(dim))
            dim = "2d";

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["type"] = node.GetClass(),
            ["collision_layer"] = layer,
            ["collision_layer_info"] = LayerBitmaskToInfo(layer, dim),
            ["collision_mask"] = mask,
            ["collision_mask_info"] = LayerBitmaskToInfo(mask, dim),
        }));
    }

    private Task<Dictionary> AddRaycast(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        var dim = DetectDimension(node);
        if (string.IsNullOrEmpty(dim))
            dim = OptionalString(@params, "dimension", "2d");

        var rayName = OptionalString(@params, "name", "RayCast");
        var enabled = OptionalBool(@params, "enabled", true);
        var collisionMask = OptionalInt(@params, "collision_mask", 1);
        var collideWithAreas = OptionalBool(@params, "collide_with_areas", false);
        var collideWithBodies = OptionalBool(@params, "collide_with_bodies", true);
        var hitFromInside = OptionalBool(@params, "hit_from_inside", false);

        var undoRedo = GetUndoRedo();

        if (dim == "2d")
        {
            var ray = new RayCast2D
            {
                Name = rayName,
                Enabled = enabled,
                CollisionMask = (uint)collisionMask,
                CollideWithAreas = collideWithAreas,
                CollideWithBodies = collideWithBodies,
                HitFromInside = hitFromInside,
            };

            var tx = (float)OptionalFloat(@params, "target_x", 0.0);
            var ty = (float)OptionalFloat(@params, "target_y", 50.0);
            ray.TargetPosition = new Vector2(tx, ty);

            undoRedo.CreateAction($"MCP: Add RayCast2D to {node.Name}");
            undoRedo.AddDoMethod(node, Node.MethodName.AddChild, ray);
            undoRedo.AddDoMethod(ray, Node.MethodName.SetOwner, root);
            undoRedo.AddDoReference(ray);
            undoRedo.AddUndoMethod(node, Node.MethodName.RemoveChild, ray);
            undoRedo.CommitAction();

            return Task.FromResult(Success(new Dictionary
            {
                ["node_path"] = root.GetPathTo(ray).ToString(),
                ["type"] = "RayCast2D",
                ["target_position"] = $"Vector2({tx}, {ty})",
                ["collision_mask"] = collisionMask,
            }));
        }
        else
        {
            var ray = new RayCast3D
            {
                Name = rayName,
                Enabled = enabled,
                CollisionMask = (uint)collisionMask,
                CollideWithAreas = collideWithAreas,
                CollideWithBodies = collideWithBodies,
                HitFromInside = hitFromInside,
            };

            var tx = (float)OptionalFloat(@params, "target_x", 0.0);
            var ty = (float)OptionalFloat(@params, "target_y", -1.0);
            var tz = (float)OptionalFloat(@params, "target_z", 0.0);
            ray.TargetPosition = new Vector3(tx, ty, tz);

            undoRedo.CreateAction($"MCP: Add RayCast3D to {node.Name}");
            undoRedo.AddDoMethod(node, Node.MethodName.AddChild, ray);
            undoRedo.AddDoMethod(ray, Node.MethodName.SetOwner, root);
            undoRedo.AddDoReference(ray);
            undoRedo.AddUndoMethod(node, Node.MethodName.RemoveChild, ray);
            undoRedo.CommitAction();

            return Task.FromResult(Success(new Dictionary
            {
                ["node_path"] = root.GetPathTo(ray).ToString(),
                ["type"] = "RayCast3D",
                ["target_position"] = $"Vector3({tx}, {ty}, {tz})",
                ["collision_mask"] = collisionMask,
            }));
        }
    }

    private Task<Dictionary> SetupPhysicsBody(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Setup physics body {node.Name}");

        var applied = new Dictionary();

        if (node is CharacterBody2D or CharacterBody3D)
        {
            if (@params.ContainsKey("floor_stop_on_slope"))
            {
                var oldVal = node.Get("floor_stop_on_slope").AsBool();
                var newVal = CoerceBool(@params["floor_stop_on_slope"]);
                undoRedo.AddDoProperty(node, "floor_stop_on_slope", newVal);
                undoRedo.AddUndoProperty(node, "floor_stop_on_slope", oldVal);
                applied["floor_stop_on_slope"] = newVal;
            }

            if (@params.ContainsKey("floor_max_angle"))
            {
                var oldVal = node.Get("floor_max_angle").AsDouble();
                var newVal = @params["floor_max_angle"].AsDouble();
                undoRedo.AddDoProperty(node, "floor_max_angle", newVal);
                undoRedo.AddUndoProperty(node, "floor_max_angle", oldVal);
                applied["floor_max_angle"] = newVal;
            }

            if (@params.ContainsKey("floor_snap_length"))
            {
                var oldVal = node.Get("floor_snap_length").AsDouble();
                var newVal = @params["floor_snap_length"].AsDouble();
                undoRedo.AddDoProperty(node, "floor_snap_length", newVal);
                undoRedo.AddUndoProperty(node, "floor_snap_length", oldVal);
                applied["floor_snap_length"] = newVal;
            }

            if (@params.ContainsKey("wall_min_slide_angle"))
            {
                var oldVal = node.Get("wall_min_slide_angle").AsDouble();
                var newVal = @params["wall_min_slide_angle"].AsDouble();
                undoRedo.AddDoProperty(node, "wall_min_slide_angle", newVal);
                undoRedo.AddUndoProperty(node, "wall_min_slide_angle", oldVal);
                applied["wall_min_slide_angle"] = newVal;
            }

            if (@params.ContainsKey("motion_mode"))
            {
                var modeStr = @params["motion_mode"].VariantType == Variant.Type.String
                    ? @params["motion_mode"].AsString()
                    : @params["motion_mode"].ToString();
                var lowered = modeStr.ToLowerInvariant();
                long modeVal;
                if (node is CharacterBody2D)
                {
                    modeVal = lowered switch
                    {
                        "grounded" => (long)CharacterBody2D.MotionModeEnum.Grounded,
                        "floating" => (long)CharacterBody2D.MotionModeEnum.Floating,
                        _ => (long)ParseLayerValue(@params["motion_mode"]),
                    };
                }
                else
                {
                    modeVal = lowered switch
                    {
                        "grounded" => (long)CharacterBody3D.MotionModeEnum.Grounded,
                        "floating" => (long)CharacterBody3D.MotionModeEnum.Floating,
                        _ => (long)ParseLayerValue(@params["motion_mode"]),
                    };
                }
                var oldVal = node.Get("motion_mode").AsInt64();
                undoRedo.AddDoProperty(node, "motion_mode", modeVal);
                undoRedo.AddUndoProperty(node, "motion_mode", oldVal);
                applied["motion_mode"] = modeStr;
            }

            if (@params.ContainsKey("max_slides"))
            {
                var oldVal = node.Get("max_slides").AsInt64();
                var newVal = ParseLayerValue(@params["max_slides"]);
                undoRedo.AddDoProperty(node, "max_slides", newVal);
                undoRedo.AddUndoProperty(node, "max_slides", oldVal);
                applied["max_slides"] = newVal;
            }

            if (@params.ContainsKey("slide_on_ceiling"))
            {
                var oldVal = node.Get("slide_on_ceiling").AsBool();
                var newVal = CoerceBool(@params["slide_on_ceiling"]);
                undoRedo.AddDoProperty(node, "slide_on_ceiling", newVal);
                undoRedo.AddUndoProperty(node, "slide_on_ceiling", oldVal);
                applied["slide_on_ceiling"] = newVal;
            }
        }
        else if (node is RigidBody2D or RigidBody3D)
        {
            if (@params.ContainsKey("mass"))
            {
                var oldVal = node.Get("mass").AsDouble();
                var newVal = @params["mass"].AsDouble();
                undoRedo.AddDoProperty(node, "mass", newVal);
                undoRedo.AddUndoProperty(node, "mass", oldVal);
                applied["mass"] = newVal;
            }

            if (@params.ContainsKey("gravity_scale"))
            {
                var oldVal = node.Get("gravity_scale").AsDouble();
                var newVal = @params["gravity_scale"].AsDouble();
                undoRedo.AddDoProperty(node, "gravity_scale", newVal);
                undoRedo.AddUndoProperty(node, "gravity_scale", oldVal);
                applied["gravity_scale"] = newVal;
            }

            if (@params.ContainsKey("linear_damp"))
            {
                var oldVal = node.Get("linear_damp").AsDouble();
                var newVal = @params["linear_damp"].AsDouble();
                undoRedo.AddDoProperty(node, "linear_damp", newVal);
                undoRedo.AddUndoProperty(node, "linear_damp", oldVal);
                applied["linear_damp"] = newVal;
            }

            if (@params.ContainsKey("angular_damp"))
            {
                var oldVal = node.Get("angular_damp").AsDouble();
                var newVal = @params["angular_damp"].AsDouble();
                undoRedo.AddDoProperty(node, "angular_damp", newVal);
                undoRedo.AddUndoProperty(node, "angular_damp", oldVal);
                applied["angular_damp"] = newVal;
            }

            if (@params.ContainsKey("freeze"))
            {
                var oldVal = node.Get("freeze").AsBool();
                var newVal = CoerceBool(@params["freeze"]);
                undoRedo.AddDoProperty(node, "freeze", newVal);
                undoRedo.AddUndoProperty(node, "freeze", oldVal);
                applied["freeze"] = newVal;
            }

            if (@params.ContainsKey("freeze_mode"))
            {
                var modeStr = @params["freeze_mode"].VariantType == Variant.Type.String
                    ? @params["freeze_mode"].AsString()
                    : @params["freeze_mode"].ToString();
                var lowered = modeStr.ToLowerInvariant();
                long modeVal;
                if (node is RigidBody2D)
                {
                    modeVal = lowered switch
                    {
                        "static" => (long)RigidBody2D.FreezeModeEnum.Static,
                        "kinematic" => (long)RigidBody2D.FreezeModeEnum.Kinematic,
                        _ => (long)ParseLayerValue(@params["freeze_mode"]),
                    };
                }
                else
                {
                    modeVal = lowered switch
                    {
                        "static" => (long)RigidBody3D.FreezeModeEnum.Static,
                        "kinematic" => (long)RigidBody3D.FreezeModeEnum.Kinematic,
                        _ => (long)ParseLayerValue(@params["freeze_mode"]),
                    };
                }
                var oldVal = node.Get("freeze_mode").AsInt64();
                undoRedo.AddDoProperty(node, "freeze_mode", modeVal);
                undoRedo.AddUndoProperty(node, "freeze_mode", oldVal);
                applied["freeze_mode"] = modeStr;
            }

            if (@params.ContainsKey("continuous_cd"))
            {
                if (node is RigidBody2D)
                {
                    var ccdStr = @params["continuous_cd"].VariantType == Variant.Type.String
                        ? @params["continuous_cd"].AsString()
                        : @params["continuous_cd"].ToString();
                    var lowered = ccdStr.ToLowerInvariant();
                    var ccdVal = lowered switch
                    {
                        "disabled" => (long)RigidBody2D.CcdMode.Disabled,
                        "cast_ray" => (long)RigidBody2D.CcdMode.CastRay,
                        "cast_shape" => (long)RigidBody2D.CcdMode.CastShape,
                        _ => (long)ParseLayerValue(@params["continuous_cd"]),
                    };
                    var oldVal = node.Get("continuous_cd").AsInt64();
                    undoRedo.AddDoProperty(node, "continuous_cd", ccdVal);
                    undoRedo.AddUndoProperty(node, "continuous_cd", oldVal);
                    applied["continuous_cd"] = ccdStr;
                }
                else
                {
                    var oldVal = node.Get("continuous_cd").AsBool();
                    var newVal = CoerceBool(@params["continuous_cd"]);
                    undoRedo.AddDoProperty(node, "continuous_cd", newVal);
                    undoRedo.AddUndoProperty(node, "continuous_cd", oldVal);
                    applied["continuous_cd"] = newVal;
                }
            }

            if (@params.ContainsKey("contact_monitor"))
            {
                var oldVal = node.Get("contact_monitor").AsBool();
                var newVal = CoerceBool(@params["contact_monitor"]);
                undoRedo.AddDoProperty(node, "contact_monitor", newVal);
                undoRedo.AddUndoProperty(node, "contact_monitor", oldVal);
                applied["contact_monitor"] = newVal;
            }

            if (@params.ContainsKey("max_contacts_reported"))
            {
                var oldVal = node.Get("max_contacts_reported").AsInt64();
                var newVal = ParseLayerValue(@params["max_contacts_reported"]);
                undoRedo.AddDoProperty(node, "max_contacts_reported", newVal);
                undoRedo.AddUndoProperty(node, "max_contacts_reported", oldVal);
                applied["max_contacts_reported"] = newVal;
            }
        }
        else if (node is StaticBody2D or StaticBody3D or AnimatableBody2D or AnimatableBody3D)
        {
            if (@params.ContainsKey("physics_material_override"))
            {
                undoRedo.CommitAction();
                return Task.FromResult(ErrorInvalidParams("Use add_resource to set physics_material_override"));
            }
        }
        else
        {
            undoRedo.CommitAction();
            return Task.FromResult(ErrorInvalidParams(
                $"Node '{nodePath}' ({node.GetClass()}) is not a recognized physics body type. Supported: CharacterBody2D/3D, RigidBody2D/3D, StaticBody2D/3D, AnimatableBody2D/3D"));
        }

        if (applied.Count == 0)
        {
            undoRedo.CommitAction();
            return Task.FromResult(ErrorInvalidParams($"No valid properties provided for {node.GetClass()}"));
        }

        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["type"] = node.GetClass(),
            ["applied"] = applied,
        }));
    }

    private Task<Dictionary> GetCollisionInfo(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        var includeChildren = OptionalBool(@params, "include_children", true);

        var info = new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["type"] = node.GetClass(),
        };

        if (NodeHasProperty(node, "collision_layer"))
        {
            var dim = DetectDimension(node);
            if (string.IsNullOrEmpty(dim))
                dim = "2d";
            var layer = (int)node.Get("collision_layer").AsInt64();
            var mask = (int)node.Get("collision_mask").AsInt64();
            info["collision_layer"] = layer;
            info["collision_layer_info"] = LayerBitmaskToInfo(layer, dim);
            info["collision_mask"] = mask;
            info["collision_mask_info"] = LayerBitmaskToInfo(mask, dim);
        }

        if (node is CharacterBody2D or CharacterBody3D)
        {
            info["body_settings"] = new Dictionary
            {
                ["motion_mode"] = node.Get("motion_mode"),
                ["floor_stop_on_slope"] = node.Get("floor_stop_on_slope"),
                ["floor_max_angle"] = node.Get("floor_max_angle"),
                ["floor_snap_length"] = node.Get("floor_snap_length"),
                ["wall_min_slide_angle"] = node.Get("wall_min_slide_angle"),
                ["max_slides"] = node.Get("max_slides"),
                ["slide_on_ceiling"] = node.Get("slide_on_ceiling"),
            };
        }
        else if (node is RigidBody2D or RigidBody3D)
        {
            info["body_settings"] = new Dictionary
            {
                ["mass"] = node.Get("mass"),
                ["gravity_scale"] = node.Get("gravity_scale"),
                ["linear_damp"] = node.Get("linear_damp"),
                ["angular_damp"] = node.Get("angular_damp"),
                ["freeze"] = node.Get("freeze"),
                ["freeze_mode"] = node.Get("freeze_mode"),
                ["contact_monitor"] = node.Get("contact_monitor"),
                ["max_contacts_reported"] = node.Get("max_contacts_reported"),
            };
        }

        var shapes = new Godot.Collections.Array();
        var raycasts = new Godot.Collections.Array();
        var nodesToCheck = new List<Node> { node };

        if (includeChildren)
        {
            var queue = new Queue<Node>();
            queue.Enqueue(node);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var child in current.GetChildren())
                {
                    nodesToCheck.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        foreach (var checkNode in nodesToCheck)
        {
            switch (checkNode)
            {
                case CollisionShape2D cs2:
                {
                    var shapeInfo = new Dictionary
                    {
                        ["node_path"] = root.GetPathTo(cs2).ToString(),
                        ["disabled"] = cs2.Disabled,
                        ["one_way_collision"] = cs2.OneWayCollision,
                    };
                    if (cs2.Shape != null)
                    {
                        shapeInfo["shape_type"] = cs2.Shape.GetClass();
                        switch (cs2.Shape)
                        {
                            case RectangleShape2D rect2:
                                shapeInfo["size"] = $"Vector2({rect2.Size.X}, {rect2.Size.Y})";
                                break;
                            case CircleShape2D circle2:
                                shapeInfo["radius"] = circle2.Radius;
                                break;
                            case CapsuleShape2D capsule2:
                                shapeInfo["radius"] = capsule2.Radius;
                                shapeInfo["height"] = capsule2.Height;
                                break;
                        }
                    }
                    shapes.Add(shapeInfo);
                    break;
                }
                case CollisionShape3D cs3:
                {
                    var shapeInfo = new Dictionary
                    {
                        ["node_path"] = root.GetPathTo(cs3).ToString(),
                        ["disabled"] = cs3.Disabled,
                    };
                    if (cs3.Shape != null)
                    {
                        shapeInfo["shape_type"] = cs3.Shape.GetClass();
                        switch (cs3.Shape)
                        {
                            case BoxShape3D box3:
                                shapeInfo["size"] = $"Vector3({box3.Size.X}, {box3.Size.Y}, {box3.Size.Z})";
                                break;
                            case SphereShape3D sphere3:
                                shapeInfo["radius"] = sphere3.Radius;
                                break;
                            case CapsuleShape3D capsule3:
                                shapeInfo["radius"] = capsule3.Radius;
                                shapeInfo["height"] = capsule3.Height;
                                break;
                            case CylinderShape3D cylinder3:
                                shapeInfo["radius"] = cylinder3.Radius;
                                shapeInfo["height"] = cylinder3.Height;
                                break;
                        }
                    }
                    shapes.Add(shapeInfo);
                    break;
                }
                case CollisionPolygon2D poly2:
                    shapes.Add(new Dictionary
                    {
                        ["node_path"] = root.GetPathTo(poly2).ToString(),
                        ["shape_type"] = "CollisionPolygon2D",
                        ["disabled"] = poly2.Disabled,
                        ["one_way_collision"] = poly2.OneWayCollision,
                        ["polygon_points"] = poly2.Polygon.Length,
                    });
                    break;
                case CollisionPolygon3D poly3:
                    shapes.Add(new Dictionary
                    {
                        ["node_path"] = root.GetPathTo(poly3).ToString(),
                        ["shape_type"] = "CollisionPolygon3D",
                        ["disabled"] = poly3.Disabled,
                        ["polygon_points"] = poly3.Polygon.Length,
                    });
                    break;
                case RayCast2D ray2:
                    raycasts.Add(new Dictionary
                    {
                        ["node_path"] = root.GetPathTo(ray2).ToString(),
                        ["type"] = "RayCast2D",
                        ["enabled"] = ray2.Enabled,
                        ["target_position"] = $"Vector2({ray2.TargetPosition.X}, {ray2.TargetPosition.Y})",
                        ["collision_mask"] = ray2.CollisionMask,
                        ["collide_with_areas"] = ray2.CollideWithAreas,
                        ["collide_with_bodies"] = ray2.CollideWithBodies,
                    });
                    break;
                case RayCast3D ray3:
                    raycasts.Add(new Dictionary
                    {
                        ["node_path"] = root.GetPathTo(ray3).ToString(),
                        ["type"] = "RayCast3D",
                        ["enabled"] = ray3.Enabled,
                        ["target_position"] = $"Vector3({ray3.TargetPosition.X}, {ray3.TargetPosition.Y}, {ray3.TargetPosition.Z})",
                        ["collision_mask"] = ray3.CollisionMask,
                        ["collide_with_areas"] = ray3.CollideWithAreas,
                        ["collide_with_bodies"] = ray3.CollideWithBodies,
                    });
                    break;
            }
        }

        info["collision_shapes"] = shapes;
        info["raycasts"] = raycasts;

        return Task.FromResult(Success(info));
    }
}
