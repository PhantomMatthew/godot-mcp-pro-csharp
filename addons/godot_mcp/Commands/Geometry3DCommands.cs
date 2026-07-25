using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// 3D geometry construction tools: CSG boolean modelling and MultiMesh instancing.
/// All mutations go through EditorUndoRedoManager so they participate in the
/// editor's undo history.
/// </summary>
[Tool]
public partial class Geometry3DCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "create_csg",
            Description = "Create a CSG shape node (CsgBox3D, CsgCylinder3D, CsgSphere3D, CsgTorus3D, " +
                "CsgPolygon3D, or CsgMesh3D) under an existing parent. Supports the boolean operation " +
                "(union/intersection/subtraction), transform, and shape-specific parameters. Undo-tracked.",
            Category = "geometry_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["shape"] = Schema.Enum("CSG shape class to create",
                    "box", "cylinder", "sphere", "torus", "polygon", "mesh"),
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new CSG node"),
                ["operation"] = Schema.Enum("CSG boolean operation", "union", "intersection", "subtraction"),
                ["position"] = Schema.Any("Position as Vector3(x,y,z) string or {x,y,z} dict"),
                ["rotation"] = Schema.Any("Rotation in degrees as Vector3(x,y,z) string or {x,y,z} dict"),
                ["scale"] = Schema.Any("Scale as Vector3(x,y,z) string or {x,y,z} dict"),
                // Box
                ["width"] = Schema.Num("Box width"),
                ["height"] = Schema.Num("Box/cylinder height"),
                ["depth"] = Schema.Num("Box depth"),
                // Cylinder / Sphere / Torus
                ["radius"] = Schema.Num("Cylinder/sphere radius"),
                ["rings"] = Schema.Num("Sphere rings"),
                ["sides"] = Schema.Num("Cylinder/torus sides"),
                ["radial_segments"] = Schema.Num("Sphere radial segments"),
                ["cone"] = Schema.Bool("Cylinder is a cone"),
                ["inner_radius"] = Schema.Num("Torus inner radius"),
                ["outer_radius"] = Schema.Num("Torus outer radius"),
                ["ring_sides"] = Schema.Num("Torus ring sides"),
                // Polygon
                ["polygon"] = Schema.Arr("Polygon points as [[x,y],...] or [{x,y},...]"),
                ["polygon_mode"] = Schema.Enum("CsgPolygon3D mode", "depth", "spin", "path"),
                ["spin_degrees"] = Schema.Num("Spin mode degrees"),
                ["spin_sides"] = Schema.Num("Spin mode sides"),
                // Mesh
                ["mesh_path"] = Schema.Str("res:// path to a Mesh resource for CsgMesh3D"),
                // Common
                ["use_collision"] = Schema.Bool("Enable collision generation"),
            }, "shape"),
            Handler = CreateCsg,
        },
        new()
        {
            Name = "configure_csg",
            Description = "Update properties on an existing CSG node: operation, shape parameters, " +
                "material override, or collision. Undo-tracked.",
            Category = "geometry_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the CSG node"),
                ["operation"] = Schema.Enum("CSG boolean operation", "union", "intersection", "subtraction"),
                ["position"] = Schema.Any("Position as Vector3(x,y,z) string or {x,y,z} dict"),
                ["rotation"] = Schema.Any("Rotation in degrees as Vector3(x,y,z) string or {x,y,z} dict"),
                ["scale"] = Schema.Any("Scale as Vector3(x,y,z) string or {x,y,z} dict"),
                ["width"] = Schema.Num("Box width"),
                ["height"] = Schema.Num("Box/cylinder height"),
                ["depth"] = Schema.Num("Box depth"),
                ["radius"] = Schema.Num("Cylinder/sphere radius"),
                ["rings"] = Schema.Num("Sphere rings"),
                ["sides"] = Schema.Num("Cylinder/torus sides"),
                ["radial_segments"] = Schema.Num("Sphere radial segments"),
                ["cone"] = Schema.Bool("Cylinder is a cone"),
                ["inner_radius"] = Schema.Num("Torus inner radius"),
                ["outer_radius"] = Schema.Num("Torus outer radius"),
                ["ring_sides"] = Schema.Num("Torus ring sides"),
                ["use_collision"] = Schema.Bool("Enable collision generation"),
                ["collision_layer"] = Schema.Int("Physics collision layer bitmask"),
                ["collision_mask"] = Schema.Int("Physics collision mask bitmask"),
                ["material_path"] = Schema.Str("res:// path to a Material resource to assign"),
                ["albedo_color"] = Schema.Any("Create a StandardMaterial3D with this albedo color and assign"),
                ["mesh_path"] = Schema.Str("res:// path to a Mesh resource (CsgMesh3D only)"),
            }, "node_path"),
            Handler = ConfigureCsg,
        },
        new()
        {
            Name = "setup_multimesh",
            Description = "Create or reconfigure a MultiMeshInstance3D: assign a mesh (primitive type or " +
                "res:// path), set instance count and transform format, and optionally populate per-instance " +
                "transforms. Undo-tracked.",
            Category = "geometry_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node (when creating)", "."),
                ["name"] = Schema.Str("Name for the new node", "MultiMeshInstance3D"),
                ["node_path"] = Schema.Str("Scene-relative path of an existing MultiMeshInstance3D to reconfigure"),
                ["mesh_type"] = Schema.Enum("Primitive mesh to instance",
                    "BoxMesh", "SphereMesh", "CylinderMesh", "CapsuleMesh", "PlaneMesh", "PrismMesh", "TorusMesh", "QuadMesh"),
                ["mesh_path"] = Schema.Str("res:// path to a Mesh resource to instance"),
                ["instance_count"] = Schema.Int("Number of instances"),
                ["transform_format"] = Schema.Enum("Instance transform format", "3d", "2d"),
                ["visible_instance_count"] = Schema.Int("Visible instance count (-1 = all)"),
                ["transforms"] = Schema.Arr("Per-instance transforms as [{position:{x,y,z}, rotation:{x,y,z}, scale:{x,y,z}}, ...]"),
                ["position"] = Schema.Any("Node position as Vector3(x,y,z) string or {x,y,z} dict"),
                ["rotation"] = Schema.Any("Node rotation in degrees as Vector3(x,y,z) string or {x,y,z} dict"),
            }),
            Handler = SetupMultiMesh,
        },
        new()
        {
            Name = "get_multimesh_info",
            Description = "Read a MultiMeshInstance3D's configuration: instance_count, mesh_type, " +
                "visible_instances, transform_format, and a small sample of instance transforms.",
            Category = "geometry_3d",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the MultiMeshInstance3D"),
                ["sample_count"] = Schema.Int("Number of instance transforms to sample (0 = none)", 0),
            }, "node_path"),
            Handler = GetMultiMeshInfo,
        },
    };

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Vector3 ParseVector3(Dictionary @params, string key, Vector3 @default)
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
                return new Vector3(
                    (float)arr[0].AsDouble(),
                    (float)arr[1].AsDouble(),
                    (float)arr[2].AsDouble());
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

    private static CsgShape3D.OperationEnum ParseOperation(string op)
    {
        return op.ToLowerInvariant() switch
        {
            "union" or "0" => CsgShape3D.OperationEnum.Union,
            "intersection" or "1" => CsgShape3D.OperationEnum.Intersection,
            "subtraction" or "2" => CsgShape3D.OperationEnum.Subtraction,
            _ => CsgShape3D.OperationEnum.Union,
        };
    }

    private static string OperationName(CsgShape3D.OperationEnum op) => op switch
    {
        CsgShape3D.OperationEnum.Union => "union",
        CsgShape3D.OperationEnum.Intersection => "intersection",
        CsgShape3D.OperationEnum.Subtraction => "subtraction",
        _ => "union",
    };

    /// <summary>Build the box Size vector from optional width/height/depth params, defaulting to current.</summary>
    private static Vector3 BuildBoxSize(Dictionary @params, Vector3 current)
    {
        var w = @params.ContainsKey("width") ? (float)OptionalFloat(@params, "width", current.X) : current.X;
        var h = @params.ContainsKey("height") ? (float)OptionalFloat(@params, "height", current.Y) : current.Y;
        var d = @params.ContainsKey("depth") ? (float)OptionalFloat(@params, "depth", current.Z) : current.Z;
        return new Vector3(w, h, d);
    }

    private void ApplyCsgShapeParams(CsgShape3D csg, Dictionary @params)
    {
        switch (csg)
        {
            case CsgBox3D box:
                if (@params.ContainsKey("width") || @params.ContainsKey("height") || @params.ContainsKey("depth"))
                    box.Size = BuildBoxSize(@params, box.Size);
                break;
            case CsgCylinder3D cyl:
                if (@params.ContainsKey("radius")) cyl.Radius = (float)OptionalFloat(@params, "radius", cyl.Radius);
                if (@params.ContainsKey("height")) cyl.Height = (float)OptionalFloat(@params, "height", cyl.Height);
                if (@params.ContainsKey("sides")) cyl.Sides = OptionalInt(@params, "sides", cyl.Sides);
                if (@params.ContainsKey("cone")) cyl.Cone = OptionalBool(@params, "cone", cyl.Cone);
                break;
            case CsgSphere3D sphere:
                if (@params.ContainsKey("radius")) sphere.Radius = (float)OptionalFloat(@params, "radius", sphere.Radius);
                if (@params.ContainsKey("radial_segments")) sphere.RadialSegments = OptionalInt(@params, "radial_segments", sphere.RadialSegments);
                if (@params.ContainsKey("rings")) sphere.Rings = OptionalInt(@params, "rings", sphere.Rings);
                break;
            case CsgTorus3D torus:
                if (@params.ContainsKey("inner_radius")) torus.InnerRadius = (float)OptionalFloat(@params, "inner_radius", torus.InnerRadius);
                if (@params.ContainsKey("outer_radius")) torus.OuterRadius = (float)OptionalFloat(@params, "outer_radius", torus.OuterRadius);
                if (@params.ContainsKey("sides")) torus.Sides = OptionalInt(@params, "sides", torus.Sides);
                if (@params.ContainsKey("ring_sides")) torus.RingSides = OptionalInt(@params, "ring_sides", torus.RingSides);
                break;
            case CsgPolygon3D polygon:
                if (@params.ContainsKey("depth")) polygon.Depth = (float)OptionalFloat(@params, "depth", polygon.Depth);
                if (@params.ContainsKey("spin_degrees")) polygon.SpinDegrees = (float)OptionalFloat(@params, "spin_degrees", polygon.SpinDegrees);
                if (@params.ContainsKey("spin_sides")) polygon.SpinSides = OptionalInt(@params, "spin_sides", polygon.SpinSides);
                if (@params.TryGetValue("polygon_mode", out var modeVal) && modeVal.VariantType == Variant.Type.String)
                {
                    polygon.Mode = modeVal.AsString().ToLowerInvariant() switch
                    {
                        "depth" or "0" => CsgPolygon3D.ModeEnum.Depth,
                        "spin" or "1" => CsgPolygon3D.ModeEnum.Spin,
                        "path" or "2" => CsgPolygon3D.ModeEnum.Path,
                        _ => polygon.Mode,
                    };
                }
                var polyArr = OptionalArray(@params, "polygon");
                if (polyArr != null && polyArr.Count >= 3)
                {
                    var points = new Vector2[polyArr.Count];
                    for (var i = 0; i < polyArr.Count; i++)
                    {
                        var p = polyArr[i];
                        if (p.VariantType == Variant.Type.Array)
                        {
                            var pair = p.AsGodotArray();
                            if (pair.Count >= 2)
                                points[i] = new Vector2((float)pair[0].AsDouble(), (float)pair[1].AsDouble());
                        }
                        else if (p.VariantType == Variant.Type.Dictionary)
                        {
                            var pd = p.AsGodotDictionary();
                            points[i] = new Vector2(
                                (float)GetNum(pd, "x", 0),
                                (float)GetNum(pd, "y", 0));
                        }
                        else if (p.VariantType == Variant.Type.Vector2)
                        {
                            points[i] = p.AsVector2();
                        }
                    }
                    polygon.Polygon = points;
                }
                break;
            case CsgMesh3D meshCsg:
                if (@params.TryGetValue("mesh_path", out var meshPathVal))
                {
                    var meshPath = meshPathVal.AsString();
                    if (!string.IsNullOrEmpty(meshPath) && ResourceLoader.Exists(meshPath))
                    {
                        var loaded = ResourceLoader.Load(meshPath);
                        if (loaded is Mesh m)
                            meshCsg.Mesh = m;
                    }
                }
                break;
        }
    }

    // ── 1. create_csg ────────────────────────────────────────────────────────

    private Task<Dictionary> CreateCsg(Dictionary @params)
    {
        var (shape, shapeErr) = RequireString(@params, "shape");
        if (shapeErr != null) return Task.FromResult(shapeErr);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

        var shapeLower = shape.ToLowerInvariant();
        CsgShape3D csg;
        string className;
        switch (shapeLower)
        {
            case "box":
                csg = new CsgBox3D();
                className = "CsgBox3D";
                break;
            case "cylinder":
                csg = new CsgCylinder3D();
                className = "CsgCylinder3D";
                break;
            case "sphere":
                csg = new CsgSphere3D();
                className = "CsgSphere3D";
                break;
            case "torus":
                csg = new CsgTorus3D();
                className = "CsgTorus3D";
                break;
            case "polygon":
                csg = new CsgPolygon3D();
                className = "CsgPolygon3D";
                break;
            case "mesh":
                csg = new CsgMesh3D();
                className = "CsgMesh3D";
                break;
            default:
                return Task.FromResult(ErrorInvalidParams(
                    $"Unknown shape '{shape}'. Available: box, cylinder, sphere, torus, polygon, mesh"));
        }

        var nodeName = OptionalString(@params, "name", className);
        csg.Name = nodeName;

        if (@params.TryGetValue("operation", out var opVal))
            csg.Operation = ParseOperation(opVal.ToString());

        ApplyCsgShapeParams(csg, @params);

        if (@params.ContainsKey("use_collision"))
            csg.UseCollision = OptionalBool(@params, "use_collision", false);

        csg.Position = ParseVector3(@params, "position", Vector3.Zero);
        csg.RotationDegrees = ParseVector3(@params, "rotation", Vector3.Zero);
        csg.Scale = ParseVector3(@params, "scale", Vector3.One);

        AddChildWithUndo(parent, csg, root, $"MCP: Create {className}");

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(csg).ToString(),
            ["name"] = csg.Name.ToString(),
            ["shape"] = shapeLower,
            ["class"] = className,
            ["operation"] = OperationName(csg.Operation),
        }));
    }

    // ── 2. configure_csg ─────────────────────────────────────────────────────

    private Task<Dictionary> ConfigureCsg(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'"));
        if (node is not CsgShape3D csg)
            return Task.FromResult(ErrorInvalidParams(
                $"Node '{nodePath}' is not a CsgShape3D (is {node.GetClass()})"));

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Configure {csg.Name}");
        var anyChange = false;

        void TrackProp(StringName property, Variant newValue)
        {
            var oldValue = csg.Get(property);
            undoRedo.AddDoProperty(csg, property, newValue);
            undoRedo.AddUndoProperty(csg, property, oldValue);
            anyChange = true;
        }

        if (@params.TryGetValue("operation", out var opVal))
            TrackProp(CsgShape3D.PropertyName.Operation, (int)ParseOperation(opVal.ToString()));

        if (@params.ContainsKey("use_collision"))
            TrackProp(CsgShape3D.PropertyName.UseCollision, OptionalBool(@params, "use_collision", csg.UseCollision));
        if (@params.ContainsKey("collision_layer"))
            TrackProp(CsgShape3D.PropertyName.CollisionLayer, (uint)OptionalInt(@params, "collision_layer", (int)csg.CollisionLayer));
        if (@params.ContainsKey("collision_mask"))
            TrackProp(CsgShape3D.PropertyName.CollisionMask, (uint)OptionalInt(@params, "collision_mask", (int)csg.CollisionMask));

        // Material handling: explicit material_path OR albedo_color → build StandardMaterial3D.
        // Material lives on the concrete subclasses (CsgBox3D.Material etc.), so we use the
        // generic StringName "Material" — every concrete CSG shape exposes it.
        Material? newMaterial = null;
        if (@params.TryGetValue("material_path", out var matPathVal))
        {
            var matPath = matPathVal.AsString();
            if (!string.IsNullOrEmpty(matPath))
            {
                if (!ResourceLoader.Exists(matPath))
                {
                    undoRedo.CommitAction();
                    return Task.FromResult(ErrorNotFound($"Material '{matPath}'"));
                }
                if (ResourceLoader.Load(matPath) is Material m)
                    newMaterial = m;
            }
        }
        else if (@params.TryGetValue("albedo_color", out var albedoVal))
        {
            Color color = Colors.White;
            if (albedoVal.VariantType == Variant.Type.String)
                color = PropertyParser.ParseValue(albedoVal, Variant.Type.Color).AsColor();
            else if (albedoVal.VariantType == Variant.Type.Dictionary)
            {
                var d = albedoVal.AsGodotDictionary();
                color = new Color(
                    (float)GetNum(d, "r", 1.0),
                    (float)GetNum(d, "g", 1.0),
                    (float)GetNum(d, "b", 1.0),
                    (float)GetNum(d, "a", 1.0));
            }
            else if (albedoVal.VariantType == Variant.Type.Color)
            {
                color = albedoVal.AsColor();
            }
            newMaterial = new StandardMaterial3D { AlbedoColor = color };
        }
        if (newMaterial != null)
        {
            TrackProp("Material", newMaterial);
            undoRedo.AddDoReference(newMaterial);
        }

        // Shape-specific params
        var hadShapeParams = false;
        switch (csg)
        {
            case CsgBox3D box:
                if (@params.ContainsKey("width") || @params.ContainsKey("height") || @params.ContainsKey("depth"))
                {
                    TrackProp(CsgBox3D.PropertyName.Size, BuildBoxSize(@params, box.Size));
                    hadShapeParams = true;
                }
                break;
            case CsgCylinder3D cyl:
                if (@params.ContainsKey("radius")) { TrackProp(CsgCylinder3D.PropertyName.Radius, (float)OptionalFloat(@params, "radius", cyl.Radius)); hadShapeParams = true; }
                if (@params.ContainsKey("height")) { TrackProp(CsgCylinder3D.PropertyName.Height, (float)OptionalFloat(@params, "height", cyl.Height)); hadShapeParams = true; }
                if (@params.ContainsKey("sides")) { TrackProp(CsgCylinder3D.PropertyName.Sides, OptionalInt(@params, "sides", cyl.Sides)); hadShapeParams = true; }
                if (@params.ContainsKey("cone")) { TrackProp(CsgCylinder3D.PropertyName.Cone, OptionalBool(@params, "cone", cyl.Cone)); hadShapeParams = true; }
                break;
            case CsgSphere3D sphere:
                if (@params.ContainsKey("radius")) { TrackProp(CsgSphere3D.PropertyName.Radius, (float)OptionalFloat(@params, "radius", sphere.Radius)); hadShapeParams = true; }
                if (@params.ContainsKey("radial_segments")) { TrackProp(CsgSphere3D.PropertyName.RadialSegments, OptionalInt(@params, "radial_segments", sphere.RadialSegments)); hadShapeParams = true; }
                if (@params.ContainsKey("rings")) { TrackProp(CsgSphere3D.PropertyName.Rings, OptionalInt(@params, "rings", sphere.Rings)); hadShapeParams = true; }
                break;
            case CsgTorus3D torus:
                if (@params.ContainsKey("inner_radius")) { TrackProp(CsgTorus3D.PropertyName.InnerRadius, (float)OptionalFloat(@params, "inner_radius", torus.InnerRadius)); hadShapeParams = true; }
                if (@params.ContainsKey("outer_radius")) { TrackProp(CsgTorus3D.PropertyName.OuterRadius, (float)OptionalFloat(@params, "outer_radius", torus.OuterRadius)); hadShapeParams = true; }
                if (@params.ContainsKey("sides")) { TrackProp(CsgTorus3D.PropertyName.Sides, OptionalInt(@params, "sides", torus.Sides)); hadShapeParams = true; }
                if (@params.ContainsKey("ring_sides")) { TrackProp(CsgTorus3D.PropertyName.RingSides, OptionalInt(@params, "ring_sides", torus.RingSides)); hadShapeParams = true; }
                break;
            case CsgMesh3D meshCsg:
                if (@params.TryGetValue("mesh_path", out var meshPathVal))
                {
                    var meshPath = meshPathVal.AsString();
                    if (!string.IsNullOrEmpty(meshPath))
                    {
                        if (!ResourceLoader.Exists(meshPath))
                        {
                            undoRedo.CommitAction();
                            return Task.FromResult(ErrorNotFound($"Mesh '{meshPath}'"));
                        }
                        if (ResourceLoader.Load(meshPath) is Mesh m)
                        {
                            TrackProp(CsgMesh3D.PropertyName.Mesh, m);
                            hadShapeParams = true;
                        }
                    }
                }
                break;
        }

        // Transform
        if (@params.ContainsKey("position"))
            TrackProp(Node3D.PropertyName.Position, ParseVector3(@params, "position", csg.Position));
        if (@params.ContainsKey("rotation"))
            TrackProp(Node3D.PropertyName.RotationDegrees, ParseVector3(@params, "rotation", csg.RotationDegrees));
        if (@params.ContainsKey("scale"))
            TrackProp(Node3D.PropertyName.Scale, ParseVector3(@params, "scale", csg.Scale));

        if (!anyChange)
        {
            undoRedo.CommitAction();
            return Task.FromResult(ErrorInvalidParams("No configurable parameters were provided"));
        }

        undoRedo.CommitAction();

        var result = new Dictionary
        {
            ["node_path"] = root.GetPathTo(csg).ToString(),
            ["name"] = csg.Name.ToString(),
            ["operation"] = OperationName(csg.Operation),
            ["use_collision"] = csg.UseCollision,
            ["shape_params_updated"] = hadShapeParams,
        };
        return Task.FromResult(Success(result));
    }

    // ── 3. setup_multimesh ───────────────────────────────────────────────────

    private Task<Dictionary> SetupMultiMesh(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var nodePath = OptionalString(@params, "node_path", "");
        MultiMeshInstance3D? mmi = null;
        var isExisting = false;

        if (!string.IsNullOrEmpty(nodePath))
        {
            var existing = FindNodeByPath(nodePath);
            if (existing is MultiMeshInstance3D existingMmi)
            {
                mmi = existingMmi;
                isExisting = true;
            }
            else if (existing != null)
            {
                return Task.FromResult(ErrorInvalidParams(
                    $"Node '{nodePath}' is not a MultiMeshInstance3D (is {existing.GetClass()})"));
            }
        }

        Node? parent = null;
        if (!isExisting)
        {
            var parentPath = OptionalString(@params, "parent_path", ".");
            parent = FindNodeByPath(parentPath);
            if (parent == null)
                return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'"));

            mmi = new MultiMeshInstance3D
            {
                Name = OptionalString(@params, "name", "MultiMeshInstance3D"),
            };
        }

        mmi ??= new MultiMeshInstance3D { Name = "MultiMeshInstance3D" };

        // Build or fetch the MultiMesh resource
        var multimesh = mmi.Multimesh ?? new MultiMesh();

        // Transform format
        if (@params.TryGetValue("transform_format", out var tfVal))
        {
            var tfStr = tfVal.ToString().ToLowerInvariant();
            multimesh.TransformFormat = tfStr switch
            {
                "2d" or "transform_2d" or "transform2d" => MultiMesh.TransformFormatEnum.Transform2D,
                "3d" or "transform_3d" or "transform3d" => MultiMesh.TransformFormatEnum.Transform3D,
                _ => multimesh.TransformFormat,
            };
        }

        // Mesh
        var meshPath = OptionalString(@params, "mesh_path", "");
        var meshType = OptionalString(@params, "mesh_type", "");
        if (!string.IsNullOrEmpty(meshPath))
        {
            if (!ResourceLoader.Exists(meshPath))
            {
                if (!isExisting) mmi.QueueFree();
                return Task.FromResult(ErrorNotFound($"Mesh '{meshPath}'"));
            }
            if (ResourceLoader.Load(meshPath) is Mesh loadedMesh)
                multimesh.Mesh = loadedMesh;
        }
        else if (!string.IsNullOrEmpty(meshType))
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
                if (!isExisting) mmi.QueueFree();
                return Task.FromResult(ErrorInvalidParams(
                    $"Unknown mesh_type '{meshType}'. Available: {string.Join(", ", meshClasses.Keys)}"));
            }
            multimesh.Mesh = factory();
        }

        // Instance count
        if (@params.ContainsKey("instance_count"))
            multimesh.InstanceCount = OptionalInt(@params, "instance_count", multimesh.InstanceCount);
        else if (!isExisting && multimesh.InstanceCount == 0)
            multimesh.InstanceCount = 1;

        if (@params.ContainsKey("visible_instance_count"))
            multimesh.VisibleInstanceCount = OptionalInt(@params, "visible_instance_count", multimesh.VisibleInstanceCount);

        // Per-instance transforms
        var transforms = OptionalArray(@params, "transforms");
        if (transforms != null && multimesh.TransformFormat == MultiMesh.TransformFormatEnum.Transform3D)
        {
            var count = Mathf.Min(transforms.Count, multimesh.InstanceCount);
            for (var i = 0; i < count; i++)
            {
                var t = transforms[i];
                if (t.VariantType != Variant.Type.Dictionary)
                    continue;
                var td = t.AsGodotDictionary();

                var pos = Vector3.Zero;
                if (td.TryGetValue("position", out var pv))
                    pos = pv.VariantType == Variant.Type.Dictionary
                        ? new Vector3(
                            (float)GetNum(pv.AsGodotDictionary(), "x", 0),
                            (float)GetNum(pv.AsGodotDictionary(), "y", 0),
                            (float)GetNum(pv.AsGodotDictionary(), "z", 0))
                        : pv.VariantType == Variant.Type.Vector3 ? pv.AsVector3() : Vector3.Zero;

                var rotDeg = Vector3.Zero;
                if (td.TryGetValue("rotation", out var rv))
                    rotDeg = rv.VariantType == Variant.Type.Dictionary
                        ? new Vector3(
                            (float)GetNum(rv.AsGodotDictionary(), "x", 0),
                            (float)GetNum(rv.AsGodotDictionary(), "y", 0),
                            (float)GetNum(rv.AsGodotDictionary(), "z", 0))
                        : rv.VariantType == Variant.Type.Vector3 ? rv.AsVector3() : Vector3.Zero;

                var scl = Vector3.One;
                if (td.TryGetValue("scale", out var sv))
                    scl = sv.VariantType == Variant.Type.Dictionary
                        ? new Vector3(
                            (float)GetNum(sv.AsGodotDictionary(), "x", 1),
                            (float)GetNum(sv.AsGodotDictionary(), "y", 1),
                            (float)GetNum(sv.AsGodotDictionary(), "z", 1))
                        : sv.VariantType == Variant.Type.Vector3 ? sv.AsVector3() : Vector3.One;

                var basis = Basis.FromEuler(new Vector3(
                    Mathf.DegToRad(rotDeg.X),
                    Mathf.DegToRad(rotDeg.Y),
                    Mathf.DegToRad(rotDeg.Z))).Scaled(scl);
                multimesh.SetInstanceTransform(i, new Transform3D(basis, pos));
            }
        }

        // Node transform
        if (@params.ContainsKey("position"))
            mmi.Position = ParseVector3(@params, "position", mmi.Position);
        if (@params.ContainsKey("rotation"))
            mmi.RotationDegrees = ParseVector3(@params, "rotation", mmi.RotationDegrees);

        // Assign multimesh (undo-tracked) and add to scene if new
        var oldMultimesh = mmi.Multimesh;
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction(isExisting ? $"MCP: Configure {mmi.Name}" : "MCP: Add MultiMeshInstance3D");
        undoRedo.AddDoProperty(mmi, MultiMeshInstance3D.PropertyName.Multimesh, multimesh);
        undoRedo.AddDoReference(multimesh);
        if (!isExisting)
        {
            undoRedo.AddDoMethod(parent!, Node.MethodName.AddChild, mmi);
            undoRedo.AddDoMethod(mmi, Node.MethodName.SetOwner, root);
            undoRedo.AddDoReference(mmi);
            undoRedo.AddUndoMethod(parent!, Node.MethodName.RemoveChild, mmi);
        }
        if (oldMultimesh != null)
            undoRedo.AddUndoProperty(mmi, MultiMeshInstance3D.PropertyName.Multimesh, oldMultimesh);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(mmi).ToString(),
            ["name"] = mmi.Name.ToString(),
            ["is_existing"] = isExisting,
            ["instance_count"] = multimesh.InstanceCount,
            ["visible_instance_count"] = multimesh.VisibleInstanceCount,
            ["transform_format"] = multimesh.TransformFormat == MultiMesh.TransformFormatEnum.Transform3D ? "3d" : "2d",
            ["has_mesh"] = multimesh.Mesh != null,
            ["transforms_applied"] = transforms?.Count ?? 0,
        }));
    }

    // ── 4. get_multimesh_info ────────────────────────────────────────────────

    private Task<Dictionary> GetMultiMeshInfo(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'"));
        if (node is not MultiMeshInstance3D mmi)
            return Task.FromResult(ErrorInvalidParams(
                $"Node '{nodePath}' is not a MultiMeshInstance3D (is {node.GetClass()})"));

        var mm = mmi.Multimesh;
        if (mm == null)
        {
            return Task.FromResult(Success(new Dictionary
            {
                ["node_path"] = nodePath,
                ["has_multimesh"] = false,
                ["instance_count"] = 0,
                ["visible_instances"] = 0,
            }));
        }

        var meshType = mm.Mesh != null ? mm.Mesh.GetClass() : "";
        var meshPathStr = mm.Mesh != null && !string.IsNullOrEmpty(mm.Mesh.ResourcePath)
            ? mm.Mesh.ResourcePath
            : "";

        var result = new Dictionary
        {
            ["node_path"] = nodePath,
            ["has_multimesh"] = true,
            ["instance_count"] = mm.InstanceCount,
            ["visible_instances"] = mm.VisibleInstanceCount,
            ["mesh_type"] = meshType,
            ["mesh_path"] = meshPathStr,
            ["transform_format"] = mm.TransformFormat == MultiMesh.TransformFormatEnum.Transform3D ? "3d" : "2d",
            ["use_colors"] = mm.UseColors,
            ["use_custom_data"] = mm.UseCustomData,
        };

        var sampleCount = OptionalInt(@params, "sample_count", 0);
        if (sampleCount > 0 && mm.TransformFormat == MultiMesh.TransformFormatEnum.Transform3D)
        {
            var samples = new Godot.Collections.Array();
            var n = Mathf.Min(sampleCount, mm.InstanceCount);
            for (var i = 0; i < n; i++)
            {
                var t = mm.GetInstanceTransform(i);
                samples.Add(new Dictionary
                {
                    ["index"] = i,
                    ["origin"] = t.Origin.ToString(),
                    ["basis"] = t.Basis.ToString(),
                });
            }
            result["transform_samples"] = samples;
        }

        return Task.FromResult(Success(result));
    }
}
