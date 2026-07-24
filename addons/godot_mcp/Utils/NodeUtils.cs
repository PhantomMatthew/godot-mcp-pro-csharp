using Godot;
using Godot.Collections;

namespace GodotMcpPro.Utils;

/// <summary>
/// Node/scene-tree helpers. Faithful port of addons/godot_mcp/utils/node_utils.gd.
/// </summary>
public static class NodeUtils
{
    /// <summary>Recursively set owner for all children (needed when adding nodes via code).</summary>
    public static void SetOwnerRecursive(Node node, Node owner)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = owner;
            SetOwnerRecursive(child, owner);
        }
    }

    /// <summary>
    /// Get a simplified tree structure from a node.
    /// Paths are scene-relative (usable as node_path input for other tools) —
    /// node.GetPath() would leak the editor's internal dock hierarchy.
    /// </summary>
    public static Dictionary GetNodeTree(Node node, Node? root = null, int maxDepth = -1, int currentDepth = 0)
    {
        root ??= node;
        var result = new Dictionary
        {
            ["name"] = node.Name.ToString(),
            ["type"] = node.GetClass(),
            ["path"] = node == root ? "." : root.GetPathTo(node).ToString(),
        };

        if (node.GetScript().As<Script>() is { } script && !string.IsNullOrEmpty(script.ResourcePath))
            result["script"] = script.ResourcePath;

        if (maxDepth == -1 || currentDepth < maxDepth)
        {
            var children = new Godot.Collections.Array();
            foreach (var child in node.GetChildren())
                children.Add(GetNodeTree(child, root, maxDepth, currentDepth + 1));
            if (children.Count > 0)
                result["children"] = children;
        }

        return result;
    }

    /// <summary>Get all user-facing (editor-visible) properties of a node as a serializable dictionary.</summary>
    public static Dictionary GetNodePropertiesDict(Node node)
    {
        var result = new Dictionary();
        foreach (var propInfo in node.GetPropertyList())
        {
            var propName = propInfo["name"].AsString();
            var usage = propInfo["usage"].AsInt64();

            // Only include user-facing properties (PROPERTY_USAGE_EDITOR)
            if ((usage & (long)PropertyUsageFlags.Editor) == 0)
                continue;

            // Skip internal/meta properties
            if (propName.StartsWith("_") || propName == "script")
                continue;

            var value = node.Get(propName);
            result[propName] = PropertyParser.SerializeValue(value);
        }
        return result;
    }

    /// <summary>Duplicate a node and all its children, properly setting owners.</summary>
    public static Node DuplicateNodeInScene(Node node, string newName, Node root)
    {
        var dup = node.Duplicate();
        dup.Name = newName;
        node.GetParent().AddChild(dup);
        dup.Owner = root;
        SetOwnerRecursive(dup, root);
        return dup;
    }

    /// <summary>Find nodes by class type in subtree (matches exact class or inherited via IsClass).</summary>
    public static List<Node> FindNodesByType(Node root, string typeName)
    {
        var result = new List<Node>();
        if (root.GetClass() == typeName || root.IsClass(typeName))
            result.Add(root);
        foreach (var child in root.GetChildren())
            result.AddRange(FindNodesByType(child, typeName));
        return result;
    }
}
