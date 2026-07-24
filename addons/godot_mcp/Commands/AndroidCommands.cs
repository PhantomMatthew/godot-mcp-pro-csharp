using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Android export/deploy tools: list adb devices, inspect the configured Android
/// export preset, and export+install+launch an APK on a connected device/emulator.
/// Faithful port of addons/godot_mcp/commands/android_commands.gd.
/// </summary>
[Tool]
public partial class AndroidCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "list_android_devices",
            Description = "List Android devices/emulators currently visible to adb, along with their " +
                "state (device/offline/unauthorized) and any product/model/device metadata adb reports.",
            InputSchema = Schema.Object(),
            Category = "android",
            Handler = p => Task.FromResult(ListAndroidDevices(p)),
        },
        new()
        {
            Name = "get_android_preset_info",
            Description = "Read metadata for an Android export preset from export_presets.cfg: index, name, " +
                "platform, runnable flag, export path, and package name. Selects by name or index if given, " +
                "otherwise the first Android preset found.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["preset_name"] = Schema.Str("Exact export preset name to look up.", ""),
                ["preset_index"] = Schema.Int("Preset index (0-based) to look up when preset_name is not given.", -1),
            }),
            Category = "android",
            Handler = p => Task.FromResult(GetAndroidPresetInfo(p)),
        },
        new()
        {
            Name = "deploy_to_android",
            Description = "Export an APK via the Godot CLI for the given Android preset (unless skip_export=true), " +
                "install it on a device with 'adb install -r', and optionally launch its main activity via " +
                "'adb shell monkey'. Returns a step-by-step log of each command run.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["preset_name"] = Schema.Str("Exact export preset name to deploy.", ""),
                ["preset_index"] = Schema.Int("Preset index (0-based) to deploy when preset_name is not given.", -1),
                ["device_serial"] = Schema.Str("Target device serial for 'adb -s <serial>'. Defaults to the only/default device.", ""),
                ["debug"] = Schema.Bool("Export a debug build (--export-debug) instead of release (--export-release).", true),
                ["launch"] = Schema.Bool("Launch the app's main activity after install via 'adb shell monkey'.", true),
                ["skip_export"] = Schema.Bool("Skip the export step and install the APK already at the preset's export path.", false),
            }),
            Category = "android",
            Destructive = true,
            Handler = p => Task.FromResult(DeployToAndroid(p)),
        },
    };

    // ── adb / process helpers ────────────────────────────────────────────────

    private readonly record struct RunResult(int ExitCode, string Stdout);

    /// <summary>Resolve adb path from editor settings or PATH fallback.</summary>
    private static string ResolveAdbPath()
    {
        var editorSettings = GetEditor().GetEditorSettings();
        // Godot exposes this under export/android/adb (may be stored as an absolute path).
        var configured = "";
        if (editorSettings.HasSetting("export/android/adb"))
            configured = editorSettings.GetSetting("export/android/adb").AsString();
        if (!string.IsNullOrEmpty(configured) && Godot.FileAccess.FileExists(configured))
            return configured;
        // Fallback: assume adb is on PATH. OS.Execute will resolve it at call time.
        return "adb";
    }

    private static RunResult Run(string cmd, string[] args)
    {
        var output = new Godot.Collections.Array();
        var exitCode = OS.Execute(cmd, args, output, true);
        var stdout = "";
        if (output.Count > 0)
            stdout = output[0].AsString();
        return new RunResult(exitCode, stdout);
    }

    private static Godot.Collections.Array StringsToArray(string[] values)
    {
        var arr = new Godot.Collections.Array();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    // ── list_android_devices ────────────────────────────────────────────────

    private static Dictionary ListAndroidDevices(Dictionary @params)
    {
        var adb = ResolveAdbPath();
        var result = Run(adb, new[] { "devices", "-l" });
        if (result.ExitCode != 0)
            return Error(CodeGeneric,
                $"adb failed (exit {result.ExitCode}). Install Android platform-tools or set Editor Settings > Export > Android > Adb.",
                new Dictionary { ["adb_path"] = adb, ["output"] = result.Stdout });

        // Parse `adb devices -l` output:
        //   List of devices attached
        //   R58M12345  device usb:3-1 product:foo model:Pixel_5 device:redfin
        var devices = new Godot.Collections.Array();
        var lines = result.Stdout.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.StripEdges();
            if (string.IsNullOrEmpty(line) || line.StartsWith("List of devices") || line.StartsWith("* daemon"))
                continue;
            var parts = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            var dev = new Dictionary { ["serial"] = parts[0], ["state"] = parts[1] };
            for (var i = 2; i < parts.Length; i++)
            {
                var kv = parts[i];
                var eq = kv.IndexOf(':');
                if (eq > 0)
                    dev[kv[..eq]] = kv[(eq + 1)..];
            }
            devices.Add(dev);
        }

        return Success(new Dictionary { ["devices"] = devices, ["count"] = devices.Count, ["adb_path"] = adb });
    }

    // ── export_presets.cfg parsing ──────────────────────────────────────────

    /// <summary>Find an Android preset in export_presets.cfg. Returns the preset dict or empty.</summary>
    private static Dictionary FindAndroidPreset(string presetName, int presetIndex)
    {
        var presetsPath = "res://export_presets.cfg";
        if (!Godot.FileAccess.FileExists(presetsPath))
            return new Dictionary();
        var cfg = new ConfigFile();
        if (cfg.Load(presetsPath) != Godot.Error.Ok)
            return new Dictionary();

        var idx = 0;
        while (cfg.HasSection($"preset.{idx}"))
        {
            var section = $"preset.{idx}";
            var platform = cfg.GetValue(section, "platform", "").AsString();
            var name = cfg.GetValue(section, "name", "").AsString();
            bool matches;
            if (!string.IsNullOrEmpty(presetName))
                matches = name == presetName;
            else if (presetIndex >= 0)
                matches = idx == presetIndex;
            else
                // No filter: pick the first Android preset.
                matches = platform == "Android";
            if (matches)
            {
                var optionsSection = $"preset.{idx}.options";
                var packageName = "";
                if (cfg.HasSection(optionsSection))
                    packageName = cfg.GetValue(optionsSection, "package/unique_name", "").AsString();
                return new Dictionary
                {
                    ["index"] = idx,
                    ["name"] = name,
                    ["platform"] = platform,
                    ["runnable"] = cfg.GetValue(section, "runnable", false).AsBool(),
                    ["export_path"] = cfg.GetValue(section, "export_path", "").AsString(),
                    ["package_name"] = packageName,
                };
            }
            idx++;
        }
        return new Dictionary();
    }

    // ── get_android_preset_info ─────────────────────────────────────────────

    private static Dictionary GetAndroidPresetInfo(Dictionary @params)
    {
        var presetName = OptionalString(@params, "preset_name", "");
        var presetIndex = OptionalInt(@params, "preset_index", -1);
        var preset = FindAndroidPreset(presetName, presetIndex);
        if (preset.Count == 0)
            return ErrorNotFound("Android export preset", "Configure an Android preset in Project > Export first.");
        if (preset["platform"].AsString() != "Android")
            return Error(CodeGeneric,
                $"Preset '{preset["name"].AsString()}' is not an Android preset (platform={preset["platform"].AsString()})");
        return Success(preset);
    }

    // ── deploy_to_android ────────────────────────────────────────────────────

    private static Dictionary DeployToAndroid(Dictionary @params)
    {
        var presetName = OptionalString(@params, "preset_name", "");
        var presetIndex = OptionalInt(@params, "preset_index", -1);
        var deviceSerial = OptionalString(@params, "device_serial", "");
        var debug = OptionalBool(@params, "debug", true);
        var launch = OptionalBool(@params, "launch", true);
        var skipExport = OptionalBool(@params, "skip_export", false);

        var preset = FindAndroidPreset(presetName, presetIndex);
        if (preset.Count == 0)
            return ErrorNotFound("Android export preset", "Configure an Android preset in Project > Export first.");
        if (preset["platform"].AsString() != "Android")
            return Error(CodeGeneric, $"Preset '{preset["name"].AsString()}' is not an Android preset");

        var exportPathRes = preset["export_path"].AsString();
        if (string.IsNullOrEmpty(exportPathRes))
            return Error(CodeGeneric, $"Export path not configured for preset '{preset["name"].AsString()}'");
        var exportPathAbs = exportPathRes.StartsWith("res://")
            ? ProjectSettings.GlobalizePath(exportPathRes)
            : exportPathRes;

        var steps = new Godot.Collections.Array();

        // Step 1: Export APK via Godot CLI (unless caller already has an APK).
        if (!skipExport)
        {
            var godotBin = OS.GetExecutablePath();
            var projectDir = ProjectSettings.GlobalizePath("res://");
            var exportFlag = debug ? "--export-debug" : "--export-release";
            var exportArgs = new[]
            {
                "--headless", "--path", projectDir, exportFlag, preset["name"].AsString(), exportPathAbs,
            };
            var exportResult = Run(godotBin, exportArgs);
            steps.Add(new Dictionary
            {
                ["step"] = "export",
                ["command"] = godotBin,
                ["args"] = StringsToArray(exportArgs),
                ["exit_code"] = exportResult.ExitCode,
            });
            if (exportResult.ExitCode != 0)
                return Error(CodeGeneric,
                    $"Godot export failed (exit {exportResult.ExitCode}). See stdout.",
                    new Dictionary { ["steps"] = steps, ["stdout"] = exportResult.Stdout });
        }

        if (!Godot.FileAccess.FileExists(exportPathAbs))
            return Error(CodeGeneric, $"APK not found at {exportPathAbs} after export", new Dictionary { ["steps"] = steps });

        // Step 2: adb install -r
        var adb = ResolveAdbPath();
        var installArgsList = new List<string>();
        if (!string.IsNullOrEmpty(deviceSerial))
        {
            installArgsList.Add("-s");
            installArgsList.Add(deviceSerial);
        }
        installArgsList.Add("install");
        installArgsList.Add("-r");
        installArgsList.Add(exportPathAbs);
        var installArgs = installArgsList.ToArray();
        var installResult = Run(adb, installArgs);
        steps.Add(new Dictionary
        {
            ["step"] = "install",
            ["command"] = adb,
            ["args"] = StringsToArray(installArgs),
            ["exit_code"] = installResult.ExitCode,
            ["stdout"] = installResult.Stdout,
        });
        if (installResult.ExitCode != 0)
            return Error(CodeGeneric, $"adb install failed (exit {installResult.ExitCode})", new Dictionary { ["steps"] = steps });

        // Step 3: adb shell am start (optional)
        if (launch)
        {
            var packageName = preset["package_name"].AsString();
            if (string.IsNullOrEmpty(packageName))
            {
                steps.Add(new Dictionary
                {
                    ["step"] = "launch",
                    ["skipped"] = true,
                    ["reason"] = "package_name not found in preset",
                });
            }
            else
            {
                var launchArgsList = new List<string>();
                if (!string.IsNullOrEmpty(deviceSerial))
                {
                    launchArgsList.Add("-s");
                    launchArgsList.Add(deviceSerial);
                }
                launchArgsList.Add("shell");
                launchArgsList.Add("monkey");
                launchArgsList.Add("-p");
                launchArgsList.Add(packageName);
                launchArgsList.Add("-c");
                launchArgsList.Add("android.intent.category.LAUNCHER");
                launchArgsList.Add("1");
                var launchArgs = launchArgsList.ToArray();
                var launchResult = Run(adb, launchArgs);
                steps.Add(new Dictionary
                {
                    ["step"] = "launch",
                    ["command"] = adb,
                    ["args"] = StringsToArray(launchArgs),
                    ["exit_code"] = launchResult.ExitCode,
                    ["stdout"] = launchResult.Stdout,
                });
            }
        }

        return Success(new Dictionary
        {
            ["preset"] = preset["name"].AsString(),
            ["apk_path"] = exportPathAbs,
            ["device"] = !string.IsNullOrEmpty(deviceSerial) ? deviceSerial : "(default)",
            ["package_name"] = preset["package_name"].AsString(),
            ["steps"] = steps,
        });
    }
}
