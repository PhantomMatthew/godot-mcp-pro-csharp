using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Faithful port of addons/godot_mcp/commands/animation_commands.gd.
/// Exposes AnimationPlayer/AnimationLibrary editing tools (list/create/track/keyframe/
/// info/remove), all undo-tracked via EditorUndoRedoManager.
/// </summary>
[Tool]
public partial class AnimationCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "list_animations",
            Description = "List all animations stored on an AnimationPlayer node, with length/loop_mode/track_count.",
            Category = "animation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationPlayer node in the edited scene."),
            }, "node_path"),
            Handler = p => Task.FromResult(ListAnimations(p)),
        },
        new()
        {
            Name = "create_animation",
            Description = "Create a new named Animation resource on an AnimationPlayer's default library (undo-tracked).",
            Category = "animation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationPlayer node in the edited scene."),
                ["name"] = Schema.Str("Name of the new animation."),
                ["length"] = Schema.Num("Animation length in seconds.", 1.0),
                ["loop_mode"] = Schema.Int("Loop mode: 0=none, 1=linear, 2=pingpong.", 0),
            }, "node_path", "name"),
            Handler = p => Task.FromResult(CreateAnimation(p)),
        },
        new()
        {
            Name = "add_animation_track",
            Description = "Add a track to an existing animation and set its node path (undo-tracked).",
            Category = "animation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationPlayer node in the edited scene."),
                ["animation"] = Schema.Str("Name of the animation to modify."),
                ["track_path"] = Schema.Str("Node/property path for the new track (e.g. 'Sprite2D:position')."),
                ["track_type"] = Schema.Enum("Track type.", "value", "position_2d", "rotation_2d", "scale_2d", "method", "bezier", "blend_shape"),
                ["update_mode"] = Schema.Enum("Update mode for value tracks.", "continuous", "discrete", "capture"),
            }, "node_path", "animation", "track_path"),
            Handler = p => Task.FromResult(AddAnimationTrack(p)),
        },
        new()
        {
            Name = "set_animation_keyframe",
            Description = "Insert or update a keyframe on an animation track at a given time (undo-tracked). String values are smart-parsed as expressions when possible.",
            Category = "animation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationPlayer node in the edited scene."),
                ["animation"] = Schema.Str("Name of the animation to modify."),
                ["track_index"] = Schema.Int("Index of the track to add/update the keyframe on.", 0),
                ["time"] = Schema.Num("Keyframe time in seconds.", 0.0),
                ["value"] = Schema.Any("Value to store at the keyframe."),
                ["easing"] = Schema.Num("Key transition/easing value.", 1.0),
            }, "node_path", "animation"),
            Handler = p => Task.FromResult(SetAnimationKeyframe(p)),
        },
        new()
        {
            Name = "get_animation_info",
            Description = "Get full details of an animation, including every track and its keyframes.",
            Category = "animation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationPlayer node in the edited scene."),
                ["animation"] = Schema.Str("Name of the animation to inspect."),
            }, "node_path", "animation"),
            Handler = p => Task.FromResult(GetAnimationInfo(p)),
        },
        new()
        {
            Name = "remove_animation",
            Description = "Remove a named animation from an AnimationPlayer's default library (undo-tracked).",
            Category = "animation",
            Destructive = true,
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationPlayer node in the edited scene."),
                ["name"] = Schema.Str("Name of the animation to remove."),
            }, "node_path", "name"),
            Handler = p => Task.FromResult(RemoveAnimation(p)),
        },
    };

    // ── shared lookup ────────────────────────────────────────────────────────

    private static AnimationPlayer? FindAnimationPlayer(string nodePath)
    {
        var node = FindNodeByPath(nodePath);
        return node as AnimationPlayer;
    }

    // ── list_animations ──────────────────────────────────────────────────────

    private static Dictionary ListAnimations(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var player = FindAnimationPlayer(nodePath);
        if (player == null)
            return ErrorNotFound($"AnimationPlayer at '{nodePath}'");

        var animations = new Godot.Collections.Array();
        foreach (var animName in player.GetAnimationList())
        {
            var anim = player.GetAnimation(animName);
            animations.Add(new Dictionary
            {
                ["name"] = animName,
                ["length"] = anim.Length,
                ["loop_mode"] = (int)anim.LoopMode,
                ["track_count"] = anim.GetTrackCount(),
            });
        }

        return Success(new Dictionary { ["node_path"] = nodePath, ["animations"] = animations, ["count"] = animations.Count });
    }

    // ── create_animation ─────────────────────────────────────────────────────

    private Dictionary CreateAnimation(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (animName, err2) = RequireString(@params, "name");
        if (err2 != null)
            return err2;

        var player = FindAnimationPlayer(nodePath);
        if (player == null)
            return ErrorNotFound($"AnimationPlayer at '{nodePath}'");

        var length = OptionalFloat(@params, "length", 1.0);
        var loopMode = OptionalInt(@params, "loop_mode", 0);

        var anim = new Animation { Length = (float)length, LoopMode = (Animation.LoopModeEnum)loopMode };

        var lib = player.GetAnimationLibrary("");
        var createdLibrary = false;
        if (lib == null)
        {
            lib = new AnimationLibrary();
            createdLibrary = true;
        }

        if (lib.HasAnimation(animName))
            return ErrorInvalidParams($"Animation '{animName}' already exists");

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Create animation {animName}");
        if (createdLibrary)
        {
            undoRedo.AddDoMethod(player, AnimationMixer.MethodName.AddAnimationLibrary, "", lib);
            undoRedo.AddDoReference(lib);
            undoRedo.AddUndoMethod(player, AnimationMixer.MethodName.RemoveAnimationLibrary, "");
        }
        undoRedo.AddDoMethod(lib, AnimationLibrary.MethodName.AddAnimation, animName, anim);
        undoRedo.AddDoReference(anim);
        undoRedo.AddUndoMethod(lib, AnimationLibrary.MethodName.RemoveAnimation, animName);
        undoRedo.CommitAction();

        return Success(new Dictionary { ["name"] = animName, ["length"] = length, ["created"] = true });
    }

    // ── add_animation_track ──────────────────────────────────────────────────

    private Dictionary AddAnimationTrack(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (animName, err2) = RequireString(@params, "animation");
        if (err2 != null)
            return err2;

        var (trackPath, err3) = RequireString(@params, "track_path");
        if (err3 != null)
            return err3;

        var player = FindAnimationPlayer(nodePath);
        if (player == null)
            return ErrorNotFound($"AnimationPlayer at '{nodePath}'");

        var anim = player.GetAnimation(animName);
        if (anim == null)
            return ErrorNotFound($"Animation '{animName}'");

        var trackTypeStr = OptionalString(@params, "track_type", "value");
        var trackType = trackTypeStr switch
        {
            "value" => Animation.TrackType.Value,
            "position_2d" => Animation.TrackType.Position3D, // Godot uses 3D type for 2D too
            "rotation_2d" => Animation.TrackType.Rotation3D,
            "scale_2d" => Animation.TrackType.Scale3D,
            "method" => Animation.TrackType.Method,
            "bezier" => Animation.TrackType.Bezier,
            "blend_shape" => Animation.TrackType.BlendShape,
            _ => Animation.TrackType.Value,
        };

        var trackIdx = anim.GetTrackCount();
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Add animation track");
        undoRedo.AddDoMethod(anim, Animation.MethodName.AddTrack, (int)trackType, trackIdx);
        undoRedo.AddDoMethod(anim, Animation.MethodName.TrackSetPath, trackIdx, new NodePath(trackPath));

        var updateModeStr = OptionalString(@params, "update_mode", "");
        if (!string.IsNullOrEmpty(updateModeStr) && trackType == Animation.TrackType.Value)
        {
            switch (updateModeStr)
            {
                case "continuous":
                    undoRedo.AddDoMethod(anim, Animation.MethodName.ValueTrackSetUpdateMode, trackIdx, (int)Animation.UpdateMode.Continuous);
                    break;
                case "discrete":
                    undoRedo.AddDoMethod(anim, Animation.MethodName.ValueTrackSetUpdateMode, trackIdx, (int)Animation.UpdateMode.Discrete);
                    break;
                case "capture":
                    undoRedo.AddDoMethod(anim, Animation.MethodName.ValueTrackSetUpdateMode, trackIdx, (int)Animation.UpdateMode.Capture);
                    break;
            }
        }
        undoRedo.AddUndoMethod(anim, Animation.MethodName.RemoveTrack, trackIdx);
        undoRedo.CommitAction();

        return Success(new Dictionary { ["track_index"] = trackIdx, ["track_path"] = trackPath, ["track_type"] = trackTypeStr });
    }

    // ── set_animation_keyframe ────────────────────────────────────────────────

    private Dictionary SetAnimationKeyframe(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (animName, err2) = RequireString(@params, "animation");
        if (err2 != null)
            return err2;

        var player = FindAnimationPlayer(nodePath);
        if (player == null)
            return ErrorNotFound($"AnimationPlayer at '{nodePath}'");

        var anim = player.GetAnimation(animName);
        if (anim == null)
            return ErrorNotFound($"Animation '{animName}'");

        var trackIndex = OptionalInt(@params, "track_index", 0);
        if (trackIndex < 0 || trackIndex >= anim.GetTrackCount())
            return ErrorInvalidParams($"Invalid track_index: {trackIndex}");

        var time = OptionalFloat(@params, "time", 0.0);
        Variant value = @params.TryGetValue("value", out var v) ? v : default;

        // Parse value string for common types
        if (value.VariantType == Variant.Type.String)
        {
            var s = value.AsString();
            var expr = new Expression();
            if (expr.Parse(s) == Godot.Error.Ok)
            {
                var parsed = expr.Execute();
                if (parsed.VariantType != Variant.Type.Nil)
                    value = parsed;
            }
        }

        var easing = OptionalFloat(@params, "easing", 1.0);
        var oldKeyIdx = FindAnimationKeyAtTime(anim, trackIndex, (float)time);
        var hadOldKey = oldKeyIdx >= 0;
        Variant oldValue = hadOldKey ? anim.TrackGetKeyValue(trackIndex, oldKeyIdx) : default;
        var oldEasing = hadOldKey ? anim.TrackGetKeyTransition(trackIndex, oldKeyIdx) : 1.0f;

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Set animation keyframe");
        undoRedo.AddDoMethod(this, MethodName.UpsertAnimationKey, anim, trackIndex, time, value, easing);
        undoRedo.AddUndoMethod(this, MethodName.RestoreAnimationKey, anim, trackIndex, time, hadOldKey, oldValue, oldEasing);
        undoRedo.CommitAction();

        var keyIdx = FindAnimationKeyAtTime(anim, trackIndex, (float)time);

        return Success(new Dictionary
        {
            ["track_index"] = trackIndex,
            ["time"] = time,
            ["key_index"] = keyIdx,
            ["easing"] = anim.TrackGetKeyTransition(trackIndex, keyIdx),
        });
    }

    // ── get_animation_info ───────────────────────────────────────────────────

    private static Dictionary GetAnimationInfo(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (animName, err2) = RequireString(@params, "animation");
        if (err2 != null)
            return err2;

        var player = FindAnimationPlayer(nodePath);
        if (player == null)
            return ErrorNotFound($"AnimationPlayer at '{nodePath}'");

        var anim = player.GetAnimation(animName);
        if (anim == null)
            return ErrorNotFound($"Animation '{animName}'");

        var tracks = new Godot.Collections.Array();
        for (var i = 0; i < anim.GetTrackCount(); i++)
        {
            var trackInfo = new Dictionary
            {
                ["index"] = i,
                ["path"] = anim.TrackGetPath(i).ToString(),
                ["type"] = (int)anim.TrackGetType(i),
                ["key_count"] = anim.TrackGetKeyCount(i),
            };
            var keys = new Godot.Collections.Array();
            for (var k = 0; k < anim.TrackGetKeyCount(i); k++)
            {
                keys.Add(new Dictionary
                {
                    ["time"] = anim.TrackGetKeyTime(i, k),
                    ["value"] = anim.TrackGetKeyValue(i, k).ToString(),
                    ["easing"] = anim.TrackGetKeyTransition(i, k),
                });
            }
            trackInfo["keys"] = keys;
            tracks.Add(trackInfo);
        }

        return Success(new Dictionary
        {
            ["name"] = animName,
            ["length"] = anim.Length,
            ["loop_mode"] = (int)anim.LoopMode,
            ["step"] = anim.Step,
            ["tracks"] = tracks,
        });
    }

    // ── remove_animation ──────────────────────────────────────────────────────

    private Dictionary RemoveAnimation(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (animName, err2) = RequireString(@params, "name");
        if (err2 != null)
            return err2;

        var player = FindAnimationPlayer(nodePath);
        if (player == null)
            return ErrorNotFound($"AnimationPlayer at '{nodePath}'");

        var lib = player.GetAnimationLibrary("");
        if (lib == null || !lib.HasAnimation(animName))
            return ErrorNotFound($"Animation '{animName}'");

        var anim = lib.GetAnimation(animName);
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Remove animation {animName}");
        undoRedo.AddDoMethod(lib, AnimationLibrary.MethodName.RemoveAnimation, animName);
        undoRedo.AddUndoMethod(lib, AnimationLibrary.MethodName.AddAnimation, animName, anim);
        undoRedo.AddUndoReference(anim);
        undoRedo.CommitAction();
        return Success(new Dictionary { ["name"] = animName, ["removed"] = true });
    }

    // ── keyframe do/undo helpers (invoked by name via EditorUndoRedoManager) ──

    private static int FindAnimationKeyAtTime(Animation anim, int trackIndex, float time)
    {
        for (var keyIndex = 0; keyIndex < anim.TrackGetKeyCount(trackIndex); keyIndex++)
        {
            if (Mathf.IsEqualApprox(anim.TrackGetKeyTime(trackIndex, keyIndex), time))
                return keyIndex;
        }
        return -1;
    }

    /// <summary>Public so EditorUndoRedoManager.AddDoMethod can invoke it by name via reflection.</summary>
    public void UpsertAnimationKey(Animation anim, int trackIndex, double time, Variant value, double easing)
    {
        var keyIdx = FindAnimationKeyAtTime(anim, trackIndex, (float)time);
        if (keyIdx < 0)
            keyIdx = anim.TrackInsertKey(trackIndex, time, value);
        else
            anim.TrackSetKeyValue(trackIndex, keyIdx, value);
        if (easing != 1.0)
            anim.TrackSetKeyTransition(trackIndex, keyIdx, (float)easing);
    }

    /// <summary>Public so EditorUndoRedoManager.AddUndoMethod can invoke it by name via reflection.</summary>
    public void RestoreAnimationKey(Animation anim, int trackIndex, double time, bool hadOldKey, Variant oldValue, double oldEasing)
    {
        var keyIdx = FindAnimationKeyAtTime(anim, trackIndex, (float)time);
        if (hadOldKey)
        {
            if (keyIdx < 0)
                keyIdx = anim.TrackInsertKey(trackIndex, time, oldValue);
            else
                anim.TrackSetKeyValue(trackIndex, keyIdx, oldValue);
            anim.TrackSetKeyTransition(trackIndex, keyIdx, (float)oldEasing);
        }
        else if (keyIdx >= 0)
        {
            anim.TrackRemoveKey(trackIndex, keyIdx);
        }
    }
}
