using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Skeleton3D inspection and runtime pose tools.
/// get_skeleton_info reads the rest-pose skeleton structure; set_bone_pose /
/// get_bone_pose operate on the live (runtime) pose and are NOT undo-tracked,
/// since bone poses are ephemeral runtime state, not scene data.
/// </summary>
[Tool]
public partial class SkeletonCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "get_skeleton_info",
            Description = "Inspect a Skeleton3D node: returns bone_count and a bones array with " +
                "each bone's name, parent index, and rest-pose transform " +
                "(rest_position / rest_rotation in degrees / rest_scale).",
            Category = "skeleton",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the Skeleton3D node in the edited scene."),
            }, "node_path"),
            Handler = p => Task.FromResult(GetSkeletonInfo(p)),
        },
        new()
        {
            Name = "set_bone_pose",
            Description = "Set the runtime pose transform of a bone on a Skeleton3D, addressed by " +
                "bone_name or bone_index. Position, rotation_degrees and scale are all optional; " +
                "omitted components keep their current pose values. NOT undo-tracked (runtime pose).",
            Category = "skeleton",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the Skeleton3D node in the edited scene."),
                ["bone_name"] = Schema.Str("Name of the bone to pose. Takes precedence over bone_index."),
                ["bone_index"] = Schema.Int("Index of the bone to pose (used when bone_name is absent).", -1),
                ["position"] = Schema.Obj("Optional local pose position as {x, y, z}."),
                ["rotation_degrees"] = Schema.Obj("Optional local pose rotation in degrees as {x, y, z}."),
                ["scale"] = Schema.Obj("Optional local pose scale as {x, y, z}."),
            }, "node_path"),
            Handler = p => Task.FromResult(SetBonePose(p)),
        },
        new()
        {
            Name = "get_bone_pose",
            Description = "Read the current runtime pose transform of a bone on a Skeleton3D, " +
                "addressed by bone_name or bone_index. Returns position, rotation_degrees and scale.",
            Category = "skeleton",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the Skeleton3D node in the edited scene."),
                ["bone_name"] = Schema.Str("Name of the bone to read. Takes precedence over bone_index."),
                ["bone_index"] = Schema.Int("Index of the bone to read (used when bone_name is absent).", -1),
            }, "node_path"),
            Handler = p => Task.FromResult(GetBonePoseTool(p)),
        },
    };

    // ── shared lookup ────────────────────────────────────────────────────────

    private static Skeleton3D? FindSkeleton(string nodePath)
    {
        var node = FindNodeByPath(nodePath);
        return node as Skeleton3D;
    }

    /// <summary>
    /// Resolve a bone index from params: bone_name wins over bone_index.
    /// Returns (-1, error) when neither resolves.
    /// </summary>
    private static (int BoneIndex, Dictionary? Error) ResolveBone(Skeleton3D skeleton, Dictionary @params)
    {
        var boneName = OptionalString(@params, "bone_name", "");
        if (!string.IsNullOrEmpty(boneName))
        {
            var idx = skeleton.FindBone(boneName);
            if (idx < 0)
                return (-1, ErrorNotFound($"Bone '{boneName}'",
                    $"Skeleton has {skeleton.GetBoneCount()} bones; use get_skeleton_info to list them"));
            return (idx, null);
        }

        var boneIndex = OptionalInt(@params, "bone_index", -1);
        if (boneIndex < 0)
            return (-1, ErrorInvalidParams("Either bone_name or bone_index is required"));
        if (boneIndex >= skeleton.GetBoneCount())
            return (-1, ErrorInvalidParams(
                $"bone_index {boneIndex} is out of range (bone_count={skeleton.GetBoneCount()})"));
        return (boneIndex, null);
    }

    // ── vector helpers ───────────────────────────────────────────────────────

    private static Dictionary Vector3ToDict(Vector3 v) =>
        new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };

    private static Vector3 EulerToDegrees(Vector3 eulerRad) =>
        new(Mathf.RadToDeg(eulerRad.X), Mathf.RadToDeg(eulerRad.Y), Mathf.RadToDeg(eulerRad.Z));

    /// <summary>Parse a {x,y,z} dictionary param. Returns false when absent / wrong shape.</summary>
    private static bool TryGetVector3Param(Dictionary @params, string key, out Vector3 result)
    {
        result = Vector3.Zero;
        if (!@params.TryGetValue(key, out var v) || v.VariantType != Variant.Type.Dictionary)
            return false;
        var d = v.AsGodotDictionary();
        result = new Vector3(
            (float)GetDictNum(d, "x", 0),
            (float)GetDictNum(d, "y", 0),
            (float)GetDictNum(d, "z", 0));
        return true;
    }

    private static double GetDictNum(Dictionary d, string key, double @default)
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

    /// <summary>Decompose a Transform3D into JSON-safe position / rotation_degrees / scale dicts.</summary>
    private static Dictionary TransformToDict(Transform3D t) =>
        new()
        {
            ["position"] = Vector3ToDict(t.Origin),
            ["rotation_degrees"] = Vector3ToDict(EulerToDegrees(t.Basis.GetEuler())),
            ["scale"] = Vector3ToDict(t.Basis.Scale),
        };

    // ── get_skeleton_info ────────────────────────────────────────────────────

    private static Dictionary GetSkeletonInfo(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var skeleton = FindSkeleton(nodePath);
        if (skeleton == null)
            return ErrorNotFound($"Skeleton3D at '{nodePath}'");

        var boneCount = skeleton.GetBoneCount();
        var bones = new Godot.Collections.Array();
        for (var i = 0; i < boneCount; i++)
        {
            var rest = skeleton.GetBoneRest(i);
            bones.Add(new Dictionary
            {
                ["name"] = skeleton.GetBoneName(i),
                ["parent"] = skeleton.GetBoneParent(i),
                ["rest_position"] = Vector3ToDict(rest.Origin),
                ["rest_rotation"] = Vector3ToDict(EulerToDegrees(rest.Basis.GetEuler())),
                ["rest_scale"] = Vector3ToDict(rest.Basis.Scale),
            });
        }

        return Success(new Dictionary
        {
            ["node_path"] = nodePath,
            ["bone_count"] = boneCount,
            ["bones"] = bones,
        });
    }

    // ── set_bone_pose ────────────────────────────────────────────────────────

    private static Dictionary SetBonePose(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var skeleton = FindSkeleton(nodePath);
        if (skeleton == null)
            return ErrorNotFound($"Skeleton3D at '{nodePath}'");

        var (boneIndex, boneErr) = ResolveBone(skeleton, @params);
        if (boneErr != null)
            return boneErr;

        var hasPosition = TryGetVector3Param(@params, "position", out var position);
        var hasRotation = TryGetVector3Param(@params, "rotation_degrees", out var rotationDegrees);
        var hasScale = TryGetVector3Param(@params, "scale", out var scale);

        if (!hasPosition && !hasRotation && !hasScale)
            return ErrorInvalidParams(
                "At least one of position, rotation_degrees or scale must be provided");

        // Start from the current pose so omitted components keep their values.
        var pose = skeleton.GetBonePose(boneIndex);
        var origin = hasPosition ? position : pose.Origin;
        var euler = hasRotation
            ? new Vector3(
                Mathf.DegToRad(rotationDegrees.X),
                Mathf.DegToRad(rotationDegrees.Y),
                Mathf.DegToRad(rotationDegrees.Z))
            : pose.Basis.GetEuler();
        var newScale = hasScale ? scale : pose.Basis.Scale;

        var basis = Basis.FromEuler(euler).Scaled(newScale);
        var newPose = new Transform3D(basis, origin);
        skeleton.SetBonePose(boneIndex, newPose);

        return Success(new Dictionary
        {
            ["node_path"] = nodePath,
            ["bone_name"] = skeleton.GetBoneName(boneIndex),
            ["bone_index"] = boneIndex,
            ["pose"] = TransformToDict(skeleton.GetBonePose(boneIndex)),
        });
    }

    // ── get_bone_pose ────────────────────────────────────────────────────────

    private static Dictionary GetBonePoseTool(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var skeleton = FindSkeleton(nodePath);
        if (skeleton == null)
            return ErrorNotFound($"Skeleton3D at '{nodePath}'");

        var (boneIndex, boneErr) = ResolveBone(skeleton, @params);
        if (boneErr != null)
            return boneErr;

        var pose = skeleton.GetBonePose(boneIndex);
        var result = TransformToDict(pose);
        result["node_path"] = nodePath;
        result["bone_name"] = skeleton.GetBoneName(boneIndex);
        result["bone_index"] = boneIndex;
        return Success(result);
    }
}
