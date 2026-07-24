using Godot;
using Godot.Collections;
using GodotMcpPro.Commands;

namespace GodotMcpPro.Server;

/// <summary>
/// Tool registry + dispatcher. Owns all command module instances as children,
/// flattens their tool definitions, and executes tools by name.
/// Port of addons/godot_mcp/command_router.gd.
/// </summary>
[Tool]
public partial class CommandRouter : Node
{
    private const string ToolConfigPath = "user://mcp_tool_config.cfg";

    private readonly System.Collections.Generic.Dictionary<string, ToolDefinition> _tools = new();
    private readonly HashSet<string> _disabledTools = new();

    public EditorPlugin? Plugin { get; set; }

    /// <summary>All registered tools (including disabled ones), in registration order.</summary>
    public IReadOnlyList<ToolDefinition> Tools => _toolOrder;
    private readonly List<ToolDefinition> _toolOrder = new();

    /// <summary>Raised after a tool executes: (toolName, isError).</summary>
    public event Action<string, bool>? ToolExecuted;

    public override void _Ready()
    {
        Name = "MCPCommandRouter";
        RegisterCommandModules();
        LoadToolConfig();
        GD.Print($"[MCP] Command router ready: {_tools.Count} tools registered");
    }

    private void RegisterCommandModules()
    {
        foreach (var module in CommandModuleFactory.CreateAll())
        {
            module.Plugin = Plugin;
            AddChild(module);
            foreach (var tool in module.GetTools())
            {
                if (_tools.ContainsKey(tool.Name))
                {
                    GD.PushWarning($"[MCP] Duplicate tool name skipped: {tool.Name}");
                    continue;
                }
                _tools[tool.Name] = tool;
                _toolOrder.Add(tool);
            }
        }
    }

    /// <summary>Execute a tool by name. Returns the {"result":...}/{"error":...} envelope.</summary>
    public async Task<Godot.Collections.Dictionary> Execute(string method, Godot.Collections.Dictionary @params)
    {
        if (!_tools.TryGetValue(method, out var tool))
        {
            return BaseCommand.Error(BaseCommand.CodeMethodNotFound, $"Method not found: {method}");
        }
        if (_disabledTools.Contains(method))
        {
            return BaseCommand.Error(BaseCommand.CodeInternal,
                $"Tool '{method}' is disabled",
                new Dictionary { ["suggestion"] = "Enable it in the MCP Pro panel's Tools tab." });
        }

        Godot.Collections.Dictionary result;
        try
        {
            result = await tool.Handler(@params);
        }
        catch (Exception e)
        {
            GD.PushError($"[MCP] Tool '{method}' threw: {e}");
            result = BaseCommand.ErrorInternal($"{e.GetType().Name}: {e.Message}");
        }
        ToolExecuted?.Invoke(method, BaseCommand.IsError(result));
        return result;
    }

    // ── Enable/disable persistence ─────────────────────────────────────────────

    public bool IsToolEnabled(string name) => !_disabledTools.Contains(name);

    public void SetToolEnabled(string name, bool enabled)
    {
        if (enabled) _disabledTools.Remove(name);
        else _disabledTools.Add(name);
        SaveToolConfig();
    }

    private void LoadToolConfig()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ToolConfigPath) != Godot.Error.Ok)
            return;
        _disabledTools.Clear();
        if (!cfg.HasSection("disabled_tools"))
            return;
        foreach (var key in cfg.GetSectionKeys("disabled_tools"))
        {
            if (cfg.GetValue("disabled_tools", key, false).AsBool())
                _disabledTools.Add(key);
        }
    }

    private void SaveToolConfig()
    {
        var cfg = new ConfigFile();
        foreach (var name in _disabledTools)
            cfg.SetValue("disabled_tools", name, true);
        cfg.Save(ToolConfigPath);
    }
}
