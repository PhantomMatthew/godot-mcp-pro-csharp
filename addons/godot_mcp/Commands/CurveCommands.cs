using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// Curve editing tools for Path2D (Curve2D) and Path3D (Curve3D) nodes: read
/// curve points with their in/out control handles, and add/modify/remove
/// points. All curve mutations go through EditorUndoRedoManager so they
/// participate in the editor's undo history.
/// </summary>
[Tool]
public partial class CurveCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "get_curve_points",
            Description = "Get all points of the Curve2D/Curve3D resource assigned to a Path2D/Path3D node. " +
                "Returns points as [{position, in, out}] with {x,y} (2D) or {x,y,z} (3D) components, " +
                "plus point_count and closed (closed is always false for Curve2D).",
            Category = "curve",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the Path2D or Path3D node"),
            }, "node_path"),
            Handler = GetCurvePoints,
        },
        new()
        {
            Name = "add_curve_point",
            Description = "Add a point to the curve of a Path2D/Path3D node. position is required; " +
                "in_control/out_control default to zero (no handle offset). index inserts at that position " +
                "(0-based); when omitted the point is appended. Undo-tracked.",
            Category = "curve",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the Path2D or Path3D node"),
                ["position"] = Schema.Obj("Point position: {x,y} for Path2D, {x,y,z} for Path3D " +
                    "(also accepts \"Vector2(x,y)\"/\"Vector3(x,y,z)\" strings)"),
                ["in_control"] = Schema.Obj("In-control handle offset relative to position (default {x:0,y:0[,z:0]})"),
                ["out_control"] = Schema.Obj("Out-control handle offset relative to position (default {x:0,y:0[,z:0]})"),
                ["index"] = Schema.Int("Insert position (0-based). Omit or pass -1 to append", -1),
            }, "node_path", "position"),
            Handler = AddCurvePoint,
        },
        new()
        {
            Name = "set_curve_point",
            Description = "Modify the point at a given index of a Path2D/Path3D node's curve. " +
                "At least one of position, in_control, out_control must be provided; omitted fields keep " +
                "their current values. Undo-tracked.",
            Category = "curve",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the Path2D or Path3D node"),
                ["index"] = Schema.Int("Index of the point to modify (0-based)"),
                ["position"] = Schema.Obj("New position: {x,y} (2D) or {x,y,z} (3D)"),
                ["in_control"] = Schema.Obj("New in-control handle offset"),
                ["out_control"] = Schema.Obj("New out-control handle offset"),
            }, "node_path", "index"),
            Handler = SetCurvePoint,
        },
        new()
        {
            Name = "remove_curve_point",
            Description = "Remove the point at a given index from a Path2D/Path3D node's curve. Undo-tracked.",
            Category = "curve",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the Path2D or Path3D node"),
                ["index"] = Schema.Int("Index of the point to remove (0-based)"),
            }, "node_path", "index"),
            Handler = RemoveCurvePoint,
        },
    };

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Resolve node_path to a Path2D/Path3D's curve resource. Exactly one of C2/C3 is non-null on success.</summary>
    private static (Curve2D? C2, Curve3D? C3, Dictionary? Error) ResolveCurve(string nodePath)
    {
        var node = FindNodeByPath(nodePath);
        if (node == null)
            return (null, null, ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));
        switch (node)
        {
            case Path2D path2:
                if (path2.Curve == null)
                    return (null, null, ErrorInvalidParams(
                        $"Path2D '{nodePath}' has no Curve2D resource assigned. Assign a curve in the inspector first."));
                return (path2.Curve, null, null);
            case Path3D path3:
                if (path3.Curve == null)
                    return (null, null, ErrorInvalidParams(
                        $"Path3D '{nodePath}' has no Curve3D resource assigned. Assign a curve in the inspector first."));
                return (null, path3.Curve, null);
            default:
                return (null, null, ErrorInvalidParams(
                    $"Node '{nodePath}' ({node.GetClass()}) is not a Path2D or Path3D"));
        }
    }

    private static Dictionary Vec2ToDict(Vector2 v) =>
        new() { ["x"] = v.X, ["y"] = v.Y };

    private static Dictionary Vec3ToDict(Vector3 v) =>
        new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };

    private static (int Index, Dictionary? Error) RequireValidIndex(Dictionary @params, int pointCount)
    {
        if (!@params.ContainsKey("index"))
            return (0, ErrorInvalidParams("Missing required parameter: index"));
        var index = OptionalInt(@params, "index", -1);
        if (index < 0 || index >= pointCount)
            return (0, ErrorInvalidParams($"index {index} is out of range (curve has {pointCount} points)"));
        return (index, null);
    }

    // ── Tool handlers ──────────────────────────────────────────────────────────

    private Task<Dictionary> GetCurvePoints(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var (c2, c3, resolveErr) = ResolveCurve(nodePath);
        if (resolveErr != null) return Task.FromResult(resolveErr);

        var points = new Godot.Collections.Array();
        int pointCount;
        bool closed;
        string curveType;

        if (c2 != null)
        {
            curveType = "Curve2D";
            closed = false; // Curve2D has no closed property (unlike Curve3D)
            pointCount = c2.PointCount;
            for (var i = 0; i < pointCount; i++)
            {
                points.Add(new Dictionary
                {
                    ["position"] = Vec2ToDict(c2.GetPointPosition(i)),
                    ["in"] = Vec2ToDict(c2.GetPointIn(i)),
                    ["out"] = Vec2ToDict(c2.GetPointOut(i)),
                });
            }
        }
        else
        {
            curveType = "Curve3D";
            closed = c3!.Closed;
            pointCount = c3.PointCount;
            for (var i = 0; i < pointCount; i++)
            {
                points.Add(new Dictionary
                {
                    ["position"] = Vec3ToDict(c3.GetPointPosition(i)),
                    ["in"] = Vec3ToDict(c3.GetPointIn(i)),
                    ["out"] = Vec3ToDict(c3.GetPointOut(i)),
                });
            }
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = nodePath,
            ["curve_type"] = curveType,
            ["point_count"] = pointCount,
            ["closed"] = closed,
            ["points"] = points,
        }));
    }

    private Task<Dictionary> AddCurvePoint(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        if (!@params.ContainsKey("position"))
            return Task.FromResult(ErrorInvalidParams("Missing required parameter: position"));

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        var (c2, c3, resolveErr) = ResolveCurve(nodePath);
        if (resolveErr != null) return Task.FromResult(resolveErr);

        var index = OptionalInt(@params, "index", -1);
        var undoRedo = GetUndoRedo();

        if (c2 != null)
        {
            var count = c2.PointCount;
            if (index < -1 || index > count)
                return Task.FromResult(ErrorInvalidParams($"index {index} is out of range (curve has {count} points; use -1 to append)"));

            var position = PropertyParser.ParseVector2(@params["position"]);
            var inControl = @params.ContainsKey("in_control")
                ? PropertyParser.ParseVector2(@params["in_control"])
                : Vector2.Zero;
            var outControl = @params.ContainsKey("out_control")
                ? PropertyParser.ParseVector2(@params["out_control"])
                : Vector2.Zero;
            var insertAt = index >= 0 ? index : count;

            undoRedo.CreateAction($"MCP: Add curve point to {node!.Name}");
            undoRedo.AddDoMethod(c2, Curve2D.MethodName.AddPoint, position, inControl, outControl, index);
            undoRedo.AddUndoMethod(c2, Curve2D.MethodName.RemovePoint, insertAt);
            undoRedo.CommitAction();

            return Task.FromResult(Success(new Dictionary
            {
                ["node_path"] = nodePath,
                ["index"] = insertAt,
                ["point_count"] = c2.PointCount,
            }));
        }
        else
        {
            var count = c3!.PointCount;
            if (index < -1 || index > count)
                return Task.FromResult(ErrorInvalidParams($"index {index} is out of range (curve has {count} points; use -1 to append)"));

            var position = PropertyParser.ParseVector3(@params["position"]);
            var inControl = @params.ContainsKey("in_control")
                ? PropertyParser.ParseVector3(@params["in_control"])
                : Vector3.Zero;
            var outControl = @params.ContainsKey("out_control")
                ? PropertyParser.ParseVector3(@params["out_control"])
                : Vector3.Zero;
            var insertAt = index >= 0 ? index : count;

            undoRedo.CreateAction($"MCP: Add curve point to {node!.Name}");
            undoRedo.AddDoMethod(c3, Curve3D.MethodName.AddPoint, position, inControl, outControl, index);
            undoRedo.AddUndoMethod(c3, Curve3D.MethodName.RemovePoint, insertAt);
            undoRedo.CommitAction();

            return Task.FromResult(Success(new Dictionary
            {
                ["node_path"] = nodePath,
                ["index"] = insertAt,
                ["point_count"] = c3.PointCount,
            }));
        }
    }

    private Task<Dictionary> SetCurvePoint(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var (c2, c3, resolveErr) = ResolveCurve(nodePath);
        if (resolveErr != null) return Task.FromResult(resolveErr);

        var pointCount = c2?.PointCount ?? c3!.PointCount;
        var (index, indexErr) = RequireValidIndex(@params, pointCount);
        if (indexErr != null) return Task.FromResult(indexErr);

        var hasPosition = @params.ContainsKey("position");
        var hasIn = @params.ContainsKey("in_control");
        var hasOut = @params.ContainsKey("out_control");
        if (!hasPosition && !hasIn && !hasOut)
            return Task.FromResult(ErrorInvalidParams(
                "Provide at least one of: position, in_control, out_control"));

        var applied = new Godot.Collections.Array();
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Set curve point {index}");

        if (c2 != null)
        {
            if (hasPosition)
            {
                var oldValue = c2.GetPointPosition(index);
                var newValue = PropertyParser.ParseVector2(@params["position"]);
                undoRedo.AddDoMethod(c2, Curve2D.MethodName.SetPointPosition, index, newValue);
                undoRedo.AddUndoMethod(c2, Curve2D.MethodName.SetPointPosition, index, oldValue);
                applied.Add("position");
            }
            if (hasIn)
            {
                var oldValue = c2.GetPointIn(index);
                var newValue = PropertyParser.ParseVector2(@params["in_control"]);
                undoRedo.AddDoMethod(c2, Curve2D.MethodName.SetPointIn, index, newValue);
                undoRedo.AddUndoMethod(c2, Curve2D.MethodName.SetPointIn, index, oldValue);
                applied.Add("in");
            }
            if (hasOut)
            {
                var oldValue = c2.GetPointOut(index);
                var newValue = PropertyParser.ParseVector2(@params["out_control"]);
                undoRedo.AddDoMethod(c2, Curve2D.MethodName.SetPointOut, index, newValue);
                undoRedo.AddUndoMethod(c2, Curve2D.MethodName.SetPointOut, index, oldValue);
                applied.Add("out");
            }
        }
        else
        {
            if (hasPosition)
            {
                var oldValue = c3!.GetPointPosition(index);
                var newValue = PropertyParser.ParseVector3(@params["position"]);
                undoRedo.AddDoMethod(c3, Curve3D.MethodName.SetPointPosition, index, newValue);
                undoRedo.AddUndoMethod(c3, Curve3D.MethodName.SetPointPosition, index, oldValue);
                applied.Add("position");
            }
            if (hasIn)
            {
                var oldValue = c3!.GetPointIn(index);
                var newValue = PropertyParser.ParseVector3(@params["in_control"]);
                undoRedo.AddDoMethod(c3, Curve3D.MethodName.SetPointIn, index, newValue);
                undoRedo.AddUndoMethod(c3, Curve3D.MethodName.SetPointIn, index, oldValue);
                applied.Add("in");
            }
            if (hasOut)
            {
                var oldValue = c3!.GetPointOut(index);
                var newValue = PropertyParser.ParseVector3(@params["out_control"]);
                undoRedo.AddDoMethod(c3, Curve3D.MethodName.SetPointOut, index, newValue);
                undoRedo.AddUndoMethod(c3, Curve3D.MethodName.SetPointOut, index, oldValue);
                applied.Add("out");
            }
        }

        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = nodePath,
            ["index"] = index,
            ["applied"] = applied,
        }));
    }

    private Task<Dictionary> RemoveCurvePoint(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        var (c2, c3, resolveErr) = ResolveCurve(nodePath);
        if (resolveErr != null) return Task.FromResult(resolveErr);

        var pointCount = c2?.PointCount ?? c3!.PointCount;
        var (index, indexErr) = RequireValidIndex(@params, pointCount);
        if (indexErr != null) return Task.FromResult(indexErr);

        var undoRedo = GetUndoRedo();

        if (c2 != null)
        {
            var oldPos = c2.GetPointPosition(index);
            var oldIn = c2.GetPointIn(index);
            var oldOut = c2.GetPointOut(index);

            undoRedo.CreateAction($"MCP: Remove curve point {index} from {node!.Name}");
            undoRedo.AddDoMethod(c2, Curve2D.MethodName.RemovePoint, index);
            undoRedo.AddUndoMethod(c2, Curve2D.MethodName.AddPoint, oldPos, oldIn, oldOut, index);
            undoRedo.CommitAction();

            return Task.FromResult(Success(new Dictionary
            {
                ["node_path"] = nodePath,
                ["removed_index"] = index,
                ["point_count"] = c2.PointCount,
            }));
        }
        else
        {
            var oldPos = c3!.GetPointPosition(index);
            var oldIn = c3.GetPointIn(index);
            var oldOut = c3.GetPointOut(index);

            undoRedo.CreateAction($"MCP: Remove curve point {index} from {node!.Name}");
            undoRedo.AddDoMethod(c3, Curve3D.MethodName.RemovePoint, index);
            undoRedo.AddUndoMethod(c3, Curve3D.MethodName.AddPoint, oldPos, oldIn, oldOut, index);
            undoRedo.CommitAction();

            return Task.FromResult(Success(new Dictionary
            {
                ["node_path"] = nodePath,
                ["removed_index"] = index,
                ["point_count"] = c3.PointCount,
            }));
        }
    }
}
