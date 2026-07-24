using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Navigation tools: NavigationRegion2D/3D setup, navmesh/navpoly baking,
/// NavigationAgent2D/3D setup, navigation layer editing, and navigation
/// introspection. Faithful port of addons/godot_mcp/commands/navigation_commands.gd.
/// Scene mutations go through EditorUndoRedoManager so they participate in the
/// editor's undo history.
/// </summary>
[Tool]
public partial class NavigationCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "setup_navigation_region",
            Description = "Add a NavigationRegion2D/3D child with a new NavigationPolygon/NavigationMesh resource " +
                "to a node. Dimension is auto-detected from the node's ancestry (Node2D vs Node3D) unless overridden.",
            Category = "navigation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the parent node."),
                ["mode"] = Schema.Enum("Force '2d' or '3d' (defaults to 'auto' = detect from node ancestry).",
                    "auto", "2d", "3d"),
                ["name"] = Schema.Str("Name for the new region node (defaults to NavigationRegion2D/3D)."),
                ["agent_radius"] = Schema.Num("Agent radius used for baking (3D default 0.5; 2D only set when provided)."),
                ["agent_height"] = Schema.Num("3D only: agent height used for baking.", 1.5),
                ["agent_max_climb"] = Schema.Num("3D only: max climb height used for baking.", 0.25),
                ["agent_max_slope"] = Schema.Num("3D only: max slope in degrees used for baking.", 45.0),
                ["cell_size"] = Schema.Num("Bake cell size (3D default 0.25; 2D only set when provided)."),
                ["cell_height"] = Schema.Num("3D only: bake cell height.", 0.25),
                ["navigation_layers"] = Schema.Int("Navigation layers bitmask for the region."),
                ["source_geometry_mode"] = Schema.Enum("2D only: which nodes are parsed as source geometry.",
                    "root_node", "groups_with_children", "groups_explicit"),
            }, "node_path"),
            Handler = p => Task.FromResult(SetupNavigationRegion(p)),
        },
        new()
        {
            Name = "bake_navigation_mesh",
            Description = "Bake a NavigationRegion3D's navmesh from scene geometry, or bake/build a " +
                "NavigationRegion2D's polygon — either from an explicit outline of vertices or from its " +
                "source geometry.",
            Category = "navigation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the NavigationRegion2D/3D node."),
                ["outline"] = Schema.Arr("2D only: outline vertices as [x,y] arrays or {x,y} objects " +
                    "(at least 3). Replaces any existing outlines.",
                    Schema.Any("Outline point: [x,y] array or {\"x\":..,\"y\":..} object.")),
            }, "node_path"),
            Handler = p => Task.FromResult(BakeNavigationMesh(p)),
        },
        new()
        {
            Name = "setup_navigation_agent",
            Description = "Add a NavigationAgent2D/3D child to a node with pathing and avoidance settings. " +
                "Dimension is auto-detected from the node's ancestry unless overridden.",
            Category = "navigation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the parent node."),
                ["mode"] = Schema.Enum("Force '2d' or '3d' (defaults to 'auto' = detect from node ancestry).",
                    "auto", "2d", "3d"),
                ["name"] = Schema.Str("Name for the new agent node (defaults to NavigationAgent2D/3D)."),
                ["path_desired_distance"] = Schema.Num("Distance to a path point before advancing to the next one."),
                ["target_desired_distance"] = Schema.Num("Distance to the target before it counts as reached."),
                ["radius"] = Schema.Num("Agent avoidance radius."),
                ["neighbor_distance"] = Schema.Num("Avoidance neighbor search distance."),
                ["max_neighbors"] = Schema.Int("Max neighbors considered for avoidance."),
                ["max_speed"] = Schema.Num("Max avoidance speed."),
                ["avoidance_enabled"] = Schema.Bool("Enable RVO avoidance."),
                ["navigation_layers"] = Schema.Int("Navigation layers bitmask for the agent."),
            }, "node_path"),
            Handler = p => Task.FromResult(SetupNavigationAgent(p)),
        },
        new()
        {
            Name = "set_navigation_layers",
            Description = "Set navigation_layers on a NavigationRegion2D/3D or NavigationAgent2D/3D (undo-tracked). " +
                "Accepts a raw bitmask ('layers'), an array of 1-based layer numbers ('layer_bits'), or an array " +
                "of layer names from ProjectSettings ('layer_names').",
            Category = "navigation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the navigation region or agent node."),
                ["layers"] = Schema.Int("navigation_layers as a raw bitmask."),
                ["layer_bits"] = Schema.Arr("Array of 1-based layer numbers (1-32) to enable.",
                    Schema.Int("Layer number (1-32).")),
                ["layer_names"] = Schema.Arr("Array of named navigation layers (from ProjectSettings) to enable.",
                    Schema.Str("Layer name.")),
            }, "node_path"),
            Handler = p => Task.FromResult(SetNavigationLayers(p)),
        },
        new()
        {
            Name = "get_navigation_info",
            Description = "List all NavigationRegion2D/3D and NavigationAgent2D/3D nodes under a node " +
                "(recursively) with their settings, plus named navigation layers from ProjectSettings.",
            Category = "navigation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Scene-relative path of the node to search from."),
            }, "node_path"),
            Handler = p => Task.FromResult(GetNavigationInfo(p)),
        },
    };

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool Is3DContext(Node node)
    {
        if (node is Node3D)
            return true;
        if (node is Node2D)
            return false;
        // Walk up to detect context
        var parent = node.GetParent();
        while (parent != null)
        {
            if (parent is Node3D)
                return true;
            if (parent is Node2D)
                return false;
            parent = parent.GetParent();
        }
        return false;
    }

    /// <summary>Mirrors GDScript int() coercion for Variant values.</summary>
    private static int VariantToInt(Variant v) => v.VariantType switch
    {
        Variant.Type.Int => (int)v.AsInt64(),
        Variant.Type.Float => (int)v.AsDouble(),
        Variant.Type.Bool => v.AsBool() ? 1 : 0,
        Variant.Type.String => (int)v.AsString().ToFloat(),
        _ => 0,
    };

    private static bool IsNavigationNode(Node node) =>
        node is NavigationRegion2D or NavigationRegion3D or NavigationAgent2D or NavigationAgent3D;

    // ── setup_navigation_region ────────────────────────────────────────────────

    private Dictionary SetupNavigationRegion(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node at '{nodePath}'");
        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        var forceMode = OptionalString(@params, "mode", "auto");
        var is3D = forceMode switch
        {
            "2d" => false,
            "3d" => true,
            _ => Is3DContext(node),
        };

        if (is3D)
        {
            var region = new NavigationRegion3D();
            region.Name = OptionalString(@params, "name", "NavigationRegion3D");

            var navMesh = new NavigationMesh
            {
                AgentRadius = (float)OptionalFloat(@params, "agent_radius", 0.5),
                AgentHeight = (float)OptionalFloat(@params, "agent_height", 1.5),
                AgentMaxClimb = (float)OptionalFloat(@params, "agent_max_climb", 0.25),
                AgentMaxSlope = (float)OptionalFloat(@params, "agent_max_slope", 45.0),
                CellSize = (float)OptionalFloat(@params, "cell_size", 0.25),
                CellHeight = (float)OptionalFloat(@params, "cell_height", 0.25),
            };
            region.NavigationMesh = navMesh;

            if (@params.ContainsKey("navigation_layers"))
                region.NavigationLayers = (uint)VariantToInt(@params["navigation_layers"]);

            AddChildWithUndo(node, region, root, "MCP: Add NavigationRegion3D");

            return Success(new Dictionary
            {
                ["node_path"] = region.GetPath().ToString(),
                ["type"] = "NavigationRegion3D",
                ["agent_radius"] = navMesh.AgentRadius,
                ["agent_height"] = navMesh.AgentHeight,
                ["cell_size"] = navMesh.CellSize,
                ["created"] = true,
            });
        }
        else
        {
            var region = new NavigationRegion2D();
            region.Name = OptionalString(@params, "name", "NavigationRegion2D");

            var navPoly = new NavigationPolygon();

            // Set parsed geometry source if available
            if (@params.ContainsKey("source_geometry_mode"))
            {
                var modeStr = @params["source_geometry_mode"].VariantType == Variant.Type.String
                    ? @params["source_geometry_mode"].AsString()
                    : @params["source_geometry_mode"].ToString();
                switch (modeStr)
                {
                    case "root_node":
                        navPoly.SourceGeometryMode = NavigationPolygon.SourceGeometryModeEnum.RootNodeChildren;
                        break;
                    case "groups_with_children":
                        navPoly.SourceGeometryMode = NavigationPolygon.SourceGeometryModeEnum.GroupsWithChildren;
                        break;
                    case "groups_explicit":
                        navPoly.SourceGeometryMode = NavigationPolygon.SourceGeometryModeEnum.GroupsExplicit;
                        break;
                }
            }

            if (@params.ContainsKey("cell_size"))
                navPoly.CellSize = (float)OptionalFloat(@params, "cell_size");

            if (@params.ContainsKey("agent_radius"))
                navPoly.AgentRadius = (float)OptionalFloat(@params, "agent_radius");

            region.NavigationPolygon = navPoly;

            if (@params.ContainsKey("navigation_layers"))
                region.NavigationLayers = (uint)VariantToInt(@params["navigation_layers"]);

            AddChildWithUndo(node, region, root, "MCP: Add NavigationRegion2D");

            return Success(new Dictionary
            {
                ["node_path"] = region.GetPath().ToString(),
                ["type"] = "NavigationRegion2D",
                ["cell_size"] = navPoly.CellSize,
                ["created"] = true,
            });
        }
    }

    // ── bake_navigation_mesh ───────────────────────────────────────────────────

    private static Dictionary BakeNavigationMesh(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node at '{nodePath}'");
        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        if (node is NavigationRegion3D region3D)
        {
            if (region3D.NavigationMesh == null)
                return ErrorInvalidParams("NavigationRegion3D has no NavigationMesh resource");
            region3D.BakeNavigationMesh();
            MarkCurrentSceneUnsaved();
            return Success(new Dictionary
            {
                ["node_path"] = nodePath,
                ["type"] = "NavigationRegion3D",
                ["baked"] = true,
            });
        }
        else if (node is NavigationRegion2D region2D)
        {
            if (region2D.NavigationPolygon == null)
                region2D.NavigationPolygon = new NavigationPolygon();

            // Set outline vertices from params
            if (@params.ContainsKey("outline"))
            {
                var outlineData = @params["outline"].AsGodotArray();
                var outline = new List<Vector2>();
                foreach (var point in outlineData)
                {
                    if (point.VariantType == Variant.Type.Array)
                    {
                        var pointArr = point.AsGodotArray();
                        if (pointArr.Count >= 2)
                            outline.Add(new Vector2((float)pointArr[0].AsDouble(), (float)pointArr[1].AsDouble()));
                    }
                    else if (point.VariantType == Variant.Type.Dictionary)
                    {
                        var pointDict = point.AsGodotDictionary();
                        outline.Add(new Vector2(
                            (float)OptionalFloat(pointDict, "x", 0),
                            (float)OptionalFloat(pointDict, "y", 0)));
                    }
                }

                if (outline.Count >= 3)
                {
                    // Clear existing outlines
                    while (region2D.NavigationPolygon.GetOutlineCount() > 0)
                        region2D.NavigationPolygon.RemoveOutline(0);
                    region2D.NavigationPolygon.AddOutline(outline.ToArray());
                    region2D.NavigationPolygon.MakePolygonsFromOutlines();
                    MarkCurrentSceneUnsaved();
                    return Success(new Dictionary
                    {
                        ["node_path"] = nodePath,
                        ["type"] = "NavigationRegion2D",
                        ["outline_vertices"] = outline.Count,
                        ["baked"] = true,
                    });
                }
                else
                {
                    return ErrorInvalidParams("Outline must have at least 3 vertices");
                }
            }
            else
            {
                // Try baking from source geometry
                region2D.BakeNavigationPolygon();
                MarkCurrentSceneUnsaved();
                return Success(new Dictionary
                {
                    ["node_path"] = nodePath,
                    ["type"] = "NavigationRegion2D",
                    ["baked"] = true,
                });
            }
        }

        return ErrorInvalidParams($"Node '{nodePath}' is not a NavigationRegion2D or NavigationRegion3D");
    }

    // ── setup_navigation_agent ─────────────────────────────────────────────────

    private Dictionary SetupNavigationAgent(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node at '{nodePath}'");
        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        var forceMode = OptionalString(@params, "mode", "auto");
        var is3D = forceMode switch
        {
            "2d" => false,
            "3d" => true,
            _ => Is3DContext(node),
        };

        var agentName = OptionalString(@params, "name", is3D ? "NavigationAgent3D" : "NavigationAgent2D");

        if (is3D)
        {
            var agent = new NavigationAgent3D();
            agent.Name = agentName;

            if (@params.ContainsKey("path_desired_distance"))
                agent.PathDesiredDistance = (float)OptionalFloat(@params, "path_desired_distance");
            if (@params.ContainsKey("target_desired_distance"))
                agent.TargetDesiredDistance = (float)OptionalFloat(@params, "target_desired_distance");
            if (@params.ContainsKey("radius"))
                agent.Radius = (float)OptionalFloat(@params, "radius");
            if (@params.ContainsKey("neighbor_distance"))
                agent.NeighborDistance = (float)OptionalFloat(@params, "neighbor_distance");
            if (@params.ContainsKey("max_neighbors"))
                agent.MaxNeighbors = OptionalInt(@params, "max_neighbors");
            if (@params.ContainsKey("max_speed"))
                agent.MaxSpeed = (float)OptionalFloat(@params, "max_speed");
            if (@params.ContainsKey("avoidance_enabled"))
                agent.AvoidanceEnabled = CoerceBool(@params["avoidance_enabled"]);
            if (@params.ContainsKey("navigation_layers"))
                agent.NavigationLayers = (uint)VariantToInt(@params["navigation_layers"]);

            AddChildWithUndo(node, agent, root, "MCP: Add NavigationAgent3D");

            return Success(new Dictionary
            {
                ["node_path"] = agent.GetPath().ToString(),
                ["type"] = "NavigationAgent3D",
                ["radius"] = agent.Radius,
                ["max_speed"] = agent.MaxSpeed,
                ["avoidance_enabled"] = agent.AvoidanceEnabled,
                ["navigation_layers"] = agent.NavigationLayers,
                ["created"] = true,
            });
        }
        else
        {
            var agent = new NavigationAgent2D();
            agent.Name = agentName;

            if (@params.ContainsKey("path_desired_distance"))
                agent.PathDesiredDistance = (float)OptionalFloat(@params, "path_desired_distance");
            if (@params.ContainsKey("target_desired_distance"))
                agent.TargetDesiredDistance = (float)OptionalFloat(@params, "target_desired_distance");
            if (@params.ContainsKey("radius"))
                agent.Radius = (float)OptionalFloat(@params, "radius");
            if (@params.ContainsKey("neighbor_distance"))
                agent.NeighborDistance = (float)OptionalFloat(@params, "neighbor_distance");
            if (@params.ContainsKey("max_neighbors"))
                agent.MaxNeighbors = OptionalInt(@params, "max_neighbors");
            if (@params.ContainsKey("max_speed"))
                agent.MaxSpeed = (float)OptionalFloat(@params, "max_speed");
            if (@params.ContainsKey("avoidance_enabled"))
                agent.AvoidanceEnabled = CoerceBool(@params["avoidance_enabled"]);
            if (@params.ContainsKey("navigation_layers"))
                agent.NavigationLayers = (uint)VariantToInt(@params["navigation_layers"]);

            AddChildWithUndo(node, agent, root, "MCP: Add NavigationAgent2D");

            return Success(new Dictionary
            {
                ["node_path"] = agent.GetPath().ToString(),
                ["type"] = "NavigationAgent2D",
                ["radius"] = agent.Radius,
                ["max_speed"] = agent.MaxSpeed,
                ["avoidance_enabled"] = agent.AvoidanceEnabled,
                ["navigation_layers"] = agent.NavigationLayers,
                ["created"] = true,
            });
        }
    }

    // ── set_navigation_layers ──────────────────────────────────────────────────

    private Dictionary SetNavigationLayers(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node at '{nodePath}'");

        // Support setting by bitmask value
        if (@params.ContainsKey("layers"))
        {
            var layersVal = VariantToInt(@params["layers"]);
            if (IsNavigationNode(node))
                SetPropertyWithUndo(node, "navigation_layers", layersVal, "MCP: Set navigation layers");
            else
                return ErrorInvalidParams($"Node '{nodePath}' is not a navigation region or agent");

            return Success(new Dictionary
            {
                ["node_path"] = nodePath,
                ["navigation_layers"] = layersVal,
                ["updated"] = true,
            });
        }

        // Support setting individual layer bits by number
        if (@params.ContainsKey("layer_bits"))
        {
            var bits = @params["layer_bits"].AsGodotArray();
            var currentLayers = 0;

            // Calculate bitmask from layer numbers (1-based)
            foreach (var bit in bits)
            {
                var layerNum = VariantToInt(bit);
                if (layerNum is >= 1 and <= 32)
                    currentLayers |= 1 << (layerNum - 1);
            }

            if (IsNavigationNode(node))
                SetPropertyWithUndo(node, "navigation_layers", currentLayers, "MCP: Set navigation layers");
            else
                return ErrorInvalidParams($"Node '{nodePath}' is not a navigation region or agent");

            return Success(new Dictionary
            {
                ["node_path"] = nodePath,
                ["navigation_layers"] = currentLayers,
                ["layer_bits"] = bits,
                ["updated"] = true,
            });
        }

        // Support named layers from ProjectSettings
        if (@params.ContainsKey("layer_names"))
        {
            var names = @params["layer_names"].AsGodotArray();
            var currentLayers = 0;
            var is2D = node is NavigationRegion2D or NavigationAgent2D;
            var prefix = is2D ? "layer_names/2d_navigation/layer_" : "layer_names/3d_navigation/layer_";

            for (var i = 1; i <= 32; i++)
            {
                var settingKey = prefix + i;
                if (ProjectSettings.HasSetting(settingKey))
                {
                    var layerName = ProjectSettings.GetSetting(settingKey).ToString();
                    if (names.Contains(layerName))
                        currentLayers |= 1 << (i - 1);
                }
            }

            if (IsNavigationNode(node))
                SetPropertyWithUndo(node, "navigation_layers", currentLayers, "MCP: Set navigation layers");
            else
                return ErrorInvalidParams($"Node '{nodePath}' is not a navigation region or agent");

            return Success(new Dictionary
            {
                ["node_path"] = nodePath,
                ["navigation_layers"] = currentLayers,
                ["layer_names"] = names,
                ["updated"] = true,
            });
        }

        return ErrorInvalidParams(
            "Must provide 'layers' (bitmask), 'layer_bits' (array of layer numbers), or 'layer_names' (array of named layers)");
    }

    // ── get_navigation_info ────────────────────────────────────────────────────

    private static Dictionary GetNavigationInfo(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null)
            return err;

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node at '{nodePath}'");

        var regions = new Godot.Collections.Array();
        var agents = new Godot.Collections.Array();

        CollectNavigationNodes(node, regions, agents);

        // Collect named layers from ProjectSettings
        var layerNames2D = new Dictionary();
        var layerNames3D = new Dictionary();
        for (var i = 1; i <= 32; i++)
        {
            var key2D = "layer_names/2d_navigation/layer_" + i;
            var key3D = "layer_names/3d_navigation/layer_" + i;
            if (ProjectSettings.HasSetting(key2D))
            {
                var name2D = ProjectSettings.GetSetting(key2D).ToString();
                if (!string.IsNullOrEmpty(name2D))
                    layerNames2D[i] = name2D;
            }
            if (ProjectSettings.HasSetting(key3D))
            {
                var name3D = ProjectSettings.GetSetting(key3D).ToString();
                if (!string.IsNullOrEmpty(name3D))
                    layerNames3D[i] = name3D;
            }
        }

        return Success(new Dictionary
        {
            ["node_path"] = nodePath,
            ["regions"] = regions,
            ["agents"] = agents,
            ["region_count"] = regions.Count,
            ["agent_count"] = agents.Count,
            ["layer_names_2d"] = layerNames2D,
            ["layer_names_3d"] = layerNames3D,
        });
    }

    private static void CollectNavigationNodes(Node node, Godot.Collections.Array regions, Godot.Collections.Array agents)
    {
        if (node is NavigationRegion2D region2D)
        {
            var regionInfo = new Dictionary
            {
                ["path"] = region2D.GetPath().ToString(),
                ["type"] = "NavigationRegion2D",
                ["enabled"] = region2D.Enabled,
                ["navigation_layers"] = region2D.NavigationLayers,
                ["has_polygon"] = region2D.NavigationPolygon != null,
            };
            if (region2D.NavigationPolygon != null)
            {
                var navPoly = region2D.NavigationPolygon;
                regionInfo["outline_count"] = navPoly.GetOutlineCount();
                regionInfo["polygon_count"] = navPoly.GetPolygonCount();
                regionInfo["cell_size"] = navPoly.CellSize;
                regionInfo["agent_radius"] = navPoly.AgentRadius;
            }
            regions.Add(regionInfo);
        }
        else if (node is NavigationRegion3D region3D)
        {
            var regionInfo = new Dictionary
            {
                ["path"] = region3D.GetPath().ToString(),
                ["type"] = "NavigationRegion3D",
                ["enabled"] = region3D.Enabled,
                ["navigation_layers"] = region3D.NavigationLayers,
                ["has_mesh"] = region3D.NavigationMesh != null,
            };
            if (region3D.NavigationMesh != null)
            {
                var navMesh = region3D.NavigationMesh;
                regionInfo["agent_radius"] = navMesh.AgentRadius;
                regionInfo["agent_height"] = navMesh.AgentHeight;
                regionInfo["agent_max_climb"] = navMesh.AgentMaxClimb;
                regionInfo["agent_max_slope"] = navMesh.AgentMaxSlope;
                regionInfo["cell_size"] = navMesh.CellSize;
                regionInfo["cell_height"] = navMesh.CellHeight;
            }
            regions.Add(regionInfo);
        }

        if (node is NavigationAgent2D agent2D)
        {
            agents.Add(new Dictionary
            {
                ["path"] = agent2D.GetPath().ToString(),
                ["type"] = "NavigationAgent2D",
                ["radius"] = agent2D.Radius,
                ["max_speed"] = agent2D.MaxSpeed,
                ["path_desired_distance"] = agent2D.PathDesiredDistance,
                ["target_desired_distance"] = agent2D.TargetDesiredDistance,
                ["neighbor_distance"] = agent2D.NeighborDistance,
                ["max_neighbors"] = agent2D.MaxNeighbors,
                ["avoidance_enabled"] = agent2D.AvoidanceEnabled,
                ["navigation_layers"] = agent2D.NavigationLayers,
            });
        }
        else if (node is NavigationAgent3D agent3D)
        {
            agents.Add(new Dictionary
            {
                ["path"] = agent3D.GetPath().ToString(),
                ["type"] = "NavigationAgent3D",
                ["radius"] = agent3D.Radius,
                ["max_speed"] = agent3D.MaxSpeed,
                ["path_desired_distance"] = agent3D.PathDesiredDistance,
                ["target_desired_distance"] = agent3D.TargetDesiredDistance,
                ["neighbor_distance"] = agent3D.NeighborDistance,
                ["max_neighbors"] = agent3D.MaxNeighbors,
                ["avoidance_enabled"] = agent3D.AvoidanceEnabled,
                ["navigation_layers"] = agent3D.NavigationLayers,
            });
        }

        foreach (var child in node.GetChildren())
            CollectNavigationNodes(child, regions, agents);
    }
}
