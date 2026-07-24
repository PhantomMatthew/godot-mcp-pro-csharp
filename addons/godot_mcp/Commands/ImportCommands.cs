using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Import pipeline tools: inspect and modify .import sidecar files
/// ([remap] / [params] sections, ConfigFile format) and trigger reimports
/// through the editor's resource filesystem.
/// </summary>
[Tool]
public partial class ImportCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "get_import_settings",
            Description = "Read the .import sidecar file for an asset. Returns all key-value pairs " +
                "from the [remap] section (importer type, destination path, MIME) and the [params] section (import parameters).",
            Category = "import",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the imported asset (e.g. res://textures/icon.png)."),
            }, "path"),
            Handler = p => Task.FromResult(GetImportSettings(p)),
        },
        new()
        {
            Name = "set_import_settings",
            Description = "Write key-value pairs into the [params] section of an asset's .import file, " +
                "then trigger a reimport of that asset.",
            Category = "import",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the imported asset."),
                ["settings"] = Schema.Obj("Key-value pairs to write into [params] (e.g. {\"compress/mode\": 2, \"process/normal_map_invert_y\": true})."),
            }, "path", "settings"),
            Handler = p => Task.FromResult(SetImportSettings(p)),
        },
        new()
        {
            Name = "reimport_assets",
            Description = "Trigger a reimport of one or more res:// asset paths via the editor's resource filesystem, " +
                "or run a full filesystem scan.",
            Category = "import",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["paths"] = Schema.Arr("res:// asset paths to reimport.", Schema.Str("res:// path")),
                ["scan"] = Schema.Bool("When true, run a full filesystem Scan() instead of a targeted reimport.", false),
            }, "paths"),
            Handler = p => Task.FromResult(ReimportAssets(p)),
        },
    };

    // ── get_import_settings ──────────────────────────────────────────────────

    private static Dictionary GetImportSettings(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        var normalized = NormalizeProjectPath(path);
        var importPath = normalized + ".import";
        if (!Godot.FileAccess.FileExists(importPath))
            return ErrorNotFound($"Import file '{importPath}'",
                "The asset may not have been imported yet. Open the project in the editor once or call reimport_assets first.");

        var cfg = new ConfigFile();
        var loadErr = cfg.Load(importPath);
        if (loadErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to load '{importPath}': {loadErr}");

        return Success(new Dictionary
        {
            ["path"] = normalized,
            ["import_file"] = importPath,
            ["remap"] = ReadSection(cfg, "remap"),
            ["params"] = ReadSection(cfg, "params"),
        });
    }

    private static Dictionary ReadSection(ConfigFile cfg, string section)
    {
        var result = new Dictionary();
        if (!cfg.HasSection(section))
            return result;
        foreach (var key in cfg.GetSectionKeys(section))
            result[key] = cfg.GetValue(section, key);
        return result;
    }

    // ── set_import_settings ──────────────────────────────────────────────────

    private static Dictionary SetImportSettings(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        var normalized = NormalizeProjectPath(path);
        var importPath = normalized + ".import";
        if (!Godot.FileAccess.FileExists(importPath))
            return ErrorNotFound($"Import file '{importPath}'",
                "The asset may not have been imported yet. Call reimport_assets first.");

        var settings = OptionalDict(@params, "settings");
        if (settings == null || settings.Count == 0)
            return ErrorInvalidParams("Missing required parameter: settings (non-empty Dictionary)");

        var cfg = new ConfigFile();
        var loadErr = cfg.Load(importPath);
        if (loadErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to load '{importPath}': {loadErr}");

        var applied = new Godot.Collections.Array();
        foreach (var keyVariant in settings.Keys)
        {
            cfg.SetValue("params", keyVariant.AsString(), settings[keyVariant]);
            applied.Add(keyVariant.AsString());
        }

        var saveErr = cfg.Save(importPath);
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save '{importPath}': {saveErr}");

        EditorInterface.Singleton.GetResourceFilesystem().ReimportFiles(new[] { normalized });

        return Success(new Dictionary
        {
            ["path"] = normalized,
            ["import_file"] = importPath,
            ["updated_keys"] = applied,
            ["reimported"] = true,
        });
    }

    // ── reimport_assets ──────────────────────────────────────────────────────

    private static Dictionary ReimportAssets(Dictionary @params)
    {
        var pathsParam = OptionalArray(@params, "paths");
        if (pathsParam == null || pathsParam.Count == 0)
            return ErrorInvalidParams("Missing required parameter: paths (non-empty Array of res:// paths)");

        var scan = OptionalBool(@params, "scan", false);

        var paths = new List<string>();
        foreach (var pathVariant in pathsParam)
        {
            if (pathVariant.VariantType != Variant.Type.String || string.IsNullOrEmpty(pathVariant.AsString()))
                return ErrorInvalidParams("Each entry in paths must be a non-empty string");
            paths.Add(NormalizeProjectPath(pathVariant.AsString()));
        }

        var fs = EditorInterface.Singleton.GetResourceFilesystem();
        if (scan)
        {
            fs.Scan();
            return Success(new Dictionary
            {
                ["scan"] = true,
                ["message"] = "Full filesystem scan triggered",
            });
        }

        fs.ReimportFiles(paths.ToArray());

        var reimported = new Godot.Collections.Array();
        foreach (var p in paths)
            reimported.Add(p);

        return Success(new Dictionary
        {
            ["reimported"] = reimported,
            ["count"] = reimported.Count,
        });
    }
}
