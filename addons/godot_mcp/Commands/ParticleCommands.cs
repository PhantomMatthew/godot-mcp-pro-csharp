using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// GPUParticles2D/3D authoring tools: creation, ParticleProcessMaterial tuning,
/// color gradients, named presets, and introspection. Faithful port of
/// addons/godot_mcp/commands/particle_commands.gd. Material/property mutations
/// go through EditorUndoRedoManager so they participate in the editor's undo
/// history.
/// </summary>
[Tool]
public partial class ParticleCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "create_particles",
            Description = "Create a GPUParticles2D or GPUParticles3D node (with a default ParticleProcessMaterial) as a child of an existing node.",
            Category = "particle",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node"),
                ["name"] = Schema.Str("Name for the new particles node", "Particles"),
                ["is_3d"] = Schema.Bool("Create a GPUParticles3D instead of GPUParticles2D", false),
                ["amount"] = Schema.Int("Number of particles", 16),
                ["lifetime"] = Schema.Num("Particle lifetime in seconds", 1.0),
                ["one_shot"] = Schema.Bool("Emit a single burst instead of continuously", false),
                ["explosiveness"] = Schema.Num("Explosiveness ratio (0-1)", 0.0),
                ["randomness"] = Schema.Num("Randomness ratio (0-1)", 0.0),
                ["emitting"] = Schema.Bool("Start emitting immediately", true),
            }, "parent_path"),
            Handler = CreateParticles,
        },
        new()
        {
            Name = "set_particle_material",
            Description = "Duplicate and modify a GPUParticles2D/3D node's ParticleProcessMaterial (undo-tracked). " +
                "Only the fields present in params are changed.",
            Category = "particle",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the GPUParticles2D/3D node"),
                ["direction"] = Schema.Any("Direction as Vector3(x,y,z) string or {x,y,z} dict"),
                ["spread"] = Schema.Num("Spread angle in degrees"),
                ["initial_velocity_min"] = Schema.Num("Minimum initial velocity"),
                ["initial_velocity_max"] = Schema.Num("Maximum initial velocity"),
                ["gravity"] = Schema.Any("Gravity vector as Vector3(x,y,z) string or {x,y,z} dict"),
                ["scale_min"] = Schema.Num("Minimum particle scale"),
                ["scale_max"] = Schema.Num("Maximum particle scale"),
                ["color"] = Schema.Str("Particle color: hex '#RRGGBB[AA]', named color, or Color(r,g,b,a) expression"),
                ["emission_shape"] = Schema.Enum("Emission shape", "point", "sphere", "sphere_surface", "box", "ring"),
                ["emission_sphere_radius"] = Schema.Num("Sphere/sphere_surface emission radius"),
                ["emission_box_extents"] = Schema.Any("Box emission extents as {x,y,z} dict"),
                ["emission_ring_radius"] = Schema.Num("Ring emission radius"),
                ["emission_ring_inner_radius"] = Schema.Num("Ring emission inner radius"),
                ["emission_ring_height"] = Schema.Num("Ring emission height"),
                ["angular_velocity_min"] = Schema.Num("Minimum angular velocity"),
                ["angular_velocity_max"] = Schema.Num("Maximum angular velocity"),
                ["orbit_velocity_min"] = Schema.Num("Minimum orbit velocity"),
                ["orbit_velocity_max"] = Schema.Num("Maximum orbit velocity"),
                ["damping_min"] = Schema.Num("Minimum damping"),
                ["damping_max"] = Schema.Num("Maximum damping"),
                ["attractor_interaction_enabled"] = Schema.Bool("Whether particles interact with attractors"),
            }, "node_path"),
            Handler = SetParticleMaterial,
        },
        new()
        {
            Name = "set_particle_color_gradient",
            Description = "Assign a Gradient/GradientTexture1D color ramp to a GPUParticles2D/3D node's ParticleProcessMaterial (undo-tracked).",
            Category = "particle",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the GPUParticles2D/3D node"),
                ["stops"] = Schema.Arr("Gradient stops", Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["offset"] = Schema.Num("Stop offset (0-1)"),
                    ["color"] = Schema.Str("Stop color: hex '#RRGGBB[AA]', named color, or Color(r,g,b,a) expression"),
                })),
            }, "node_path", "stops"),
            Handler = SetParticleColorGradient,
        },
        new()
        {
            Name = "apply_particle_preset",
            Description = "Apply a named particle preset (explosion, fire, smoke, sparks, rain, snow, magic, dust) " +
                "to a GPUParticles2D/3D node, replacing its ProcessMaterial and base emission properties (undo-tracked).",
            Category = "particle",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the GPUParticles2D/3D node"),
                ["preset"] = Schema.Enum("Preset name", "explosion", "fire", "smoke", "sparks", "rain", "snow", "magic", "dust"),
            }, "node_path", "preset"),
            Handler = ApplyParticlePreset,
        },
        new()
        {
            Name = "get_particle_info",
            Description = "Get the emission properties and ParticleProcessMaterial configuration of a GPUParticles2D/3D node.",
            Category = "particle",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the GPUParticles2D/3D node"),
            }, "node_path"),
            Handler = GetParticleInfo,
        },
    };

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Node? GetParticlesNodeAny(string nodePath)
    {
        var node = FindNodeByPath(nodePath);
        if (node is GpuParticles2D or GpuParticles3D)
            return node;
        return null;
    }

    /// <summary>Support hex "#RRGGBB", "#RRGGBBAA", named colors, or a Color(r,g,b,a) expression.</summary>
    private static Color ParseColor(string colorStr)
    {
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

    /// <summary>Parse a Vector3 from a Dictionary {x,y,z} or an expression string like "Vector3(1,2,3)".</summary>
    private static bool TryParseVector3(Variant value, out Vector3 result)
    {
        if (value.VariantType == Variant.Type.Dictionary)
        {
            var d = value.AsGodotDictionary();
            result = new Vector3(
                (float)GetNum(d, "x", 0),
                (float)GetNum(d, "y", 0),
                (float)GetNum(d, "z", 0));
            return true;
        }
        if (value.VariantType == Variant.Type.String)
        {
            using var expr = new Expression();
            if (expr.Parse(value.AsString()) == Godot.Error.Ok)
            {
                var parsed = expr.Execute();
                if (!expr.HasExecuteFailed() && parsed.VariantType == Variant.Type.Vector3)
                {
                    result = parsed.AsVector3();
                    return true;
                }
            }
        }
        result = Vector3.Zero;
        return false;
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

    private readonly record struct GradientStop(float Offset, Color Color);

    private static void ApplyGradient(ParticleProcessMaterial mat, IEnumerable<GradientStop> stops, bool deferred)
    {
        // Assign offsets/colors wholesale: Gradient.RemovePoint cannot drop below
        // 2 points, so clearing the default points one by one leaves a stray stop.
        var offsetList = new List<float>();
        var colorList = new List<Color>();
        foreach (var stop in stops)
        {
            offsetList.Add(stop.Offset);
            colorList.Add(stop.Color);
        }

        var gradient = new Gradient
        {
            Offsets = offsetList.ToArray(),
            Colors = colorList.ToArray(),
        };

        var gradTex = new GradientTexture1D { Gradient = gradient };
        if (deferred)
        {
            // Smaller texture to avoid GPU issues in compatibility mode.
            gradTex.Width = 64;
            // Defer color_ramp assignment to avoid editor crash during rendering.
            mat.SetDeferred("color_ramp", gradTex);
        }
        else
        {
            mat.ColorRamp = gradTex;
        }
    }

    private static Dictionary CaptureParticleState(Node node)
    {
        var state = new Dictionary();
        foreach (var property in new[] { "amount", "lifetime", "one_shot", "explosiveness", "randomness", "emitting", "process_material" })
        {
            if (HasProperty(node, property))
                state[property] = node.Get(property);
        }
        return state;
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

    private void RegisterParticleStateUndo(Node node, Dictionary oldState, Dictionary newState, string actionName)
    {
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction(actionName);
        foreach (var property in newState.Keys)
        {
            var propertyStr = property.AsString();
            var newValue = newState[property];
            undoRedo.AddDoProperty(node, propertyStr, newValue);
            if (newValue.VariantType == Variant.Type.Object && newValue.AsGodotObject() is Resource newRes)
                undoRedo.AddDoReference(newRes);
            var oldValue = oldState.TryGetValue(property, out var ov) ? ov : default;
            undoRedo.AddUndoProperty(node, propertyStr, oldValue);
            if (oldValue.VariantType == Variant.Type.Object && oldValue.AsGodotObject() is Resource oldRes)
                undoRedo.AddUndoReference(oldRes);
        }
        undoRedo.CommitAction();
    }

    // ── 1. create_particles ──────────────────────────────────────────────────

    private Task<Dictionary> CreateParticles(Dictionary @params)
    {
        var (parentPath, err) = RequireString(@params, "parent_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Node at '{parentPath}'"));

        var nodeName = OptionalString(@params, "name", "Particles");
        var is3D = OptionalBool(@params, "is_3d", false);
        var amount = OptionalInt(@params, "amount", 16);
        var lifetime = OptionalFloat(@params, "lifetime", 1.0);
        var oneShot = OptionalBool(@params, "one_shot", false);
        var explosiveness = (float)OptionalFloat(@params, "explosiveness", 0.0);
        var randomness = (float)OptionalFloat(@params, "randomness", 0.0);
        var emitting = OptionalBool(@params, "emitting", true);

        Node particlesNode;
        if (is3D)
        {
            var p = new GpuParticles3D
            {
                Name = nodeName,
                Amount = amount,
                Lifetime = lifetime,
                OneShot = oneShot,
                Explosiveness = explosiveness,
                Randomness = randomness,
                Emitting = emitting,
                ProcessMaterial = new ParticleProcessMaterial(),
            };
            particlesNode = p;
        }
        else
        {
            var p = new GpuParticles2D
            {
                Name = nodeName,
                Amount = amount,
                Lifetime = lifetime,
                OneShot = oneShot,
                Explosiveness = explosiveness,
                Randomness = randomness,
                Emitting = emitting,
                ProcessMaterial = new ParticleProcessMaterial(),
            };
            particlesNode = p;
        }

        AddChildWithUndo(parent, particlesNode, root, "MCP: Create particles");

        return Task.FromResult(Success(new Dictionary
        {
            ["name"] = particlesNode.Name.ToString(),
            ["parent"] = parentPath,
            ["is_3d"] = is3D,
            ["amount"] = amount,
            ["lifetime"] = lifetime,
            ["one_shot"] = oneShot,
            ["created"] = true,
        }));
    }

    // ── 2. set_particle_material ─────────────────────────────────────────────

    private Task<Dictionary> SetParticleMaterial(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var node = GetParticlesNodeAny(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"GPUParticles2D/3D at '{nodePath}'"));

        var oldMatVariant = node.Get("process_material");
        var mat = oldMatVariant.VariantType == Variant.Type.Object && oldMatVariant.AsGodotObject() is ParticleProcessMaterial oldMat
            ? (ParticleProcessMaterial)oldMat.Duplicate(true)
            : new ParticleProcessMaterial();

        var changes = new Godot.Collections.Array();

        // Direction
        if (@params.TryGetValue("direction", out var dirVal) && TryParseVector3(dirVal, out var direction))
        {
            mat.Direction = direction;
            changes.Add("direction");
        }

        // Spread
        if (@params.ContainsKey("spread"))
        {
            mat.Spread = (float)OptionalFloat(@params, "spread", mat.Spread);
            changes.Add("spread");
        }

        // Initial velocity
        if (@params.ContainsKey("initial_velocity_min"))
        {
            mat.InitialVelocityMin = (float)OptionalFloat(@params, "initial_velocity_min", mat.InitialVelocityMin);
            changes.Add("initial_velocity_min");
        }
        if (@params.ContainsKey("initial_velocity_max"))
        {
            mat.InitialVelocityMax = (float)OptionalFloat(@params, "initial_velocity_max", mat.InitialVelocityMax);
            changes.Add("initial_velocity_max");
        }

        // Gravity
        if (@params.TryGetValue("gravity", out var gravVal) && TryParseVector3(gravVal, out var gravity))
        {
            mat.Gravity = gravity;
            changes.Add("gravity");
        }

        // Scale
        if (@params.ContainsKey("scale_min"))
        {
            mat.ScaleMin = (float)OptionalFloat(@params, "scale_min", mat.ScaleMin);
            changes.Add("scale_min");
        }
        if (@params.ContainsKey("scale_max"))
        {
            mat.ScaleMax = (float)OptionalFloat(@params, "scale_max", mat.ScaleMax);
            changes.Add("scale_max");
        }

        // Color
        if (@params.TryGetValue("color", out var colorVal))
        {
            mat.Color = ParseColor(colorVal.ToString());
            changes.Add("color");
        }

        // Emission shape
        if (@params.TryGetValue("emission_shape", out var shapeVal))
        {
            var shapeStr = shapeVal.ToString().ToLowerInvariant();
            switch (shapeStr)
            {
                case "point":
                    mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Point;
                    break;
                case "sphere":
                    mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere;
                    if (@params.ContainsKey("emission_sphere_radius"))
                    {
                        mat.EmissionSphereRadius = (float)OptionalFloat(@params, "emission_sphere_radius", mat.EmissionSphereRadius);
                        changes.Add("emission_sphere_radius");
                    }
                    break;
                case "sphere_surface":
                    mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.SphereSurface;
                    if (@params.ContainsKey("emission_sphere_radius"))
                    {
                        mat.EmissionSphereRadius = (float)OptionalFloat(@params, "emission_sphere_radius", mat.EmissionSphereRadius);
                        changes.Add("emission_sphere_radius");
                    }
                    break;
                case "box":
                    mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
                    if (@params.TryGetValue("emission_box_extents", out var extVal) && extVal.VariantType == Variant.Type.Dictionary)
                    {
                        var ext = extVal.AsGodotDictionary();
                        mat.EmissionBoxExtents = new Vector3(
                            (float)GetNum(ext, "x", 1),
                            (float)GetNum(ext, "y", 1),
                            (float)GetNum(ext, "z", 1));
                        changes.Add("emission_box_extents");
                    }
                    break;
                case "ring":
                    mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring;
                    if (@params.ContainsKey("emission_ring_radius"))
                    {
                        mat.EmissionRingRadius = (float)OptionalFloat(@params, "emission_ring_radius", mat.EmissionRingRadius);
                        changes.Add("emission_ring_radius");
                    }
                    if (@params.ContainsKey("emission_ring_inner_radius"))
                    {
                        mat.EmissionRingInnerRadius = (float)OptionalFloat(@params, "emission_ring_inner_radius", mat.EmissionRingInnerRadius);
                        changes.Add("emission_ring_inner_radius");
                    }
                    if (@params.ContainsKey("emission_ring_height"))
                    {
                        mat.EmissionRingHeight = (float)OptionalFloat(@params, "emission_ring_height", mat.EmissionRingHeight);
                        changes.Add("emission_ring_height");
                    }
                    break;
            }
            changes.Add("emission_shape");
        }

        // Angular velocity
        if (@params.ContainsKey("angular_velocity_min"))
        {
            mat.AngularVelocityMin = (float)OptionalFloat(@params, "angular_velocity_min", mat.AngularVelocityMin);
            changes.Add("angular_velocity_min");
        }
        if (@params.ContainsKey("angular_velocity_max"))
        {
            mat.AngularVelocityMax = (float)OptionalFloat(@params, "angular_velocity_max", mat.AngularVelocityMax);
            changes.Add("angular_velocity_max");
        }

        // Orbit velocity
        if (@params.ContainsKey("orbit_velocity_min"))
        {
            mat.OrbitVelocityMin = (float)OptionalFloat(@params, "orbit_velocity_min", mat.OrbitVelocityMin);
            changes.Add("orbit_velocity_min");
        }
        if (@params.ContainsKey("orbit_velocity_max"))
        {
            mat.OrbitVelocityMax = (float)OptionalFloat(@params, "orbit_velocity_max", mat.OrbitVelocityMax);
            changes.Add("orbit_velocity_max");
        }

        // Damping
        if (@params.ContainsKey("damping_min"))
        {
            mat.DampingMin = (float)OptionalFloat(@params, "damping_min", mat.DampingMin);
            changes.Add("damping_min");
        }
        if (@params.ContainsKey("damping_max"))
        {
            mat.DampingMax = (float)OptionalFloat(@params, "damping_max", mat.DampingMax);
            changes.Add("damping_max");
        }

        // Attractor interaction
        if (@params.ContainsKey("attractor_interaction_enabled"))
        {
            mat.AttractorInteractionEnabled = OptionalBool(@params, "attractor_interaction_enabled", mat.AttractorInteractionEnabled);
            changes.Add("attractor_interaction_enabled");
        }

        if (changes.Count > 0)
            SetPropertyWithUndo(node, "process_material", mat, "MCP: Set particle material");

        return Task.FromResult(Success(new Dictionary { ["node_path"] = nodePath, ["changes"] = changes }));
    }

    // ── 3. set_particle_color_gradient ───────────────────────────────────────

    private Task<Dictionary> SetParticleColorGradient(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var node = GetParticlesNodeAny(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"GPUParticles2D/3D at '{nodePath}'"));

        var oldMatVariant = node.Get("process_material");
        var mat = oldMatVariant.VariantType == Variant.Type.Object && oldMatVariant.AsGodotObject() is ParticleProcessMaterial oldMat
            ? (ParticleProcessMaterial)oldMat.Duplicate(true)
            : new ParticleProcessMaterial();

        if (!@params.TryGetValue("stops", out var stopsVal) || stopsVal.VariantType != Variant.Type.Array)
            return Task.FromResult(ErrorInvalidParams("Missing required parameter: stops (array of {offset, color})"));

        var stopsArr = stopsVal.AsGodotArray();
        if (stopsArr.Count == 0)
            return Task.FromResult(ErrorInvalidParams("stops array must not be empty"));

        // Gradient.RemovePoint refuses to drop below 2 points, so a fresh Gradient
        // cannot be cleared point by point — assign offsets/colors wholesale instead.
        var offsets = new List<float>();
        var colors = new List<Color>();
        foreach (var stop in stopsArr)
        {
            if (stop.VariantType != Variant.Type.Dictionary)
                continue;
            var stopDict = stop.AsGodotDictionary();
            offsets.Add((float)GetNum(stopDict, "offset", 0.0));
            var colorStr = stopDict.TryGetValue("color", out var cv) ? cv.ToString() : "#ffffff";
            colors.Add(ParseColor(colorStr));
        }
        if (offsets.Count == 0)
            return Task.FromResult(ErrorInvalidParams("stops array must contain {offset, color} dictionaries"));

        var gradient = new Gradient
        {
            Offsets = offsets.ToArray(),
            Colors = colors.ToArray(),
        };

        var gradTex = new GradientTexture1D { Gradient = gradient };
        mat.ColorRamp = gradTex;
        SetPropertyWithUndo(node, "process_material", mat, "MCP: Set particle color gradient");

        return Task.FromResult(Success(new Dictionary { ["node_path"] = nodePath, ["stops_count"] = gradient.GetPointCount() }));
    }

    // ── 4. apply_particle_preset ──────────────────────────────────────────────

    private Task<Dictionary> ApplyParticlePreset(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var (presetRaw, err2) = RequireString(@params, "preset");
        if (err2 != null) return Task.FromResult(err2);
        var preset = presetRaw.ToLowerInvariant();

        var node = GetParticlesNodeAny(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"GPUParticles2D/3D at '{nodePath}'"));

        var oldState = CaptureParticleState(node);
        var presetState = new Dictionary();
        var mat = new ParticleProcessMaterial();
        var is2D = node is GpuParticles2D;

        // Default gravity for 2D (Y-down) vs 3D (Y-down)
        var gravityDown = new Vector3(0, is2D ? 98.0f : 9.8f, 0);
        var gravityNone = Vector3.Zero;

        switch (preset)
        {
            case "explosion":
                presetState["amount"] = 32;
                presetState["lifetime"] = 0.6;
                presetState["one_shot"] = true;
                presetState["explosiveness"] = 1.0f;
                mat.Direction = is2D ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
                mat.Spread = 180.0f;
                mat.InitialVelocityMin = is2D ? 100.0f : 5.0f;
                mat.InitialVelocityMax = is2D ? 200.0f : 10.0f;
                mat.Gravity = gravityDown * 0.5f;
                mat.DampingMin = 2.0f;
                mat.DampingMax = 4.0f;
                mat.ScaleMin = 0.5f;
                mat.ScaleMax = 1.5f;
                mat.Color = new Color(1.0f, 0.6f, 0.1f);
                ApplyGradient(mat, new[]
                {
                    new GradientStop(0.0f, Colors.White),
                    new GradientStop(0.3f, new Color(1.0f, 0.8f, 0.2f)),
                    new GradientStop(0.7f, new Color(1.0f, 0.3f, 0.0f)),
                    new GradientStop(1.0f, new Color(0.2f, 0.0f, 0.0f, 0.0f)),
                }, deferred: true);
                break;

            case "fire":
                presetState["amount"] = 24;
                presetState["lifetime"] = 1.2;
                presetState["one_shot"] = false;
                presetState["explosiveness"] = 0.0f;
                mat.Direction = is2D ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
                mat.Spread = 15.0f;
                mat.InitialVelocityMin = is2D ? 30.0f : 1.5f;
                mat.InitialVelocityMax = is2D ? 60.0f : 3.0f;
                mat.Gravity = gravityNone;
                mat.ScaleMin = 0.8f;
                mat.ScaleMax = 1.5f;
                ApplyGradient(mat, new[]
                {
                    new GradientStop(0.0f, new Color(1.0f, 1.0f, 0.5f)),
                    new GradientStop(0.3f, new Color(1.0f, 0.6f, 0.0f)),
                    new GradientStop(0.7f, new Color(0.8f, 0.2f, 0.0f)),
                    new GradientStop(1.0f, new Color(0.2f, 0.0f, 0.0f, 0.0f)),
                }, deferred: true);
                break;

            case "smoke":
                presetState["amount"] = 16;
                presetState["lifetime"] = 3.0;
                presetState["one_shot"] = false;
                presetState["explosiveness"] = 0.0f;
                mat.Direction = is2D ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
                mat.Spread = 25.0f;
                mat.InitialVelocityMin = is2D ? 10.0f : 0.5f;
                mat.InitialVelocityMax = is2D ? 25.0f : 1.2f;
                mat.Gravity = gravityNone;
                mat.ScaleMin = 1.5f;
                mat.ScaleMax = 3.0f;
                mat.DampingMin = 1.0f;
                mat.DampingMax = 2.0f;
                ApplyGradient(mat, new[]
                {
                    new GradientStop(0.0f, new Color(0.5f, 0.5f, 0.5f, 0.6f)),
                    new GradientStop(0.5f, new Color(0.6f, 0.6f, 0.6f, 0.3f)),
                    new GradientStop(1.0f, new Color(0.7f, 0.7f, 0.7f, 0.0f)),
                }, deferred: true);
                break;

            case "sparks":
                presetState["amount"] = 48;
                presetState["lifetime"] = 0.4;
                presetState["one_shot"] = true;
                presetState["explosiveness"] = 0.95f;
                mat.Direction = is2D ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
                mat.Spread = 180.0f;
                mat.InitialVelocityMin = is2D ? 200.0f : 8.0f;
                mat.InitialVelocityMax = is2D ? 400.0f : 16.0f;
                mat.Gravity = gravityDown;
                mat.ScaleMin = 0.1f;
                mat.ScaleMax = 0.3f;
                mat.DampingMin = 1.0f;
                mat.DampingMax = 3.0f;
                ApplyGradient(mat, new[]
                {
                    new GradientStop(0.0f, new Color(1.0f, 1.0f, 0.8f)),
                    new GradientStop(0.5f, new Color(1.0f, 0.7f, 0.2f)),
                    new GradientStop(1.0f, new Color(1.0f, 0.3f, 0.0f, 0.0f)),
                }, deferred: true);
                break;

            case "rain":
                presetState["amount"] = 64;
                presetState["lifetime"] = 0.8;
                presetState["one_shot"] = false;
                presetState["explosiveness"] = 0.0f;
                mat.Direction = is2D ? new Vector3(0, 1, 0) : new Vector3(0, -1, 0);
                mat.Spread = 5.0f;
                mat.InitialVelocityMin = is2D ? 300.0f : 12.0f;
                mat.InitialVelocityMax = is2D ? 400.0f : 16.0f;
                mat.Gravity = gravityDown;
                mat.ScaleMin = 0.1f;
                mat.ScaleMax = 0.2f;
                mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
                mat.EmissionBoxExtents = is2D ? new Vector3(200, 0, 0) : new Vector3(5, 0, 5);
                mat.Color = new Color(0.6f, 0.7f, 1.0f, 0.7f);
                break;

            case "snow":
                presetState["amount"] = 48;
                presetState["lifetime"] = 4.0;
                presetState["one_shot"] = false;
                presetState["explosiveness"] = 0.0f;
                mat.Direction = is2D ? new Vector3(0, 1, 0) : new Vector3(0, -1, 0);
                mat.Spread = 20.0f;
                mat.InitialVelocityMin = is2D ? 20.0f : 0.8f;
                mat.InitialVelocityMax = is2D ? 40.0f : 1.5f;
                mat.Gravity = is2D ? new Vector3(0, 20, 0) : new Vector3(0, -0.5f, 0);
                mat.ScaleMin = 0.3f;
                mat.ScaleMax = 0.8f;
                mat.AngularVelocityMin = -45.0f;
                mat.AngularVelocityMax = 45.0f;
                mat.DampingMin = 0.5f;
                mat.DampingMax = 1.5f;
                mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
                mat.EmissionBoxExtents = is2D ? new Vector3(200, 0, 0) : new Vector3(5, 0, 5);
                mat.Color = new Color(1.0f, 1.0f, 1.0f, 0.9f);
                break;

            case "magic":
                presetState["amount"] = 24;
                presetState["lifetime"] = 2.0;
                presetState["one_shot"] = false;
                presetState["explosiveness"] = 0.0f;
                mat.Direction = is2D ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
                mat.Spread = 180.0f;
                mat.InitialVelocityMin = is2D ? 20.0f : 1.0f;
                mat.InitialVelocityMax = is2D ? 50.0f : 2.5f;
                mat.Gravity = gravityNone;
                mat.OrbitVelocityMin = 0.5f;
                mat.OrbitVelocityMax = 1.5f;
                mat.ScaleMin = 0.3f;
                mat.ScaleMax = 0.8f;
                mat.DampingMin = 1.0f;
                mat.DampingMax = 2.0f;
                ApplyGradient(mat, new[]
                {
                    new GradientStop(0.0f, new Color(0.3f, 0.5f, 1.0f)),
                    new GradientStop(0.25f, new Color(1.0f, 0.3f, 0.8f)),
                    new GradientStop(0.5f, new Color(0.3f, 1.0f, 0.5f)),
                    new GradientStop(0.75f, new Color(1.0f, 0.8f, 0.2f)),
                    new GradientStop(1.0f, new Color(0.5f, 0.3f, 1.0f, 0.0f)),
                }, deferred: true);
                break;

            case "dust":
                presetState["amount"] = 12;
                presetState["lifetime"] = 5.0;
                presetState["one_shot"] = false;
                presetState["explosiveness"] = 0.0f;
                mat.Direction = is2D ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
                mat.Spread = 180.0f;
                mat.InitialVelocityMin = is2D ? 3.0f : 0.1f;
                mat.InitialVelocityMax = is2D ? 8.0f : 0.3f;
                mat.Gravity = gravityNone;
                mat.ScaleMin = 0.2f;
                mat.ScaleMax = 0.5f;
                mat.DampingMin = 0.5f;
                mat.DampingMax = 1.0f;
                mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
                mat.EmissionBoxExtents = is2D ? new Vector3(100, 100, 0) : new Vector3(3, 3, 3);
                ApplyGradient(mat, new[]
                {
                    new GradientStop(0.0f, new Color(0.8f, 0.75f, 0.65f, 0.0f)),
                    new GradientStop(0.2f, new Color(0.8f, 0.75f, 0.65f, 0.3f)),
                    new GradientStop(0.8f, new Color(0.8f, 0.75f, 0.65f, 0.3f)),
                    new GradientStop(1.0f, new Color(0.8f, 0.75f, 0.65f, 0.0f)),
                }, deferred: true);
                break;

            default:
                return Task.FromResult(ErrorInvalidParams(
                    $"Unknown preset: '{preset}'. Valid presets: explosion, fire, smoke, sparks, rain, snow, magic, dust"));
        }

        presetState["process_material"] = mat;
        RegisterParticleStateUndo(node, oldState, presetState, "MCP: Apply particle preset");

        return Task.FromResult(Success(new Dictionary { ["node_path"] = nodePath, ["preset"] = preset, ["applied"] = true }));
    }

    // ── 5. get_particle_info ─────────────────────────────────────────────────

    private Task<Dictionary> GetParticleInfo(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var node = GetParticlesNodeAny(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"GPUParticles2D/3D at '{nodePath}'"));

        var info = new Dictionary
        {
            ["node_path"] = nodePath,
            ["type"] = node.GetClass(),
            ["amount"] = node.Get("amount"),
            ["lifetime"] = node.Get("lifetime"),
            ["one_shot"] = node.Get("one_shot"),
            ["explosiveness"] = node.Get("explosiveness"),
            ["randomness"] = node.Get("randomness"),
            ["emitting"] = node.Get("emitting"),
        };

        var matVariant = node.Get("process_material");
        if (matVariant.VariantType == Variant.Type.Object && matVariant.AsGodotObject() is ParticleProcessMaterial mat)
        {
            var matInfo = new Dictionary
            {
                ["direction"] = mat.Direction.ToString(),
                ["spread"] = mat.Spread,
                ["initial_velocity_min"] = mat.InitialVelocityMin,
                ["initial_velocity_max"] = mat.InitialVelocityMax,
                ["gravity"] = mat.Gravity.ToString(),
                ["scale_min"] = mat.ScaleMin,
                ["scale_max"] = mat.ScaleMax,
                ["color"] = mat.Color.ToString(),
                ["angular_velocity_min"] = mat.AngularVelocityMin,
                ["angular_velocity_max"] = mat.AngularVelocityMax,
                ["orbit_velocity_min"] = mat.OrbitVelocityMin,
                ["orbit_velocity_max"] = mat.OrbitVelocityMax,
                ["damping_min"] = mat.DampingMin,
                ["damping_max"] = mat.DampingMax,
                ["attractor_interaction_enabled"] = mat.AttractorInteractionEnabled,
            };

            // Emission shape
            var shapeName = mat.EmissionShape switch
            {
                ParticleProcessMaterial.EmissionShapeEnum.Point => "point",
                ParticleProcessMaterial.EmissionShapeEnum.Sphere => "sphere",
                ParticleProcessMaterial.EmissionShapeEnum.SphereSurface => "sphere_surface",
                ParticleProcessMaterial.EmissionShapeEnum.Box => "box",
                ParticleProcessMaterial.EmissionShapeEnum.Ring => "ring",
                _ => $"unknown({(int)mat.EmissionShape})",
            };
            matInfo["emission_shape"] = shapeName;

            switch (mat.EmissionShape)
            {
                case ParticleProcessMaterial.EmissionShapeEnum.Sphere:
                case ParticleProcessMaterial.EmissionShapeEnum.SphereSurface:
                    matInfo["emission_sphere_radius"] = mat.EmissionSphereRadius;
                    break;
                case ParticleProcessMaterial.EmissionShapeEnum.Box:
                    matInfo["emission_box_extents"] = mat.EmissionBoxExtents.ToString();
                    break;
                case ParticleProcessMaterial.EmissionShapeEnum.Ring:
                    matInfo["emission_ring_radius"] = mat.EmissionRingRadius;
                    matInfo["emission_ring_inner_radius"] = mat.EmissionRingInnerRadius;
                    matInfo["emission_ring_height"] = mat.EmissionRingHeight;
                    break;
            }

            // Color gradient
            if (mat.ColorRamp is GradientTexture1D gradTex && gradTex.Gradient is Gradient grad)
            {
                var gradientStops = new Godot.Collections.Array();
                for (var i = 0; i < grad.GetPointCount(); i++)
                {
                    gradientStops.Add(new Dictionary
                    {
                        ["offset"] = grad.GetOffset(i),
                        ["color"] = grad.GetColor(i).ToString(),
                    });
                }
                matInfo["color_ramp"] = gradientStops;
            }

            info["material"] = matInfo;
        }
        else
        {
            info["material"] = default(Variant);
        }

        return Task.FromResult(Success(info));
    }
}
