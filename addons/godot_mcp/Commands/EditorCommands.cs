using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Faithful port of addons/godot_mcp/commands/editor_commands.gd.
/// Exposes editor-introspection tools: error/log scraping, screenshots (editor
/// and running game), ad-hoc script execution, plugin/project reload, signal
/// inspection, screenshot diffing, dialog auto-dismiss, and 3D editor camera
/// control.
/// </summary>
[Tool]
public partial class EditorCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "get_editor_errors",
            Description = "Collect recent editor errors/warnings from the Output panel, script compile markers, GDScript analyzer panels, and the debugger Errors tab.",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["max_lines"] = Schema.Int("Maximum number of Output panel lines to scan.", 50),
            }),
            Handler = p => Task.FromResult(GetEditorErrors(p)),
        },
        new()
        {
            Name = "get_output_log",
            Description = "Read recent lines from the editor's Output panel (or the log file as a fallback), optionally filtered by substring.",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["max_lines"] = Schema.Int("Maximum number of lines to return.", 100),
                ["filter"] = Schema.Str("Only return lines containing this substring.", ""),
            }),
            Handler = p => Task.FromResult(GetOutputLog(p)),
        },
        new()
        {
            Name = "get_editor_screenshot",
            Description = "Capture a screenshot of the editor's main viewport, returned as base64 PNG or saved to a file.",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["save_path"] = Schema.Str("Optional res://, user://, or absolute path to save the PNG instead of returning base64."),
            }),
            Handler = p => Task.FromResult(GetEditorScreenshot(p)),
        },
        new()
        {
            Name = "get_game_screenshot",
            Description = "Capture a screenshot of the currently running game via file IPC, returned as base64 PNG or saved to a file.",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["save_path"] = Schema.Str("Optional res://, user://, or absolute path to save the PNG instead of returning base64."),
            }),
            Handler = GetGameScreenshotAsync,
        },
        new()
        {
            Name = "execute_editor_script",
            Description = "Compile and run an ad-hoc GDScript snippet inside the editor. Direct file/resource write APIs are blocked unless allow_unsafe_editor_io is set.",
            Category = "editor",
            Destructive = true,
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["code"] = Schema.Str("GDScript statements to run inside a generated run() function."),
                ["allow_unsafe_editor_io"] = Schema.Bool("Allow direct file/resource write APIs (ResourceSaver.save, FileAccess write mode, etc.) in the script.", false),
            }, "code"),
            Handler = p => Task.FromResult(ExecuteEditorScript(p)),
        },
        new()
        {
            Name = "clear_output",
            Description = "Print blank lines to visually clear the editor's Output panel.",
            Category = "editor",
            InputSchema = Schema.Object(),
            Handler = p => Task.FromResult(ClearOutput(p)),
        },
        new()
        {
            Name = "reload_plugin",
            Description = "Disable and re-enable the godot_mcp plugin to reload all of its scripts. The MCP connection will briefly drop and auto-reconnect.",
            Category = "editor",
            InputSchema = Schema.Object(),
            Handler = p => Task.FromResult(ReloadPlugin(p)),
        },
        new()
        {
            Name = "reload_project",
            Description = "Rescan the project filesystem to pick up changed/added files and reload changed scripts.",
            Category = "editor",
            InputSchema = Schema.Object(),
            Handler = p => Task.FromResult(ReloadProject(p)),
        },
        new()
        {
            Name = "get_signals",
            Description = "List signals declared on a node in the edited scene, including argument shapes and current connections.",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the node in the edited scene."),
            }, "node_path"),
            Handler = p => Task.FromResult(GetSignals(p)),
        },
        new()
        {
            Name = "compare_screenshots",
            Description = "Pixel-diff two images (given as file paths or base64 PNG data) and return a highlighted diff image plus change statistics.",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["image_a"] = Schema.Str("First image: res://, user:// path, or base64 PNG data."),
                ["image_b"] = Schema.Str("Second image: res://, user:// path, or base64 PNG data."),
                ["threshold"] = Schema.Int("Per-channel (0-255) difference above which a pixel counts as changed.", 10),
            }, "image_a", "image_b"),
            Handler = p => Task.FromResult(CompareScreenshots(p)),
        },
        new()
        {
            Name = "set_auto_dismiss",
            Description = "Enable or disable automatic dismissal of blocking editor dialogs (AcceptDialogs) raised by the editor.",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["enabled"] = Schema.Bool("Whether to auto-dismiss blocking dialogs.", true),
            }),
            Handler = p => Task.FromResult(SetAutoDismiss(p)),
        },
        new()
        {
            Name = "get_editor_camera",
            Description = "Read the position, rotation, and lens settings of the active 3D editor viewport camera.",
            Category = "editor",
            InputSchema = Schema.Object(),
            Handler = p => Task.FromResult(GetEditorCamera(p)),
        },
        new()
        {
            Name = "set_editor_camera",
            Description = "Move/rotate/aim the active 3D editor viewport camera.",
            Category = "editor",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["position"] = Schema.Obj("New camera global position {x,y,z} (partial updates allowed)."),
                ["rotation_degrees"] = Schema.Obj("New camera rotation in degrees {x,y,z} (partial updates allowed)."),
                ["look_at"] = Schema.Obj("Point {x,y,z} for the camera to look at (overrides rotation_degrees)."),
                ["fov"] = Schema.Num("New camera field of view in degrees."),
            }),
            Handler = p => Task.FromResult(SetEditorCamera(p)),
        },
    };

    // ── get_editor_errors ───────────────────────────────────────────────────────

    private static Dictionary GetEditorErrors(Dictionary @params)
    {
        var errors = new Godot.Collections.Array();
        var maxLines = OptionalInt(@params, "max_lines", 50);
        var baseControl = GetEditor().GetBaseControl();

        // 1. Read from the editor's Output panel (EditorLog RichTextLabel).
        var editorLog = baseControl.FindChild("Output", true, false);
        if (editorLog != null)
        {
            var rtl = FindRtl(editorLog);
            if (rtl != null)
            {
                var content = rtl.GetParsedText();
                var lines = content.Split('\n');
                var start = Mathf.Max(0, lines.Length - maxLines);
                for (var i = start; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.Contains("ERROR") || line.Contains("SCRIPT ERROR")
                        || line.Contains("Parse Error") || line.Contains("WARNING"))
                        errors.Add(line.StripEdges());
                }
            }
        }

        // 2. Check the script editor for compile errors (red background lines).
        var scriptErrors = new Godot.Collections.Array();
        var scriptEditor = GetEditor().GetScriptEditor();
        if (scriptEditor != null)
        {
            var currentScript = scriptEditor.GetCurrentScript();
            var ce = FindCodeEdit(scriptEditor);
            if (ce != null && currentScript != null)
            {
                var scriptPath = currentScript.ResourcePath;
                for (var i = 0; i < ce.GetLineCount(); i++)
                {
                    var bg = ce.GetLineBackgroundColor(i);
                    if (bg.R > 0.8f && bg.A > 0)
                    {
                        var lineText = ce.GetLine(i).StripEdges();
                        scriptErrors.Add($"COMPILE ERROR: {scriptPath}:{i + 1} - {lineText}");
                    }
                }
            }
        }

        // 3. Read from script editor error/warning panels (GDScript analyzer messages).
        var analyzerErrors = new Godot.Collections.Array();
        if (scriptEditor != null)
        {
            var openEditors = scriptEditor.GetOpenScriptEditors();
            var openScripts = scriptEditor.GetOpenScripts();
            for (var ei = 0; ei < openEditors.Count; ei++)
            {
                var editorNode = openEditors[ei];
                var scriptPath = "";
                if (ei < openScripts.Count && openScripts[ei] != null)
                    scriptPath = openScripts[ei].ResourcePath;

                VSplitContainer? vsplit = null;
                foreach (var c in editorNode.GetChildren())
                {
                    if (c is VSplitContainer vs)
                    {
                        vsplit = vs;
                        break;
                    }
                }
                if (vsplit == null)
                    continue;

                var children = vsplit.GetChildren();
                // child[1] = warnings panel (RichTextLabel)
                if (children.Count > 1 && children[1] is RichTextLabel warningsRtl)
                {
                    var text = warningsRtl.GetParsedText().StripEdges();
                    if (!string.IsNullOrEmpty(text))
                    {
                        foreach (var line in text.Split('\n'))
                        {
                            var stripped = line.StripEdges();
                            if (string.IsNullOrEmpty(stripped) || stripped == "[Ignore]")
                                continue;
                            stripped = stripped.TrimPrefix("[Ignore]");
                            var prefix = string.IsNullOrEmpty(scriptPath) ? "WARNING: " : $"WARNING: {scriptPath}:";
                            analyzerErrors.Add(prefix + stripped);
                        }
                    }
                }
                // child[2] = errors panel (RichTextLabel)
                if (children.Count > 2 && children[2] is RichTextLabel errorsRtl)
                {
                    var text = errorsRtl.GetParsedText().StripEdges();
                    if (!string.IsNullOrEmpty(text))
                    {
                        foreach (var line in text.Split('\n'))
                        {
                            var stripped = line.StripEdges();
                            if (string.IsNullOrEmpty(stripped))
                                continue;
                            var prefix = string.IsNullOrEmpty(scriptPath) ? "SCRIPT ERROR: " : $"SCRIPT ERROR: {scriptPath}:";
                            analyzerErrors.Add(prefix + stripped);
                        }
                    }
                }
            }
        }

        // 4. Read from the debugger Errors tab (runtime errors/warnings).
        var debuggerErrors = new Godot.Collections.Array();
        if (baseControl != null)
        {
            var queue = new Queue<Node>();
            queue.Enqueue(baseControl);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node.GetClass() == "ScriptEditorDebugger")
                {
                    foreach (var child in node.GetChildren())
                    {
                        if (child is TabContainer tabContainer)
                        {
                            for (var tabIdx = 0; tabIdx < tabContainer.GetTabCount(); tabIdx++)
                            {
                                var tabControl = tabContainer.GetTabControl(tabIdx);
                                if (tabControl is VBoxContainer && tabControl.Name.ToString().StartsWith("Errors"))
                                {
                                    foreach (var vchild in tabControl.GetChildren())
                                    {
                                        if (vchild is not Tree tree)
                                            continue;
                                        var rootItem = tree.GetRoot();
                                        if (rootItem == null)
                                            continue;
                                        var item = rootItem.GetFirstChild();
                                        while (item != null)
                                        {
                                            var col0 = item.GetText(0).StripEdges();
                                            var col1 = item.GetText(1).StripEdges();
                                            if (!string.IsNullOrEmpty(col0) || !string.IsNullOrEmpty(col1))
                                            {
                                                var msg = col0;
                                                if (!string.IsNullOrEmpty(col1))
                                                    msg = string.IsNullOrEmpty(msg) ? col1 : msg + " " + col1;
                                                debuggerErrors.Add("DEBUGGER: " + msg);
                                            }
                                            var sub = item.GetFirstChild();
                                            while (sub != null)
                                            {
                                                var sub0 = sub.GetText(0).StripEdges();
                                                var sub1 = sub.GetText(1).StripEdges();
                                                if (!string.IsNullOrEmpty(sub0) || !string.IsNullOrEmpty(sub1))
                                                {
                                                    var subMsg = sub0;
                                                    if (!string.IsNullOrEmpty(sub1))
                                                        subMsg = string.IsNullOrEmpty(subMsg) ? sub1 : subMsg + " " + sub1;
                                                    debuggerErrors.Add("DEBUGGER:   " + subMsg);
                                                }
                                                sub = sub.GetNext();
                                            }
                                            item = item.GetNext();
                                        }
                                    }
                                    goto doneTabs;
                                }
                            }
                            doneTabs:
                            break;
                        }
                    }
                    break;
                }
                foreach (var child in node.GetChildren())
                    queue.Enqueue(child);
            }
        }

        // Fallback: read from log file if Output panel not accessible.
        if (errors.Count == 0 && scriptErrors.Count == 0 && analyzerErrors.Count == 0 && debuggerErrors.Count == 0)
        {
            const string logPath = "user://logs/godot.log";
            if (Godot.FileAccess.FileExists(logPath))
            {
                using var file = Godot.FileAccess.Open(logPath, Godot.FileAccess.ModeFlags.Read);
                if (file != null)
                {
                    var content = file.GetAsText();
                    var lines = content.Split('\n');
                    var start = Mathf.Max(0, lines.Length - maxLines);
                    for (var i = start; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        if (line.Contains("ERROR") || line.Contains("SCRIPT ERROR"))
                            errors.Add(line.StripEdges());
                    }
                }
            }
        }

        foreach (var e in scriptErrors) errors.Add(e);
        foreach (var e in analyzerErrors) errors.Add(e);
        foreach (var e in debuggerErrors) errors.Add(e);

        return Success(new Dictionary { ["errors"] = errors, ["count"] = errors.Count });
    }

    private static CodeEdit? FindCodeEdit(Node node, int depth = 0)
    {
        if (depth > 8)
            return null;
        if (node is CodeEdit ce)
            return ce;
        foreach (var child in node.GetChildren())
        {
            var found = FindCodeEdit(child, depth + 1);
            if (found != null)
                return found;
        }
        return null;
    }

    private static RichTextLabel? FindRtl(Node node, int depth = 0)
    {
        if (depth > 6)
            return null;
        if (node is RichTextLabel rtl)
            return rtl;
        foreach (var child in node.GetChildren())
        {
            var found = FindRtl(child, depth + 1);
            if (found != null)
                return found;
        }
        return null;
    }

    // ── get_output_log ──────────────────────────────────────────────────────────

    private static Dictionary GetOutputLog(Dictionary @params)
    {
        var maxLines = OptionalInt(@params, "max_lines", 100);
        var filter = OptionalString(@params, "filter", "");
        var baseControl = GetEditor().GetBaseControl();

        var editorLog = baseControl.FindChild("Output", true, false);
        if (editorLog == null)
        {
            const string logPath = "user://logs/godot.log";
            if (!Godot.FileAccess.FileExists(logPath))
                return ErrorInternal("Output panel not found and no log file available");
            using var file = Godot.FileAccess.Open(logPath, Godot.FileAccess.ModeFlags.Read);
            if (file == null)
                return ErrorInternal("Cannot read log file");
            var content = file.GetAsText();
            var lines = content.Split('\n');
            var start = Mathf.Max(0, lines.Length - maxLines);
            var outputLines = new Godot.Collections.Array();
            for (var i = start; i < lines.Length; i++)
            {
                var line = lines[i];
                if (filter.Length == 0 || line.Contains(filter))
                    outputLines.Add(line);
            }
            return Success(new Dictionary { ["lines"] = outputLines, ["count"] = outputLines.Count, ["source"] = "log_file" });
        }

        var rtl = FindRtl(editorLog);
        if (rtl == null)
            return ErrorInternal("Could not find RichTextLabel in Output panel");

        var textContent = rtl.GetParsedText();
        var allLines = textContent.Split('\n');
        var startIdx = Mathf.Max(0, allLines.Length - maxLines);
        var lines2 = new Godot.Collections.Array();
        for (var i = startIdx; i < allLines.Length; i++)
        {
            var line = allLines[i];
            if (filter.Length == 0 || line.Contains(filter))
                lines2.Add(line);
        }

        return Success(new Dictionary { ["lines"] = lines2, ["count"] = lines2.Count, ["source"] = "output_panel" });
    }

    // ── get_editor_screenshot ────────────────────────────────────────────────────

    private static Dictionary GetEditorScreenshot(Dictionary @params)
    {
        var baseControl = GetEditor().GetBaseControl();
        if (baseControl == null)
            return ErrorInternal("Could not access editor base control");

        var viewport = baseControl.GetViewport();
        if (viewport == null)
            return ErrorInternal("Could not access editor viewport");

        var texture = viewport.GetTexture();
        if (texture == null)
            return ErrorInternal("Could not get viewport texture");

        var image = texture.GetImage();
        if (image == null)
            return ErrorInternal("Could not get image from viewport");

        var savePath = OptionalString(@params, "save_path", "");
        if (savePath.Length > 0)
        {
            var absPath = ResolveSavePath(savePath);
            var err = image.SavePng(absPath);
            if (err != Godot.Error.Ok)
                return ErrorInternal($"Failed to save screenshot: {err}");
            return Success(new Dictionary
            {
                ["saved_path"] = savePath,
                ["width"] = image.GetWidth(),
                ["height"] = image.GetHeight(),
                ["format"] = "png",
            });
        }

        var pngBuffer = image.SavePngToBuffer();
        var base64 = Marshalls.RawToBase64(pngBuffer);

        return Success(new Dictionary
        {
            ["image_base64"] = base64,
            ["width"] = image.GetWidth(),
            ["height"] = image.GetHeight(),
            ["format"] = "png",
        });
    }

    private static string ResolveSavePath(string path)
    {
        if (path.StartsWith("res://") || path.StartsWith("user://"))
            return ProjectSettings.GlobalizePath(path);
        return path;
    }

    // ── get_game_screenshot ──────────────────────────────────────────────────────

    private async Task<Dictionary> GetGameScreenshotAsync(Dictionary @params)
    {
        var ei = GetEditor();
        if (!ei.IsPlayingScene())
            return Error(CodeGeneric, "No scene is currently playing",
                new Dictionary { ["suggestion"] = "Use play_scene first" });

        var userDir = GetGameUserDir();
        var requestPath = userDir + "/mcp_screenshot_request";
        var screenshotPath = userDir + "/mcp_screenshot.png";

        if (Godot.FileAccess.FileExists(screenshotPath))
            DirAccess.RemoveAbsolute(screenshotPath);

        using (var req = Godot.FileAccess.Open(requestPath, Godot.FileAccess.ModeFlags.Write))
        {
            if (req == null)
                return ErrorInternal("Could not create screenshot request file");
        }

        var attempts = 30;
        while (attempts > 0)
        {
            await DelaySeconds(0.1);
            if (Godot.FileAccess.FileExists(screenshotPath))
                break;
            attempts--;
        }

        if (!Godot.FileAccess.FileExists(screenshotPath))
        {
            if (Godot.FileAccess.FileExists(requestPath))
                DirAccess.RemoveAbsolute(requestPath);
            return Error(CodeGeneric, "Screenshot timed out",
                new Dictionary { ["suggestion"] = "Ensure the game is running and MCPScreenshot autoload is active" });
        }

        var image = new Image();
        var err = image.Load(screenshotPath);
        if (err != Godot.Error.Ok)
        {
            DirAccess.RemoveAbsolute(screenshotPath);
            return ErrorInternal($"Failed to load screenshot: {err}");
        }

        DirAccess.RemoveAbsolute(screenshotPath);

        var savePathParam = OptionalString(@params, "save_path", "");
        if (savePathParam.Length > 0)
        {
            var absPath = ResolveSavePath(savePathParam);
            var saveErr = image.SavePng(absPath);
            if (saveErr != Godot.Error.Ok)
                return ErrorInternal($"Failed to save screenshot: {saveErr}");
            return Success(new Dictionary
            {
                ["saved_path"] = savePathParam,
                ["width"] = image.GetWidth(),
                ["height"] = image.GetHeight(),
                ["format"] = "png",
            });
        }

        var pngBuffer = image.SavePngToBuffer();
        var base64 = Marshalls.RawToBase64(pngBuffer);

        return Success(new Dictionary
        {
            ["image_base64"] = base64,
            ["width"] = image.GetWidth(),
            ["height"] = image.GetHeight(),
            ["format"] = "png",
        });
    }

    // ── execute_editor_script ────────────────────────────────────────────────────

    private Dictionary ExecuteEditorScript(Dictionary @params)
    {
        var (code, requireErr) = RequireString(@params, "code");
        if (requireErr != null)
            return requireErr;

        var allowUnsafeEditorIo = OptionalBool(@params, "allow_unsafe_editor_io", false);
        var unsafeGuard = GuardEditorScriptFileIo(code, allowUnsafeEditorIo);
        if (unsafeGuard.Count > 0)
            return unsafeGuard;

        var wrappedCode = "@tool\n"
            + "extends Node\n"
            + "\n"
            + "var _mcp_output: Array = []\n"
            + "\n"
            + "func _mcp_print(value: Variant) -> void:\n"
            + "\t_mcp_output.append(str(value))\n"
            + "\n"
            + "func run() -> Variant:\n"
            + "\t# User code begins\n"
            + IndentCode(code) + "\n"
            + "\t# User code ends\n"
            + "\treturn _mcp_output\n";

        var script = new GDScript { SourceCode = wrappedCode };
        var err = script.Reload();

        if (err != Godot.Error.Ok)
            return Error(CodeScriptCompile, "Script compilation failed",
                new Dictionary { ["error"] = err.ToString(), ["code"] = wrappedCode });

        var tempNode = new Node();
        tempNode.SetScript(script);
        AddChild(tempNode);

        Variant output = default;
        if (tempNode.HasMethod("run"))
            output = tempNode.Call("run");

        var mcpOutput = new Godot.Collections.Array();
        var rawOutput = tempNode.Get("_mcp_output");
        if (rawOutput.VariantType == Variant.Type.Array)
            mcpOutput = rawOutput.AsGodotArray();

        tempNode.QueueFree();

        Variant returnValue = default;
        if (output.VariantType != Variant.Type.Nil)
            returnValue = output.ToString() ?? "";

        return Success(new Dictionary
        {
            ["output"] = mcpOutput,
            ["return_value"] = returnValue,
        });
    }

    private static Dictionary GuardEditorScriptFileIo(string code, bool allowUnsafeEditorIo)
    {
        if (allowUnsafeEditorIo)
            return new Dictionary();

        var compact = code.Replace(" ", "").Replace("\t", "").Replace("\n", "");
        var unsafePatterns = new Godot.Collections.Array();

        if (compact.Contains("ResourceSaver.save("))
            unsafePatterns.Add("ResourceSaver.save");
        if (compact.Contains("ProjectSettings.save("))
            unsafePatterns.Add("ProjectSettings.save");
        if (compact.Contains("ConfigFile.save("))
            unsafePatterns.Add("ConfigFile.save");
        if (compact.Contains("FileAccess.open(")
            && ContainsAny(compact, "FileAccess.WRITE", "FileAccess.READ_WRITE", "FileAccess.WRITE_READ"))
            unsafePatterns.Add("FileAccess.open WRITE");
        if (ContainsAny(compact, "DirAccess.remove_absolute(", "DirAccess.rename_absolute(",
                "DirAccess.copy_absolute(", "DirAccess.make_dir_absolute(", "DirAccess.make_dir_recursive_absolute("))
            unsafePatterns.Add("DirAccess filesystem mutation");

        if (unsafePatterns.Count == 0)
            return new Dictionary();

        var openScenes = new Godot.Collections.Array();
        foreach (var p in GetOpenScenePaths()) openScenes.Add(p);

        return ErrorConflict(
            "Refusing to execute editor script with direct file/resource write APIs",
            new Dictionary
            {
                ["unsafe_patterns"] = unsafePatterns,
                ["open_scenes"] = openScenes,
                ["suggestion"] = "Use dedicated MCP commands and save_scene for editor-owned resources, or pass allow_unsafe_editor_io=true only when no open editor resource can be overwritten.",
            });
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle))
                return true;
        }
        return false;
    }

    private static string IndentCode(string code)
    {
        var lines = code.Split('\n');
        var indented = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
            indented[i] = "\t" + lines[i];
        return string.Join("\n", indented);
    }

    // ── clear_output ─────────────────────────────────────────────────────────────

    private static Dictionary ClearOutput(Dictionary @params)
    {
        GD.Print(new string('\n', 50));
        return Success(new Dictionary { ["cleared"] = true });
    }

    // ── reload_plugin ────────────────────────────────────────────────────────────

    private Dictionary ReloadPlugin(Dictionary @params)
    {
        const string pluginName = "godot_mcp";
        var ei = GetEditor();

        Callable.From(() => DeferredReloadPlugin(ei, pluginName)).CallDeferred();

        return Success(new Dictionary
        {
            ["reloading"] = true,
            ["message"] = "Plugin will reload momentarily. Connection will briefly drop and auto-reconnect.",
        });
    }

    private static void DeferredReloadPlugin(EditorInterface ei, string pluginName)
    {
        ei.SetPluginEnabled(pluginName, false);
        ei.SetPluginEnabled(pluginName, true);
        GD.Print("[MCP] Plugin reloaded");
    }

    // ── reload_project ───────────────────────────────────────────────────────────

    private static Dictionary ReloadProject(Dictionary @params)
    {
        var ei = GetEditor();
        ei.GetResourceFilesystem().Scan();

        return Success(new Dictionary { ["reloaded"] = true, ["message"] = "Filesystem rescanned." });
    }

    // ── get_signals ──────────────────────────────────────────────────────────────

    private static Dictionary GetSignals(Dictionary @params)
    {
        var (nodePath, requireErr) = RequireString(@params, "node_path");
        if (requireErr != null)
            return requireErr;

        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node '{nodePath}'");

        var signals = new Godot.Collections.Array();
        foreach (Dictionary sig in node.GetSignalList())
        {
            var args = new Godot.Collections.Array();
            foreach (Dictionary arg in sig["args"].AsGodotArray())
                args.Add(new Dictionary { ["name"] = arg["name"], ["type"] = arg["type"] });

            var connections = new Godot.Collections.Array();
            foreach (Dictionary conn in node.GetSignalConnectionList(sig["name"].AsStringName()))
            {
                var callable = conn["callable"].AsCallable();
                var targetStr = callable.Target is Node targetNode
                    ? root.GetPathTo(targetNode).ToString()
                    : callable.Target?.ToString() ?? "";
                connections.Add(new Dictionary
                {
                    ["target"] = targetStr,
                    ["method"] = callable.Method.ToString(),
                });
            }

            signals.Add(new Dictionary
            {
                ["name"] = sig["name"],
                ["args"] = args,
                ["connections"] = connections,
            });
        }

        return Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["type"] = node.GetClass(),
            ["signals"] = signals,
            ["count"] = signals.Count,
        });
    }

    // ── compare_screenshots ──────────────────────────────────────────────────────

    private static (Image? Image, Dictionary? Error) LoadImageFromParam(string value, string label)
    {
        var img = new Image();
        if (value.StartsWith("res://") || value.StartsWith("user://"))
        {
            var err = img.Load(value);
            if (err != Godot.Error.Ok)
                return (null, ErrorInvalidParams($"Failed to load {label} from path '{value}': {err}"));
            return (img, null);
        }
        var buf = Marshalls.Base64ToRaw(value);
        var pngErr = img.LoadPngFromBuffer(buf);
        if (pngErr != Godot.Error.Ok)
            return (null, ErrorInvalidParams($"Failed to decode {label} from base64: {pngErr}"));
        return (img, null);
    }

    private static Dictionary CompareScreenshots(Dictionary @params)
    {
        var (imageAValue, errA) = RequireString(@params, "image_a");
        if (errA != null)
            return errA;

        var (imageBValue, errB) = RequireString(@params, "image_b");
        if (errB != null)
            return errB;

        var threshold = OptionalInt(@params, "threshold", 10);

        var (imgA, loadErrA) = LoadImageFromParam(imageAValue, "image_a");
        if (loadErrA != null)
            return loadErrA;

        var (imgB, loadErrB) = LoadImageFromParam(imageBValue, "image_b");
        if (loadErrB != null)
            return loadErrB;

        if (imgA!.GetSize() != imgB!.GetSize())
            return ErrorInvalidParams($"Image sizes differ: {imgA.GetSize()} vs {imgB.GetSize()}");

        var width = imgA.GetWidth();
        var height = imgA.GetHeight();
        var diffImage = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);

        var changedPixels = 0;
        var totalPixels = width * height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var ca = imgA.GetPixel(x, y);
                var cb = imgB.GetPixel(x, y);
                var dr = Mathf.Abs(ca.R8 - cb.R8);
                var dg = Mathf.Abs(ca.G8 - cb.G8);
                var db = Mathf.Abs(ca.B8 - cb.B8);
                var maxDiff = Mathf.Max(dr, Mathf.Max(dg, db));
                if (maxDiff > threshold)
                {
                    changedPixels++;
                    diffImage.SetPixel(x, y, new Color(1, 0, 0, Mathf.Clamp(maxDiff / 255.0f, 0.3f, 1.0f)));
                }
                else
                {
                    diffImage.SetPixel(x, y, new Color(ca.R * 0.3f, ca.G * 0.3f, ca.B * 0.3f, 1.0f));
                }
            }
        }

        var diffPercentage = totalPixels > 0 ? (double)changedPixels / totalPixels * 100.0 : 0.0;
        var identical = changedPixels == 0;

        var diffPng = diffImage.SavePngToBuffer();
        var diffBase64 = Marshalls.RawToBase64(diffPng);

        return Success(new Dictionary
        {
            ["identical"] = identical,
            ["changed_pixels"] = changedPixels,
            ["total_pixels"] = totalPixels,
            ["diff_percentage"] = Math.Round(diffPercentage, 2),
            ["threshold"] = threshold,
            ["width"] = width,
            ["height"] = height,
            ["diff_image_base64"] = diffBase64,
        });
    }

    // ── set_auto_dismiss ─────────────────────────────────────────────────────────

    private Dictionary SetAutoDismiss(Dictionary @params)
    {
        var enabled = OptionalBool(@params, "enabled", true);
        if (Plugin is GodotMcpPlugin plugin)
            plugin.AutoDismissDialogs = enabled;
        return Success(new Dictionary
        {
            ["auto_dismiss"] = enabled,
            ["message"] = $"Auto-dismiss dialogs {(enabled ? "enabled" : "disabled")}",
        });
    }

    // ── get_editor_camera / set_editor_camera ───────────────────────────────────

    private static Dictionary ErrorNoEditorCamera() =>
        Error(CodeGeneric, "No 3D editor camera found",
            new Dictionary { ["suggestion"] = "Make sure a 3D scene is open in the editor" });

    private static Camera3D? GetActiveEditorCamera3D()
    {
        var vp3d = GetEditor().GetEditorViewport3D(0);
        return vp3d?.GetCamera3D();
    }

    private static Dictionary GetEditorCamera(Dictionary @params)
    {
        var cam = GetActiveEditorCamera3D();
        if (cam == null)
            return ErrorNoEditorCamera();

        var pos = cam.GlobalPosition;
        var rot = cam.RotationDegrees;
        return Success(new Dictionary
        {
            ["position"] = new Dictionary { ["x"] = pos.X, ["y"] = pos.Y, ["z"] = pos.Z },
            ["rotation_degrees"] = new Dictionary { ["x"] = rot.X, ["y"] = rot.Y, ["z"] = rot.Z },
            ["fov"] = cam.Fov,
            ["near"] = cam.Near,
            ["far"] = cam.Far,
        });
    }

    private static Dictionary SetEditorCamera(Dictionary @params)
    {
        var cam = GetActiveEditorCamera3D();
        if (cam == null)
            return ErrorNoEditorCamera();

        if (@params.ContainsKey("position"))
        {
            var p = OptionalDict(@params, "position") ?? new Dictionary();
            cam.GlobalPosition = new Vector3(
                (float)GetNum(p, "x", cam.GlobalPosition.X),
                (float)GetNum(p, "y", cam.GlobalPosition.Y),
                (float)GetNum(p, "z", cam.GlobalPosition.Z));
        }

        if (@params.ContainsKey("rotation_degrees"))
        {
            var r = OptionalDict(@params, "rotation_degrees") ?? new Dictionary();
            cam.RotationDegrees = new Vector3(
                (float)GetNum(r, "x", cam.RotationDegrees.X),
                (float)GetNum(r, "y", cam.RotationDegrees.Y),
                (float)GetNum(r, "z", cam.RotationDegrees.Z));
        }

        if (@params.ContainsKey("look_at"))
        {
            var t = OptionalDict(@params, "look_at") ?? new Dictionary();
            cam.LookAt(new Vector3(
                (float)GetNum(t, "x", 0),
                (float)GetNum(t, "y", 0),
                (float)GetNum(t, "z", 0)));
        }

        if (@params.ContainsKey("fov"))
            cam.Fov = (float)OptionalFloat(@params, "fov", cam.Fov);

        var pos = cam.GlobalPosition;
        var rot = cam.RotationDegrees;
        return Success(new Dictionary
        {
            ["position"] = new Dictionary { ["x"] = pos.X, ["y"] = pos.Y, ["z"] = pos.Z },
            ["rotation_degrees"] = new Dictionary { ["x"] = rot.X, ["y"] = rot.Y, ["z"] = rot.Z },
            ["fov"] = cam.Fov,
        });
    }

    private static double GetNum(Dictionary d, string key, double @default)
    {
        if (!d.TryGetValue(key, out var v))
            return @default;
        return v.VariantType switch
        {
            Variant.Type.Int => v.AsInt64(),
            Variant.Type.Float => v.AsDouble(),
            Variant.Type.String => v.AsString().ToFloat(),
            Variant.Type.Bool => v.AsBool() ? 1 : 0,
            _ => @default,
        };
    }
}
