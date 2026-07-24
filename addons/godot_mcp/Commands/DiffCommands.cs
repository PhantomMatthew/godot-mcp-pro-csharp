using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// Offline scene/resource comparison tools.
/// diff_scenes instantiates two PackedScenes side-by-side and walks both trees,
/// comparing node names, classes, transform/visibility properties and script
/// @export variables. diff_resources compares the editor-visible property sets
/// of two loaded resources. Both are read-only: instantiated scenes are freed
/// again and nothing is written to disk.
/// </summary>
[Tool]
public partial class DiffCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "diff_scenes",
            Description = "Compare two scene files (res:// paths). Both are loaded as PackedScene, " +
                "instantiated, and their node trees walked side-by-side, comparing node names, " +
                "classes, transform/visibility properties and script @export variables. " +
                "Returns same, a differences list, and nodes only present in one scene.",
            Category = "diff",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["scene_a"] = Schema.Str("res:// path of the first scene to compare."),
                ["scene_b"] = Schema.Str("res:// path of the second scene to compare."),
            }, "scene_a", "scene_b"),
            Handler = p => Task.FromResult(DiffScenes(p)),
        },
        new()
        {
            Name = "diff_resources",
            Description = "Compare two resource files (.tres/.res). Both are loaded and their " +
                "editor-visible properties compared. Returns same and a differences list with " +
                "each differing property's value from resource A and B.",
            Category = "diff",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["resource_a"] = Schema.Str("res:// path of the first resource to compare."),
                ["resource_b"] = Schema.Str("res:// path of the second resource to compare."),
            }, "resource_a", "resource_b"),
            Handler = p => Task.FromResult(DiffResources(p)),
        },
    };

    // ── shared helpers ───────────────────────────────────────────────────────

    private const string MissingMarker = "<missing>";

    /// <summary>Serialize a Variant for comparison output (JSON-safe).</summary>
    private static Variant Serialize(Variant value) => PropertyParser.SerializeValue(value);

    /// <summary>Stable string form of a serialized Variant, used for equality checks.</summary>
    private static string StableString(Variant value) => Json.Stringify(Serialize(value));

    private static HashSet<string> PropertyNames(GodotObject obj)
    {
        var names = new HashSet<string>();
        foreach (var propInfo in obj.GetPropertyList())
            names.Add(propInfo["name"].AsString());
        return names;
    }

    // ── diff_scenes ──────────────────────────────────────────────────────────

    private static Dictionary DiffScenes(Dictionary @params)
    {
        var (sceneA, err1) = RequireString(@params, "scene_a");
        if (err1 != null)
            return err1;
        var (sceneB, err2) = RequireString(@params, "scene_b");
        if (err2 != null)
            return err2;

        if (!ResourceLoader.Exists(sceneA))
            return ErrorNotFound($"Scene '{sceneA}'");
        if (!ResourceLoader.Exists(sceneB))
            return ErrorNotFound($"Scene '{sceneB}'");

        if (ResourceLoader.Load(sceneA) is not PackedScene packedA)
            return ErrorInternal($"Failed to load scene as PackedScene: {sceneA}");
        if (ResourceLoader.Load(sceneB) is not PackedScene packedB)
            return ErrorInternal($"Failed to load scene as PackedScene: {sceneB}");

        var rootA = packedA.Instantiate();
        var rootB = packedB.Instantiate();
        if (rootA == null)
            return ErrorInternal($"Failed to instantiate scene: {sceneA}");
        if (rootB == null)
        {
            rootA.QueueFree();
            return ErrorInternal($"Failed to instantiate scene: {sceneB}");
        }

        try
        {
            var nodesA = new System.Collections.Generic.Dictionary<string, Node>();
            var nodesB = new System.Collections.Generic.Dictionary<string, Node>();
            CollectNodes(rootA, "/", nodesA);
            CollectNodes(rootB, "/", nodesB);

            var differences = new Godot.Collections.Array();
            var onlyInA = new Godot.Collections.Array();
            var onlyInB = new Godot.Collections.Array();

            foreach (var (path, nodeA) in nodesA)
            {
                if (!nodesB.ContainsKey(path))
                    onlyInA.Add(path);
                else
                    CompareNodePair(path, nodeA, nodesB[path], differences);
            }
            foreach (var path in nodesB.Keys)
            {
                if (!nodesA.ContainsKey(path))
                    onlyInB.Add(path);
            }

            var same = differences.Count == 0 && onlyInA.Count == 0 && onlyInB.Count == 0;
            return Success(new Dictionary
            {
                ["same"] = same,
                ["scene_a"] = sceneA,
                ["scene_b"] = sceneB,
                ["differences"] = differences,
                ["nodes_only_in_a"] = onlyInA,
                ["nodes_only_in_b"] = onlyInB,
            });
        }
        finally
        {
            rootA.QueueFree();
            rootB.QueueFree();
        }
    }

    /// <summary>Build a path → node map for the whole subtree. Root is "/", children "/Name/...".</summary>
    private static void CollectNodes(Node node, string path, System.Collections.Generic.Dictionary<string, Node> map)
    {
        map[path] = node;
        foreach (var child in node.GetChildren())
        {
            var childPath = path == "/"
                ? "/" + child.Name.ToString()
                : path + "/" + child.Name.ToString();
            CollectNodes(child, childPath, map);
        }
    }

    /// <summary>Type-based properties compared for every node pair, by node class.</summary>
    private static string[] StandardPropsFor(Node node)
    {
        if (node is Node3D)
            return new[] { "position", "rotation_degrees", "scale", "visible" };
        if (node is Control)
            return new[] { "position", "rotation_degrees", "scale", "size", "visible" };
        if (node is Node2D)
            return new[] { "position", "rotation_degrees", "scale", "visible" };
        if (node is CanvasItem)
            return new[] { "visible" };
        return System.Array.Empty<string>();
    }

    /// <summary>Script @export variable names declared on a node (empty when no script).</summary>
    private static List<string> ScriptVarNames(Node node)
    {
        var names = new List<string>();
        foreach (var propInfo in node.GetPropertyList())
        {
            var usage = propInfo["usage"].AsInt32();
            if ((usage & (int)PropertyUsageFlags.ScriptVariable) == 0)
                continue;
            var name = propInfo["name"].AsString();
            if (!name.StartsWith('_'))
                names.Add(name);
        }
        return names;
    }

    private static void CompareNodePair(string path, Node a, Node b, Godot.Collections.Array differences)
    {
        // Root names are not encoded in the path, so compare them explicitly.
        if (path == "/" && a.Name != b.Name)
        {
            differences.Add(new Dictionary
            {
                ["path"] = path,
                ["property"] = "name",
                ["scene_a_value"] = a.Name.ToString(),
                ["scene_b_value"] = b.Name.ToString(),
            });
        }

        var classA = a.GetClass();
        var classB = b.GetClass();
        if (classA != classB)
        {
            differences.Add(new Dictionary
            {
                ["path"] = path,
                ["property"] = "class",
                ["scene_a_value"] = classA,
                ["scene_b_value"] = classB,
            });
            // Different node types: comparing individual properties is noise.
            return;
        }

        // Attached script (by resource path).
        var scriptA = a.GetScript().As<Script>()?.ResourcePath ?? "";
        var scriptB = b.GetScript().As<Script>()?.ResourcePath ?? "";
        if (scriptA != scriptB)
        {
            differences.Add(new Dictionary
            {
                ["path"] = path,
                ["property"] = "script",
                ["scene_a_value"] = scriptA,
                ["scene_b_value"] = scriptB,
            });
        }

        var propsA = PropertyNames(a);
        var propsB = PropertyNames(b);

        // Union of standard type props and script @export vars from both sides.
        var candidates = new List<string>();
        var seen = new HashSet<string>();
        foreach (var p in StandardPropsFor(a))
            if (seen.Add(p)) candidates.Add(p);
        foreach (var p in ScriptVarNames(a))
            if (seen.Add(p)) candidates.Add(p);
        foreach (var p in ScriptVarNames(b))
            if (seen.Add(p)) candidates.Add(p);

        foreach (var prop in candidates)
        {
            var hasA = propsA.Contains(prop);
            var hasB = propsB.Contains(prop);
            if (hasA && hasB)
            {
                var valueA = a.Get(prop);
                var valueB = b.Get(prop);
                if (StableString(valueA) == StableString(valueB))
                    continue;
                differences.Add(new Dictionary
                {
                    ["path"] = path,
                    ["property"] = prop,
                    ["scene_a_value"] = Serialize(valueA),
                    ["scene_b_value"] = Serialize(valueB),
                });
            }
            else
            {
                differences.Add(new Dictionary
                {
                    ["path"] = path,
                    ["property"] = prop,
                    ["scene_a_value"] = hasA ? Serialize(a.Get(prop)) : MissingMarker,
                    ["scene_b_value"] = hasB ? Serialize(b.Get(prop)) : MissingMarker,
                });
            }
        }
    }

    // ── diff_resources ───────────────────────────────────────────────────────

    private static Dictionary DiffResources(Dictionary @params)
    {
        var (resA, err1) = RequireString(@params, "resource_a");
        if (err1 != null)
            return err1;
        var (resB, err2) = RequireString(@params, "resource_b");
        if (err2 != null)
            return err2;

        if (!Godot.FileAccess.FileExists(resA))
            return ErrorNotFound($"Resource '{resA}'");
        if (!Godot.FileAccess.FileExists(resB))
            return ErrorNotFound($"Resource '{resB}'");

        var resourceA = ResourceLoader.Load(resA);
        if (resourceA == null)
            return ErrorInternal($"Failed to load resource: {resA}");
        var resourceB = ResourceLoader.Load(resB);
        if (resourceB == null)
            return ErrorInternal($"Failed to load resource: {resB}");

        var differences = new Godot.Collections.Array();

        var classA = resourceA.GetClass();
        var classB = resourceB.GetClass();
        if (classA != classB)
        {
            differences.Add(new Dictionary
            {
                ["property"] = "class",
                ["a_value"] = classA,
                ["b_value"] = classB,
            });
        }

        var propsA = DiffableResourceProps(resourceA);
        var propsB = DiffableResourceProps(resourceB);

        var allProps = new List<string>();
        var seen = new HashSet<string>();
        foreach (var p in propsA)
            if (seen.Add(p)) allProps.Add(p);
        foreach (var p in propsB)
            if (seen.Add(p)) allProps.Add(p);

        var namesA = PropertyNames(resourceA);
        var namesB = PropertyNames(resourceB);

        foreach (var prop in allProps)
        {
            var hasA = namesA.Contains(prop);
            var hasB = namesB.Contains(prop);
            if (hasA && hasB)
            {
                var valueA = resourceA.Get(prop);
                var valueB = resourceB.Get(prop);
                if (StableString(valueA) == StableString(valueB))
                    continue;
                differences.Add(new Dictionary
                {
                    ["property"] = prop,
                    ["a_value"] = Serialize(valueA),
                    ["b_value"] = Serialize(valueB),
                });
            }
            else
            {
                differences.Add(new Dictionary
                {
                    ["property"] = prop,
                    ["a_value"] = hasA ? Serialize(resourceA.Get(prop)) : MissingMarker,
                    ["b_value"] = hasB ? Serialize(resourceB.Get(prop)) : MissingMarker,
                });
            }
        }

        return Success(new Dictionary
        {
            ["same"] = differences.Count == 0,
            ["resource_a"] = resA,
            ["resource_b"] = resB,
            ["type_a"] = classA,
            ["type_b"] = classB,
            ["differences"] = differences,
        });
    }

    /// <summary>Editor-visible, diff-worthy property names of a resource (same filter as read_resource).</summary>
    private static List<string> DiffableResourceProps(Resource resource)
    {
        var props = new List<string>();
        foreach (var propInfo in resource.GetPropertyList())
        {
            var propName = propInfo["name"].AsString();
            var usage = propInfo["usage"].AsInt32();
            if ((usage & (int)PropertyUsageFlags.Editor) == 0)
                continue;
            if (propName.StartsWith('_') || propName == "script" || propName == "resource_local_to_scene"
                || propName == "resource_name" || propName == "resource_path")
                continue;
            props.Add(propName);
        }
        return props;
    }
}
