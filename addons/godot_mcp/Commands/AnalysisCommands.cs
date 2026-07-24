using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Project-wide analysis tools: unused-resource scanning, signal-flow mapping,
/// scene-complexity metrics, script/text reference search, circular scene
/// dependency detection, and aggregate project statistics. Faithful port of
/// addons/godot_mcp/commands/analysis_commands.gd.
/// </summary>
[Tool]
public partial class AnalysisCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "find_unused_resources",
            Description = "Scan the project for resource files not referenced by any .tscn, .gd, .tres, .cfg or " +
                ".godot file. Also seeds references held implicitly by ProjectSettings (main scene, autoloads, " +
                "icon, audio bus layout, etc.) so those are never reported as unused.",
            Category = "analysis",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Root res:// path to scan", "res://"),
                ["include_addons"] = Schema.Bool("Include the res://addons directory in the scan", false),
            }),
            Handler = FindUnusedResources,
        },
        new()
        {
            Name = "analyze_signal_flow",
            Description = "Map all persistent (scene-serialized) signal connections in the currently edited scene, " +
                "grouped per node by signals emitted (with targets) and signals connected to (with sources).",
            Category = "analysis",
            InputSchema = Schema.Object(),
            Handler = AnalyzeSignalFlow,
        },
        new()
        {
            Name = "analyze_scene_complexity",
            Description = "Analyze a scene's complexity: total node count, max nesting depth, node counts by type, " +
                "attached scripts, and heuristic issues (too many nodes / too deep nesting). Defaults to the " +
                "currently edited scene when 'path' is omitted.",
            Category = "analysis",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the scene to analyze (defaults to the currently edited scene)"),
            }),
            Handler = AnalyzeSceneComplexity,
        },
        new()
        {
            Name = "find_script_references",
            Description = "Find all places where a given script path, class_name, or resource path literal is used " +
                "across .tscn, .gd, .tres, .cfg and .godot files, with per-line results.",
            Category = "analysis",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["query"] = Schema.Str("Literal text pattern to search for"),
                ["path"] = Schema.Str("Root res:// path to scan", "res://"),
                ["include_addons"] = Schema.Bool("Include the res://addons directory in the scan", false),
            }, "query"),
            Handler = FindScriptReferences,
        },
        new()
        {
            Name = "detect_circular_dependencies",
            Description = "Detect circular scene dependencies among .tscn files (scenes that [ext_resource]-reference " +
                "each other in a cycle) via DFS over the ext_resource scene reference graph.",
            Category = "analysis",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Root res:// path to scan", "res://"),
                ["include_addons"] = Schema.Bool("Include the res://addons directory in the scan", false),
            }),
            Handler = DetectCircularDependencies,
        },
        new()
        {
            Name = "get_project_statistics",
            Description = "Aggregate project statistics: file counts by extension, total .gd script line count, " +
                "scene/resource counts, registered autoloads, and enabled/available editor plugins under res://addons.",
            Category = "analysis",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Root res:// path to scan", "res://"),
                ["include_addons"] = Schema.Bool("Include the res://addons directory in the scan", false),
            }),
            Handler = GetProjectStatistics,
        },
    };

    // ── find_unused_resources ────────────────────────────────────────────────

    private static readonly string[] ResourceExtensions =
    {
        "tres", "tscn", "png", "jpg", "jpeg", "svg",
        "wav", "ogg", "mp3", "ttf", "otf", "gdshader", "material",
        "theme", "stylebox", "font", "anim",
    };

    private static readonly string[] RefExtensions = { "tscn", "gd", "tres", "cfg", "godot" };

    private Task<Dictionary> FindUnusedResources(Dictionary @params)
    {
        var path = OptionalString(@params, "path", "res://");
        var includeAddons = OptionalBool(@params, "include_addons", false);

        // Step 1: Collect all resource files
        var allResources = new List<string>();
        CollectFilesByExt(path, ResourceExtensions, allResources, includeAddons);

        // Step 2: Collect all referencing files (.tscn, .gd, .tres, .cfg, .godot)
        var refFiles = new List<string>();
        CollectFilesByExt(path, RefExtensions, refFiles, includeAddons);

        // Step 3: Build a set of all referenced paths (both res:// and uid:// forms)
        var referenced = new HashSet<string>();
        foreach (var refFile in refFiles)
        {
            var content = ReadFileText(refFile);
            if (string.IsNullOrEmpty(content))
                continue;
            CollectReferencedPaths(content, refFile, referenced);
        }

        // Step 3b: Seed references held by the engine via ProjectSettings
        // (main scene, audio bus layout, icon, autoloads…) — these may be
        // default values that never appear as literals in project.godot
        foreach (Dictionary setting in ProjectSettings.Singleton.GetPropertyList())
        {
            var settingName = setting["name"].AsString();
            if (!ProjectSettings.HasSetting(settingName))
                continue;
            var value = ProjectSettings.GetSetting(settingName);
            if (value.VariantType != Variant.Type.String)
                continue;
            var s = value.AsString();
            if (s.StartsWith('*')) // autoload paths are prefixed with '*'
                s = s[1..];
            if (s.StartsWith("res://"))
            {
                referenced.Add(s);
            }
            else if (s.StartsWith("uid://"))
            {
                var uid = ResourceUid.TextToId(s);
                if (uid != ResourceUid.InvalidId && ResourceUid.HasId(uid))
                    referenced.Add(ResourceUid.GetIdPath(uid));
            }
        }

        // Step 4: Find unreferenced resources
        var unused = new Godot.Collections.Array();
        foreach (var resPath in allResources)
        {
            if (!referenced.Contains(resPath))
                unused.Add(resPath);
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["unused_resources"] = unused,
            ["unused_count"] = unused.Count,
            ["total_resources_scanned"] = allResources.Count,
            ["total_files_checked"] = refFiles.Count,
        }));
    }

    /// <summary>Extract res:// / uid:// literal path references from file content, skipping self-references
    /// (a .tres/.tscn header carries the file's OWN uid, which must not count as being referenced by someone else).</summary>
    private static void CollectReferencedPaths(string content, string ownFile, HashSet<string> referenced)
    {
        foreach (var prefix in new[] { "res://", "uid://" })
        {
            var idx = 0;
            while (idx < content.Length)
            {
                var found = content.IndexOf(prefix, idx, System.StringComparison.Ordinal);
                if (found == -1)
                    break;
                // Extract the path (up to quote, space, or end of line)
                var end = found + prefix.Length;
                while (end < content.Length)
                {
                    var c = content[end];
                    if (c is '"' or '\'' or ' ' or '\n' or '\r' or ')' or ']' or '}')
                        break;
                    end++;
                }
                var refPath = content.Substring(found, end - found);
                if (refPath.StartsWith("uid://"))
                {
                    var uid = ResourceUid.TextToId(refPath);
                    if (uid != ResourceUid.InvalidId && ResourceUid.HasId(uid))
                    {
                        var uidPath = ResourceUid.GetIdPath(uid);
                        if (uidPath != ownFile)
                            referenced.Add(uidPath);
                    }
                }
                else
                {
                    referenced.Add(refPath);
                }
                idx = end;
            }
        }
    }

    // ── analyze_signal_flow ──────────────────────────────────────────────────

    private Task<Dictionary> AnalyzeSignalFlow(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var nodesData = new Godot.Collections.Array();
        CollectSignalData(root, root, nodesData);

        return Task.FromResult(Success(new Dictionary
        {
            ["scene"] = root.SceneFilePath,
            ["nodes"] = nodesData,
            ["total_nodes"] = nodesData.Count,
        }));
    }

    private static void CollectSignalData(Node node, Node root, Godot.Collections.Array outArr)
    {
        var nodePath = root.GetPathTo(node).ToString();
        var signalsEmitted = new Godot.Collections.Array();
        var signalsConnectedTo = new Godot.Collections.Array();

        // Get all signals this node defines
        foreach (Dictionary sig in node.GetSignalList())
        {
            var sigName = sig["name"].AsString();
            var connections = node.GetSignalConnectionList(sigName);
            var targets = new Godot.Collections.Array();
            foreach (Dictionary conn in connections)
            {
                // Skip editor-internal bookkeeping connections — only persistent
                // (scene-serialized) connections are user-relevant
                var flags = conn.TryGetValue("flags", out var f) ? f.AsInt64() : 0;
                if ((flags & (long)GodotObject.ConnectFlags.Persist) == 0)
                    continue;
                var callable = conn["callable"].AsCallable();
                var targetNode = callable.Target as Node;
                // Skip targets outside the edited scene (editor dock nodes)
                if (targetNode == null || (targetNode != root && !root.IsAncestorOf(targetNode)))
                    continue;
                targets.Add(new Dictionary
                {
                    ["target_node"] = root.GetPathTo(targetNode).ToString(),
                    ["method"] = callable.Method.ToString(),
                });
                // Also record on the target side
                signalsConnectedTo.Add(new Dictionary
                {
                    ["from_node"] = nodePath,
                    ["signal"] = sigName,
                    ["method"] = callable.Method.ToString(),
                });
            }
            if (targets.Count > 0)
            {
                signalsEmitted.Add(new Dictionary { ["signal"] = sigName, ["targets"] = targets });
            }
        }

        // Only include nodes that have signal activity
        if (signalsEmitted.Count > 0 || signalsConnectedTo.Count > 0)
        {
            outArr.Add(new Dictionary
            {
                ["name"] = node.Name.ToString(),
                ["path"] = nodePath,
                ["type"] = node.GetClass(),
                ["signals_emitted"] = signalsEmitted,
                ["signals_connected_to"] = signalsConnectedTo,
            });
        }

        foreach (var child in node.GetChildren())
            CollectSignalData(child, root, outArr);
    }

    // ── analyze_scene_complexity ─────────────────────────────────────────────

    private Task<Dictionary> AnalyzeSceneComplexity(Dictionary @params)
    {
        var scenePath = OptionalString(@params, "path", "");

        Node root;
        if (string.IsNullOrEmpty(scenePath))
        {
            var edited = GetEditedRoot();
            if (edited == null) return Task.FromResult(ErrorNoScene());
            root = edited;
            scenePath = root.SceneFilePath;
        }
        else
        {
            if (!ResourceLoader.Exists(scenePath))
                return Task.FromResult(ErrorNotFound($"Scene '{scenePath}'"));
            var packed = ResourceLoader.Load(scenePath) as PackedScene;
            if (packed == null)
                return Task.FromResult(ErrorInternal($"Failed to load scene: {scenePath}"));
            root = packed.Instantiate();
        }

        var types = new Dictionary(); // class_name -> count
        var scriptsAttached = new Godot.Collections.Array();
        // NOTE: mirrors the .gd exactly — the resources dict is declared but never populated
        // by the recursive walk, so unique_resource_count is always 0. This is intentional
        // parity with the source, not an omission.
        var resourcesUsed = new Dictionary();

        AnalyzeNode(root, root, types, scriptsAttached);

        // Count totals from a fresh recursive walk (mirrors the .gd's redundant second pass)
        var totalNodes = CountNodesRecursive(root);
        var maxDepth = GetMaxDepth(root, 0);

        // Detect potential issues
        var issues = new Godot.Collections.Array();
        if (totalNodes > 1000)
            issues.Add(new Dictionary { ["severity"] = "warning", ["message"] = $"Scene has {totalNodes} nodes (>1000). Consider splitting into sub-scenes." });
        else if (totalNodes > 500)
            issues.Add(new Dictionary { ["severity"] = "info", ["message"] = $"Scene has {totalNodes} nodes (>500). Monitor performance." });

        if (maxDepth > 15)
            issues.Add(new Dictionary { ["severity"] = "warning", ["message"] = $"Max nesting depth is {maxDepth} (>15). Deep hierarchies can be hard to maintain." });
        else if (maxDepth > 10)
            issues.Add(new Dictionary { ["severity"] = "info", ["message"] = $"Max nesting depth is {maxDepth} (>10)." });

        // If we instantiated the scene ourselves, free it
        if (!string.IsNullOrEmpty(scenePath) && root != GetEditedRoot())
            root.QueueFree();

        return Task.FromResult(Success(new Dictionary
        {
            ["scene_path"] = scenePath,
            ["total_nodes"] = totalNodes,
            ["max_depth"] = maxDepth,
            ["nodes_by_type"] = types,
            ["scripts_attached"] = scriptsAttached,
            ["unique_resource_count"] = resourcesUsed.Count,
            ["issues"] = issues,
        }));
    }

    private static void AnalyzeNode(Node node, Node root, Dictionary types, Godot.Collections.Array scripts)
    {
        var typeName = node.GetClass();
        types[typeName] = (types.TryGetValue(typeName, out var c) ? c.AsInt64() : 0) + 1;

        if (node.GetScript().As<Script>() is { } script)
        {
            var scriptPath = script.ResourcePath;
            if (!string.IsNullOrEmpty(scriptPath))
            {
                scripts.Add(new Dictionary
                {
                    ["node"] = root.GetPathTo(node).ToString(),
                    ["script"] = scriptPath,
                });
            }
        }

        foreach (var child in node.GetChildren())
            AnalyzeNode(child, root, types, scripts);
    }

    private static int CountNodesRecursive(Node node)
    {
        var count = 1;
        foreach (var child in node.GetChildren())
            count += CountNodesRecursive(child);
        return count;
    }

    private static int GetMaxDepth(Node node, int currentDepth)
    {
        var maxD = currentDepth;
        foreach (var child in node.GetChildren())
        {
            var childDepth = GetMaxDepth(child, currentDepth + 1);
            if (childDepth > maxD)
                maxD = childDepth;
        }
        return maxD;
    }

    // ── find_script_references ───────────────────────────────────────────────

    private Task<Dictionary> FindScriptReferences(Dictionary @params)
    {
        var (query, err) = RequireString(@params, "query");
        if (err != null) return Task.FromResult(err);

        var path = OptionalString(@params, "path", "res://");
        var includeAddons = OptionalBool(@params, "include_addons", false);

        var searchFiles = new List<string>();
        CollectFilesByExt(path, RefExtensions, searchFiles, includeAddons);

        var references = new Godot.Collections.Array();
        foreach (var filePath in searchFiles)
        {
            var content = ReadFileText(filePath);
            if (string.IsNullOrEmpty(content))
                continue;

            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(query))
                {
                    references.Add(new Dictionary
                    {
                        ["file"] = filePath,
                        ["line"] = i + 1,
                        ["content"] = lines[i].StripEdges(),
                    });
                }
            }
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["query"] = query,
            ["references"] = references,
            ["reference_count"] = references.Count,
            ["files_searched"] = searchFiles.Count,
        }));
    }

    // ── detect_circular_dependencies ─────────────────────────────────────────

    private Task<Dictionary> DetectCircularDependencies(Dictionary @params)
    {
        var path = OptionalString(@params, "path", "res://");
        var includeAddons = OptionalBool(@params, "include_addons", false);

        // Step 1: Collect all .tscn files
        var tscnFiles = new List<string>();
        CollectFilesByExt(path, new[] { "tscn" }, tscnFiles, includeAddons);

        // Step 2: Build dependency graph: scene_path -> [referenced_scene_paths]
        var depGraph = new Dictionary(); // string -> Godot.Collections.Array<string>
        foreach (var tp in tscnFiles)
        {
            var content = ReadFileText(tp);
            if (string.IsNullOrEmpty(content))
                continue;

            var deps = new Godot.Collections.Array();
            foreach (var line in content.Split('\n'))
            {
                // Match [ext_resource ... path="res://..." ...] lines that reference .tscn
                if (line.StartsWith("[ext_resource") && line.Contains(".tscn"))
                {
                    var pathStart = line.IndexOf("path=\"", System.StringComparison.Ordinal);
                    if (pathStart == -1)
                        continue;
                    pathStart += 6; // len('path="')
                    var pathEnd = line.IndexOf('"', pathStart);
                    if (pathEnd == -1)
                        continue;
                    var refPath = line.Substring(pathStart, pathEnd - pathStart);
                    if (refPath.EndsWith(".tscn"))
                        deps.Add(refPath);
                }
            }
            depGraph[tp] = deps;
        }

        // Step 3: Detect cycles using DFS
        var cycles = new Godot.Collections.Array();
        var visited = new System.Collections.Generic.Dictionary<string, string>(); // path -> "unvisited" | "visiting" | "visited"
        foreach (var sceneKey in depGraph.Keys)
            visited[sceneKey.AsString()] = "unvisited";

        foreach (var sceneKey in depGraph.Keys)
        {
            var scene = sceneKey.AsString();
            if (visited[scene] == "unvisited")
            {
                var pathStack = new List<string>();
                DfsDetectCycle(scene, depGraph, visited, pathStack, cycles);
            }
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["scenes_checked"] = tscnFiles.Count,
            ["circular_dependencies"] = cycles,
            ["has_circular"] = cycles.Count > 0,
            ["dependency_graph"] = depGraph,
        }));
    }

    private static void DfsDetectCycle(string node, Dictionary graph, System.Collections.Generic.Dictionary<string, string> visited,
        List<string> pathStack, Godot.Collections.Array cycles)
    {
        visited[node] = "visiting";
        pathStack.Add(node);

        if (graph.TryGetValue(node, out var depsVariant))
        {
            foreach (var depVariant in depsVariant.AsGodotArray())
            {
                var dep = depVariant.AsString();
                if (!visited.TryGetValue(dep, out var depState))
                    // Scene referenced but not in our graph (might not exist or outside scope)
                    continue;
                if (depState == "visiting")
                {
                    // Found a cycle — extract it from the stack
                    var cycleStart = pathStack.IndexOf(dep);
                    var cycle = new Godot.Collections.Array();
                    for (var i = cycleStart; i < pathStack.Count; i++)
                        cycle.Add(pathStack[i]);
                    cycle.Add(dep); // Close the cycle
                    cycles.Add(cycle);
                }
                else if (depState == "unvisited")
                {
                    DfsDetectCycle(dep, graph, visited, pathStack, cycles);
                }
            }
        }

        pathStack.RemoveAt(pathStack.Count - 1);
        visited[node] = "visited";
    }

    // ── get_project_statistics ───────────────────────────────────────────────

    private static readonly string[] StatsResourceExtensions = { "tres", "material", "theme", "stylebox", "font" };

    private Task<Dictionary> GetProjectStatistics(Dictionary @params)
    {
        var path = OptionalString(@params, "path", "res://");
        var includeAddons = OptionalBool(@params, "include_addons", false);

        var fileCounts = new Dictionary(); // extension -> count (plus internal "_..." counters)
        CollectStatistics(path, includeAddons, fileCounts);

        // Extract internal counters and remove them from the visible dict
        var totalScriptLines = fileCounts.TryGetValue("_total_script_lines", out var tsl) ? (int)tsl.AsInt64() : 0;
        var sceneCount = fileCounts.TryGetValue("_scene_count", out var sc) ? (int)sc.AsInt64() : 0;
        var resourceCount = fileCounts.TryGetValue("_resource_count", out var rc) ? (int)rc.AsInt64() : 0;
        var totalFiles = fileCounts.TryGetValue("_total_files", out var tf) ? (int)tf.AsInt64() : 0;
        fileCounts.Remove("_total_script_lines");
        fileCounts.Remove("_scene_count");
        fileCounts.Remove("_resource_count");
        fileCounts.Remove("_total_files");

        // Autoloads
        var autoloads = new Dictionary();
        foreach (Dictionary prop in ProjectSettings.Singleton.GetPropertyList())
        {
            var propName = prop["name"].AsString();
            if (propName.StartsWith("autoload/"))
            {
                var val = ProjectSettings.GetSetting(propName);
                autoloads[propName[9..]] = val.VariantType == Variant.Type.String ? val.AsString() : val.ToString();
            }
        }

        // Enabled plugins
        var plugins = new Godot.Collections.Array();
        var pluginCfgPath = "res://addons";
        var enabledPlugins = ProjectSettings.GetSetting("editor_plugins/enabled", System.Array.Empty<string>()).AsStringArray();

        var pluginDir = DirAccess.Open(pluginCfgPath);
        if (pluginDir != null)
        {
            pluginDir.ListDirBegin();
            var dirName = pluginDir.GetNext();
            while (!string.IsNullOrEmpty(dirName))
            {
                if (pluginDir.CurrentIsDir() && !dirName.StartsWith('.'))
                {
                    var cfgPath = pluginCfgPath.PathJoin(dirName).PathJoin("plugin.cfg");
                    if (Godot.FileAccess.FileExists(cfgPath))
                    {
                        var pluginPath = $"res://addons/{dirName}/plugin.cfg";
                        plugins.Add(new Dictionary
                        {
                            ["name"] = dirName,
                            ["enabled"] = System.Array.IndexOf(enabledPlugins, pluginPath) >= 0,
                        });
                    }
                }
                dirName = pluginDir.GetNext();
            }
            pluginDir.ListDirEnd();
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["file_counts_by_extension"] = fileCounts,
            ["total_files"] = totalFiles,
            ["total_script_lines"] = totalScriptLines,
            ["scene_count"] = sceneCount,
            ["resource_count"] = resourceCount,
            ["autoloads"] = autoloads,
            ["plugins"] = plugins,
        }));
    }

    private static void CollectStatistics(string path, bool includeAddons, Dictionary fileCounts)
    {
        var dir = DirAccess.Open(path);
        if (dir == null)
            return;

        dir.ListDirBegin();
        var fileName = dir.GetNext();

        while (!string.IsNullOrEmpty(fileName))
        {
            if (fileName.StartsWith('.'))
            {
                fileName = dir.GetNext();
                continue;
            }

            var fullPath = path.PathJoin(fileName);

            if (dir.CurrentIsDir())
            {
                if (fileName == "addons" && !includeAddons)
                {
                    fileName = dir.GetNext();
                    continue;
                }
                CollectStatistics(fullPath, includeAddons, fileCounts);
            }
            else
            {
                var ext = fileName.GetExtension().ToLowerInvariant();
                fileCounts[ext] = (fileCounts.TryGetValue(ext, out var v) ? v.AsInt64() : 0) + 1;

                if (ext == "gd")
                {
                    var content = ReadFileText(fullPath);
                    var lineCount = string.IsNullOrEmpty(content) ? 0 : CountNewlines(content) + 1;
                    fileCounts["_total_script_lines"] = (fileCounts.TryGetValue("_total_script_lines", out var tsl) ? tsl.AsInt64() : 0) + lineCount;
                }

                if (ext == "tscn")
                    fileCounts["_scene_count"] = (fileCounts.TryGetValue("_scene_count", out var sc) ? sc.AsInt64() : 0) + 1;

                if (System.Array.IndexOf(StatsResourceExtensions, ext) >= 0)
                    fileCounts["_resource_count"] = (fileCounts.TryGetValue("_resource_count", out var rc) ? rc.AsInt64() : 0) + 1;

                fileCounts["_total_files"] = (fileCounts.TryGetValue("_total_files", out var tf) ? tf.AsInt64() : 0) + 1;
            }

            fileName = dir.GetNext();
        }

        dir.ListDirEnd();
    }

    private static int CountNewlines(string content)
    {
        var count = 0;
        foreach (var c in content)
            if (c == '\n') count++;
        return count;
    }

    // ── shared helpers ────────────────────────────────────────────────────────

    /// <summary>Recursively collect files matching given extensions (case-insensitive).</summary>
    private static void CollectFilesByExt(string path, string[] extensions, List<string> outFiles, bool includeAddons)
    {
        var dir = DirAccess.Open(path);
        if (dir == null)
            return;

        dir.ListDirBegin();
        var fileName = dir.GetNext();

        while (!string.IsNullOrEmpty(fileName))
        {
            if (fileName.StartsWith('.'))
            {
                fileName = dir.GetNext();
                continue;
            }

            var fullPath = path.PathJoin(fileName);

            if (dir.CurrentIsDir())
            {
                if (fileName == "addons" && !includeAddons)
                {
                    fileName = dir.GetNext();
                    continue;
                }
                CollectFilesByExt(fullPath, extensions, outFiles, includeAddons);
            }
            else
            {
                var ext = fileName.GetExtension().ToLowerInvariant();
                if (System.Array.IndexOf(extensions, ext) >= 0)
                    outFiles.Add(fullPath);
            }

            fileName = dir.GetNext();
        }

        dir.ListDirEnd();
    }

    /// <summary>Read a file's text content. Returns empty string on failure.</summary>
    private static string ReadFileText(string filePath)
    {
        using var file = Godot.FileAccess.Open(filePath, Godot.FileAccess.ModeFlags.Read);
        return file?.GetAsText() ?? "";
    }
}
