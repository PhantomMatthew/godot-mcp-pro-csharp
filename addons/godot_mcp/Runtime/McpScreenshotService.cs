using Godot;

namespace GodotMcpPro.Runtime;

/// <summary>
/// Autoload injected by Godot MCP Pro CSharp plugin at runtime.
/// Monitors for screenshot requests from the editor and captures the game viewport.
/// Faithful port of addons/godot_mcp/mcp_screenshot_service.gd.
/// </summary>
public partial class McpScreenshotService : Node
{
    private const string RequestPath = "user://mcp_screenshot_request";
    private const string ScreenshotPath = "user://mcp_screenshot.png";

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Process(double delta)
    {
        if (Godot.FileAccess.FileExists(RequestPath))
            TakeScreenshot();
    }

    private async void TakeScreenshot()
    {
        // Delete request file immediately to avoid re-triggering
        DirAccess.RemoveAbsolute(RequestPath);

        // Wait one frame so the viewport has a fully rendered image
        // process_mode = Always so the timer ticks even when tree is paused
        await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);

        var viewport = GetViewport();
        if (viewport == null)
            return;

        var image = viewport.GetTexture().GetImage();
        if (image == null)
            return;

        image.SavePng(ScreenshotPath);
    }
}
