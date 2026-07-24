using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

[Tool]
public partial class VisualShaderCommands : BaseCommand
{
    private static readonly VisualShader.Type[] GraphTypes =
    {
        VisualShader.Type.Vertex,
        VisualShader.Type.Fragment,
        VisualShader.Type.Light,
        VisualShader.Type.Start,
        VisualShader.Type.Process,
        VisualShader.Type.Collide,
        VisualShader.Type.Sky,
        VisualShader.Type.Fog,
    };

    private static readonly System.Collections.Generic.Dictionary<string, Shader.Mode> ModeMap = new()
    {
        ["spatial"] = Shader.Mode.Spatial,
        ["canvas_item"] = Shader.Mode.CanvasItem,
        ["particles"] = Shader.Mode.Particles,
        ["sky"] = Shader.Mode.Sky,
    };

    private static readonly System.Collections.Generic.Dictionary<string, VisualShader.Type> TypeMap = new()
    {
        ["vertex"] = VisualShader.Type.Vertex,
        ["fragment"] = VisualShader.Type.Fragment,
        ["light"] = VisualShader.Type.Light,
        ["start"] = VisualShader.Type.Start,
        ["process"] = VisualShader.Type.Process,
        ["collide"] = VisualShader.Type.Collide,
        ["sky"] = VisualShader.Type.Sky,
        ["fog"] = VisualShader.Type.Fog,
    };

