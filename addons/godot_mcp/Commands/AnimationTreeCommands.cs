using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Faithful port of addons/godot_mcp/commands/animation_tree_commands.gd.
/// Exposes AnimationTree / AnimationNodeStateMachine / AnimationNodeBlendTree
/// editing tools, all undo-tracked via EditorUndoRedoManager.
/// </summary>
[Tool]
public partial class AnimationTreeCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "create_animation_tree",
            Description = "Create an AnimationTree child node with an AnimationNodeStateMachine root, optionally linked to an AnimationPlayer (undo-tracked).",
            Category = "animation_tree",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the parent node to attach the AnimationTree under."),
                ["anim_player"] = Schema.Str("Path (relative to the AnimationTree) to the AnimationPlayer to link.", ""),
                ["name"] = Schema.Str("Name for the new AnimationTree node.", "AnimationTree"),
            }, "node_path"),
            Handler = p => Task.FromResult(CreateAnimationTree(p)),
        },
        new()
        {
            Name = "get_animation_tree_structure",
            Description = "Get the full structure of an AnimationTree's root node graph (state machine states/transitions or blend tree nodes).",
            Category = "animation_tree",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationTree node in the edited scene."),
            }, "node_path"),
            Handler = p => Task.FromResult(GetAnimationTreeStructure(p)),
        },
        new()
        {
            Name = "add_state_machine_state",
            Description = "Add a new state (Animation/BlendTree/StateMachine node) to a state machine (undo-tracked).",
            Category = "animation_tree",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationTree node in the edited scene."),
                ["state_name"] = Schema.Str("Name of the new state."),
                ["state_machine_path"] = Schema.Str("Slash-separated path to a nested state machine (e.g. 'Run/SubState'). Empty targets the root.", ""),
                ["state_type"] = Schema.Enum("Type of node to create for the state.", "animation", "blend_tree", "state_machine"),
                ["animation"] = Schema.Str("Animation name to assign when state_type is 'animation'.", ""),
                ["position_x"] = Schema.Num("Graph editor X position for the new state.", 0.0),
                ["position_y"] = Schema.Num("Graph editor Y position for the new state.", 0.0),
            }, "node_path", "state_name"),
            Handler = p => Task.FromResult(AddStateMachineState(p)),
        },
        new()
        {
            Name = "remove_state_machine_state",
            Description = "Remove a state from a state machine (undo-tracked).",
            Category = "animation_tree",
            Destructive = true,
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationTree node in the edited scene."),
                ["state_name"] = Schema.Str("Name of the state to remove."),
                ["state_machine_path"] = Schema.Str("Slash-separated path to a nested state machine. Empty targets the root.", ""),
            }, "node_path", "state_name"),
            Handler = p => Task.FromResult(RemoveStateMachineState(p)),
        },
        new()
        {
            Name = "add_state_machine_transition",
            Description = "Add a transition between two states ('Start'/'End' are built-in) in a state machine (undo-tracked).",
            Category = "animation_tree",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationTree node in the edited scene."),
                ["from_state"] = Schema.Str("Source state name ('Start' allowed)."),
                ["to_state"] = Schema.Str("Destination state name ('End' allowed)."),
                ["state_machine_path"] = Schema.Str("Slash-separated path to a nested state machine. Empty targets the root.", ""),
                ["switch_mode"] = Schema.Enum("Transition switch mode.", "at_end", "immediate", "sync"),
                ["advance_mode"] = Schema.Enum("Transition advance mode.", "disabled", "enabled", "auto"),
                ["advance_expression"] = Schema.Str("Optional GDScript expression gating automatic advance.", ""),
                ["xfade_time"] = Schema.Num("Cross-fade time in seconds."),
            }, "node_path", "from_state", "to_state"),
            Handler = p => Task.FromResult(AddStateMachineTransition(p)),
        },
        new()
        {
            Name = "remove_state_machine_transition",
            Description = "Remove a transition between two states in a state machine (undo-tracked).",
            Category = "animation_tree",
            Destructive = true,
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationTree node in the edited scene."),
                ["from_state"] = Schema.Str("Source state name."),
                ["to_state"] = Schema.Str("Destination state name."),
                ["state_machine_path"] = Schema.Str("Slash-separated path to a nested state machine. Empty targets the root.", ""),
            }, "node_path", "from_state", "to_state"),
            Handler = p => Task.FromResult(RemoveStateMachineTransition(p)),
        },
        new()
        {
            Name = "set_blend_tree_node",
            Description = "Add or replace a node inside a BlendTree (found via a state machine state) and optionally connect it to another node (undo-tracked).",
            Category = "animation_tree",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationTree node in the edited scene."),
                ["blend_tree_state"] = Schema.Str("Name of the state (inside the resolved state machine) that holds the BlendTree."),
                ["bt_node_name"] = Schema.Str("Name of the node inside the BlendTree to add/replace."),
                ["bt_node_type"] = Schema.Enum("Blend tree node type.", "Animation", "Add2", "Blend2", "Add3", "Blend3", "TimeScale", "TimeSeek", "Transition", "OneShot", "Sub2"),
                ["state_machine_path"] = Schema.Str("Slash-separated path to the state machine containing blend_tree_state. Empty targets the root.", ""),
                ["animation"] = Schema.Str("Animation name to assign when bt_node_type is 'Animation'.", ""),
                ["position_x"] = Schema.Num("Graph editor X position for the node.", 0.0),
                ["position_y"] = Schema.Num("Graph editor Y position for the node.", 0.0),
                ["connect_to"] = Schema.Str("Optional name of another BlendTree node whose output feeds this node's input.", ""),
                ["connect_port"] = Schema.Int("Input port index on connect_to to wire to this node.", 0),
            }, "node_path", "blend_tree_state", "bt_node_name", "bt_node_type"),
            Handler = p => Task.FromResult(SetBlendTreeNode(p)),
        },
        new()
        {
            Name = "set_tree_parameter",
            Description = "Set a runtime parameter on an AnimationTree (auto-prefixed with 'parameters/'). String values are smart-parsed as expressions when possible (undo-tracked).",
            Category = "animation_tree",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the AnimationTree node in the edited scene."),
                ["parameter"] = Schema.Str("Parameter name (with or without the 'parameters/' prefix)."),
                ["value"] = Schema.Any("Value to assign to the parameter."),
            }, "node_path", "parameter"),
            Handler = p => Task.FromResult(SetTreeParameter(p)),
        },
    };

    // ── shared lookup / resolution ───────────────────────────────────────────

    /// <summary>Find AnimationTree on a node or return null.</summary>
    private static AnimationTree? FindAnimationTree(string nodePath)
    {
        var node = FindNodeByPath(nodePath);
        return node as AnimationTree;
    }

    /// <summary>
    /// Navigate to a nested state machine by slash-separated path (e.g. "Run/SubState").
    /// </summary>
    private static (AnimationNodeStateMachine? Sm, Dictionary? Error) ResolveStateMachine(AnimationTree tree, string smPath)
    {
        if (tree.TreeRoot is not AnimationNodeStateMachine root)
            return (null, ErrorInvalidParams("AnimationTree root is not an AnimationNodeStateMachine"));

        if (string.IsNullOrEmpty(smPath) || smPath == ".")
            return (root, null);

        var current = root;
        var parts = smPath.Split('/');
        foreach (var part in parts)
        {
            if (!current.HasNode(part))
                return (null, ErrorNotFound($"State machine node '{part}' in path '{smPath}'"));
            var child = current.GetNode(part);
            if (child is not AnimationNodeStateMachine childSm)
                return (null, ErrorInvalidParams($"Node '{part}' is not a StateMachine"));
            current = childSm;
        }
        return (current, null);
    }

    /// <summary>
    /// Resolve a BlendTree inside the tree. bt_path can be a state name inside a state machine,
    /// or a slash-separated path. The last segment is the BlendTree node name.
    /// </summary>
    private static (AnimationNodeBlendTree? Bt, Dictionary? Error) ResolveBlendTree(AnimationTree tree, string smPath, string btName)
    {
        var (sm, smErr) = ResolveStateMachine(tree, smPath);
        if (smErr != null)
            return (null, smErr);

        if (!sm!.HasNode(btName))
            return (null, ErrorNotFound($"BlendTree node '{btName}'"));

        var node = sm.GetNode(btName);
        if (node is not AnimationNodeBlendTree bt)
            return (null, ErrorInvalidParams($"Node '{btName}' is not an AnimationNodeBlendTree"));

        return (bt, null);
    }

    // ── create_animation_tree ────────────────────────────────────────────────

    private Dictionary CreateAnimationTree(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        var parent = FindNodeByPath(nodePath);
        if (parent == null)
            return ErrorNotFound($"Node at '{nodePath}'");

        var animPlayerPath = OptionalString(@params, "anim_player", "");
        var treeName = OptionalString(@params, "name", "AnimationTree");

        // Create the AnimationTree
        var tree = new AnimationTree { Name = treeName };

        // Set root to AnimationNodeStateMachine
        var stateMachine = new AnimationNodeStateMachine();
        tree.TreeRoot = stateMachine;

        // Link to AnimationPlayer if provided
        if (!string.IsNullOrEmpty(animPlayerPath))
            tree.AnimPlayer = new NodePath(animPlayerPath);

        AddChildWithUndo(parent, tree, root, "MCP: Create AnimationTree");

        return Success(new Dictionary
        {
            ["name"] = tree.Name,
            ["node_path"] = root.GetPathTo(tree).ToString(),
            ["root_type"] = "AnimationNodeStateMachine",
            ["anim_player"] = animPlayerPath,
            ["created"] = true,
        });
    }

    // ── get_animation_tree_structure ─────────────────────────────────────────

    private static Dictionary GetAnimationTreeStructure(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var tree = FindAnimationTree(nodePath);
        if (tree == null)
            return ErrorNotFound($"AnimationTree at '{nodePath}'");

        var root = tree.TreeRoot;
        if (root == null)
            return Success(new Dictionary { ["node_path"] = nodePath, ["root"] = default });

        var structure = ReadNodeStructure(root);
        structure["active"] = tree.Active;
        structure["anim_player"] = tree.AnimPlayer.ToString();
        structure["node_path"] = nodePath;

        return Success(structure);
    }

    private static Dictionary ReadNodeStructure(AnimationNode node)
    {
        switch (node)
        {
            case AnimationNodeStateMachine sm:
                return ReadStateMachineStructure(sm);
            case AnimationNodeBlendTree bt:
                return ReadBlendTreeStructure(bt);
            case AnimationNodeAnimation animNode:
                return new Dictionary { ["type"] = "AnimationNodeAnimation", ["animation"] = animNode.Animation.ToString() };
            default:
                return new Dictionary { ["type"] = node.GetClass() };
        }
    }

    private static Dictionary ReadStateMachineStructure(AnimationNodeStateMachine sm)
    {
        var states = new Godot.Collections.Array();
        var nodeList = GetSmNodeNames(sm);
        foreach (var stateName in nodeList)
        {
            var child = sm.GetNode(stateName);
            var position = sm.GetNodePosition(stateName);
            var stateInfo = new Dictionary
            {
                ["name"] = stateName,
                ["position"] = new Dictionary { ["x"] = position.X, ["y"] = position.Y },
            };
            foreach (var (key, val) in ReadNodeStructure(child))
            {
                if (!stateInfo.ContainsKey(key))
                    stateInfo[key] = val;
            }
            states.Add(stateInfo);
        }

        var transitions = new Godot.Collections.Array();
        for (var i = 0; i < sm.GetTransitionCount(); i++)
        {
            var fromNode = sm.GetTransitionFrom(i);
            var toNode = sm.GetTransitionTo(i);
            var trans = sm.GetTransition(i);
            var transInfo = new Dictionary
            {
                ["from"] = fromNode.ToString(),
                ["to"] = toNode.ToString(),
                ["switch_mode"] = (int)trans.SwitchMode,
                ["advance_mode"] = (int)trans.AdvanceMode,
            };
            if (!string.IsNullOrEmpty(trans.AdvanceExpression))
                transInfo["advance_expression"] = trans.AdvanceExpression;
            if (trans.AdvanceMode == AnimationNodeStateMachineTransition.AdvanceModeEnum.Auto)
                transInfo["auto"] = true;
            transitions.Add(transInfo);
        }

        return new Dictionary
        {
            ["type"] = "AnimationNodeStateMachine",
            ["states"] = states,
            ["transitions"] = transitions,
        };
    }

    /// <summary>
    /// AnimationNodeStateMachine doesn't expose a simple node-list method, so we
    /// derive state names from the resource's property list (states/&lt;name&gt;/node),
    /// excluding the built-in Start/End nodes.
    /// </summary>
    private static List<string> GetSmNodeNames(AnimationNodeStateMachine sm)
    {
        var names = new List<string>();
        foreach (Dictionary prop in sm.GetPropertyList())
        {
            var pname = prop["name"].AsString();
            if (pname.StartsWith("states/") && pname.EndsWith("/node"))
            {
                var stateName = pname.Split('/')[1];
                if (stateName != "Start" && stateName != "End")
                    names.Add(stateName);
            }
        }
        return names;
    }

    private static Dictionary ReadBlendTreeStructure(AnimationNodeBlendTree bt)
    {
        var nodesInfo = new Godot.Collections.Array();
        var propList = bt.GetPropertyList();
        var nodeNames = new List<string>();
        foreach (Dictionary prop in propList)
        {
            var pname = prop["name"].AsString();
            if (pname.StartsWith("nodes/") && pname.EndsWith("/node"))
            {
                var n = pname.Split('/')[1];
                if (n != "output")
                    nodeNames.Add(n);
            }
        }

        foreach (var nName in nodeNames)
        {
            var child = bt.GetNode(nName);
            var position = bt.GetNodePosition(nName);
            var nodeInfo = new Dictionary
            {
                ["name"] = nName,
                ["type"] = child.GetClass(),
                ["position"] = new Dictionary { ["x"] = position.X, ["y"] = position.Y },
            };
            if (child is AnimationNodeAnimation animNode)
                nodeInfo["animation"] = animNode.Animation.ToString();
            nodesInfo.Add(nodeInfo);
        }

        return new Dictionary { ["type"] = "AnimationNodeBlendTree", ["nodes"] = nodesInfo };
    }

    // ── add_state_machine_state ──────────────────────────────────────────────

    private Dictionary AddStateMachineState(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (stateName, err2) = RequireString(@params, "state_name");
        if (err2 != null)
            return err2;

        var tree = FindAnimationTree(nodePath);
        if (tree == null)
            return ErrorNotFound($"AnimationTree at '{nodePath}'");

        var smPath = OptionalString(@params, "state_machine_path", "");
        var (sm, smErr) = ResolveStateMachine(tree, smPath);
        if (smErr != null)
            return smErr;

        if (sm!.HasNode(stateName))
            return ErrorInvalidParams($"State '{stateName}' already exists");

        var stateType = OptionalString(@params, "state_type", "animation");
        var positionX = OptionalFloat(@params, "position_x", 0.0);
        var positionY = OptionalFloat(@params, "position_y", 0.0);
        var position = new Vector2((float)positionX, (float)positionY);

        AnimationNode node;
        switch (stateType)
        {
            case "animation":
            {
                var animNode = new AnimationNodeAnimation();
                var animName = OptionalString(@params, "animation", "");
                if (!string.IsNullOrEmpty(animName))
                    animNode.Animation = animName;
                node = animNode;
                break;
            }
            case "blend_tree":
                node = new AnimationNodeBlendTree();
                break;
            case "state_machine":
                node = new AnimationNodeStateMachine();
                break;
            default:
                return ErrorInvalidParams($"Unknown state_type: '{stateType}'. Use 'animation', 'blend_tree', or 'state_machine'");
        }

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Add state machine state");
        undoRedo.AddDoMethod(sm, AnimationNodeStateMachine.MethodName.AddNode, stateName, node, position);
        undoRedo.AddDoReference(node);
        undoRedo.AddUndoMethod(sm, AnimationNodeStateMachine.MethodName.RemoveNode, stateName);
        undoRedo.CommitAction();

        return Success(new Dictionary
        {
            ["state_name"] = stateName,
            ["state_type"] = stateType,
            ["position"] = new Dictionary { ["x"] = positionX, ["y"] = positionY },
            ["added"] = true,
        });
    }

    // ── remove_state_machine_state ───────────────────────────────────────────

    private Dictionary RemoveStateMachineState(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (stateName, err2) = RequireString(@params, "state_name");
        if (err2 != null)
            return err2;

        var tree = FindAnimationTree(nodePath);
        if (tree == null)
            return ErrorNotFound($"AnimationTree at '{nodePath}'");

        var smPath = OptionalString(@params, "state_machine_path", "");
        var (sm, smErr) = ResolveStateMachine(tree, smPath);
        if (smErr != null)
            return smErr;

        if (!sm!.HasNode(stateName))
            return ErrorNotFound($"State '{stateName}'");

        var oldNode = sm.GetNode(stateName);
        var oldPosition = sm.GetNodePosition(stateName);
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Remove state machine state");
        undoRedo.AddDoMethod(sm, AnimationNodeStateMachine.MethodName.RemoveNode, stateName);
        undoRedo.AddUndoMethod(sm, AnimationNodeStateMachine.MethodName.AddNode, stateName, oldNode, oldPosition);
        undoRedo.AddUndoReference(oldNode);
        undoRedo.CommitAction();

        return Success(new Dictionary { ["state_name"] = stateName, ["removed"] = true });
    }

    // ── add_state_machine_transition ─────────────────────────────────────────

    private Dictionary AddStateMachineTransition(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (fromState, err2) = RequireString(@params, "from_state");
        if (err2 != null)
            return err2;

        var (toState, err3) = RequireString(@params, "to_state");
        if (err3 != null)
            return err3;

        var tree = FindAnimationTree(nodePath);
        if (tree == null)
            return ErrorNotFound($"AnimationTree at '{nodePath}'");

        var smPath = OptionalString(@params, "state_machine_path", "");
        var (sm, smErr) = ResolveStateMachine(tree, smPath);
        if (smErr != null)
            return smErr;

        // Validate states exist (Start and End are special built-in nodes)
        if (fromState != "Start" && fromState != "End" && !sm!.HasNode(fromState))
            return ErrorNotFound($"State '{fromState}'");
        if (toState != "Start" && toState != "End" && !sm!.HasNode(toState))
            return ErrorNotFound($"State '{toState}'");

        var transition = new AnimationNodeStateMachineTransition();

        // switch_mode: AT_END=0, IMMEDIATE=1, SYNC=2 (per the source .gd's own comment/mapping)
        var switchModeStr = OptionalString(@params, "switch_mode", "immediate");
        transition.SwitchMode = switchModeStr switch
        {
            "at_end" => AnimationNodeStateMachineTransition.SwitchModeEnum.AtEnd,
            "immediate" => AnimationNodeStateMachineTransition.SwitchModeEnum.Immediate,
            "sync" => AnimationNodeStateMachineTransition.SwitchModeEnum.AtEnd, // SYNC maps similarly (faithful to source .gd)
            _ => AnimationNodeStateMachineTransition.SwitchModeEnum.Immediate,
        };

        // advance_mode: DISABLED=0, ENABLED=1, AUTO=2
        var advanceModeStr = OptionalString(@params, "advance_mode", "enabled");
        transition.AdvanceMode = advanceModeStr switch
        {
            "disabled" => AnimationNodeStateMachineTransition.AdvanceModeEnum.Disabled,
            "enabled" => AnimationNodeStateMachineTransition.AdvanceModeEnum.Enabled,
            "auto" => AnimationNodeStateMachineTransition.AdvanceModeEnum.Auto,
            _ => AnimationNodeStateMachineTransition.AdvanceModeEnum.Enabled,
        };

        // advance_expression
        var expression = OptionalString(@params, "advance_expression", "");
        if (!string.IsNullOrEmpty(expression))
            transition.AdvanceExpression = expression;

        // xfade_time
        if (@params.ContainsKey("xfade_time"))
            transition.XfadeTime = (float)OptionalFloat(@params, "xfade_time", 0.0);

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Add state machine transition");
        undoRedo.AddDoMethod(sm, AnimationNodeStateMachine.MethodName.AddTransition, fromState, toState, transition);
        undoRedo.AddDoReference(transition);
        undoRedo.AddUndoMethod(sm, AnimationNodeStateMachine.MethodName.RemoveTransition, fromState, toState);
        undoRedo.CommitAction();

        return Success(new Dictionary
        {
            ["from"] = fromState,
            ["to"] = toState,
            ["switch_mode"] = switchModeStr,
            ["advance_mode"] = advanceModeStr,
            ["advance_expression"] = expression,
            ["added"] = true,
        });
    }

    // ── remove_state_machine_transition ──────────────────────────────────────

    private Dictionary RemoveStateMachineTransition(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (fromState, err2) = RequireString(@params, "from_state");
        if (err2 != null)
            return err2;

        var (toState, err3) = RequireString(@params, "to_state");
        if (err3 != null)
            return err3;

        var tree = FindAnimationTree(nodePath);
        if (tree == null)
            return ErrorNotFound($"AnimationTree at '{nodePath}'");

        var smPath = OptionalString(@params, "state_machine_path", "");
        var (sm, smErr) = ResolveStateMachine(tree, smPath);
        if (smErr != null)
            return smErr;

        // Check if transition exists
        var found = false;
        for (var i = 0; i < sm!.GetTransitionCount(); i++)
        {
            if (sm.GetTransitionFrom(i).ToString() == fromState && sm.GetTransitionTo(i).ToString() == toState)
            {
                found = true;
                break;
            }
        }

        if (!found)
            return ErrorNotFound($"Transition from '{fromState}' to '{toState}'");

        AnimationNodeStateMachineTransition? transition = null;
        for (var i = 0; i < sm.GetTransitionCount(); i++)
        {
            if (sm.GetTransitionFrom(i).ToString() == fromState && sm.GetTransitionTo(i).ToString() == toState)
            {
                transition = sm.GetTransition(i);
                break;
            }
        }

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Remove state machine transition");
        undoRedo.AddDoMethod(sm, AnimationNodeStateMachine.MethodName.RemoveTransition, fromState, toState);
        undoRedo.AddUndoMethod(sm, AnimationNodeStateMachine.MethodName.AddTransition, fromState, toState, transition!);
        undoRedo.AddUndoReference(transition!);
        undoRedo.CommitAction();

        return Success(new Dictionary { ["from"] = fromState, ["to"] = toState, ["removed"] = true });
    }

    // ── set_blend_tree_node ───────────────────────────────────────────────────

    private Dictionary SetBlendTreeNode(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (btState, err2) = RequireString(@params, "blend_tree_state");
        if (err2 != null)
            return err2;

        var (btNodeName, err3) = RequireString(@params, "bt_node_name");
        if (err3 != null)
            return err3;

        var (btNodeType, err4) = RequireString(@params, "bt_node_type");
        if (err4 != null)
            return err4;

        var tree = FindAnimationTree(nodePath);
        if (tree == null)
            return ErrorNotFound($"AnimationTree at '{nodePath}'");

        var smPath = OptionalString(@params, "state_machine_path", "");
        var (bt, btErr) = ResolveBlendTree(tree, smPath, btState);
        if (btErr != null)
            return btErr;

        var positionX = OptionalFloat(@params, "position_x", 0.0);
        var positionY = OptionalFloat(@params, "position_y", 0.0);
        var position = new Vector2((float)positionX, (float)positionY);

        var hadOldNode = bt!.HasNode(btNodeName);
        AnimationNode? oldNode = hadOldNode ? bt.GetNode(btNodeName) : null;
        var oldPosition = hadOldNode ? bt.GetNodePosition(btNodeName) : Vector2.Zero;

        AnimationNode node;
        switch (btNodeType)
        {
            case "Animation":
            {
                var animNode = new AnimationNodeAnimation();
                var animName = OptionalString(@params, "animation", "");
                if (!string.IsNullOrEmpty(animName))
                    animNode.Animation = animName;
                node = animNode;
                break;
            }
            case "Add2":
                node = new AnimationNodeAdd2();
                break;
            case "Blend2":
                node = new AnimationNodeBlend2();
                break;
            case "Add3":
                node = new AnimationNodeAdd3();
                break;
            case "Blend3":
                node = new AnimationNodeBlend3();
                break;
            case "TimeScale":
                node = new AnimationNodeTimeScale();
                break;
            case "TimeSeek":
                node = new AnimationNodeTimeSeek();
                break;
            case "Transition":
                node = new AnimationNodeTransition();
                break;
            case "OneShot":
                node = new AnimationNodeOneShot();
                break;
            case "Sub2":
                node = new AnimationNodeSub2();
                break;
            default:
                return ErrorInvalidParams(
                    $"Unknown bt_node_type: '{btNodeType}'. Use: Animation, Add2, Blend2, Add3, Blend3, TimeScale, TimeSeek, Transition, OneShot, Sub2");
        }

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Set blend tree node");
        if (hadOldNode)
        {
            undoRedo.AddDoMethod(bt, AnimationNodeBlendTree.MethodName.RemoveNode, btNodeName);
            undoRedo.AddUndoMethod(bt, AnimationNodeBlendTree.MethodName.AddNode, btNodeName, oldNode!, oldPosition);
            undoRedo.AddUndoReference(oldNode!);
        }
        undoRedo.AddDoMethod(bt, AnimationNodeBlendTree.MethodName.AddNode, btNodeName, node, position);
        undoRedo.AddDoReference(node);
        undoRedo.AddUndoMethod(bt, AnimationNodeBlendTree.MethodName.RemoveNode, btNodeName);

        // Connect to another node if specified
        var connectTo = OptionalString(@params, "connect_to", "");
        var connectPort = OptionalInt(@params, "connect_port", 0);
        if (!string.IsNullOrEmpty(connectTo))
            undoRedo.AddDoMethod(bt, AnimationNodeBlendTree.MethodName.ConnectNode, connectTo, connectPort, btNodeName);
        undoRedo.CommitAction();

        Variant connectedToValue = default;
        if (!string.IsNullOrEmpty(connectTo))
            connectedToValue = connectTo;
        return Success(new Dictionary
        {
            ["blend_tree_state"] = btState,
            ["bt_node_name"] = btNodeName,
            ["bt_node_type"] = btNodeType,
            ["position"] = new Dictionary { ["x"] = positionX, ["y"] = positionY },
            ["connected_to"] = connectedToValue,
            ["added"] = true,
        });
    }

    // ── set_tree_parameter ────────────────────────────────────────────────────

    private Dictionary SetTreeParameter(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (parameter, err2) = RequireString(@params, "parameter");
        if (err2 != null)
            return err2;

        var tree = FindAnimationTree(nodePath);
        if (tree == null)
            return ErrorNotFound($"AnimationTree at '{nodePath}'");

        if (!@params.ContainsKey("value"))
            return ErrorInvalidParams("Missing required parameter: value");

        Variant value = @params["value"];

        // Prefix with "parameters/" if not already
        if (!parameter.StartsWith("parameters/"))
            parameter = "parameters/" + parameter;

        // Parse string values for common types
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

        SetPropertyWithUndo(tree, parameter, value, "MCP: Set AnimationTree parameter");

        // Read back to confirm
        var actual = tree.Get(parameter);

        return Success(new Dictionary
        {
            ["parameter"] = parameter,
            ["value"] = actual.ToString(),
            ["set"] = true,
        });
    }
}
