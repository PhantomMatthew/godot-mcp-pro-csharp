using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Faithful port of addons/godot_mcp/commands/export_commands.gd.
/// Project export tools: list export presets from export_presets.cfg, build
/// the headless command line for exporting a preset, and report general
/// export-related project info (templates, executable path).
/// </summary>
[Tool]
public partial class ExportCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "list_export_presets",
            Description = "List the export presets configured in export_presets.cfg (name, platform, runnable, export path).",
            Category = "export",
            InputSchema = Schema.Object(),
            Handler = p => Task.FromResult(ListExportPresets(p)),
        },
        new()
        {
            Name = "export_project",
            Description = "Resolve an export preset (by name or index) and return the headless command line to export it. " +
                "Direct export from an editor plugin is not supported in Godot 4.",
            Category = "export",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["preset_index"] = Schema.Int("Index of the export preset to use (ignored when preset_name is given).", -1),
                ["preset_name"] = Schema.Str("Name of the export preset to use."),
                ["debug"] = Schema.Bool("Export a debug build (--export-debug) instead of release.", true),
            }),
            Handler = p => Task.FromResult(ExportProject(p)),
        },
        new()
        {
            Name = "get_export_info",
            Description = "Report general export-related project info: presence of export_presets.cfg, Godot executable path, project path, and export templates directory.",
            Category = "export",
            InputSchema = Schema.Object(),
            Handler = p => Task.FromResult(GetExportInfo(p)),
        },
    };

    // ── list_export_presets ─────────────────────────────────────────────────────

    private static Dictionary ListExportPresets(Dictionary @params)
    {
        // Read export_presets.cfg
        const string presetsPath = "res://export_presets.cfg";
        if (!Godot.FileAccess.FileExists(presetsPath))
            return Success(new Dictionary
            {
                ["presets"] = new Godot.Collections.Array(),
                ["count"] = 0,
                ["message"] = "No export_presets.cfg found",
            });

        var cfg = new ConfigFile();
        var err = cfg.Load(presetsPath);
        if (err != Godot.Error.Ok)
            return ErrorInternal($"Failed to read export_presets.cfg: {err}");

        var presets = new Godot.Collections.Array();
        var idx = 0;
        while (cfg.HasSection($"preset.{idx}"))
        {
            var section = $"preset.{idx}";
            presets.Add(new Dictionary
            {
                ["index"] = idx,
                ["name"] = cfg.GetValue(section, "name", ""),
                ["platform"] = cfg.GetValue(section, "platform", ""),
                ["runnable"] = cfg.GetValue(section, "runnable", false),
                ["export_path"] = cfg.GetValue(section, "export_path", ""),
            });
            idx++;
        }

        return Success(new Dictionary { ["presets"] = presets, ["count"] = presets.Count });
    }

    // ── export_project ──────────────────────────────────────────────────────────

    private static Dictionary ExportProject(Dictionary @params)
    {
        var presetIndex = OptionalInt(@params, "preset_index", -1);
        var presetName = OptionalString(@params, "preset_name", "");
        var debug = OptionalBool(@params, "debug", true);

        // Find preset
        const string presetsPath = "res://export_presets.cfg";
        if (!Godot.FileAccess.FileExists(presetsPath))
            return Error(CodeGeneric, "No export_presets.cfg found. Configure exports in Project > Export first.");

        var cfg = new ConfigFile();
        var err = cfg.Load(presetsPath);
        if (err != Godot.Error.Ok)
            return ErrorInternal("Failed to read export_presets.cfg");

        // Find by name or index
        var targetSection = "";
        var targetName = "";
        var targetPath = "";

        if (!string.IsNullOrEmpty(presetName))
        {
            var idx = 0;
            while (cfg.HasSection($"preset.{idx}"))
            {
                var section = $"preset.{idx}";
                if (cfg.GetValue(section, "name", "").AsString() == presetName)
                {
                    targetSection = section;
                    targetName = presetName;
                    targetPath = cfg.GetValue(section, "export_path", "").AsString();
                    break;
                }
                idx++;
            }
        }
        else if (presetIndex >= 0)
        {
            var section = $"preset.{presetIndex}";
            if (cfg.HasSection(section))
            {
                targetSection = section;
                targetName = cfg.GetValue(section, "name", "").AsString();
                targetPath = cfg.GetValue(section, "export_path", "").AsString();
            }
        }

        if (string.IsNullOrEmpty(targetSection))
            return ErrorNotFound("Export preset");

        if (string.IsNullOrEmpty(targetPath))
            return Error(CodeGeneric, $"Export path not configured for preset '{targetName}'");

        // Use EditorExportPlatform via command line
        // We can't directly call export from the plugin, so we return the command to run
        var godotPath = OS.GetExecutablePath();
        var projectPath = ProjectSettings.GlobalizePath("res://");
        var exportPath = targetPath.StartsWith("res://") ? ProjectSettings.GlobalizePath(targetPath) : targetPath;

        var flag = debug ? "--export-debug" : "--export-release";
        var command = $"\"{godotPath}\" --headless --path \"{projectPath}\" {flag} \"{targetName}\"";

        return Success(new Dictionary
        {
            ["preset"] = targetName,
            ["export_path"] = exportPath,
            ["debug"] = debug,
            ["command"] = command,
            ["message"] = "Run the command above to export. Direct export from editor plugin is not supported in Godot 4.",
        });
    }

    // ── get_export_info ─────────────────────────────────────────────────────────

    private static Dictionary GetExportInfo(Dictionary @params)
    {
        // General export-related project info
        var info = new Dictionary();

        // Check if export_presets.cfg exists
        info["has_export_presets"] = Godot.FileAccess.FileExists("res://export_presets.cfg");

        // Get Godot executable path (useful for command-line exports)
        info["godot_executable"] = OS.GetExecutablePath();
        info["project_path"] = ProjectSettings.GlobalizePath("res://");

        // Check for common export templates
        var templatesPath = OS.GetDataDir().PathJoin("export_templates");
        info["templates_dir"] = templatesPath;
        info["templates_installed"] = DirAccess.DirExistsAbsolute(templatesPath);

        return Success(info);
    }
}