    public override IReadOnlyList<ToolDefinition> GetTools()
    {
        return new List<ToolDefinition>
        {
            new()
            {
                Name = "list_visual_shaders",
                Description = "Find all VisualShader (.vsh) resource files in the project.",
                Category = "visual_shader",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["path"] = Schema.Str("Directory to search.", "res://"),
                }),
                Handler = p => Task.FromResult(ListVisualShaders(p)),
            },
            new()
            {
                Name = "create_visual_shader",
                Description = "Create a new VisualShader resource file (.vsh) with a given mode.",
                Category = "visual_shader",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["path"] = Schema.Str("Output res:// path ending in .vsh."),
                    ["mode"] = Schema.Enum("Shader mode.", "spatial", "canvas_item", "particles", "sky"),
                }, "path"),
                Handler = p => Task.FromResult(CreateVisualShader(p)),
            },
            new()
            {
                Name = "read_visual_shader",
                Description = "Dump a VisualShader graph: nodes, connections across all graph types.",
                Category = "visual_shader",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["path"] = Schema.Str("res:// path to the .vsh file."),
                }, "path"),
                Handler = p => Task.FromResult(ReadVisualShader(p)),
            },
            new()
            {
                Name = "add_visual_shader_node",
                Description = "Add a node to a VisualShader graph by type name.",
                Category = "visual_shader",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["path"] = Schema.Str("res:// path to the .vsh file."),
                    ["node_type"] = Schema.Str("ClassDB class name, e.g. 'VisualShaderNodeTexture2D'."),
                    ["graph_type"] = Schema.Enum("Graph section.", "vertex", "fragment", "light"),
                    ["node_id"] = Schema.Int("Unique node ID.", 2),
                    ["position_x"] = Schema.Num("X position.", 0),
                    ["position_y"] = Schema.Num("Y position.", 0),
                    ["properties"] = Schema.Obj("Initial property values."),
                }, "path", "node_type"),
                Handler = p => Task.FromResult(AddVisualShaderNode(p)),
            },
            new()
            {
                Name = "connect_visual_shader_nodes",
                Description = "Connect an output port of one node to an input port of another.",
                Category = "visual_shader",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["path"] = Schema.Str("res:// path to the .vsh file."),
                    ["graph_type"] = Schema.Enum("Graph section.", "vertex", "fragment", "light"),
                    ["from_node"] = Schema.Int("Source node ID."),
                    ["from_port"] = Schema.Int("Source output port index."),
                    ["to_node"] = Schema.Int("Destination node ID."),
                    ["to_port"] = Schema.Int("Destination input port index."),
                }, "path", "from_node", "from_port", "to_node", "to_port"),
                Handler = p => Task.FromResult(ConnectVisualShaderNodes(p)),
            },
            new()
            {
                Name = "set_visual_shader_parameter",
                Description = "Set a uniform parameter value on a VisualShader.",
                Category = "visual_shader",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["path"] = Schema.Str("res:// path to the .vsh file."),
                    ["parameter"] = Schema.Str("Parameter name."),
                    ["value"] = Schema.Any("Parameter value."),
                }, "path", "parameter", "value"),
                Handler = p => Task.FromResult(SetVisualShaderParameter(p)),
            },
        };
    }

    private Dictionary ListVisualShaders(Dictionary @params)
    {
        var path = OptionalString(@params, "path", "res://");
        var results = new Godot.Collections.Array();

        void ScanDir(string dirPath)
        {
            using var dir = DirAccess.Open(dirPath);
            if (dir == null) return;
            dir.ListDirBegin();
            var name = dir.GetNext();
            while (!string.IsNullOrEmpty(name))
            {
                if (dir.CurrentIsDir() && !name.StartsWith("."))
                    ScanDir($"{dirPath}/{name}");
                else if (name.EndsWith(".vsh"))
                    results.Add($"{dirPath}/{name}");
                name = dir.GetNext();
            }
            dir.ListDirEnd();
        }

        ScanDir(path);
        return Success(new Dictionary { ["shaders"] = results, ["count"] = results.Count });
    }

    private static Dictionary CreateVisualShader(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null) return err;
        var modeStr = OptionalString(@params, "mode", "spatial");

        if (!ModeMap.TryGetValue(modeStr, out var mode))
            return ErrorInvalidParams($"Unknown shader mode '{modeStr}'.");

        var vs = new VisualShader();
        vs.SetMode(mode);

        var sceneGuard = GuardOfflineSceneSave(path);
        if (sceneGuard.Count > 0) return sceneGuard;
        var dirGuard = EnsureParentDir(path);
        if (dirGuard.Count > 0) return dirGuard;

        var saveErr = ResourceSaver.Save(vs, path);
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save VisualShader: {saveErr}");

        EditorInterface.Singleton.GetResourceFilesystem().Scan();
        return Success(new Dictionary { ["path"] = path, ["mode"] = modeStr, ["created"] = true });
    }

    private Dictionary ReadVisualShader(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null) return err;

        var normalized = NormalizeProjectPath(path);
        if (!ResourceLoader.Exists(normalized))
            return ErrorNotFound($"VisualShader at '{normalized}'");

        var res = ResourceLoader.Load(normalized);
        if (res is not VisualShader vs)
            return ErrorInvalidParams($"Resource at '{normalized}' is not a VisualShader.");

        var allNodes = new Godot.Collections.Array();
        var allConnections = new Godot.Collections.Array();

        foreach (var graphType in GraphTypes)
        {
            foreach (var id in vs.GetNodeList(graphType))
            {
                var node = vs.GetNode(graphType, id);
                var pos = vs.GetNodePosition(graphType, id);
                allNodes.Add(new Dictionary
                {
                    ["id"] = id,
                    ["type"] = node?.GetClass() ?? "unknown",
                    ["graph_type"] = graphType.ToString().ToLowerInvariant(),
                    ["position"] = new Dictionary { ["x"] = pos.X, ["y"] = pos.Y },
                });
            }

            foreach (var conn in vs.GetNodeConnections(graphType))
            {
                allConnections.Add(new Dictionary
                {
                    ["graph_type"] = graphType.ToString().ToLowerInvariant(),
                    ["from_node"] = conn.TryGetValue("from_node", out var fn) ? fn.AsInt32() : 0,
                    ["from_port"] = conn.TryGetValue("from_port", out var fp) ? fp.AsInt32() : 0,
                    ["to_node"] = conn.TryGetValue("to_node", out var tn) ? tn.AsInt32() : 0,
                    ["to_port"] = conn.TryGetValue("to_port", out var tp) ? tp.AsInt32() : 0,
                });
            }
        }

        return Success(new Dictionary
        {
            ["path"] = normalized,
            ["mode"] = vs.GetMode().ToString(),
            ["nodes"] = allNodes,
            ["node_count"] = allNodes.Count,
            ["connections"] = allConnections,
            ["connection_count"] = allConnections.Count,
        });
    }

    private Dictionary AddVisualShaderNode(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null) return err;
        var (nodeType, e2) = RequireString(@params, "node_type");
        if (e2 != null) return e2;
        var graphTypeStr = OptionalString(@params, "graph_type", "fragment");
        var nodeId = OptionalInt(@params, "node_id", 2);
        var posX = OptionalFloat(@params, "position_x", 0);
        var posY = OptionalFloat(@params, "position_y", 0);
        var properties = OptionalDict(@params, "properties");

        if (!TypeMap.TryGetValue(graphTypeStr, out var graphType))
            return ErrorInvalidParams($"Unknown graph_type '{graphTypeStr}'.");

        var normalized = NormalizeProjectPath(path);
        if (!ResourceLoader.Exists(normalized))
            return ErrorNotFound($"VisualShader at '{normalized}'");

        var res = ResourceLoader.Load(normalized);
        if (res is not VisualShader vs)
            return ErrorInvalidParams($"Resource at '{normalized}' is not a VisualShader.");

        if (!ClassDB.ClassExists(nodeType) || !ClassDB.IsParentClass("VisualShaderNode", nodeType))
            return ErrorInvalidParams($"'{nodeType}' is not a valid VisualShaderNode subclass.");

        var nodeVar = ClassDB.Instantiate(nodeType);
        if (nodeVar.VariantType != Variant.Type.Object || nodeVar.AsGodotObject() is not VisualShaderNode node)
            return ErrorInternal($"Failed to instantiate {nodeType}.");

        if (properties != null)
        {
            foreach (var key in properties.Keys)
            {
                var propName = key.AsString();
                node.Set(propName, PropertyParser.ParseValue(properties[key]));
            }
        }

        vs.AddNode(graphType, node, new Vector2((float)posX, (float)posY), nodeId);
        vs.EmitChanged();
        ResourceSaver.Save(vs, normalized);
        EditorInterface.Singleton.GetResourceFilesystem().Scan();

        return Success(new Dictionary
        {
            ["path"] = normalized,
            ["node_id"] = nodeId,
            ["node_type"] = nodeType,
            ["graph_type"] = graphTypeStr,
        });
    }

    private Dictionary ConnectVisualShaderNodes(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null) return err;
        var graphTypeStr = OptionalString(@params, "graph_type", "fragment");
        var fromNode = OptionalInt(@params, "from_node", 0);
        var fromPort = OptionalInt(@params, "from_port", 0);
        var toNode = OptionalInt(@params, "to_node", 0);
        var toPort = OptionalInt(@params, "to_port", 0);

        if (!TypeMap.TryGetValue(graphTypeStr, out var graphType))
            return ErrorInvalidParams($"Unknown graph_type '{graphTypeStr}'.");

        var normalized = NormalizeProjectPath(path);
        if (!ResourceLoader.Exists(normalized))
            return ErrorNotFound($"VisualShader at '{normalized}'");

        var res = ResourceLoader.Load(normalized);
        if (res is not VisualShader vs)
            return ErrorInvalidParams($"Resource at '{normalized}' is not a VisualShader.");

        vs.ConnectNodesForced(graphType, fromNode, fromPort, toNode, toPort);
        vs.EmitChanged();
        ResourceSaver.Save(vs, normalized);

        return Success(new Dictionary
        {
            ["path"] = normalized,
            ["graph_type"] = graphTypeStr,
            ["from_node"] = fromNode,
            ["from_port"] = fromPort,
            ["to_node"] = toNode,
            ["to_port"] = toPort,
        });
    }

    private static Dictionary SetVisualShaderParameter(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null) return err;
        var (parameter, e2) = RequireString(@params, "parameter");
        if (e2 != null) return e2;
        if (!@params.ContainsKey("value"))
            return ErrorInvalidParams("Missing required parameter: value");

        var normalized = NormalizeProjectPath(path);
        if (!ResourceLoader.Exists(normalized))
            return ErrorNotFound($"VisualShader at '{normalized}'");

        var res = ResourceLoader.Load(normalized);
        if (res is not VisualShader vs)
            return ErrorInvalidParams($"Resource at '{normalized}' is not a VisualShader.");

        var value = PropertyParser.ParseValue(@params["value"]);
        vs.Set($"shader_parameter/{parameter}", value);
        vs.EmitChanged();
        ResourceSaver.Save(vs, normalized);

        return Success(new Dictionary
        {
            ["path"] = normalized,
            ["parameter"] = parameter,
            ["set"] = true,
        });
    }
}
