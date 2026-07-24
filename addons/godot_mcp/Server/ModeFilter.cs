using System.Collections.Generic;

namespace GodotMcpPro.Server;

/// <summary>
/// Tool-mode filter. Defines which categories/tools are exposed under each
/// mode flag (--full, --3d, --lite, --minimal) mirroring the GDScript v1.14+
/// mode catalog. The router uses this to prune tools/list responses.
/// </summary>
public static class ModeFilter
{
    public enum Mode { Full, ThreeD, Lite, Minimal }

    private static readonly HashSet<string> MinimalCategories = new()
    {
        "project", "scene", "node", "script", "editor", "runtime",
    };

    private static readonly HashSet<string> LiteCategories = new()
    {
        "project", "scene", "node", "script", "editor", "input", "input_map",
        "runtime", "resource", "shader", "batch", "analysis",
    };

    private static readonly HashSet<string> ThreeDExcludeCategories = new()
    {
        "theme", "tilemap", "animation_tree", "test", "android",
    };

    /// <summary>Returns true if the given tool should be exposed under the mode.</summary>
    public static bool IsToolVisible(Mode mode, string category, string toolName, bool destructive)
    {
        return mode switch
        {
            Mode.Full => true,
            Mode.Minimal => MinimalCategories.Contains(category) && !destructive,
            Mode.Lite => LiteCategories.Contains(category),
            Mode.ThreeD => !ThreeDExcludeCategories.Contains(category),
            _ => true,
        };
    }

    public static Mode Parse(string? flag)
    {
        if (string.IsNullOrEmpty(flag)) return Mode.Full;
        return flag switch
        {
            "3d" or "--3d" => Mode.ThreeD,
            "lite" or "--lite" => Mode.Lite,
            "minimal" or "--minimal" => Mode.Minimal,
            _ => Mode.Full,
        };
    }
}
