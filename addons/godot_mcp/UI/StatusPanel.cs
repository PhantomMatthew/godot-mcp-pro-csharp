using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro;

/// <summary>
/// Bottom-panel UI for the MCP plugin. Built programmatically (no .tscn).
/// Three sections: connection status, activity log, per-tool enable/disable.
/// </summary>
[Tool]
public partial class StatusPanel : Control
{
    private Label? _statusLabel;
    private Label? _portLabel;
    private RichTextLabel? _logView;
    private ItemList? _toolList;
    private GodotMcpPlugin? _plugin;
    private const int MaxLogLines = 200;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(0, 180);
        var vb = new VBoxContainer { AnchorsPreset = (int)Control.LayoutPreset.FullRect, OffsetLeft = 8, OffsetTop = 4, OffsetRight = -8, OffsetBottom = -4 };
        AddChild(vb);

        // Header row
        var header = new HBoxContainer();
        _statusLabel = new Label { Text = "MCP Pro: starting..." };
        header.AddChild(_statusLabel);
        header.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0), SizeFlagsHorizontal = Control.SizeFlags.Expand });
        _portLabel = new Label { Text = "" };
        header.AddChild(_portLabel);
        vb.AddChild(header);

        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.Expand };
        vb.AddChild(tabs);

        _logView = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SizeFlagsVertical = Control.SizeFlags.Expand,
        };
        tabs.AddChild(_logView);
        tabs.SetTabTitle(0, "Activity");

        _toolList = new ItemList { SizeFlagsVertical = Control.SizeFlags.Expand };
        tabs.AddChild(_toolList);
        tabs.SetTabTitle(1, "Tools");
    }

    public void Bind(GodotMcpPlugin plugin)
    {
        _plugin = plugin;
        var router = plugin.Router;
        if (router == null) return;
        router.ToolExecuted += OnToolExecuted;
        PopulateToolList();
    }

    public void UpdateStatus(int port, bool running)
    {
        if (_statusLabel == null) return;
        _statusLabel.Text = running ? "● MCP Pro: connected" : "○ MCP Pro: stopped";
        _statusLabel.AddThemeColorOverride("font_color", running ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.8f, 0.4f, 0.3f));
        _portLabel!.Text = running ? $"http://127.0.0.1:{port}/mcp" : "";
    }

    public void LogActivity(string text)
    {
        if (_logView == null) return;
        var timestamp = Time.GetTimeStringFromSystem();
        _logView.AppendText($"[color=gray]{timestamp}[/color] {text}\n");
    }

    private void OnToolExecuted(string toolName, bool isError)
    {
        var color = isError ? "red" : "green";
        LogActivity($"<[{color}]>tools/call {toolName}");
    }

    private void PopulateToolList()
    {
        if (_toolList == null || _plugin?.Router == null) return;
        _toolList.Clear();

        foreach (var tool in _plugin.Router.Tools)
        {
            var enabled = _plugin.Router.IsToolEnabled(tool.Name);
            _toolList.AddItem($"{(enabled ? "[on]" : "[off]")} [{tool.Category}] {tool.Name}");
        }

        _toolList.ItemSelected += idx =>
        {
            var i = (int)idx;
            var tool = _plugin.Router!.Tools[i];
            var newState = !_plugin.Router.IsToolEnabled(tool.Name);
            _plugin.Router.SetToolEnabled(tool.Name, newState);
            _toolList!.SetItemText(i, $"{(newState ? "[on]" : "[off]")} [{tool.Category}] {tool.Name}");
        };
    }
}
