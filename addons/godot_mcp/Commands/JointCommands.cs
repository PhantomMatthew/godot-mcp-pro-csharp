using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// Physics joint tools: create joints between physics bodies, configure
/// joint-specific params/flags (hinge limits & motor, slider limits, cone-twist
/// spans, 6DOF per-axis settings), and list joints in the edited scene.
/// All scene mutations go through EditorUndoRedoManager so they participate in
/// the editor's undo history.
/// </summary>
[Tool]
public partial class JointCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "create_joint",
            Description = "Create a physics joint node connecting two physics bodies. " +
                "joint_type: pin_2d (PinJoint2D), pin_3d (PinJoint3D), hinge_3d (HingeJoint3D), " +
                "slider_3d (SliderJoint3D), cone_twist_3d (ConeTwistJoint3D), generic_6dof_3d (Generic6DOFJoint3D). " +
                "node_a/node_b are scene-relative paths to the bodies (2D joints need PhysicsBody2D, 3D joints need PhysicsBody3D); " +
                "they are stored as NodePaths relative to the joint. Undo-tracked.",
            Category = "joints",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["joint_type"] = Schema.Enum("Type of joint to create",
                    "pin_2d", "pin_3d", "hinge_3d", "slider_3d", "cone_twist_3d", "generic_6dof_3d"),
                ["node_a"] = Schema.Str("Scene-relative path of the first physics body"),
                ["node_b"] = Schema.Str("Scene-relative path of the second physics body"),
                ["name"] = Schema.Str("Name for the new joint node (defaults to the engine class name)"),
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node the joint is added to", "."),
                ["properties"] = Schema.Obj("Optional extra joint properties, e.g. softness (pin_2d), bias, " +
                    "disable_collision (2D) / exclude_nodes_from_collision (3D), solver_priority (3D)"),
            }, "joint_type", "node_a", "node_b"),
            Handler = CreateJoint,
        },
        new()
        {
            Name = "configure_joint",
            Description = "Configure an existing physics joint: re-target node_a/node_b, set generic properties " +
                "(bias, softness, disable_collision, exclude_nodes_from_collision, solver_priority), type-specific " +
                "float params (pin_3d: bias/damping/impulse_clamp; hinge_3d: limit_upper/limit_lower/limit_bias/" +
                "limit_softness/limit_relaxation/motor_target_velocity/motor_max_impulse/bias; slider_3d: " +
                "linear_limit_upper/linear_limit_lower/linear_motion_*/linear_orthogonal_*/angular_*; " +
                "cone_twist_3d: swing_span/twist_span/bias/softness/relaxation; generic_6dof_3d: per-axis params " +
                "with _x/_y/_z suffix, e.g. linear_limit_upper_x, angular_motor_target_velocity_z) and boolean flags " +
                "(hinge_3d: use_limit/enable_motor; generic_6dof_3d: enable_linear_limit_*/enable_angular_limit_*/" +
                "enable_linear_spring_*/enable_angular_spring_*/enable_linear_motor_*/enable_motor_*). Undo-tracked.",
            Category = "joints",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the joint node"),
                ["node_a"] = Schema.Str("New scene-relative path for the first body"),
                ["node_b"] = Schema.Str("New scene-relative path for the second body"),
                ["properties"] = Schema.Obj("Generic joint properties: bias, softness, disable_collision, " +
                    "exclude_nodes_from_collision, solver_priority, angular_limit_enabled, angular_limit_lower, " +
                    "angular_limit_upper, motor_enabled, motor_target_velocity (pin_2d)"),
                ["params"] = Schema.Obj("Type-specific float params, e.g. for hinge_3d: {limit_upper: 1.57, " +
                    "motor_target_velocity: 2.0}; for generic_6dof_3d append _x/_y/_z (default _x)"),
                ["flags"] = Schema.Obj("Type-specific boolean flags, e.g. for hinge_3d: {use_limit: true, " +
                    "enable_motor: false}; for generic_6dof_3d append _x/_y/_z (default _x)"),
            }, "node_path"),
            Handler = ConfigureJoint,
        },
        new()
        {
            Name = "list_joints",
            Description = "Find all physics joint nodes (Joint2D/Joint3D descendants) in the edited scene tree. " +
                "Returns an array of {path, type, node_a, node_b}.",
            Category = "joints",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Optional scene-relative path to scope the search (defaults to the scene root)", "."),
            }),
            Handler = ListJoints,
        },
    };

    // ── Type-specific param/flag name tables ───────────────────────────────────

    private static readonly System.Collections.Generic.Dictionary<string, PinJoint3D.Param> Pin3DParams = new()
    {
        ["bias"] = PinJoint3D.Param.Bias,
        ["damping"] = PinJoint3D.Param.Damping,
        ["impulse_clamp"] = PinJoint3D.Param.ImpulseClamp,
    };

    private static readonly System.Collections.Generic.Dictionary<string, HingeJoint3D.Param> HingeParams = new()
    {
        ["bias"] = HingeJoint3D.Param.Bias,
        ["limit_upper"] = HingeJoint3D.Param.LimitUpper,
        ["limit_lower"] = HingeJoint3D.Param.LimitLower,
        ["limit_bias"] = HingeJoint3D.Param.LimitBias,
#pragma warning disable CS0618
        ["limit_softness"] = HingeJoint3D.Param.LimitSoftness,
#pragma warning restore CS0618
        ["limit_relaxation"] = HingeJoint3D.Param.LimitRelaxation,
        ["motor_target_velocity"] = HingeJoint3D.Param.MotorTargetVelocity,
        ["motor_max_impulse"] = HingeJoint3D.Param.MotorMaxImpulse,
    };

    private static readonly System.Collections.Generic.Dictionary<string, HingeJoint3D.Flag> HingeFlags = new()
    {
        ["use_limit"] = HingeJoint3D.Flag.UseLimit,
        ["enable_motor"] = HingeJoint3D.Flag.EnableMotor,
    };

    private static readonly System.Collections.Generic.Dictionary<string, SliderJoint3D.Param> SliderParams = new()
    {
        ["linear_limit_upper"] = SliderJoint3D.Param.LinearLimitUpper,
        ["linear_limit_lower"] = SliderJoint3D.Param.LinearLimitLower,
        ["linear_limit_softness"] = SliderJoint3D.Param.LinearLimitSoftness,
        ["linear_limit_restitution"] = SliderJoint3D.Param.LinearLimitRestitution,
        ["linear_limit_damping"] = SliderJoint3D.Param.LinearLimitDamping,
        ["linear_motion_softness"] = SliderJoint3D.Param.LinearMotionSoftness,
        ["linear_motion_restitution"] = SliderJoint3D.Param.LinearMotionRestitution,
        ["linear_motion_damping"] = SliderJoint3D.Param.LinearMotionDamping,
        ["linear_orthogonal_softness"] = SliderJoint3D.Param.LinearOrthogonalSoftness,
        ["linear_orthogonal_restitution"] = SliderJoint3D.Param.LinearOrthogonalRestitution,
        ["linear_orthogonal_damping"] = SliderJoint3D.Param.LinearOrthogonalDamping,
        ["angular_limit_upper"] = SliderJoint3D.Param.AngularLimitUpper,
        ["angular_limit_lower"] = SliderJoint3D.Param.AngularLimitLower,
        ["angular_limit_softness"] = SliderJoint3D.Param.AngularLimitSoftness,
        ["angular_limit_restitution"] = SliderJoint3D.Param.AngularLimitRestitution,
        ["angular_limit_damping"] = SliderJoint3D.Param.AngularLimitDamping,
        ["angular_motion_softness"] = SliderJoint3D.Param.AngularMotionSoftness,
        ["angular_motion_restitution"] = SliderJoint3D.Param.AngularMotionRestitution,
        ["angular_motion_damping"] = SliderJoint3D.Param.AngularMotionDamping,
        ["angular_orthogonal_softness"] = SliderJoint3D.Param.AngularOrthogonalSoftness,
        ["angular_orthogonal_restitution"] = SliderJoint3D.Param.AngularOrthogonalRestitution,
        ["angular_orthogonal_damping"] = SliderJoint3D.Param.AngularOrthogonalDamping,
    };

    private static readonly System.Collections.Generic.Dictionary<string, ConeTwistJoint3D.Param> ConeTwistParams = new()
    {
        ["swing_span"] = ConeTwistJoint3D.Param.SwingSpan,
        ["twist_span"] = ConeTwistJoint3D.Param.TwistSpan,
        ["bias"] = ConeTwistJoint3D.Param.Bias,
        ["softness"] = ConeTwistJoint3D.Param.Softness,
        ["relaxation"] = ConeTwistJoint3D.Param.Relaxation,
    };

    private static readonly System.Collections.Generic.Dictionary<string, Generic6DofJoint3D.Param> SixDofParams = new()
    {
        ["linear_lower_limit"] = Generic6DofJoint3D.Param.LinearLowerLimit,
        ["linear_upper_limit"] = Generic6DofJoint3D.Param.LinearUpperLimit,
        ["linear_limit_lower"] = Generic6DofJoint3D.Param.LinearLowerLimit,
        ["linear_limit_upper"] = Generic6DofJoint3D.Param.LinearUpperLimit,
        ["linear_limit_softness"] = Generic6DofJoint3D.Param.LinearLimitSoftness,
        ["linear_restitution"] = Generic6DofJoint3D.Param.LinearRestitution,
        ["linear_limit_restitution"] = Generic6DofJoint3D.Param.LinearRestitution,
        ["linear_damping"] = Generic6DofJoint3D.Param.LinearDamping,
        ["linear_limit_damping"] = Generic6DofJoint3D.Param.LinearDamping,
        ["linear_motor_target_velocity"] = Generic6DofJoint3D.Param.LinearMotorTargetVelocity,
        ["linear_motor_force_limit"] = Generic6DofJoint3D.Param.LinearMotorForceLimit,
        ["linear_spring_stiffness"] = Generic6DofJoint3D.Param.LinearSpringStiffness,
        ["linear_spring_damping"] = Generic6DofJoint3D.Param.LinearSpringDamping,
        ["linear_spring_equilibrium_point"] = Generic6DofJoint3D.Param.LinearSpringEquilibriumPoint,
        ["angular_lower_limit"] = Generic6DofJoint3D.Param.AngularLowerLimit,
        ["angular_upper_limit"] = Generic6DofJoint3D.Param.AngularUpperLimit,
        ["angular_limit_lower"] = Generic6DofJoint3D.Param.AngularLowerLimit,
        ["angular_limit_upper"] = Generic6DofJoint3D.Param.AngularUpperLimit,
        ["angular_limit_softness"] = Generic6DofJoint3D.Param.AngularLimitSoftness,
        ["angular_damping"] = Generic6DofJoint3D.Param.AngularDamping,
        ["angular_limit_damping"] = Generic6DofJoint3D.Param.AngularDamping,
        ["angular_restitution"] = Generic6DofJoint3D.Param.AngularRestitution,
        ["angular_limit_restitution"] = Generic6DofJoint3D.Param.AngularRestitution,
        ["angular_force_limit"] = Generic6DofJoint3D.Param.AngularForceLimit,
        ["angular_erp"] = Generic6DofJoint3D.Param.AngularErp,
        ["angular_motor_target_velocity"] = Generic6DofJoint3D.Param.AngularMotorTargetVelocity,
        ["angular_motor_force_limit"] = Generic6DofJoint3D.Param.AngularMotorForceLimit,
        ["angular_spring_stiffness"] = Generic6DofJoint3D.Param.AngularSpringStiffness,
        ["angular_spring_damping"] = Generic6DofJoint3D.Param.AngularSpringDamping,
        ["angular_spring_equilibrium_point"] = Generic6DofJoint3D.Param.AngularSpringEquilibriumPoint,
    };

    private static readonly System.Collections.Generic.Dictionary<string, Generic6DofJoint3D.Flag> SixDofFlags = new()
    {
        ["enable_linear_limit"] = Generic6DofJoint3D.Flag.EnableLinearLimit,
        ["enable_angular_limit"] = Generic6DofJoint3D.Flag.EnableAngularLimit,
        ["enable_angular_spring"] = Generic6DofJoint3D.Flag.EnableAngularSpring,
        ["enable_linear_spring"] = Generic6DofJoint3D.Flag.EnableLinearSpring,
        ["enable_motor"] = Generic6DofJoint3D.Flag.EnableMotor,
        ["enable_linear_motor"] = Generic6DofJoint3D.Flag.EnableLinearMotor,
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

    private static float CoerceFloat(Variant v) => v.VariantType switch
    {
        Variant.Type.Int => (float)v.AsInt64(),
        Variant.Type.Float => (float)v.AsDouble(),
        Variant.Type.String => (float)v.AsString().ToFloat(),
        Variant.Type.Bool => v.AsBool() ? 1f : 0f,
        _ => 0f,
    };

    /// <summary>
    /// NodePath from a (future) direct child of <paramref name="parent"/> to
    /// <paramref name="target"/>. Both parent and target must already be in the tree.
    /// </summary>
    private static NodePath PathFromFutureChild(Node parent, Node target)
    {
        if (parent == target)
            return new NodePath("..");
        return new NodePath("../" + parent.GetPathTo(target).ToString());
    }

    /// <summary>Split a 6DOF key like "linear_limit_upper_y" into base name + axis (0=x, 1=y, 2=z).</summary>
    private static (string BaseName, int Axis) SplitAxisSuffix(string key)
    {
        if (key.EndsWith("_x")) return (key[..^2], 0);
        if (key.EndsWith("_y")) return (key[..^2], 1);
        if (key.EndsWith("_z")) return (key[..^2], 2);
        return (key, 0);
    }

    private static string ValidNames(System.Collections.Generic.IEnumerable<string> names, bool axisSuffix)
    {
        var joined = string.Join(", ", names);
        return axisSuffix ? joined + " (append _x/_y/_z, default _x)" : joined;
    }

    // ── Tool handlers ──────────────────────────────────────────────────────────

    private Task<Dictionary> CreateJoint(Dictionary @params)
    {
        var (jointType, err) = RequireString(@params, "joint_type");
        if (err != null) return Task.FromResult(err);

        var (nodeAPath, err2) = RequireString(@params, "node_a");
        if (err2 != null) return Task.FromResult(err2);

        var (nodeBPath, err3) = RequireString(@params, "node_b");
        if (err3 != null) return Task.FromResult(err3);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parentPath = OptionalString(@params, "parent_path", ".");
        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'", "Use get_scene_tree to see available nodes"));

        var nodeA = FindNodeByPath(nodeAPath);
        if (nodeA == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodeAPath}' (node_a)", "Use get_scene_tree to see available nodes"));

        var nodeB = FindNodeByPath(nodeBPath);
        if (nodeB == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodeBPath}' (node_b)", "Use get_scene_tree to see available nodes"));

        if (nodeA == nodeB)
            return Task.FromResult(ErrorInvalidParams("node_a and node_b must be different nodes"));

        Node joint = jointType switch
        {
            "pin_2d" => new PinJoint2D(),
            "pin_3d" => new PinJoint3D(),
            "hinge_3d" => new HingeJoint3D(),
            "slider_3d" => new SliderJoint3D(),
            "cone_twist_3d" => new ConeTwistJoint3D(),
            "generic_6dof_3d" => new Generic6DofJoint3D(),
            _ => null!,
        };
        if (joint == null)
            return Task.FromResult(ErrorInvalidParams(
                $"Unknown joint_type: '{jointType}'. Available: pin_2d, pin_3d, hinge_3d, slider_3d, cone_twist_3d, generic_6dof_3d"));

        var is2D = jointType == "pin_2d";
        var bodyClass = is2D ? "PhysicsBody2D" : "PhysicsBody3D";
        if (!nodeA.IsClass(bodyClass))
            return Task.FromResult(ErrorInvalidParams(
                $"node_a '{nodeAPath}' ({nodeA.GetClass()}) is not a {bodyClass}. Joints connect physics bodies."));
        if (!nodeB.IsClass(bodyClass))
            return Task.FromResult(ErrorInvalidParams(
                $"node_b '{nodeBPath}' ({nodeB.GetClass()}) is not a {bodyClass}. Joints connect physics bodies."));

        var jointName = OptionalString(@params, "name", "");
        joint.Name = !string.IsNullOrEmpty(jointName) ? jointName : joint.GetClass();

        var pathA = PathFromFutureChild(parent, nodeA);
        var pathB = PathFromFutureChild(parent, nodeB);

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Create {joint.GetClass()}");
        undoRedo.AddDoMethod(parent, Node.MethodName.AddChild, joint);
        undoRedo.AddDoMethod(joint, Node.MethodName.SetOwner, root);
        undoRedo.AddDoProperty(joint, "node_a", pathA);
        undoRedo.AddDoProperty(joint, "node_b", pathB);

        // Extra properties (softness, bias, disable_collision, solver_priority, ...)
        var properties = OptionalDict(@params, "properties");
        var appliedProps = new Godot.Collections.Array();
        if (properties != null)
        {
            foreach (var propName in properties.Keys)
            {
                var propNameStr = propName.AsString();
                if (propNameStr is "node_a" or "node_b" || !NodeHasProperty(joint, propNameStr))
                    continue;
                var current = joint.Get(propNameStr);
                var parsed = PropertyParser.ParseValue(properties[propName], current.VariantType);
                undoRedo.AddDoProperty(joint, propNameStr, parsed);
                appliedProps.Add(propNameStr);
            }
        }

        undoRedo.AddDoReference(joint);
        undoRedo.AddUndoMethod(parent, Node.MethodName.RemoveChild, joint);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(joint).ToString(),
            ["type"] = joint.GetClass(),
            ["joint_type"] = jointType,
            ["node_a"] = pathA.ToString(),
            ["node_b"] = pathB.ToString(),
            ["applied_properties"] = appliedProps,
        }));
    }

    private Task<Dictionary> ConfigureJoint(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        if (node is not Joint2D && node is not Joint3D)
            return Task.FromResult(ErrorInvalidParams(
                $"Node '{nodePath}' ({node.GetClass()}) is not a joint (Joint2D/Joint3D descendant)"));

        var typedParams = OptionalDict(@params, "params");
        var typedFlags = OptionalDict(@params, "flags");

        // Pre-validate param/flag names against the concrete joint type so we
        // fail before opening an undo action.
        if (typedParams != null)
        {
            foreach (var key in typedParams.Keys)
            {
                var keyStr = key.AsString();
                var (baseName, _) = SplitAxisSuffix(keyStr);
                var valid = node switch
                {
                    PinJoint3D => Pin3DParams.ContainsKey(keyStr),
                    HingeJoint3D => HingeParams.ContainsKey(keyStr),
                    SliderJoint3D => SliderParams.ContainsKey(keyStr),
                    ConeTwistJoint3D => ConeTwistParams.ContainsKey(keyStr),
                    Generic6DofJoint3D => SixDofParams.ContainsKey(baseName),
                    _ => false,
                };
                if (!valid)
                {
                    var validNames = node switch
                    {
                        PinJoint3D => ValidNames(Pin3DParams.Keys, false),
                        HingeJoint3D => ValidNames(HingeParams.Keys, false),
                        SliderJoint3D => ValidNames(SliderParams.Keys, false),
                        ConeTwistJoint3D => ValidNames(ConeTwistParams.Keys, false),
                        Generic6DofJoint3D => ValidNames(SixDofParams.Keys, true),
                        _ => "(params are not supported on 2D joints; use properties instead)",
                    };
                    return Task.FromResult(ErrorInvalidParams(
                        $"Unknown param '{keyStr}' for {node.GetClass()}. Valid: {validNames}"));
                }
            }
        }

        if (typedFlags != null)
        {
            foreach (var key in typedFlags.Keys)
            {
                var keyStr = key.AsString();
                var (baseName, _) = SplitAxisSuffix(keyStr);
                var valid = node switch
                {
                    HingeJoint3D => HingeFlags.ContainsKey(keyStr),
                    Generic6DofJoint3D => SixDofFlags.ContainsKey(baseName),
                    _ => false,
                };
                if (!valid)
                {
                    var validNames = node switch
                    {
                        HingeJoint3D => ValidNames(HingeFlags.Keys, false),
                        Generic6DofJoint3D => ValidNames(SixDofFlags.Keys, true),
                        _ => "(flags are only supported on hinge_3d and generic_6dof_3d joints)",
                    };
                    return Task.FromResult(ErrorInvalidParams(
                        $"Unknown flag '{keyStr}' for {node.GetClass()}. Valid: {validNames}"));
                }
            }
        }

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Configure joint {node.Name}");

        var applied = new Godot.Collections.Array();

        // ── node_a / node_b re-targeting ──
        foreach (var key in new[] { "node_a", "node_b" })
        {
            var targetPath = OptionalString(@params, key, "");
            if (string.IsNullOrEmpty(targetPath))
                continue;
            var target = FindNodeByPath(targetPath);
            if (target == null)
            {
                undoRedo.CommitAction();
                return Task.FromResult(ErrorNotFound($"Node '{targetPath}' ({key})", "Use get_scene_tree to see available nodes"));
            }
            var newPath = node.GetPathTo(target);
            var oldPath = node.Get(key);
            undoRedo.AddDoProperty(node, key, newPath);
            undoRedo.AddUndoProperty(node, key, oldPath);
            applied.Add($"{key} -> {newPath}");
        }

        // ── Generic properties ──
        var properties = OptionalDict(@params, "properties");
        if (properties != null)
        {
            foreach (var propName in properties.Keys)
            {
                var propNameStr = propName.AsString();
                if (!NodeHasProperty(node, propNameStr))
                {
                    undoRedo.CommitAction();
                    return Task.FromResult(ErrorInvalidParams(
                        $"Node '{nodePath}' ({node.GetClass()}) has no property '{propNameStr}'"));
                }
                var current = node.Get(propNameStr);
                var parsed = PropertyParser.ParseValue(properties[propName], current.VariantType);
                undoRedo.AddDoProperty(node, propNameStr, parsed);
                undoRedo.AddUndoProperty(node, propNameStr, current);
                applied.Add(propNameStr);
            }
        }

        // ── Type-specific params ──
        if (typedParams != null)
        {
            foreach (var key in typedParams.Keys)
            {
                var keyStr = key.AsString();
                var value = CoerceFloat(typedParams[key]);
                switch (node)
                {
                    case PinJoint3D pin:
                    {
                        var p = Pin3DParams[keyStr];
                        var old = pin.GetParam(p);
                        undoRedo.AddDoMethod(pin, PinJoint3D.MethodName.SetParam, (int)p, value);
                        undoRedo.AddUndoMethod(pin, PinJoint3D.MethodName.SetParam, (int)p, old);
                        break;
                    }
                    case HingeJoint3D hinge:
                    {
                        var p = HingeParams[keyStr];
                        var old = hinge.GetParam(p);
                        undoRedo.AddDoMethod(hinge, HingeJoint3D.MethodName.SetParam, (int)p, value);
                        undoRedo.AddUndoMethod(hinge, HingeJoint3D.MethodName.SetParam, (int)p, old);
                        break;
                    }
                    case SliderJoint3D slider:
                    {
                        var p = SliderParams[keyStr];
                        var old = slider.GetParam(p);
                        undoRedo.AddDoMethod(slider, SliderJoint3D.MethodName.SetParam, (int)p, value);
                        undoRedo.AddUndoMethod(slider, SliderJoint3D.MethodName.SetParam, (int)p, old);
                        break;
                    }
                    case ConeTwistJoint3D coneTwist:
                    {
                        var p = ConeTwistParams[keyStr];
                        var old = coneTwist.GetParam(p);
                        undoRedo.AddDoMethod(coneTwist, ConeTwistJoint3D.MethodName.SetParam, (int)p, value);
                        undoRedo.AddUndoMethod(coneTwist, ConeTwistJoint3D.MethodName.SetParam, (int)p, old);
                        break;
                    }
                    case Generic6DofJoint3D sixDof:
                    {
                        var (baseName, axis) = SplitAxisSuffix(keyStr);
                        var p = SixDofParams[baseName];
                        var (setMethod, old) = axis switch
                        {
                            1 => (Generic6DofJoint3D.MethodName.SetParamY, sixDof.GetParamY(p)),
                            2 => (Generic6DofJoint3D.MethodName.SetParamZ, sixDof.GetParamZ(p)),
                            _ => (Generic6DofJoint3D.MethodName.SetParamX, sixDof.GetParamX(p)),
                        };
                        undoRedo.AddDoMethod(sixDof, setMethod, (int)p, value);
                        undoRedo.AddUndoMethod(sixDof, setMethod, (int)p, old);
                        break;
                    }
                }
                applied.Add($"param:{keyStr}");
            }
        }

        // ── Type-specific flags ──
        if (typedFlags != null)
        {
            foreach (var key in typedFlags.Keys)
            {
                var keyStr = key.AsString();
                var value = CoerceBool(typedFlags[key]);
                switch (node)
                {
                    case HingeJoint3D hinge:
                    {
                        var f = HingeFlags[keyStr];
                        var old = hinge.GetFlag(f);
                        undoRedo.AddDoMethod(hinge, HingeJoint3D.MethodName.SetFlag, (int)f, value);
                        undoRedo.AddUndoMethod(hinge, HingeJoint3D.MethodName.SetFlag, (int)f, old);
                        break;
                    }
                    case Generic6DofJoint3D sixDof:
                    {
                        var (baseName, axis) = SplitAxisSuffix(keyStr);
                        var f = SixDofFlags[baseName];
                        var (setMethod, old) = axis switch
                        {
                            1 => (Generic6DofJoint3D.MethodName.SetFlagY, sixDof.GetFlagY(f)),
                            2 => (Generic6DofJoint3D.MethodName.SetFlagZ, sixDof.GetFlagZ(f)),
                            _ => (Generic6DofJoint3D.MethodName.SetFlagX, sixDof.GetFlagX(f)),
                        };
                        undoRedo.AddDoMethod(sixDof, setMethod, (int)f, value);
                        undoRedo.AddUndoMethod(sixDof, setMethod, (int)f, old);
                        break;
                    }
                }
                applied.Add($"flag:{keyStr}");
            }
        }

        if (applied.Count == 0)
        {
            undoRedo.CommitAction();
            return Task.FromResult(ErrorInvalidParams(
                "No valid settings provided. Pass node_a/node_b, properties, params, and/or flags."));
        }

        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["type"] = node.GetClass(),
            ["applied"] = applied,
        }));
    }

    private Task<Dictionary> ListJoints(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var startNode = root;
        var scopePath = OptionalString(@params, "node_path", ".");
        if (!string.IsNullOrEmpty(scopePath) && scopePath != ".")
        {
            startNode = FindNodeByPath(scopePath);
            if (startNode == null)
                return Task.FromResult(ErrorNotFound($"Node '{scopePath}'", "Use get_scene_tree to see available nodes"));
        }

        var joints = new Godot.Collections.Array();
        var queue = new Queue<Node>();
        queue.Enqueue(startNode);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            switch (current)
            {
                case Joint2D j2:
                    joints.Add(new Dictionary
                    {
                        ["path"] = root.GetPathTo(j2).ToString(),
                        ["type"] = j2.GetClass(),
                        ["node_a"] = j2.NodeA.ToString(),
                        ["node_b"] = j2.NodeB.ToString(),
                    });
                    break;
                case Joint3D j3:
                    joints.Add(new Dictionary
                    {
                        ["path"] = root.GetPathTo(j3).ToString(),
                        ["type"] = j3.GetClass(),
                        ["node_a"] = j3.NodeA.ToString(),
                        ["node_b"] = j3.NodeB.ToString(),
                    });
                    break;
            }
            foreach (var child in current.GetChildren())
                queue.Enqueue(child);
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["joints"] = joints,
            ["count"] = joints.Count,
        }));
    }
}
