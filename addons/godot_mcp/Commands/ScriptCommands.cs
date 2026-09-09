using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Faithful port of addons/godot_mcp/commands/script_commands.gd.
/// Exposes script file management tools: list, read, create, edit, attach,
/// validate, and inspect open scripts in the script editor.
/// </summary>
[Tool]
public partial class ScriptCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "list_scripts",
            Description = "Recursively list script files (.gd, .cs, .gdshader) under a project directory.",
            Category = "script",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Directory to scan (default: res://).", "res://"),
                ["recursive"] = Schema.Bool("Recurse into subdirectories.", true),
            }),
            Handler = p => Task.FromResult(ListScripts(p)),
        },
        new()
        {
            Name = "read_script",
            Description = "Read the full text content of a script file.",
            Category = "script",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Path to the script file (res://...)."),
            }, "path"),
            Handler = p => Task.FromResult(ReadScript(p)),
        },
        new()
        {
            Name = "create_script",
            Description = "Create a new .gd or .cs script file. When content is omitted, a template matching the file extension is generated (.gd → GDScript, .cs → C#).",
            Category = "script",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Path for the new script file (res://...). The extension (.gd/.cs) selects the template language."),
                ["content"] = Schema.Str("Full script source. When omitted, a default template is generated."),
                ["extends"] = Schema.Str("Base class for the generated template.", "Node"),
                ["class_name"] = Schema.Str("Optional class_name (GDScript) or class name (C#, defaults to PascalCase file name) for the generated template."),
                ["force"] = Schema.Bool("Overwrite even if the target is open in the script editor.", false),
            }, "path"),
            Handler = p => Task.FromResult(CreateScript(p)),
        },
        new()
        {
            Name = "edit_script",
            Description = "Edit an existing script via search/replace (optionally regex), ranged line replacement, full-content overwrite, or line insertion.",
            Category = "script",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Path to the script file (res://...)."),
                ["replacements"] = Schema.Arr(
                    "List of {search, replace, regex?} objects applied in order.",
                    Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                    {
                        ["search"] = Schema.Str("Text or regex pattern to search for."),
                        ["replace"] = Schema.Str("Replacement text."),
                        ["regex"] = Schema.Bool("Treat 'search' as a regular expression.", false),
                    })),
                ["content"] = Schema.Any("New content: full-file replacement, or the replacement block when start_line/end_line are given."),
                ["start_line"] = Schema.Int("1-based inclusive start line for ranged replacement."),
                ["end_line"] = Schema.Int("1-based inclusive end line for ranged replacement (defaults to start_line)."),
                ["insert_at_line"] = Schema.Int("0-based line index to insert 'text' before."),
                ["text"] = Schema.Str("Text to insert when using insert_at_line."),
                ["force"] = Schema.Bool("Overwrite even if the target is open in the script editor.", false),
            }, "path"),
            Handler = p => Task.FromResult(EditScript(p)),
        },
        new()
        {
            Name = "attach_script",
            Description = "Attach a script resource to a node in the currently edited scene (undo-tracked).",
            Category = "script",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["node_path"] = Schema.Str("Path to the target node in the edited scene."),
                ["script_path"] = Schema.Str("Path to the script resource to attach (res://...)."),
            }, "node_path", "script_path"),
            Handler = p => Task.FromResult(AttachScript(p)),
        },
        new()
        {
            Name = "get_open_scripts",
            Description = "List scripts currently open in the editor's script editor.",
            Category = "script",
            InputSchema = Schema.Object(),
            Handler = p => Task.FromResult(GetOpenScripts(p)),
        },
        new()
        {
            Name = "validate_script",
            Description = "Validate a script by attempting to compile it. Only .gd scripts are compile-checked in-editor.",
            Category = "script",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Path to the script file (res://...)."),
            }, "path"),
            Handler = p => Task.FromResult(ValidateScript(p)),
        },
    };

    // ── Shared guard ────────────────────────────────────────────────────────────

    private static Dictionary GuardScriptFilePath(string path, string operation)
    {
        var ext = path.GetExtension().ToLowerInvariant();
        if (ext is "gd" or "cs")
            return new Dictionary();
        return Error(
            CodeInvalidParams,
            $"{operation} only supports script files (.gd, .cs): {NormalizeProjectPath(path)}",
            new Dictionary
            {
                ["path"] = NormalizeProjectPath(path),
                ["extension"] = ext,
                ["suggestion"] = "Use scene commands for .tscn/.scn files and shader commands for shader resources.",
            });
    }

    // ── list_scripts ─────────────────────────────────────────────────────────

    private static Dictionary ListScripts(Dictionary @params)
    {
        var path = OptionalString(@params, "path", "res://");
        var recursive = OptionalBool(@params, "recursive", true);

        var scripts = new Godot.Collections.Array();
        FindScripts(path, recursive, scripts);

        return Success(new Dictionary { ["scripts"] = scripts, ["count"] = scripts.Count });
    }

    private static void FindScripts(string path, bool recursive, Godot.Collections.Array scripts)
    {
        using var dir = DirAccess.Open(path);
        if (dir == null)
            return;

        dir.ListDirBegin();
        var fileName = dir.GetNext();

        while (!string.IsNullOrEmpty(fileName))
        {
            if (fileName.StartsWith('.'))
            {
                fileName = dir.GetNext();
                continue;
            }

            var fullPath = path.PathJoin(fileName);

            if (dir.CurrentIsDir())
            {
                if (recursive)
                    FindScripts(fullPath, recursive, scripts);
            }
            else if (fileName.GetExtension() is "gd" or "cs" or "gdshader")
            {
                var info = new Dictionary { ["path"] = fullPath, ["type"] = fileName.GetExtension() };
                using var file = Godot.FileAccess.Open(fullPath, Godot.FileAccess.ModeFlags.Read);
                if (file != null)
                {
                    info["size"] = file.GetLength();
                    var firstLine = file.GetLine().StripEdges();
                    if (firstLine.StartsWith("class_name "))
                        info["class_name"] = firstLine[11..].StripEdges();
                    else if (firstLine.StartsWith("extends "))
                        info["extends"] = firstLine[8..].StripEdges();
                }
                scripts.Add(info);
            }

            fileName = dir.GetNext();
        }

        dir.ListDirEnd();
    }

    // ── read_script ──────────────────────────────────────────────────────────

    private static Dictionary ReadScript(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Script '{path}'");

        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
            return ErrorInternal($"Cannot read script: {Godot.FileAccess.GetOpenError()}");

        var content = file.GetAsText();
        var lineCount = content.Count(c => c == '\n') + 1;

        return Success(new Dictionary
        {
            ["path"] = path,
            ["content"] = content,
            ["line_count"] = lineCount,
            ["size"] = content.Length,
        });
    }

    // ── create_script ────────────────────────────────────────────────────────

    private static Dictionary CreateScript(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;
        var pathGuard = GuardScriptFilePath(path, "create_script");
        if (pathGuard.Count > 0)
            return pathGuard;

        var content = OptionalString(@params, "content", "");
        var baseClass = OptionalString(@params, "extends", "Node");
        var classNameStr = OptionalString(@params, "class_name", "");
        var force = OptionalBool(@params, "force", false);

        var guard = GuardTextResourceWrite(path, force);
        if (guard.Count > 0)
            return guard;

        // Generate a language-appropriate template when no content is provided.
        // A .cs path must never receive the GDScript template — GDScript source
        // inside a .cs file fails the .NET build and breaks the whole solution.
        if (string.IsNullOrEmpty(content))
        {
            content = path.GetExtension().ToLowerInvariant() == "cs"
                ? BuildCSharpTemplate(path, baseClass, classNameStr)
                : BuildGdScriptTemplate(baseClass, classNameStr);
        }

        // Ensure directory exists
        var dirErr = EnsureParentDir(path);
        if (dirErr.Count > 0)
            return dirErr;

        using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
        {
            if (file == null)
                return ErrorInternal($"Cannot create script: {Godot.FileAccess.GetOpenError()}");
            file.StoreString(content);
        }

        EditorInterface.Singleton.GetResourceFilesystem().Scan();

        // Pre-load so the script is available immediately
        if (ResourceLoader.Exists(path))
        {
            var script = ResourceLoader.Load(path);
            if (script is Script s)
                s.Reload(true);
        }

        return Success(new Dictionary { ["path"] = path, ["created"] = true });
    }

    /// <summary>GDScript template: class_name (optional) + extends + empty _ready().</summary>
    private static string BuildGdScriptTemplate(string baseClass, string className)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(className))
            lines.Add($"class_name {className}");
        lines.Add($"extends {baseClass}");
        lines.Add("");
        lines.Add("");
        lines.Add("func _ready() -> void:");
        lines.Add("\tpass");
        lines.Add("");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// C# template matching the extends/class_name params: the class name falls
    /// back to a PascalCase type name derived from the file name.
    /// </summary>
    private static string BuildCSharpTemplate(string path, string baseClass, string className)
    {
        var name = !string.IsNullOrEmpty(className)
            ? className
            : PascalCaseFromFileName(path);
        return
            "using Godot;\n" +
            "\n" +
            $"public partial class {name} : {baseClass}\n" +
            "{\n" +
            "\tpublic override void _Ready()\n" +
            "\t{\n" +
            "\t}\n" +
            "}\n";
    }

    /// <summary>Derive a valid C# type name from a script file name ("my_node.cs" → "MyNode").</summary>
    private static string PascalCaseFromFileName(string path)
    {
        var parts = path.GetFile().GetBaseName()
            .Split(new[] { '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
                sb.Append(part[1..]);
        }
        var result = sb.ToString();
        if (result.Length == 0)
            return "NewScript";
        if (char.IsDigit(result[0]))
            result = "_" + result;
        return result;
    }

    // ── edit_script ──────────────────────────────────────────────────────────

    private static Dictionary EditScript(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;
        var pathGuard = GuardScriptFilePath(path, "edit_script");
        if (pathGuard.Count > 0)
            return pathGuard;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Script '{path}'");

        var force = OptionalBool(@params, "force", false);
        var guard = GuardTextResourceWrite(path, force);
        if (guard.Count > 0)
            return guard;

        // Read current content
        string content;
        using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read))
        {
            if (file == null)
                return ErrorInternal($"Cannot read script: {Godot.FileAccess.GetOpenError()}");
            content = file.GetAsText();
        }

        var changesMade = 0;

        // Support search-and-replace
        if (@params.TryGetValue("replacements", out var replacementsVariant)
            && replacementsVariant.VariantType == Variant.Type.Array)
        {
            var replacements = replacementsVariant.AsGodotArray();
            foreach (var replacementVariant in replacements)
            {
                if (replacementVariant.VariantType != Variant.Type.Dictionary)
                    continue;
                var replacement = replacementVariant.AsGodotDictionary();
                var search = replacement.TryGetValue("search", out var searchV) ? searchV.AsString() : "";
                var replace = replacement.TryGetValue("replace", out var replaceV) ? replaceV.AsString() : "";
                if (string.IsNullOrEmpty(search))
                    continue;
                var useRegex = replacement.TryGetValue("regex", out var regexV) && CoerceBool(regexV);
                if (useRegex)
                {
                    var regex = new RegEx();
                    var compileErr = regex.Compile(search);
                    if (compileErr == Godot.Error.Ok)
                    {
                        var newContent = regex.Sub(content, replace, true);
                        if (newContent != content)
                        {
                            content = newContent;
                            changesMade += 1;
                        }
                    }
                }
                else if (content.Contains(search))
                {
                    content = content.Replace(search, replace);
                    changesMade += 1;
                }
            }
        }
        // Support 1-based inclusive line range replacement
        else if (@params.ContainsKey("content") && (@params.ContainsKey("start_line") || @params.ContainsKey("end_line")))
        {
            if (!@params.ContainsKey("start_line"))
                return ErrorInvalidParams("start_line is required when end_line is provided");
            var startLine = OptionalInt(@params, "start_line");
            var endLine = @params.ContainsKey("end_line") ? OptionalInt(@params, "end_line") : startLine;
            var lines = new List<string>(content.Split('\n'));
            if (startLine < 1)
                return ErrorInvalidParams("start_line must be >= 1");
            if (endLine < startLine)
                return ErrorInvalidParams("end_line must be >= start_line");
            if (startLine > lines.Count)
                return ErrorInvalidParams("start_line is beyond the end of the file");
            if (endLine > lines.Count)
                return ErrorInvalidParams("end_line is beyond the end of the file");

            var replacementLines = @params["content"].AsString().Split('\n');
            var startIndex = startLine - 1;
            var removeCount = endLine - startLine + 1;
            lines.RemoveRange(startIndex, removeCount);
            lines.InsertRange(startIndex, replacementLines);
            content = string.Join("\n", lines);
            changesMade = 1;
        }
        // Support full content replacement
        else if (@params.ContainsKey("content"))
        {
            content = @params["content"].AsString();
            changesMade = 1;
        }
        // Support insert at line
        else if (@params.ContainsKey("insert_at_line") && @params.ContainsKey("text"))
        {
            var lineNum = OptionalInt(@params, "insert_at_line");
            var text = @params["text"].AsString();
            var lines = new List<string>(content.Split('\n'));
            lineNum = Mathf.Clamp(lineNum, 0, lines.Count);
            lines.Insert(lineNum, text);
            content = string.Join("\n", lines);
            changesMade = 1;
        }

        if (changesMade == 0)
            return Success(new Dictionary { ["path"] = path, ["changes_made"] = 0, ["message"] = "No changes applied" });

        // Write back
        using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
        {
            if (file == null)
                return ErrorInternal($"Cannot write script: {Godot.FileAccess.GetOpenError()}");
            file.StoreString(content);
        }

        // Reload the script resource so the editor picks up changes immediately
        ReloadScript(path);

        return Success(new Dictionary { ["path"] = path, ["changes_made"] = changesMade });
    }

    /// <summary>Force-reload a script so the editor reflects disk changes immediately.</summary>
    private static void ReloadScript(string path)
    {
        // First, trigger a filesystem scan so Godot knows the file changed
        EditorInterface.Singleton.GetResourceFilesystem().Scan();

        // If the script is already loaded in memory, reload it
        if (ResourceLoader.Exists(path))
        {
            var script = ResourceLoader.Load(path);
            if (script is Script s)
                s.Reload(true);
        }

        // If the script is open in the script editor, the reload above updates it.
        // But we also need to notify the editor to refresh its error indicators.
        EditorInterface.Singleton.GetScriptEditor().Notification((int)Control.NotificationVisibilityChanged);
    }

    // ── attach_script ────────────────────────────────────────────────────────

    private Dictionary AttachScript(Dictionary @params)
    {
        var (nodePath, err1) = RequireString(@params, "node_path");
        if (err1 != null)
            return err1;

        var (scriptPath, err2) = RequireString(@params, "script_path");
        if (err2 != null)
            return err2;

        var root = GetEditedRoot();
        if (root == null)
            return ErrorNoScene();

        var node = FindNodeByPath(nodePath);
        if (node == null)
            return ErrorNotFound($"Node '{nodePath}'", "Use get_scene_tree to see available nodes");

        if (!Godot.FileAccess.FileExists(scriptPath))
            return ErrorNotFound($"Script '{scriptPath}'");

        var script = ResourceLoader.Load(scriptPath);
        if (script == null)
            return ErrorInternal($"Failed to load script: {scriptPath}");

        var oldScript = node.GetScript();

        var undoRedo = GetUndoRedo();
        undoRedo.CreateAction($"MCP: Attach script to {node.Name}");
        undoRedo.AddDoMethod(node, Node.MethodName.SetScript, script);
        undoRedo.AddUndoMethod(node, Node.MethodName.SetScript, oldScript);
        undoRedo.CommitAction();

        return Success(new Dictionary
        {
            ["node_path"] = root.GetPathTo(node).ToString(),
            ["script_path"] = scriptPath,
            ["attached"] = true,
        });
    }

    // ── validate_script ──────────────────────────────────────────────────────

    private static Dictionary ValidateScript(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;
        var pathGuard = GuardScriptFilePath(path, "validate_script");
        if (pathGuard.Count > 0)
            return pathGuard;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Script '{path}'");

        string sourceCode;
        using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read))
        {
            if (file == null)
                return ErrorInternal($"Cannot read script: {Godot.FileAccess.GetOpenError()}");
            sourceCode = file.GetAsText();
        }

        // In-editor compile checking is GDScript-only. Feeding C# source to the
        // GDScript compiler would always report a false failure; C# scripts are
        // validated by the .NET toolchain instead.
        if (path.GetExtension().ToLowerInvariant() == "cs")
        {
            return Success(new Dictionary
            {
                ["path"] = path,
                ["validated"] = false,
                ["message"] = "In-editor compile check is only available for .gd scripts. " +
                              "C# scripts are compiled by the .NET toolchain — use get_editor_errors or run 'dotnet build'.",
            });
        }

        var script = new GDScript { SourceCode = sourceCode };
        var compileErr = script.Reload();

        if (compileErr == Godot.Error.Ok)
            return Success(new Dictionary { ["path"] = path, ["valid"] = true, ["message"] = "Script compiles successfully" });

        return Success(new Dictionary
        {
            ["path"] = path,
            ["valid"] = false,
            ["error_code"] = (int)compileErr,
            ["error_string"] = compileErr.ToString(),
            ["message"] = "Compilation failed. Use get_output_log or get_editor_errors for details.",
        });
    }

    // ── get_open_scripts ─────────────────────────────────────────────────────

    private static Dictionary GetOpenScripts(Dictionary @params)
    {
        var scriptEditor = EditorInterface.Singleton.GetScriptEditor();
        var openScripts = new Godot.Collections.Array();

        foreach (var scriptBase in scriptEditor.GetOpenScripts())
        {
            if (scriptBase == null)
                continue;
            var info = new Dictionary
            {
                ["path"] = scriptBase.ResourcePath,
                ["type"] = scriptBase.GetClass(),
            };
            openScripts.Add(info);
        }

        return Success(new Dictionary { ["scripts"] = openScripts, ["count"] = openScripts.Count });
    }
}
