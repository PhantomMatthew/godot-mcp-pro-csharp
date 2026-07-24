using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// Batch and cross-scene mutation tools: type-based bulk queries/edits within
/// the edited scene, plus project-wide (potentially cross-scene) property
/// writes. Faithful port of addons/godot_mcp/commands/batch_commands.gd.
///
/// cross_scene_set_property carries the v1.14.0 safety model:
///   - dry_run defaults to !force unless explicitly passed.
///   - Real writes to disk require dry_run=false AND force=true.
///   - Scenes currently open in the editor are never written offline: they
///     are reported in skipped_open_scenes[] UNLESS the scene is the active
///     editor scene AND force=true AND dry_run=false, in which case the
///     change is applied live via EditorUndoRedoManager (mode "live_open_scene")
///     instead of an offline resource save.
///   - Closed-scene writes go through GuardOfflineSceneSave (IsScenePathOpen)
///     before ResourceSaver.Save, refusing to clobber a scene that got
///     reopened between the scan and the write.
/// </summary>
[Tool]
public partial class BatchCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "find_nodes_by_type",
            Description = "Find all nodes of a given type (class name) in the edited scene, recursively by default.",
            Category = "batch",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["type"] = Schema.Str("Node class name to search for (e.g. 'Sprite2D')"),
                ["recursive"] = Schema.Bool("Search all descendants, not just direct children", true),
            }, "type"),
            Handler = FindNodesByType,
        },
        new()
        {
            Name = "find_signal_connections",
            Description = "List signal connections in the edited scene, optionally filtered by signal name and/or source node path (substring match on both).",
            Category = "batch",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["signal_name"] = Schema.Str("Only include connections whose signal name contains this substring (optional)"),
                ["node_path"] = Schema.Str("Only include connections whose source node path contains this substring (optional)"),
            }),
            Handler = FindSignalConnections,
        },
        new()
        {
            Name = "batch_set_property",
            Description = "Set a property on every node of a given type in the edited scene, in a single undoable action. " +
                "String values are smart-parsed via GDScript expression syntax (numbers, booleans, Vector2/Vector3/Color literals, etc.) " +
                "before being applied.",
            Category = "batch",
            Destructive = true,
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["type"] = Schema.Str("Node class name to match"),
                ["property"] = Schema.Str("Property name to set"),
                ["value"] = Schema.Any("New value for the property"),
            }, "type", "property", "value"),
            Handler = BatchSetProperty,
        },
        new()
        {
            Name = "batch_add_nodes",
            Description = "Add multiple nodes to the edited scene in a single call. Each entry is processed " +
                "independently: failures for one entry (missing/invalid 'type', unknown class, missing parent) are " +
                "accumulated in 'errors' without aborting the rest of the batch.",
            Category = "batch",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["nodes"] = Schema.Arr(
                    "Array of node specs: {type (required), parent_path (default '.'), name, properties}",
                    Schema.Obj("Node spec")),
            }, "nodes"),
            Handler = BatchAddNodes,
        },
        new()
        {
            Name = "find_node_references",
            Description = "Search .tscn, .gd, .tres and .gdshader files under res:// for a literal text pattern " +
                "(e.g. a node name, resource path, or identifier). Returns up to 100 matching files with line numbers " +
                "(up to 5 line numbers per file).",
            Category = "batch",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["pattern"] = Schema.Str("Literal text pattern to search for"),
            }, "pattern"),
            Handler = FindNodeReferences,
        },
        new()
        {
            Name = "get_scene_dependencies",
            Description = "List the resource dependencies of a scene or resource file, as reported by ResourceLoader.",
            Category = "batch",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the scene or resource file"),
            }, "path"),
            Handler = GetSceneDependencies,
        },
        new()
        {
            Name = "cross_scene_set_property",
            Description = "Set a property on every node of a given type across ALL .tscn scenes under path_filter " +
                "(not just the currently edited scene). SAFETY MODEL (v1.14.0): dry_run defaults to the opposite of " +
                "'force' when not explicitly provided (dry_run = !force), so a bare call previews changes without " +
                "writing anything. Real writes to disk require BOTH dry_run=false AND force=true — omitting dry_run " +
                "while force=false still defaults dry_run to true and the call is a no-op preview; passing " +
                "dry_run=false without force=true is REJECTED with an invalid_params error. Scenes currently open in " +
                "the editor are never written offline: they are reported in skipped_open_scenes[] with a reason, " +
                "UNLESS the scene is the active editor scene AND force=true AND dry_run=false, in which case the " +
                "change is applied live via the undo/redo manager (mode 'live_open_scene') instead of an offline " +
                "resource save. Closed scenes with matching nodes are only saved via ResourceSaver when dry_run=false " +
                "(mode 'offline_saved'); otherwise they are reported with mode 'dry_run' and never touched on disk.",
            Category = "batch",
            Destructive = true,
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["type"] = Schema.Str("Node class name to match"),
                ["property"] = Schema.Str("Property name to set"),
                ["value"] = Schema.Any("New value for the property"),
                ["path_filter"] = Schema.Str("Root res:// path to scan for .tscn scenes", "res://"),
                ["exclude_addons"] = Schema.Bool("Skip the res://addons directory while scanning", true),
                ["force"] = Schema.Bool("Required (together with dry_run=false) to actually write changes to disk or the active open scene", false),
                ["dry_run"] = Schema.Bool("Preview changes without writing. Defaults to !force when omitted."),
            }, "type", "property", "value"),
            Handler = CrossSceneSetProperty,
        },
    };

    // ── find_nodes_by_type ───────────────────────────────────────────────────

    private Task<Dictionary> FindNodesByType(Dictionary @params)
    {
        var (typeName, err) = RequireString(@params, "type");
        if (err != null) return Task.FromResult(err);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var recursive = OptionalBool(@params, "recursive", true);
        var matches = new Godot.Collections.Array();
        SearchByType(root, root, typeName, recursive, matches);

        return Task.FromResult(Success(new Dictionary
        {
            ["type"] = typeName,
            ["matches"] = matches,
            ["count"] = matches.Count,
        }));
    }

    private static void SearchByType(Node node, Node root, string typeName, bool recursive, Godot.Collections.Array matches)
    {
        if (node.IsClass(typeName) || node.GetClass() == typeName)
        {
            matches.Add(new Dictionary
            {
                ["name"] = node.Name.ToString(),
                ["path"] = root.GetPathTo(node).ToString(),
                ["type"] = node.GetClass(),
            });
        }
        if (recursive)
        {
            foreach (var child in node.GetChildren())
                SearchByType(child, root, typeName, recursive, matches);
        }
    }

    // ── find_signal_connections ──────────────────────────────────────────────

    private Task<Dictionary> FindSignalConnections(Dictionary @params)
    {
        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var signalFilter = OptionalString(@params, "signal_name", "");
        var nodeFilter = OptionalString(@params, "node_path", "");

        var connections = new Godot.Collections.Array();
        CollectSignals(root, root, signalFilter, nodeFilter, connections);

        return Task.FromResult(Success(new Dictionary
        {
            ["connections"] = connections,
            ["count"] = connections.Count,
        }));
    }

    private static void CollectSignals(Node node, Node root, string signalFilter, string nodeFilter, Godot.Collections.Array connections)
    {
        var nodePath = root.GetPathTo(node).ToString();

        if (string.IsNullOrEmpty(nodeFilter) || nodePath.Contains(nodeFilter))
        {
            foreach (Dictionary sigInfo in node.GetSignalList())
            {
                var sigName = sigInfo["name"].AsString();
                if (!string.IsNullOrEmpty(signalFilter) && !sigName.Contains(signalFilter))
                    continue;

                foreach (Dictionary conn in node.GetSignalConnectionList(sigName))
                {
                    var callable = conn["callable"].AsCallable();
                    var targetStr = callable.Target is Node targetNode
                        ? root.GetPathTo(targetNode).ToString()
                        : callable.Target?.ToString() ?? "";
                    connections.Add(new Dictionary
                    {
                        ["source"] = nodePath,
                        ["signal"] = sigName,
                        ["target"] = targetStr,
                        ["method"] = callable.Method.ToString(),
                    });
                }
            }
        }

        foreach (var child in node.GetChildren())
            CollectSignals(child, root, signalFilter, nodeFilter, connections);
    }

    // ── batch_set_property ───────────────────────────────────────────────────

    private Task<Dictionary> BatchSetProperty(Dictionary @params)
    {
        var (typeName, err) = RequireString(@params, "type");
        if (err != null) return Task.FromResult(err);

        var (property, err2) = RequireString(@params, "property");
        if (err2 != null) return Task.FromResult(err2);

        if (!@params.ContainsKey("value"))
            return Task.FromResult(ErrorInvalidParams("Missing required parameter: value"));
        var value = ParseExpressionValue(@params["value"]);

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var affected = new Godot.Collections.Array();
        var changes = new List<(Node Node, Variant OldValue)>();
        BatchCollectPropertyChanges(root, root, typeName, property, affected, changes);

        if (changes.Count > 0)
            ApplyPropertyChangesWithUndo(changes, property, value, $"MCP: Batch set {property}");

        return Task.FromResult(Success(new Dictionary
        {
            ["property"] = property,
            ["affected"] = affected,
            ["count"] = affected.Count,
        }));
    }

    private static void BatchCollectPropertyChanges(Node node, Node root, string typeName, string property,
        Godot.Collections.Array affected, List<(Node Node, Variant OldValue)> changes)
    {
        if (node.IsClass(typeName) || node.GetClass() == typeName)
        {
            if (NodeHasProperty(node, property))
            {
                affected.Add(root.GetPathTo(node).ToString());
                changes.Add((node, node.Get(property)));
            }
        }
        foreach (var child in node.GetChildren())
            BatchCollectPropertyChanges(child, root, typeName, property, affected, changes);
    }

    /// <summary>Shared by batch_set_property and cross_scene_set_property (mirrors _apply_property_changes_with_undo).</summary>
    private void ApplyPropertyChangesWithUndo(List<(Node Node, Variant OldValue)> changes, string property,
        Variant newValue, string actionName)
    {
        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction(actionName);
        foreach (var (node, oldValue) in changes)
        {
            undoRedo.AddDoProperty(node, property, newValue);
            undoRedo.AddUndoProperty(node, property, oldValue);
        }
        undoRedo.CommitAction();
    }

    // ── batch_add_nodes ──────────────────────────────────────────────────────

    private Task<Dictionary> BatchAddNodes(Dictionary @params)
    {
        if (!@params.TryGetValue("nodes", out var nodesVariant) || nodesVariant.VariantType != Variant.Type.Array)
            return Task.FromResult(ErrorInvalidParams("Missing required parameter: nodes (Array)"));

        var nodesData = nodesVariant.AsGodotArray();
        if (nodesData.Count == 0)
            return Task.FromResult(ErrorInvalidParams("nodes array is empty"));

        var root = GetEditedRoot();
        if (root == null) return Task.FromResult(ErrorNoScene());

        var created = new Godot.Collections.Array();
        var errors = new Godot.Collections.Array();

        for (var i = 0; i < nodesData.Count; i++)
        {
            if (nodesData[i].VariantType != Variant.Type.Dictionary)
            {
                errors.Add(new Dictionary { ["index"] = i, ["error"] = "Missing or invalid 'type'" });
                continue;
            }
            var entry = nodesData[i].AsGodotDictionary();

            if (!entry.TryGetValue("type", out var typeVariant) || typeVariant.VariantType != Variant.Type.String)
            {
                errors.Add(new Dictionary { ["index"] = i, ["error"] = "Missing or invalid 'type'" });
                continue;
            }

            var type = typeVariant.AsString();
            if (!ClassDB.ClassExists(type))
            {
                errors.Add(new Dictionary { ["index"] = i, ["error"] = $"Unknown node type: {type}" });
                continue;
            }

            var parentPath = entry.TryGetValue("parent_path", out var pp) && pp.VariantType == Variant.Type.String
                ? pp.AsString() : ".";
            var nodeName = entry.TryGetValue("name", out var nm) && nm.VariantType == Variant.Type.String
                ? nm.AsString() : "";
            var properties = entry.TryGetValue("properties", out var props) && props.VariantType == Variant.Type.Dictionary
                ? props.AsGodotDictionary() : new Dictionary();

            var parent = FindNodeByPath(parentPath);
            if (parent == null)
            {
                errors.Add(new Dictionary { ["index"] = i, ["error"] = $"Parent node '{parentPath}' not found" });
                continue;
            }

            var instance = ClassDB.Instantiate(type);
            if (instance.AsGodotObject() is not Node node)
            {
                errors.Add(new Dictionary { ["index"] = i, ["error"] = $"'{type}' does not derive from Node" });
                continue;
            }

            if (!string.IsNullOrEmpty(nodeName))
                node.Name = nodeName;

            foreach (var propNameVariant in properties.Keys)
            {
                var propName = propNameVariant.AsString();
                if (!NodeHasProperty(node, propName))
                    continue;
                var current = node.Get(propName);
                node.Set(propName, PropertyParser.ParseValue(properties[propNameVariant], current.VariantType));
            }

            AddChildWithUndo(parent, node, root, $"MCP: Batch add {type}");

            created.Add(new Dictionary
            {
                ["index"] = i,
                ["type"] = type,
                ["name"] = node.Name.ToString(),
                ["parent"] = parentPath,
                ["node_path"] = root.GetPathTo(node).ToString(),
            });
        }

        var result = new Dictionary { ["created"] = created, ["count"] = created.Count };
        if (errors.Count > 0)
            result["errors"] = errors;

        return Task.FromResult(Success(result));
    }

    // ── find_node_references ─────────────────────────────────────────────────

    private static readonly string[] ReferenceSearchExtensions = { "tscn", "gd", "tres", "gdshader" };

    private Task<Dictionary> FindNodeReferences(Dictionary @params)
    {
        var (pattern, err) = RequireString(@params, "pattern");
        if (err != null) return Task.FromResult(err);

        var matches = new Godot.Collections.Array();
        SearchFilesForPattern("res://", pattern, matches, 100);

        return Task.FromResult(Success(new Dictionary
        {
            ["pattern"] = pattern,
            ["matches"] = matches,
            ["count"] = matches.Count,
        }));
    }

    private static void SearchFilesForPattern(string path, string pattern, Godot.Collections.Array matches, int maxResults)
    {
        if (matches.Count >= maxResults)
            return;

        var dir = DirAccess.Open(path);
        if (dir == null)
            return;

        dir.ListDirBegin();
        var fileName = dir.GetNext();

        while (!string.IsNullOrEmpty(fileName) && matches.Count < maxResults)
        {
            if (fileName.StartsWith('.'))
            {
                fileName = dir.GetNext();
                continue;
            }

            var fullPath = path.PathJoin(fileName);

            if (dir.CurrentIsDir())
            {
                SearchFilesForPattern(fullPath, pattern, matches, maxResults);
            }
            else if (System.Array.IndexOf(ReferenceSearchExtensions, fileName.GetExtension()) >= 0)
            {
                using var file = Godot.FileAccess.Open(fullPath, Godot.FileAccess.ModeFlags.Read);
                if (file != null)
                {
                    var content = file.GetAsText();
                    if (content.Contains(pattern))
                    {
                        var lines = content.Split('\n');
                        var lineMatches = new Godot.Collections.Array();
                        for (var i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Contains(pattern))
                            {
                                lineMatches.Add(i + 1);
                                if (lineMatches.Count >= 5)
                                    break;
                            }
                        }
                        matches.Add(new Dictionary
                        {
                            ["file"] = fullPath,
                            ["lines"] = lineMatches,
                        });
                    }
                }
            }

            fileName = dir.GetNext();
        }

        dir.ListDirEnd();
    }

    // ── get_scene_dependencies ───────────────────────────────────────────────

    private Task<Dictionary> GetSceneDependencies(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null) return Task.FromResult(err);

        if (!Godot.FileAccess.FileExists(path))
            return Task.FromResult(ErrorNotFound($"File '{path}'"));

        var deps = ResourceLoader.GetDependencies(path);
        var dependencies = new Godot.Collections.Array();
        foreach (var dep in deps)
        {
            // Format: "path::type" (or "path::subresource_id::type")
            var parts = dep.Split("::");
            dependencies.Add(new Dictionary
            {
                ["path"] = parts.Length > 0 ? parts[0] : dep,
                ["type"] = parts.Length > 2 ? parts[2] : "",
            });
        }

        return Task.FromResult(Success(new Dictionary
        {
            ["path"] = path,
            ["dependencies"] = dependencies,
            ["count"] = dependencies.Count,
        }));
    }

    // ── cross_scene_set_property ─────────────────────────────────────────────

    private Task<Dictionary> CrossSceneSetProperty(Dictionary @params)
    {
        var (typeName, err) = RequireString(@params, "type");
        if (err != null) return Task.FromResult(err);

        var (property, err2) = RequireString(@params, "property");
        if (err2 != null) return Task.FromResult(err2);

        if (!@params.ContainsKey("value"))
            return Task.FromResult(ErrorInvalidParams("Missing required parameter: value"));
        var value = ParseExpressionValue(@params["value"]);

        var pathFilter = OptionalString(@params, "path_filter", "res://");
        var excludeAddons = OptionalBool(@params, "exclude_addons", true);
        var force = OptionalBool(@params, "force", false);
        var dryRun = OptionalBool(@params, "dry_run", !force);
        if (!dryRun && !force)
            return Task.FromResult(ErrorInvalidParams("cross_scene_set_property requires force=true when dry_run=false"));

        var scenesAffected = new Godot.Collections.Array();
        var skippedOpenScenes = new Godot.Collections.Array();
        var totalNodes = 0;
        var sceneFiles = new List<string>();
        CollectSceneFiles(pathFilter, sceneFiles, excludeAddons);

        foreach (var scenePath in sceneFiles)
        {
            var normalizedScenePath = NormalizeProjectPath(scenePath);

            if (IsScenePathOpen(normalizedScenePath))
            {
                if (IsActiveScenePath(normalizedScenePath) && force && !dryRun)
                {
                    var liveRoot = GetEditedRoot()!;
                    var liveChanges = new List<(Node Node, Variant OldValue)>();
                    var liveAffectedNodes = new Godot.Collections.Array();
                    CrossSceneCollectChanges(liveRoot, liveRoot, typeName, property, liveAffectedNodes, liveChanges);

                    if (liveChanges.Count > 0)
                    {
                        ApplyPropertyChangesWithUndo(liveChanges, property, value, $"MCP: Cross-scene set {property}");
                        scenesAffected.Add(new Dictionary
                        {
                            ["scene"] = normalizedScenePath,
                            ["nodes"] = liveAffectedNodes,
                            ["count"] = liveAffectedNodes.Count,
                            ["mode"] = "live_open_scene",
                        });
                        totalNodes += liveAffectedNodes.Count;
                    }
                }
                else
                {
                    var reason = dryRun ? "open scene skipped during dry_run" : "open scene is not the active editor scene";
                    skippedOpenScenes.Add(new Dictionary { ["scene"] = normalizedScenePath, ["reason"] = reason });
                }
                continue;
            }

            var packed = ResourceLoader.Load(scenePath) as PackedScene;
            if (packed == null)
                continue;
            var instance = packed.Instantiate();
            if (instance == null)
                continue;

            var affectedNodes = new Godot.Collections.Array();
            var changes = new List<(Node Node, Variant OldValue)>();
            CrossSceneCollectChanges(instance, instance, typeName, property, affectedNodes, changes);

            if (changes.Count > 0)
            {
                if (!dryRun)
                {
                    var guard = GuardOfflineSceneSave(normalizedScenePath);
                    if (guard.Count > 0)
                    {
                        instance.Free();
                        return Task.FromResult(guard);
                    }

                    foreach (var (changedNode, _) in changes)
                        changedNode.Set(property, value);

                    var newPacked = new PackedScene();
                    var packErr = newPacked.Pack(instance);
                    if (packErr != Godot.Error.Ok)
                    {
                        instance.Free();
                        return Task.FromResult(ErrorInternal($"Failed to pack scene '{normalizedScenePath}': {packErr}"));
                    }
                    var saveErr = ResourceSaver.Save(newPacked, normalizedScenePath);
                    if (saveErr != Godot.Error.Ok)
                    {
                        instance.Free();
                        return Task.FromResult(ErrorInternal($"Failed to save scene '{normalizedScenePath}': {saveErr}"));
                    }
                }

                scenesAffected.Add(new Dictionary
                {
                    ["scene"] = normalizedScenePath,
                    ["nodes"] = affectedNodes,
                    ["count"] = affectedNodes.Count,
                    ["mode"] = dryRun ? "dry_run" : "offline_saved",
                });
                totalNodes += affectedNodes.Count;
            }

            instance.Free();
        }

        // Rescan filesystem so editor picks up changes
        if (scenesAffected.Count > 0)
            GetEditor().GetResourceFilesystem().Scan();

        return Task.FromResult(Success(new Dictionary
        {
            ["type"] = typeName,
            ["property"] = property,
            ["dry_run"] = dryRun,
            ["force"] = force,
            ["scenes_affected"] = scenesAffected,
            ["skipped_open_scenes"] = skippedOpenScenes,
            ["total_scenes"] = scenesAffected.Count,
            ["total_nodes"] = totalNodes,
            ["message"] = dryRun
                ? "Dry run only. Re-run with force=true and dry_run=false to write closed scenes and live-edit the active open scene."
                : "Changes applied.",
        }));
    }

    private static void CollectSceneFiles(string path, List<string> files, bool excludeAddons)
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
                if (excludeAddons && fileName == "addons")
                {
                    fileName = dir.GetNext();
                    continue;
                }
                CollectSceneFiles(fullPath, files, excludeAddons);
            }
            else if (fileName.GetExtension() == "tscn")
            {
                files.Add(fullPath);
            }

            fileName = dir.GetNext();
        }

        dir.ListDirEnd();
    }

    private static void CrossSceneCollectChanges(Node node, Node root, string typeName, string property,
        Godot.Collections.Array affected, List<(Node Node, Variant OldValue)> changes)
    {
        if (node.IsClass(typeName) || node.GetClass() == typeName)
        {
            if (NodeHasProperty(node, property))
            {
                affected.Add(root.GetPathTo(node).ToString());
                changes.Add((node, node.Get(property)));
            }
        }
        foreach (var child in node.GetChildren())
            CrossSceneCollectChanges(child, root, typeName, property, affected, changes);
    }

    // ── shared helpers ────────────────────────────────────────────────────────

    /// <summary>Mirrors GDScript's "if value is String: Expression.parse/execute" smart-value parsing used by
    /// both batch_set_property and cross_scene_set_property.</summary>
    private static Variant ParseExpressionValue(Variant value)
    {
        if (value.VariantType != Variant.Type.String)
            return value;

        var expr = new Expression();
        if (expr.Parse(value.AsString()) == Godot.Error.Ok)
        {
            var parsed = expr.Execute();
            if (parsed.VariantType != Variant.Type.Nil)
                return parsed;
        }
        return value;
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
}
