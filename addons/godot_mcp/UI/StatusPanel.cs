using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro;

/// <summary>
/// Bottom-panel UI for the MCP plugin. Built programmatically (no .tscn).
/// Three sections: connection status, activity log, per-tool enable/disable.
/// </summary>
/// <remarks>
/// Layout is pure Container + size flags (NO manual anchors). EditorDock is a
/// MarginContainer and forces this panel to fill the dock; this panel is itself a
/// MarginContainer that forces its VBox child to fill minus 8/4px padding; the VBox
/// gives the header its natural height and the TabContainer ExpandFill takes the rest.
/// Mixing manual anchors with a Container parent caused the VBox to collapse to its
/// minimum size and truncate the tab content — see git history for the broken version.
/// </remarks>
[Tool]
public partial class StatusPanel : MarginContainer
{
    private Label? _statusLabel;
    private Label? _portLabel;
    private RichTextLabel? _logView;
    private ItemList? _toolList;
    private GodotMcpPlugin? _plugin;
    private VBoxContainer? _content;
    private TabContainer? _tabs;
    private bool _toolsPopulated;
    private const int MaxLogLines = 200;

    public override void _Ready()
    {
        // Padding around the whole panel.
        AddThemeConstantOverride("margin_left", 8);
        AddThemeConstantOverride("margin_right", 8);
        AddThemeConstantOverride("margin_top", 4);
        AddThemeConstantOverride("margin_bottom", 4);

        _content = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        AddChild(_content);

        // Header row
        var header = new HBoxContainer();
        _statusLabel = new Label { Text = "MCP Pro: starting..." };
        header.AddChild(_statusLabel);
        header.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0), SizeFlagsHorizontal = Control.SizeFlags.Expand });
        _portLabel = new Label { Text = "" };
        header.AddChild(_portLabel);
        _content.AddChild(header);

        _tabs = new TabContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _content.AddChild(_tabs);

        _logView = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _tabs.AddChild(_logView);
        _tabs.SetTabTitle(0, "Activity");

        _toolList = new ItemList
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _tabs.AddChild(_toolList);
        _tabs.SetTabTitle(1, "Tools");

        // Subscribe once here; PopulateToolList only refills the items.
        _toolList.ItemSelected += OnToolItemSelected;
    }

    public void Bind(GodotMcpPlugin plugin)
    {
        _plugin = plugin;
        var router = plugin.Router;
        if (router == null) return;
        router.ToolExecuted += OnToolExecuted;
        // The tool list is populated from _Process once the router has registered its tools:
        // CommandRouter fills Tools in its _Ready(), which runs a frame after this Bind().
    }

    public override void _Process(double delta)
    {
        if (!_toolsPopulated && _plugin?.Router != null && _plugin.Router.Tools.Count > 0)
        {
            _toolsPopulated = true;
            PopulateToolList();
        }
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
        LogActivity($"[color={color}]tools/call {toolName}[/color]");
    }

    private void OnToolItemSelected(long idx)
    {
        if (_toolList == null || _plugin?.Router == null) return;
        var i = (int)idx;
        if (i < 0 || i >= _plugin.Router.Tools.Count) return;
        var tool = _plugin.Router.Tools[i];
        var newState = !_plugin.Router.IsToolEnabled(tool.Name);
        _plugin.Router.SetToolEnabled(tool.Name, newState);
        _toolList.SetItemText(i, $"{(newState ? "[on]" : "[off]")} [{tool.Category}] {tool.Name}");
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
    }
}
