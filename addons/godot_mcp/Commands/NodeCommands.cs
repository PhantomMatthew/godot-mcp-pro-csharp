using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// Node manipulation tools: add/delete/duplicate/move nodes, edit properties,
/// signals, groups, and editor selection. Faithful port of
/// addons/godot_mcp/commands/node_commands.gd. All mutations go through
/// EditorUndoRedoManager so they participate in the editor's undo history.
/// </summary>
[Tool]
public partial class NodeCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "add_node",
            Description = "Add a new node as a child of an existing node in the edited scene. " +
                "'type' may be a built-in ClassDB class name or a script class_name.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["type"] = Schema.Str("Node class name (e.g. 'Sprite2D') or script class_name"),
                ["parent_path"] = Schema.Str("Scene-relative path of the parent node", "."),
                ["name"] = Schema.Str("Name for the new node (optional)"),
                ["properties"] = Schema.Obj("Initial property values to apply to the new node"),
            }, "type"),
            Handler = AddNode,
        },
        new()
        {
            Name = "delete_node",
            Description = "Delete a node from the edited scene (cannot delete the root node).",
            Category = "node",
            Destructive = true,
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node to delete"),
            }, "node_path"),
            Handler = DeleteNode,
        },
        new()
        {
            Name = "duplicate_node",
            Description = "Duplicate a node (and its children) within the edited scene.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node to duplicate"),
                ["name"] = Schema.Str("Name for the duplicate (defaults to '<name>_copy')"),
            }, "node_path"),
            Handler = DuplicateNode,
        },
        new()
        {
            Name = "move_node",
            Description = "Reparent a node to a new parent within the edited scene.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node to move"),
                ["new_parent_path"] = Schema.Str("Scene-relative path of the new parent"),
            }, "node_path", "new_parent_path"),
            Handler = MoveNode,
        },
        new()
        {
            Name = "update_property",
            Description = "Set a property on a node, smart-parsing the value to match the property's current type.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node"),
                ["property"] = Schema.Str("Property name to set"),
                ["value"] = Schema.Any("New value for the property"),
            }, "node_path", "property", "value"),
            Handler = UpdateProperty,
        },
        new()
        {
            Name = "get_node_properties",
            Description = "Get all editor-visible properties of a node, optionally filtered by category prefix.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node"),
                ["category"] = Schema.Str("Only include properties whose name starts with this prefix (optional)"),
            }, "node_path"),
            Handler = GetNodeProperties,
        },
        new()
        {
            Name = "add_resource",
            Description = "Instantiate a Resource-derived type and assign it to a node property.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node"),
                ["property"] = Schema.Str("Property name to assign the resource to"),
                ["resource_type"] = Schema.Str("ClassDB name of the Resource-derived type to instantiate"),
                ["resource_properties"] = Schema.Obj("Initial property values to apply to the new resource"),
            }, "node_path", "property", "resource_type"),
            Handler = AddResource,
        },
        new()
        {
            Name = "set_anchor_preset",
            Description = "Apply a layout anchor preset to a Control node.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the Control node"),
                ["preset"] = Schema.Enum("Anchor preset name",
                    "top_left", "top_right", "bottom_left", "bottom_right",
                    "center_left", "center_top", "center_right", "center_bottom", "center",
                    "left_wide", "top_wide", "right_wide", "bottom_wide",
                    "vcenter_wide", "hcenter_wide", "full_rect"),
                ["keep_offsets"] = Schema.Bool("Keep current size instead of resizing to minimum size", false),
            }, "node_path", "preset"),
            Handler = SetAnchorPreset,
        },
        new()
        {
            Name = "rename_node",
            Description = "Rename a node in the edited scene.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node to rename"),
                ["new_name"] = Schema.Str("New name for the node"),
            }, "node_path", "new_name"),
            Handler = RenameNode,
        },
        new()
        {
            Name = "connect_signal",
            Description = "Connect a signal from a source node to a method on a target node (persistent connection).",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["source_path"] = Schema.Str("Scene-relative path of the signal source node"),
                ["signal_name"] = Schema.Str("Signal name on the source node"),
                ["target_path"] = Schema.Str("Scene-relative path of the target node"),
                ["method_name"] = Schema.Str("Method name on the target node to call"),
                ["deferred"] = Schema.Bool("Use CONNECT_DEFERRED", false),
                ["one_shot"] = Schema.Bool("Use CONNECT_ONE_SHOT", false),
            }, "source_path", "signal_name", "target_path", "method_name"),
            Handler = ConnectSignal,
        },
        new()
        {
            Name = "disconnect_signal",
            Description = "Disconnect a previously connected signal.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["source_path"] = Schema.Str("Scene-relative path of the signal source node"),
                ["signal_name"] = Schema.Str("Signal name on the source node"),
                ["target_path"] = Schema.Str("Scene-relative path of the target node"),
                ["method_name"] = Schema.Str("Method name on the target node"),
            }, "source_path", "signal_name", "target_path", "method_name"),
            Handler = DisconnectSignal,
        },
        new()
        {
            Name = "get_node_groups",
            Description = "Get the (non-internal) groups a node belongs to.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node"),
            }, "node_path"),
            Handler = GetNodeGroups,
        },
        new()
        {
            Name = "set_node_groups",
            Description = "Set the full group membership of a node, adding/removing groups as needed.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node"),
                ["groups"] = Schema.Arr("Desired list of group names", Schema.Str("Group name")),
            }, "node_path", "groups"),
            Handler = SetNodeGroups,
        },
        new()
        {
            Name = "find_nodes_in_group",
            Description = "Find all nodes in the edited scene belonging to a given group.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["group"] = Schema.Str("Group name to search for"),
            }, "group"),
            Handler = FindNodesInGroup,
        },
        new()
        {
            Name = "get_editor_selection",
            Description = "Get the nodes currently selected in the editor.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["top_only"] = Schema.Bool("Only include top-level selected nodes", false),
            }),
            Handler = GetEditorSelection,
        },
        new()
        {
            Name = "select_nodes",
            Description = "Select one or more nodes in the editor, optionally focusing/inspecting them.",
            Category = "node",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of a single node to select"),
                ["node_paths"] = Schema.Arr("Scene-relative paths of multiple nodes to select", Schema.Str("Node path")),
                ["mode"] = Schema.Enum("Selection mode", "replace", "add", "remove"),
                ["inspect"] = Schema.Bool("Inspect the node in the Inspector dock (single-node selections only)", true),
                ["focus"] = Schema.Bool("Focus/edit the node (single-node selections only, defaults to 'inspect')"),
                ["inspector_only"] = Schema.Bool("Pass inspector_only to EditorInterface.inspect_object", false),
                ["for_property"] = Schema.Str("Property to focus in the Inspector dock"),
            }),
            Handler = SelectNodes,
        },
        new()
        {
            Name = "clear_editor_selection",
            Description = "Clear the current editor node selection.",
            Category = "node",
            InputSchema = Schema.Object(),
            Handler = ClearEditorSelection,
        },
    };

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Search project files for a script with matching class_name.</summary>
    private static Script? FindScriptByClassName(string className)
    {
        var globalClasses = ProjectSettings.GetGlobalClassList();
        foreach (var entry in globalClasses)
        {
            var cls = entry.TryGetValue("class", out var c) ? c.AsString() : "";
            if (cls == className)
            {
                var path = entry.TryGetValue("path", out var p) ? p.AsString() : "";
                if (!string.IsNullOrEmpty(path))
                    return GD.Load<Script>(path);
            }
        }
        return null;
    }

    private static bool NodeHasProperty(GodotObject obj, string propertyName)
    {
        foreach (var prop in obj.GetPropertyList())
        {
            if (prop["name"].AsString() == propertyName)
                return true;
        }
        return false;
    }

    private static Godot.Collections.Array SerializeSelectionNodes(Node root, IEnumerable<Node> nodes)
    {
        var serialized = new Godot.Collections.Array();
        foreach (var node in nodes)
        {
            if (node == null)
                continue;
            if (node != root && !root.IsAncestorOf(node))
                continue;
            serialized.Add(new Dictionary
            {
                ["name"] = node.Name.ToString(),
                ["path"] = node != root ? root.GetPathTo(node).ToString() : ".",
                ["type"] = node.GetClass(),
            });
        }
        return serialized;
    }

    private static void FindInGroupRecursive(Node node, Node root, string groupName, Godot.Collections.Array matches)
    {
        if (node.IsInGroup(groupName))
        {
            matches.Add(new Dictionary
            {
                ["name"] = node.Name.ToString(),
                ["path"] = root.GetPathTo(node).ToString(),
                ["type"] = node.GetClass(),
            });
        }
        foreach (var child in node.GetChildren())
            FindInGroupRecursive(child, root, groupName, matches);
    }

    /// <summary>Mirror of _get_selection_node_paths: resolve node_paths[] or fall back to a single node_path.</summary>
    private static (Godot.Collections.Array Paths, Dictionary? Error) GetSelectionNodePaths(Dictionary @params)
    {
        if (@params.ContainsKey("node_paths"))
        {
            if (@params["node_paths"].VariantType != Variant.Type.Array)
                return (new Godot.Collections.Array(), ErrorInvalidParams("node_paths must be an array of node paths"));
            return (@params["node_paths"].AsGodotArray(), null);
        }

        var (value, err) = RequireString(@params, "node_path");
        if (err != null)
            return (new Godot.Collections.Array(), err);
        return (new Godot.Collections.Array { value }, null);
    }

    // ── Tool handlers ────────────────────────────────────────────────────────────

    private Task<Dictionary> AddNode(Dictionary @params)
    {
        var (type, err) = RequireString(@params, "type");
        if (err != null) return Task.FromResult(err);

        var parentPath = OptionalString(@params, "parent_path", ".");
        var nodeName = OptionalString(@params, "name", "");
        var properties = OptionalDict(@params, "properties") ?? new Dictionary();

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var parent = FindNodeByPath(parentPath);
        if (parent == null)
            return Task.FromResult(ErrorNotFound($"Parent node '{parentPath}'", "Use get_scene_tree to see available nodes"));

        Node node;
        Script? customScript = null;

        if (ClassDB.ClassExists(type))
        {
            var instance = ClassDB.Instantiate(type);
            if (instance.AsGodotObject() is not Node instantiatedNode)
                return Task.FromResult(ErrorInvalidParams($"'{type}' does not derive from Node"));
            node = instantiatedNode;
        }
        else
        {
            customScript = FindScriptByClassName(type);
            if (customScript == null)
                return Task.FromResult(ErrorInvalidParams(
                    $"Unknown node type: '{type}'. Not found in ClassDB or as a script class_name. Use list_scripts to see available script classes."));
            var baseType = customScript.GetInstanceBaseType();
            if (!ClassDB.ClassExists(baseType))
                return Task.FromResult(ErrorInvalidParams($"Script '{type}' extends '{baseType}' which is not a valid node type"));
            var instance = ClassDB.Instantiate(baseType);
            if (instance.AsGodotObject() is not Node instantiatedNode)
                return Task.FromResult(ErrorInvalidParams($"'{baseType}' does not derive from Node"));
            node = instantiatedNode;
            node.SetScript(customScript);
        }

        if (!string.IsNullOrEmpty(nodeName))
            node.Name = nodeName;

        // Apply properties
        foreach (var propName in properties.Keys)
        {
            var propNameStr = propName.AsString();
            if (!NodeHasProperty(node, propNameStr))
                continue;
            var current = node.Get(propNameStr);
            var parsed = PropertyParser.ParseValue(properties[propName], current.VariantType);
            node.Set(propNameStr, parsed);
        }

        AddChildWithUndo(parent, node, root, $"MCP: Add {type}");

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["type"] = type,
            ["name"] = node.Name.ToString(),
        }));
    }

    private Task<Dictionary> DeleteNode(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        if (node == root)
            return Task.FromResult(ErrorInvalidParams("Cannot delete the root node"));

        var parent = node.GetParent();
        var nodeName = node.Name.ToString();

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Delete {nodeName}");
        undoRedo.AddDoMethod(parent, Node.MethodName.RemoveChild, node);
        undoRedo.AddUndoMethod(parent, Node.MethodName.AddChild, node);
        undoRedo.AddUndoMethod(node, Node.MethodName.SetOwner, root);
        undoRedo.AddUndoReference(node);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary { ["deleted"] = nodeName }));
    }

    private Task<Dictionary> DuplicateNode(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var newName = OptionalString(@params, "name", "");

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        if (string.IsNullOrEmpty(newName))
            newName = node.Name + "_copy";

        var dup = node.Duplicate();
        dup.Name = newName;
        var parent = node.GetParent();

        AddChildWithUndo(parent, dup, root, $"MCP: Duplicate {node.Name}");

        NodeUtils.SetOwnerRecursive(dup, root);

        return Task.FromResult(Success(new Dictionary
        {
            ["original"] = root.GetPathTo(node).ToString(),
            ["duplicate"] = root.GetPathTo(dup).ToString(),
            ["name"] = dup.Name.ToString(),
        }));
    }

    private Task<Dictionary> MoveNode(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var (newParentPath, err2) = RequireString(@params, "new_parent_path");
        if (err2 != null) return Task.FromResult(err2);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        if (node == root)
            return Task.FromResult(ErrorInvalidParams("Cannot move the root node"));

        var newParent = FindNodeByPath(newParentPath);
        if (newParent == null)
            return Task.FromResult(ErrorNotFound($"Target parent '{newParentPath}'", "Use get_scene_tree to see available nodes"));

        // Check we're not moving a node into its own subtree
        if (newParent == node || node.IsAncestorOf(newParent))
            return Task.FromResult(ErrorInvalidParams("Cannot move a node into its own subtree"));

        var oldParent = node.GetParent();

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Move {node.Name}");
        undoRedo.AddDoMethod(oldParent, Node.MethodName.RemoveChild, node);
        undoRedo.AddDoMethod(newParent, Node.MethodName.AddChild, node);
        undoRedo.AddDoMethod(node, Node.MethodName.SetOwner, root);
        undoRedo.AddUndoMethod(newParent, Node.MethodName.RemoveChild, node);
        undoRedo.AddUndoMethod(oldParent, Node.MethodName.AddChild, node);
        undoRedo.AddUndoMethod(node, Node.MethodName.SetOwner, root);
        undoRedo.CommitAction();

        NodeUtils.SetOwnerRecursive(node, root);

        return Task.FromResult(Success(new Dictionary
        {
            ["node"] = node.Name.ToString(),
            ["old_parent"] = root.GetPathTo(oldParent).ToString(),
            ["new_parent"] = root.GetPathTo(newParent).ToString(),
            ["new_path"] = root.GetPathTo(node).ToString(),
        }));
    }

    private Task<Dictionary> UpdateProperty(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var (property, err2) = RequireString(@params, "property");
        if (err2 != null) return Task.FromResult(err2);

        if (!@params.ContainsKey("value"))
            return Task.FromResult(ErrorInvalidParams("Missing required parameter: value"));
        var value = @params["value"];

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        // Check property exists
        Dictionary? matchedProp = null;
        var available = new Godot.Collections.Array();
        foreach (var prop in node.GetPropertyList())
        {
            if (prop["name"].AsString() == property)
                matchedProp = prop;
            if ((prop["usage"].AsInt64() & (long)PropertyUsageFlags.Editor) != 0)
                available.Add(prop["name"]);
        }
        if (matchedProp == null)
        {
            var slice = new Godot.Collections.Array();
            for (var i = 0; i < Math.Min(20, available.Count); i++) slice.Add(available[i]);
            return Task.FromResult(ErrorNotFound($"Property '{property}' on {node.GetClass()}", $"Available: {slice}"));
        }

        var oldValue = node.Get(property);
        var targetType = oldValue.VariantType;
        var parsedValue = PropertyParser.ParseValue(value, targetType);

        // Handle @export node references (e.g. @export var hud: HUD)
        // VariantType returns Nil when unset or Object when set, neither resolves
        // a string path to a node — check the property hint instead.
        if (value.VariantType == Variant.Type.String)
        {
            if (matchedProp["hint"].As<PropertyHint>() == PropertyHint.NodeType)
            {
                var pathStr = value.AsString();
                var targetNode = node.GetNodeOrNull(new NodePath(pathStr));
                targetNode ??= root.GetNodeOrNull(new NodePath(pathStr));
                if (targetNode == null)
                    return Task.FromResult(ErrorNotFound($"Node '{pathStr}'", $"Could not resolve node path for property '{property}'"));
                parsedValue = targetNode;
            }
        }

        // Fail loudly instead of committing a String into an Object-typed property —
        // the engine would coerce it to null and silently destroy the existing value.
        if (value.VariantType == Variant.Type.String && targetType == Variant.Type.Object
            && parsedValue.VariantType == Variant.Type.Nil)
        {
            return Task.FromResult(ErrorInvalidParams(
                $"Could not resolve '{value.AsString()}' to a Resource for property '{property}'"));
        }
        if (value.VariantType == Variant.Type.String && parsedValue.VariantType == Variant.Type.String
            && (targetType == Variant.Type.Nil || targetType == Variant.Type.Object))
        {
            var s = value.AsString();
            if (s.StartsWith("res://") || s.StartsWith("uid://"))
                return Task.FromResult(ErrorNotFound($"Resource '{s}'", $"The path could not be loaded for property '{property}'"));
        }

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Set {node.Name}.{property}");
        undoRedo.AddDoProperty(node, property, parsedValue);
        undoRedo.AddUndoProperty(node, property, oldValue);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node"] = root.GetPathTo(node).ToString(),
            ["property"] = property,
            ["old_value"] = PropertyParser.SerializeValue(oldValue),
            ["new_value"] = PropertyParser.SerializeValue(node.Get(property)),
        }));
    }

    private Task<Dictionary> GetNodeProperties(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        var category = OptionalString(@params, "category", "");
        var props = NodeUtils.GetNodePropertiesDict(node);

        if (!string.IsNullOrEmpty(category))
        {
            var filtered = new Dictionary();
            foreach (var key in props.Keys)
            {
                var keyStr = key.AsString();
                if (keyStr.StartsWith(category))
                    filtered[keyStr] = props[key];
            }
            props = filtered;
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["type"] = node.GetClass(),
            ["properties"] = props,
        }));
    }

    private Task<Dictionary> GetEditorSelection(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var selection = GetEditor().GetSelection();
        var includeTopOnly = OptionalBool(@params, "top_only", false);
        var selectedNodes = includeTopOnly ? selection.GetTopSelectedNodes() : selection.GetSelectedNodes();

        return Task.FromResult(Success(new Dictionary
        {
            ["nodes"] = SerializeSelectionNodes(root, selectedNodes),
            ["count"] = selectedNodes.Count,
            ["top_only"] = includeTopOnly,
        }));
    }

    private Task<Dictionary> SelectNodes(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var (nodePaths, pathsErr) = GetSelectionNodePaths(@params);
        if (pathsErr != null) return Task.FromResult(pathsErr);

        var mode = OptionalString(@params, "mode", "replace");
        if (mode != "replace" && mode != "add" && mode != "remove")
            return Task.FromResult(ErrorInvalidParams("mode must be one of: replace, add, remove"));

        var inspect = OptionalBool(@params, "inspect", true);
        var focus = OptionalBool(@params, "focus", inspect);
        var inspectorOnly = OptionalBool(@params, "inspector_only", false);
        var forProperty = OptionalString(@params, "for_property", "");

        var resolvedNodes = new List<Node>();
        foreach (var nodePathVariant in nodePaths)
        {
            var nodePath = nodePathVariant.AsString();
            if (string.IsNullOrEmpty(nodePath))
                return Task.FromResult(ErrorInvalidParams("node_paths cannot contain empty values"));
            var node = FindNodeByPath(nodePath);
            if (node == null)
                return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));
            resolvedNodes.Add(node);
        }

        var selection = GetEditor().GetSelection();
        if (mode == "replace")
            selection.Clear();

        foreach (var node in resolvedNodes)
        {
            if (mode == "remove")
                selection.RemoveNode(node);
            else
                selection.AddNode(node);
        }

        // EditNode() and InspectObject() both reset EditorSelection to a single
        // node, which would collapse a multi-node selection down to the last node.
        // Only focus/inspect when exactly one node was selected.
        if (mode != "remove" && resolvedNodes.Count == 1)
        {
            var editedNode = resolvedNodes[0];
            if (focus)
                GetEditor().EditNode(editedNode);
            if (inspect)
                GetEditor().InspectObject(editedNode, forProperty, inspectorOnly);
        }

        var selectedNodes = selection.GetSelectedNodes();
        return Task.FromResult(Success(new Dictionary
        {
            ["mode"] = mode,
            ["selected"] = SerializeSelectionNodes(root, selectedNodes),
            ["count"] = selectedNodes.Count,
        }));
    }

    private Task<Dictionary> ClearEditorSelection(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var selection = GetEditor().GetSelection();
        var beforeCount = selection.GetSelectedNodes().Count;
        selection.Clear();

        return Task.FromResult(Success(new Dictionary
        {
            ["cleared"] = beforeCount,
            ["selected"] = new Godot.Collections.Array(),
            ["count"] = 0,
        }));
    }

    private Task<Dictionary> AddResource(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var (property, err2) = RequireString(@params, "property");
        if (err2 != null) return Task.FromResult(err2);

        var (resourceType, err3) = RequireString(@params, "resource_type");
        if (err3 != null) return Task.FromResult(err3);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        if (!ClassDB.ClassExists(resourceType))
            return Task.FromResult(ErrorInvalidParams($"Unknown resource type: {resourceType}"));

        if (!ClassDB.IsParentClass(resourceType, "Resource"))
            return Task.FromResult(ErrorInvalidParams($"'{resourceType}' is not a Resource type"));

        var instance = ClassDB.Instantiate(resourceType);
        if (instance.AsGodotObject() is not Resource resource)
            return Task.FromResult(ErrorInternal($"Failed to create resource: {resourceType}"));

        // Apply resource properties if provided
        var resourceProps = OptionalDict(@params, "resource_properties") ?? new Dictionary();
        foreach (var propName in resourceProps.Keys)
        {
            var propNameStr = propName.AsString();
            if (!NodeHasProperty(resource, propNameStr))
                continue;
            var current = resource.Get(propNameStr);
            resource.Set(propNameStr, PropertyParser.ParseValue(resourceProps[propName], current.VariantType));
        }

        var oldValue = NodeHasProperty(node, property) ? node.Get(property) : default;

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Add {resourceType} to {node.Name}");
        undoRedo.AddDoProperty(node, property, resource);
        undoRedo.AddUndoProperty(node, property, oldValue);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["property"] = property,
            ["resource_type"] = resourceType,
        }));
    }

    private Task<Dictionary> SetAnchorPreset(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var (presetName, err2) = RequireString(@params, "preset");
        if (err2 != null) return Task.FromResult(err2);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        if (node is not Control control)
            return Task.FromResult(ErrorInvalidParams($"Node '{nodePath}' is not a Control (is {node.GetClass()})"));

        var presets = new System.Collections.Generic.Dictionary<string, Control.LayoutPreset>
        {
            ["top_left"] = Control.LayoutPreset.TopLeft,
            ["top_right"] = Control.LayoutPreset.TopRight,
            ["bottom_left"] = Control.LayoutPreset.BottomLeft,
            ["bottom_right"] = Control.LayoutPreset.BottomRight,
            ["center_left"] = Control.LayoutPreset.CenterLeft,
            ["center_top"] = Control.LayoutPreset.CenterTop,
            ["center_right"] = Control.LayoutPreset.CenterRight,
            ["center_bottom"] = Control.LayoutPreset.CenterBottom,
            ["center"] = Control.LayoutPreset.Center,
            ["left_wide"] = Control.LayoutPreset.LeftWide,
            ["top_wide"] = Control.LayoutPreset.TopWide,
            ["right_wide"] = Control.LayoutPreset.RightWide,
            ["bottom_wide"] = Control.LayoutPreset.BottomWide,
            ["vcenter_wide"] = Control.LayoutPreset.VcenterWide,
            ["hcenter_wide"] = Control.LayoutPreset.HcenterWide,
            ["full_rect"] = Control.LayoutPreset.FullRect,
        };

        if (!presets.TryGetValue(presetName, out var preset))
            return Task.FromResult(ErrorInvalidParams($"Unknown preset: '{presetName}'. Available: {string.Join(", ", presets.Keys)}"));

        var keepOffsets = OptionalBool(@params, "keep_offsets", false);

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Set anchor preset on {node.Name}");

        // Store old values
        var oldAnchors = new[] { control.AnchorLeft, control.AnchorTop, control.AnchorRight, control.AnchorBottom };
        var oldOffsets = new[] { control.OffsetLeft, control.OffsetTop, control.OffsetRight, control.OffsetBottom };

        var target = (Control)control.Duplicate();
        target.SetAnchorsAndOffsetsPreset(preset,
            keepOffsets ? Control.LayoutPresetMode.KeepSize : Control.LayoutPresetMode.Minsize);

        undoRedo.AddDoProperty(control, "anchor_left", target.AnchorLeft);
        undoRedo.AddDoProperty(control, "anchor_top", target.AnchorTop);
        undoRedo.AddDoProperty(control, "anchor_right", target.AnchorRight);
        undoRedo.AddDoProperty(control, "anchor_bottom", target.AnchorBottom);
        undoRedo.AddDoProperty(control, "offset_left", target.OffsetLeft);
        undoRedo.AddDoProperty(control, "offset_top", target.OffsetTop);
        undoRedo.AddDoProperty(control, "offset_right", target.OffsetRight);
        undoRedo.AddDoProperty(control, "offset_bottom", target.OffsetBottom);

        undoRedo.AddUndoProperty(control, "anchor_left", oldAnchors[0]);
        undoRedo.AddUndoProperty(control, "anchor_top", oldAnchors[1]);
        undoRedo.AddUndoProperty(control, "anchor_right", oldAnchors[2]);
        undoRedo.AddUndoProperty(control, "anchor_bottom", oldAnchors[3]);
        undoRedo.AddUndoProperty(control, "offset_left", oldOffsets[0]);
        undoRedo.AddUndoProperty(control, "offset_top", oldOffsets[1]);
        undoRedo.AddUndoProperty(control, "offset_right", oldOffsets[2]);
        undoRedo.AddUndoProperty(control, "offset_bottom", oldOffsets[3]);

        target.Free();
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(control).ToString(),
            ["preset"] = presetName,
        }));
    }

    private Task<Dictionary> RenameNode(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var (newName, err2) = RequireString(@params, "new_name");
        if (err2 != null) return Task.FromResult(err2);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        var oldName = node.Name.ToString();
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Rename {oldName} to {newName}");
        undoRedo.AddDoProperty(node, "name", newName);
        undoRedo.AddUndoProperty(node, "name", oldName);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["old_name"] = oldName,
            ["new_name"] = node.Name.ToString(),
            ["node_path"] = root.GetPathTo(node).ToString(),
        }));
    }

    private Task<Dictionary> ConnectSignal(Dictionary @params)
    {
        var (sourcePath, err) = RequireString(@params, "source_path");
        if (err != null) return Task.FromResult(err);

        var (signalName, err2) = RequireString(@params, "signal_name");
        if (err2 != null) return Task.FromResult(err2);

        var (targetPath, err3) = RequireString(@params, "target_path");
        if (err3 != null) return Task.FromResult(err3);

        var (methodName, err4) = RequireString(@params, "method_name");
        if (err4 != null) return Task.FromResult(err4);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var source = FindNodeByPath(sourcePath);
        if (source == null)
            return Task.FromResult(ErrorNotFound($"Source node '{sourcePath}'"));

        var target = FindNodeByPath(targetPath);
        if (target == null)
            return Task.FromResult(ErrorNotFound($"Target node '{targetPath}'"));

        if (!source.HasSignal(signalName))
            return Task.FromResult(ErrorInvalidParams($"Signal '{signalName}' not found on {source.GetClass()}"));

        var callable = new Callable(target, methodName);
        if (source.IsConnected(signalName, callable))
            return Task.FromResult(Success(new Dictionary { ["already_connected"] = true, ["signal"] = signalName }));

        // CONNECT_PERSIST is required for PackedScene.pack() to serialize the
        // connection into the .tscn — without it the connection is editor-memory only.
        var flags = (long)GodotObject.ConnectFlags.Persist;
        if (OptionalBool(@params, "deferred", false))
            flags |= (long)GodotObject.ConnectFlags.Deferred;
        if (OptionalBool(@params, "one_shot", false))
            flags |= (long)GodotObject.ConnectFlags.OneShot;

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Connect signal");
        undoRedo.AddDoMethod(source, GodotObject.MethodName.Connect, signalName, callable, flags);
        undoRedo.AddUndoMethod(source, GodotObject.MethodName.Disconnect, signalName, callable);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["source"] = root.GetPathTo(source).ToString(),
            ["signal"] = signalName,
            ["target"] = root.GetPathTo(target).ToString(),
            ["method"] = methodName,
            ["connected"] = true,
            ["flags"] = flags,
            ["persistent"] = true,
        }));
    }

    private Task<Dictionary> DisconnectSignal(Dictionary @params)
    {
        var (sourcePath, err) = RequireString(@params, "source_path");
        if (err != null) return Task.FromResult(err);

        var (signalName, err2) = RequireString(@params, "signal_name");
        if (err2 != null) return Task.FromResult(err2);

        var (targetPath, err3) = RequireString(@params, "target_path");
        if (err3 != null) return Task.FromResult(err3);

        var (methodName, err4) = RequireString(@params, "method_name");
        if (err4 != null) return Task.FromResult(err4);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var source = FindNodeByPath(sourcePath);
        if (source == null)
            return Task.FromResult(ErrorNotFound($"Source node '{sourcePath}'"));

        var target = FindNodeByPath(targetPath);
        if (target == null)
            return Task.FromResult(ErrorNotFound($"Target node '{targetPath}'"));

        var callable = new Callable(target, methodName);
        if (!source.IsConnected(signalName, callable))
            return Task.FromResult(Success(new Dictionary { ["was_connected"] = false }));

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction("MCP: Disconnect signal");
        undoRedo.AddDoMethod(source, GodotObject.MethodName.Disconnect, signalName, callable);
        undoRedo.AddUndoMethod(source, GodotObject.MethodName.Connect, signalName, callable);
        undoRedo.CommitAction();

        return Task.FromResult(Success(new Dictionary
        {
            ["source"] = root.GetPathTo(source).ToString(),
            ["signal"] = signalName,
            ["target"] = root.GetPathTo(target).ToString(),
            ["method"] = methodName,
            ["disconnected"] = true,
        }));
    }

    private Task<Dictionary> GetNodeGroups(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        var groups = new Godot.Collections.Array();
        foreach (var group in node.GetGroups())
        {
            var g = group.ToString();
            if (!g.StartsWith("_"))
                groups.Add(g);
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["groups"] = groups,
            ["count"] = groups.Count,
        }));
    }

    private Task<Dictionary> SetNodeGroups(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return Task.FromResult(err);

        if (!@params.ContainsKey("groups") || @params["groups"].VariantType != Variant.Type.Array)
            return Task.FromResult(ErrorInvalidParams("'groups' array is required"));
        var desiredGroups = @params["groups"].AsGodotArray();

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return Task.FromResult(ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes"));

        // Get current non-internal groups
        var currentGroups = new List<string>();
        foreach (var group in node.GetGroups())
        {
            var g = group.ToString();
            if (!g.StartsWith("_"))
                currentGroups.Add(g);
        }

        var desiredGroupStrings = new List<string>();
        foreach (var g in desiredGroups) desiredGroupStrings.Add(g.AsString());

        var added = new List<string>();
        var removed = new List<string>();

        foreach (var group in currentGroups)
        {
            if (!desiredGroupStrings.Contains(group))
                removed.Add(group);
        }

        foreach (var group in desiredGroupStrings)
        {
            if (!currentGroups.Contains(group))
                added.Add(group);
        }

        if (added.Count > 0 || removed.Count > 0)
        {
            var undoRedo = GetUndoRedo();
            undoRedo.CreateAction("MCP: Set node groups");
            foreach (var group in removed)
            {
                undoRedo.AddDoMethod(node, Node.MethodName.RemoveFromGroup, group);
                undoRedo.AddUndoMethod(node, Node.MethodName.AddToGroup, group, true);
            }
            foreach (var group in added)
            {
                undoRedo.AddDoMethod(node, Node.MethodName.AddToGroup, group, true);
                undoRedo.AddUndoMethod(node, Node.MethodName.RemoveFromGroup, group);
            }
            undoRedo.CommitAction();
        }

        var addedArr = new Godot.Collections.Array();
        foreach (var a in added) addedArr.Add(a);
        var removedArr = new Godot.Collections.Array();
        foreach (var r in removed) removedArr.Add(r);

        return Task.FromResult(Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["groups"] = desiredGroups,
            ["added"] = addedArr,
            ["removed"] = removedArr,
        }));
    }

    private Task<Dictionary> FindNodesInGroup(Dictionary @params)
    {
        var (groupName, err) = RequireString(@params, "group");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var matches = new Godot.Collections.Array();
        FindInGroupRecursive(root, root, groupName, matches);

        return Task.FromResult(Success(new Dictionary
        {
            ["group"] = groupName,
            ["nodes"] = matches,
            ["count"] = matches.Count,
        }));
    }
}
