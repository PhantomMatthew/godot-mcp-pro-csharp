using System.Text.RegularExpressions;
using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Project-wide inspection and settings tools.
/// Faithful port of addons/godot_mcp/commands/project_commands.gd.
/// </summary>
[Tool]
public partial class ProjectCommands : BaseCommand
{
    private static readonly HashSet<string> TextExtensions = new()
    {
        "gd", "tscn", "tres", "cfg", "godot", "gdshader", "md", "txt", "json",
    };

    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "get_project_info",
            Description = "Get high-level information about the current project: name, Godot version, " +
                "project path, main scene, viewport/window size, renderer, and configured autoloads.",
            InputSchema = Schema.Object(),
            Category = "project",
            Handler = p => Task.FromResult(GetProjectInfo(p)),
        },
        new()
        {
            Name = "get_filesystem_tree",
            Description = "Recursively scan a project directory and return it as a nested tree of " +
                "directory/file nodes, optionally filtering files by a glob pattern.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Root res:// directory to scan.", "res://"),
                ["filter"] = Schema.Str("Optional glob pattern to filter file names, e.g. '*.gd' or '*.tscn'.", ""),
                ["max_depth"] = Schema.Int("Maximum recursion depth.", 10),
            }),
            Category = "project",
            Handler = p => Task.FromResult(GetFilesystemTree(p)),
        },
        new()
        {
            Name = "search_files",
            Description = "Search the project filesystem for files whose name fuzzy-matches or glob-matches " +
                "the query, optionally filtered by file extension.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["query"] = Schema.Str("Search text or glob pattern to match against file names."),
                ["path"] = Schema.Str("Root res:// directory to search from.", "res://"),
                ["file_type"] = Schema.Str("Optional file extension filter (without the dot), e.g. 'gd'.", ""),
                ["max_results"] = Schema.Int("Maximum number of matches to return.", 50),
            }, "query"),
            Category = "project",
            Handler = p => Task.FromResult(SearchFiles(p)),
        },
        new()
        {
            Name = "search_in_files",
            Description = "Search the text content of project files for a substring or regular expression, " +
                "returning matching file/line/text triples.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["query"] = Schema.Str("Text to search for, or a regex pattern when regex=true."),
                ["path"] = Schema.Str("Root res:// directory to search from.", "res://"),
                ["max_results"] = Schema.Int("Maximum number of matches to return.", 50),
                ["regex"] = Schema.Bool("Treat query as a regular expression instead of plain substring.", false),
                ["file_type"] = Schema.Str(
                    "Optional file extension filter (without the dot). Defaults to common text extensions.", ""),
            }, "query"),
            Category = "project",
            Handler = p => Task.FromResult(SearchInFiles(p)),
        },
        new()
        {
            Name = "get_project_settings",
            Description = "Read one specific project setting by key, or list all settings under a given " +
                "section prefix (or all settings if neither key nor section is given).",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["section"] = Schema.Str("Setting name prefix to filter by, e.g. 'rendering/'.", ""),
                ["key"] = Schema.Str("Exact setting key to fetch a single value for.", ""),
            }),
            Category = "project",
            Handler = p => Task.FromResult(GetProjectSettings(p)),
        },
        new()
        {
            Name = "set_project_setting",
            Description = "Set a project setting to a value and persist project.godot. String values are " +
                "smart-coerced to bool/int/float/Vector2 where applicable.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["key"] = Schema.Str("Setting key to write, e.g. 'application/config/name'."),
                ["value"] = Schema.Any("New value for the setting."),
            }, "key", "value"),
            Category = "project",
            Handler = p => Task.FromResult(SetProjectSetting(p)),
        },
        new()
        {
            Name = "uid_to_project_path",
            Description = "Resolve a Godot resource UID string (uid://...) to its current res:// project path.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["uid"] = Schema.Str("UID string to resolve, e.g. 'uid://abc123'."),
            }, "uid"),
            Category = "project",
            Handler = p => Task.FromResult(UidToProjectPath(p)),
        },
        new()
        {
            Name = "project_path_to_uid",
            Description = "Resolve a res:// project path to its assigned Godot resource UID string.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the resource to look up."),
            }, "path"),
            Category = "project",
            Handler = p => Task.FromResult(ProjectPathToUid(p)),
        },
        new()
        {
            Name = "add_autoload",
            Description = "Register a new autoload singleton pointing at a script/scene file and persist " +
                "project.godot. Fails if an autoload with the same name already exists.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["name"] = Schema.Str("Autoload singleton name."),
                ["path"] = Schema.Str("res:// path to the script or scene to autoload."),
            }, "name", "path"),
            Category = "project",
            Handler = p => Task.FromResult(AddAutoload(p)),
        },
        new()
        {
            Name = "remove_autoload",
            Description = "Remove a previously registered autoload singleton by name and persist project.godot.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["name"] = Schema.Str("Autoload singleton name to remove."),
            }, "name"),
            Category = "project",
            Destructive = true,
            Handler = p => Task.FromResult(RemoveAutoload(p)),
        },
    };

    // ── get_project_info ───────────────────────────────────────────────────

    private static Dictionary GetProjectInfo(Dictionary @params)
    {
        var info = new Dictionary
        {
            ["project_name"] = ProjectSettings.GetSetting("application/config/name", ""),
            ["godot_version"] = Engine.GetVersionInfo(),
            ["project_path"] = ProjectSettings.GlobalizePath("res://"),
            ["main_scene"] = ProjectSettings.GetSetting("application/run/main_scene", ""),
            ["viewport_width"] = ProjectSettings.GetSetting("display/window/size/viewport_width", 0),
            ["viewport_height"] = ProjectSettings.GetSetting("display/window/size/viewport_height", 0),
            ["window_width"] = ProjectSettings.GetSetting("display/window/size/window_width_override", 0),
            ["window_height"] = ProjectSettings.GetSetting("display/window/size/window_height_override", 0),
            ["renderer"] = ProjectSettings.GetSetting("rendering/renderer/rendering_method", ""),
        };

        var autoloads = new Dictionary();
        foreach (var prop in ProjectSettings.Singleton.GetPropertyList())
        {
            var name = prop["name"].AsString();
            if (name.StartsWith("autoload/"))
                autoloads[name[9..]] = ProjectSettings.GetSetting(name);
        }
        info["autoloads"] = autoloads;

        return Success(info);
    }

    // ── get_filesystem_tree ─────────────────────────────────────────────────

    private static Dictionary GetFilesystemTree(Dictionary @params)
    {
        var path = OptionalString(@params, "path", "res://");
        var filter = OptionalString(@params, "filter", "");
        var maxDepth = OptionalInt(@params, "max_depth", 10);

        var tree = ScanDirectory(path, filter, maxDepth, 0);
        return Success(new Dictionary { ["tree"] = tree });
    }

    private static Dictionary ScanDirectory(string path, string filter, int maxDepth, int depth)
    {
        var result = new Dictionary
        {
            ["name"] = path.GetFile(),
            ["path"] = path,
            ["type"] = "directory",
        };

        if (depth >= maxDepth)
            return result;

        var dir = DirAccess.Open(path);
        if (dir == null)
            return result;

        var children = new Godot.Collections.Array();
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
                children.Add(ScanDirectory(fullPath, filter, maxDepth, depth + 1));
            }
            else if (string.IsNullOrEmpty(filter) || GlobMatch(fileName, filter))
            {
                children.Add(new Dictionary
                {
                    ["name"] = fileName,
                    ["path"] = fullPath,
                    ["type"] = "file",
                });
            }

            fileName = dir.GetNext();
        }

        dir.ListDirEnd();

        if (children.Count > 0)
            result["children"] = children;

        return result;
    }

    // ── search_files ─────────────────────────────────────────────────────

    private static Dictionary SearchFiles(Dictionary @params)
    {
        var (query, err) = RequireString(@params, "query");
        if (err != null)
            return err;

        var path = OptionalString(@params, "path", "res://");
        var fileType = OptionalString(@params, "file_type", "");
        var maxResults = OptionalInt(@params, "max_results", 50);

        var matches = new Godot.Collections.Array();
        SearchRecursive(path, query, fileType, matches, maxResults);

        return Success(new Dictionary { ["matches"] = matches, ["count"] = matches.Count });
    }

    private static void SearchRecursive(string path, string query, string fileType,
        Godot.Collections.Array matches, int maxResults)
    {
        if (matches.Count >= maxResults)
            return;

        var dir = DirAccess.Open(path);
        if (dir == null)
            return;

        dir.ListDirBegin();
        var fileName = dir.GetNext();

        while (!string.IsNullOrEmpty(fileName) && matches.Count < maxResults)
        {
            if (fileName.StartsWith('.'))
            {
                fileName = dir.GetNext();
                continue;
            }

            var fullPath = path.PathJoin(fileName);

            if (dir.CurrentIsDir())
            {
                SearchRecursive(fullPath, query, fileType, matches, maxResults);
            }
            else
            {
                if (!string.IsNullOrEmpty(fileType) && fileName.GetExtension() != fileType)
                {
                    fileName = dir.GetNext();
                    continue;
                }

                if (fileName.ToLowerInvariant().Contains(query.ToLowerInvariant()))
                    matches.Add(fullPath);
                else if (GlobMatch(fileName, query))
                    matches.Add(fullPath);
            }

            fileName = dir.GetNext();
        }

        dir.ListDirEnd();
    }

    // ── search_in_files ─────────────────────────────────────────────────

    private static Dictionary SearchInFiles(Dictionary @params)
    {
        var (query, err) = RequireString(@params, "query");
        if (err != null)
            return err;

        var path = OptionalString(@params, "path", "res://");
        var maxResults = OptionalInt(@params, "max_results", 50);
        var useRegex = OptionalBool(@params, "regex", false);
        var fileType = OptionalString(@params, "file_type", "");

        RegEx? regex = null;
        if (useRegex)
        {
            regex = new RegEx();
            var compileErr = regex.Compile(query);
            if (compileErr != Godot.Error.Ok)
                return ErrorInvalidParams($"Invalid regex pattern: {compileErr}");
        }

        var matches = new Godot.Collections.Array();
        SearchInFilesRecursive(path, query, regex, fileType, matches, maxResults);

        return Success(new Dictionary { ["matches"] = matches, ["count"] = matches.Count, ["query"] = query });
    }

    private static void SearchInFilesRecursive(string path, string query, RegEx? regex, string fileType,
        Godot.Collections.Array matches, int maxResults)
    {
        if (matches.Count >= maxResults)
            return;

        var dir = DirAccess.Open(path);
        if (dir == null)
            return;

        dir.ListDirBegin();
        var fileName = dir.GetNext();

        while (!string.IsNullOrEmpty(fileName) && matches.Count < maxResults)
        {
            if (fileName.StartsWith('.'))
            {
                fileName = dir.GetNext();
                continue;
            }

            var fullPath = path.PathJoin(fileName);

            if (dir.CurrentIsDir())
            {
                if (fileName != "addons" && fileName != ".godot")
                    SearchInFilesRecursive(fullPath, query, regex, fileType, matches, maxResults);
            }
            else
            {
                var ext = fileName.GetExtension();
                if (!string.IsNullOrEmpty(fileType))
                {
                    if (ext != fileType)
                    {
                        fileName = dir.GetNext();
                        continue;
                    }
                }
                else if (!TextExtensions.Contains(ext))
                {
                    fileName = dir.GetNext();
                    continue;
                }

                using var file = Godot.FileAccess.Open(fullPath, Godot.FileAccess.ModeFlags.Read);
                if (file != null)
                {
                    var content = file.GetAsText();
                    var lines = content.Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (matches.Count >= maxResults)
                            break;
                        var line = lines[i];
                        var matched = regex != null ? regex.Search(line) != null : line.Contains(query);
                        if (matched)
                        {
                            matches.Add(new Dictionary
                            {
                                ["file"] = fullPath,
                                ["line"] = i + 1,
                                ["text"] = line.StripEdges(),
                            });
                        }
                    }
                }
            }

            fileName = dir.GetNext();
        }

        dir.ListDirEnd();
    }

    // ── get_project_settings / set_project_setting ─────────────────────────

    /// <summary>String-ify a Variant the way GDScript's str() does (no JSON quoting of strings),
    /// matching Variant.Stringify() only for non-string types.</summary>
    private static string GdStr(Variant value) =>
        value.VariantType == Variant.Type.String ? value.AsString() : value.ToString();

    private static Dictionary GetProjectSettings(Dictionary @params)
    {
        var section = OptionalString(@params, "section", "");
        var key = OptionalString(@params, "key", "");

        if (!string.IsNullOrEmpty(key))
        {
            if (ProjectSettings.HasSetting(key))
            {
                var value = ProjectSettings.GetSetting(key);
                return Success(new Dictionary
                {
                    ["key"] = key,
                    ["value"] = GdStr(value),
                    ["type"] = (int)value.VariantType,
                });
            }
            return ErrorNotFound($"Setting '{key}'");
        }

        var settings = new Dictionary();
        foreach (var prop in ProjectSettings.Singleton.GetPropertyList())
        {
            var name = prop["name"].AsString();
            if (string.IsNullOrEmpty(section) || name.StartsWith(section))
                settings[name] = GdStr(ProjectSettings.GetSetting(name));
        }

        return Success(new Dictionary { ["settings"] = settings, ["count"] = settings.Count });
    }

    private static Dictionary SetProjectSetting(Dictionary @params)
    {
        var (key, err) = RequireString(@params, "key");
        if (err != null)
            return err;

        if (!@params.ContainsKey("value"))
            return ErrorInvalidParams("Missing required parameter: value");

        var value = @params["value"];

        if (value.VariantType == Variant.Type.String)
        {
            var s = value.AsString();
            if (s.StartsWith("Vector2("))
            {
                var expr = new Expression();
                if (expr.Parse(s) == Godot.Error.Ok)
                {
                    var parsed = expr.Execute();
                    if (parsed.VariantType == Variant.Type.Vector2)
                        value = parsed;
                }
            }
            else if (s == "true")
            {
                value = true;
            }
            else if (s == "false")
            {
                value = false;
            }
            else if (s.IsValidInt())
            {
                value = s.ToInt();
            }
            else if (s.IsValidFloat())
            {
                value = s.ToFloat();
            }
        }

        ProjectSettings.SetSetting(key, value);
        var saveErr = ProjectSettings.Save();
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save project settings: {saveErr}");

        return Success(new Dictionary
        {
            ["key"] = key,
            ["value"] = GdStr(ProjectSettings.GetSetting(key)),
            ["saved"] = true,
        });
    }

    // ── uid_to_project_path / project_path_to_uid ──────────────────────────

    private static Dictionary UidToProjectPath(Dictionary @params)
    {
        var (uidStr, err) = RequireString(@params, "uid");
        if (err != null)
            return err;

        var uid = ResourceUid.TextToId(uidStr);
        if (uid == ResourceUid.InvalidId)
            return ErrorInvalidParams($"Invalid UID format: {uidStr}");

        if (!ResourceUid.HasId(uid))
            return ErrorNotFound($"UID '{uidStr}'");

        var path = ResourceUid.GetIdPath(uid);
        return Success(new Dictionary { ["uid"] = uidStr, ["path"] = path });
    }

    private static Dictionary ProjectPathToUid(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        if (!ResourceLoader.Exists(path))
            return ErrorNotFound($"Resource at '{path}'");

        var uid = ResourceLoader.GetResourceUid(path);
        if (uid == ResourceUid.InvalidId)
            return Error(CodeNotFound, $"No UID assigned to '{path}'");

        var uidStr = ResourceUid.IdToText(uid);
        return Success(new Dictionary { ["path"] = path, ["uid"] = uidStr });
    }

    // ── add_autoload / remove_autoload ─────────────────────────────────────

    private static Dictionary AddAutoload(Dictionary @params)
    {
        var (autoloadName, err) = RequireString(@params, "name");
        if (err != null)
            return err;

        var (autoloadPath, err2) = RequireString(@params, "path");
        if (err2 != null)
            return err2;

        if (!Godot.FileAccess.FileExists(autoloadPath))
            return ErrorNotFound($"File '{autoloadPath}'");

        var settingKey = "autoload/" + autoloadName;
        if (ProjectSettings.HasSetting(settingKey))
        {
            return Error(CodeGeneric, $"Autoload '{autoloadName}' already exists", new Dictionary
            {
                ["current_value"] = GdStr(ProjectSettings.GetSetting(settingKey)),
                ["suggestion"] = "Use remove_autoload first to replace it",
            });
        }

        // Autoload format: "*res://path.gd" (the * prefix means it's a singleton)
        ProjectSettings.SetSetting(settingKey, "*" + autoloadPath);
        var saveErr = ProjectSettings.Save();
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save project settings: {saveErr}");

        return Success(new Dictionary
        {
            ["name"] = autoloadName,
            ["path"] = autoloadPath,
            ["added"] = true,
        });
    }

    private static Dictionary RemoveAutoload(Dictionary @params)
    {
        var (autoloadName, err) = RequireString(@params, "name");
        if (err != null)
            return err;

        var settingKey = "autoload/" + autoloadName;
        if (!ProjectSettings.HasSetting(settingKey))
            return ErrorNotFound($"Autoload '{autoloadName}'");

        var oldValue = GdStr(ProjectSettings.GetSetting(settingKey));
        ProjectSettings.Clear(settingKey);
        var saveErr = ProjectSettings.Save();
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save project settings: {saveErr}");

        return Success(new Dictionary
        {
            ["name"] = autoloadName,
            ["old_path"] = oldValue,
            ["removed"] = true,
        });
    }

    // ── glob matching (GDScript String.match equivalent) ───────────────────

    private static bool GlobMatch(string text, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, regexPattern, RegexOptions.None);
    }
}
