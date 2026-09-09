using Godot;
using GodotMcpPro.Server;

namespace GodotMcpPro;

/// <summary>
/// Godot MCP Pro CSharp — EditorPlugin entry point.
/// Hosts a self-contained MCP server (Streamable HTTP) inside the editor and
/// injects the runtime autoload services used for live-game inspection.
/// Port of addons/godot_mcp/plugin.gd.
/// </summary>
[Tool]
public partial class GodotMcpPlugin : EditorPlugin
{
    private const string DebuggerContinueFlag = "user://mcp_debugger_continue";

    private static readonly (string Name, string Path)[] RuntimeAutoloads =
    {
        ("MCPScreenshot", "res://addons/godot_mcp/Runtime/McpScreenshotService.cs"),
        ("MCPInputService", "res://addons/godot_mcp/Runtime/McpInputService.cs"),
        ("MCPGameInspector", "res://addons/godot_mcp/Runtime/McpGameInspectorService.cs"),
    };

    private CommandRouter? _router;
    private McpProtocolHandler? _protocolHandler;
    private StreamableHttpServer? _httpServer;
    private StatusPanel? _statusPanel;
    private EditorDock? _editorDock;
    private readonly List<string> _injectedAutoloads = new();

    /// <summary>Toggled by the set_auto_dismiss tool. When enabled, blocking editor dialogs are auto-accepted.</summary>
    public bool AutoDismissDialogs { get; set; }

    public override void _EnterTree()
    {
        _router = new CommandRouter { Plugin = this };
        AddChild(_router);

        _protocolHandler = new McpProtocolHandler(_router);
        _httpServer = new StreamableHttpServer(_protocolHandler);
        _httpServer.RequestLogged += msg =>
        {
            if (_statusPanel != null && IsInstanceValid(_statusPanel))
                _statusPanel.CallDeferred(nameof(StatusPanel.LogActivity), msg);
        };
        _httpServer.ListenerRestarted += () =>
        {
            if (_statusPanel != null && IsInstanceValid(_statusPanel))
                _statusPanel.UpdateStatus(_httpServer.Port, _httpServer.IsRunning);
        };
        _httpServer.Start();

        InjectAutoloads();
        SeedDefaultScriptLanguage();

        _statusPanel = new StatusPanel();
        // EditorDock (a MarginContainer) is the 4.7 replacement for the obsolete
        // AddControlToBottomPanel, which failed to stretch the control to fill the dock.
        _editorDock = new EditorDock
        {
            Title = "Godot MCP Pro CSharp",
            DefaultSlot = EditorDock.DockSlot.Bottom,
            Closable = false,
        };
        _editorDock.AddChild(_statusPanel);
        AddDock(_editorDock);
        _statusPanel.Bind(this);
        _statusPanel.UpdateStatus(_httpServer.Port, _httpServer.IsRunning);

        GD.Print($"[MCP] Godot MCP Pro CSharp v{McpProtocolHandler.ServerVersion} initialized on port {_httpServer.Port}");
    }

    public override void _ExitTree()
    {
        if (_editorDock != null && IsInstanceValid(_editorDock))
        {
            RemoveDock(_editorDock);
            _editorDock.QueueFree();
            _editorDock = null;
        }
        _statusPanel = null;

        _httpServer?.Stop();
        _httpServer = null;
        _protocolHandler = null;

        RemoveInjectedAutoloads();

        if (_router != null)
        {
            RemoveChild(_router);
            _router.QueueFree();
            _router = null;
        }
    }

    public override void _Process(double delta)
    {
        _httpServer?.ProcessPending();
        CheckDebuggerContinueFlag();
        if (AutoDismissDialogs)
            DismissBlockingDialogs();
    }

    public CommandRouter? Router => _router;

    // ── Autoload injection ─────────────────────────────────────────────────────
    // Only autoloads added by this session are removed on unload, so
    // project-owned autoloads with the same names survive plugin toggling.

    private void InjectAutoloads()
    {
        foreach (var (name, path) in RuntimeAutoloads)
        {
            var settingKey = $"autoload/{name}";
            if (ProjectSettings.HasSetting(settingKey))
                continue; // project already defines it
            if (!Godot.FileAccess.FileExists(path))
                continue; // runtime service not shipped yet
            ProjectSettings.SetSetting(settingKey, $"*{path}");
            _injectedAutoloads.Add(settingKey);
        }
        if (_injectedAutoloads.Count > 0)
            ProjectSettings.Save();
    }

    private void RemoveInjectedAutoloads()
    {
        foreach (var settingKey in _injectedAutoloads)
        {
            if (ProjectSettings.HasSetting(settingKey))
                ProjectSettings.SetSetting(settingKey, default);
        }
        if (_injectedAutoloads.Count > 0)
            ProjectSettings.Save();
        _injectedAutoloads.Clear();
    }

    // ── Default script language ────────────────────────────────────────────────
    // Godot's Script Create dialog (Attach Script / New Script) defaults to the
    // project's *last used* language, stored in per-project editor metadata
    // (.godot/editor/project_metadata.cfg → [script_setup] last_selected_language).
    // The engine only writes that key after a script is actually created through
    // the dialog, so a project where that never happened falls back to GDScript
    // on every editor start / "Reload Current Project". Seed it here so the
    // dialog opens with C# preselected from the very first use. This only
    // affects the initial dropdown selection — plugin loading/reloading is
    // determined solely by the script path in plugin.cfg.
    private static void SeedDefaultScriptLanguage()
    {
        EditorInterface.Singleton.GetEditorSettings()
            .SetProjectMetadata("script_setup", "last_selected_language", "C#");
    }

    // ── Debugger auto-continue ────────────────────────────────────────────────
    // The in-game inspector drops a flag file when it detects the game is stuck
    // paused on a runtime error; the editor side presses Continue.

    private void CheckDebuggerContinueFlag()
    {
        if (!Godot.FileAccess.FileExists(DebuggerContinueFlag))
            return;
        DirAccess.RemoveAbsolute(DebuggerContinueFlag);
        Commands.BaseCommand.TryDebuggerContinue();
    }

    // ── Dialog auto-dismiss (opt-in via set_auto_dismiss) ────────────────────

    private void DismissBlockingDialogs()
    {
        var baseControl = EditorInterface.Singleton.GetBaseControl();
        if (baseControl == null)
            return;
        foreach (var child in baseControl.GetChildren())
        {
            if (child is AcceptDialog { Visible: true } dialog && child is not ConfirmationDialog)
            {
                GD.Print($"[MCP] Auto-dismissing dialog: {dialog.Title}");
                dialog.Hide();
            }
        }
    }
}
