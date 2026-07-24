using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Translation tools: discover, read, and write Godot translation files.
/// CSV translations use the first row as locale headers ("keys", "en", "de", ...)
/// and the first column as the message key. Gettext .po files are listed with
/// their locale inferred from the file name (&lt;name&gt;.&lt;locale&gt;.po).
/// </summary>
[Tool]
public partial class TranslationCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "list_translations",
            Description = "Find all translation files (*.csv and *.po) under res://. " +
                "Returns [{path, locales[], entry_count}] for each file.",
            Category = "translation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("Directory to search recursively (default res://).", "res://"),
            }),
            Handler = p => Task.FromResult(ListTranslations(p)),
        },
        new()
        {
            Name = "read_translation",
            Description = "Read a CSV translation file. Returns {path, locales[], entries[{key, translations{locale: text}}]}.",
            Category = "translation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the CSV translation file (e.g. res://translations/game.csv)."),
            }, "path"),
            Handler = p => Task.FromResult(ReadTranslation(p)),
        },
        new()
        {
            Name = "write_translation",
            Description = "Create or overwrite a CSV translation file. Columns follow the given locales[] order; " +
                "when locales[] is omitted, the union of translation keys across entries is used.",
            Category = "translation",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the CSV translation file to create or overwrite (must end with .csv)."),
                ["entries"] = Schema.Arr("Translation entries: [{key: \"GREETING\", translations: {en: \"Hello\", de: \"Hallo\"}}, ...]."),
                ["locales"] = Schema.Arr("Locale codes defining the CSV column order (e.g. [\"en\", \"de\"]). " +
                    "Optional when entries already carry translations."),
            }, "path", "entries"),
            Handler = p => Task.FromResult(WriteTranslation(p)),
        },
    };

    // ── list_translations ────────────────────────────────────────────────────

    private static Dictionary ListTranslations(Dictionary @params)
    {
        var rootPath = OptionalString(@params, "path", "res://");
        if (string.IsNullOrEmpty(rootPath))
            rootPath = "res://";

        var files = new Godot.Collections.Array();
        FindTranslationFiles(rootPath, files);

        var translations = new Godot.Collections.Array();
        foreach (var fileVariant in files)
        {
            var path = fileVariant.AsString();
            var ext = path.GetExtension().ToLowerInvariant();
            var info = ext == "csv" ? InspectCsv(path) : InspectPo(path);
            if (info != null)
                translations.Add(info);
        }

        return Success(new Dictionary
        {
            ["translations"] = translations,
            ["count"] = translations.Count,
            ["search_path"] = rootPath,
        });
    }

    private static void FindTranslationFiles(string path, Godot.Collections.Array results)
    {
        var dir = DirAccess.Open(path);
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
                FindTranslationFiles(fullPath, results);
            }
            else
            {
                var ext = fileName.GetExtension().ToLowerInvariant();
                if (ext is "csv" or "po")
                    results.Add(fullPath);
            }

            fileName = dir.GetNext();
        }
        dir.ListDirEnd();
    }

    private static Dictionary? InspectCsv(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
            return null;

        var headers = file.GetCsvLine();
        if (headers.Length < 2)
            return null;

        var locales = new Godot.Collections.Array();
        for (var i = 1; i < headers.Length; i++)
        {
            var locale = headers[i].StripEdges();
            if (!string.IsNullOrEmpty(locale))
                locales.Add(locale);
        }

        var count = 0;
        while (!file.EofReached())
        {
            var line = file.GetCsvLine();
            if (line.Length > 0 && !string.IsNullOrEmpty(line[0].StripEdges()))
                count++;
        }

        return new Dictionary
        {
            ["path"] = path,
            ["type"] = "csv",
            ["locales"] = locales,
            ["entry_count"] = count,
        };
    }

    private static Dictionary? InspectPo(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
            return null;

        var count = 0;
        while (!file.EofReached())
        {
            var line = file.GetLine().StripEdges();
            // Skip the metadata header entry (msgid "")
            if (line.StartsWith("msgid") && !line.StartsWith("msgid \"\""))
                count++;
        }

        // Godot convention: <name>.<locale>.po
        var stem = path.GetFile().GetBaseName();
        var locale = stem.GetExtension();
        var locales = new Godot.Collections.Array();
        if (!string.IsNullOrEmpty(locale))
            locales.Add(locale);

        return new Dictionary
        {
            ["path"] = path,
            ["type"] = "po",
            ["locales"] = locales,
            ["entry_count"] = count,
        };
    }

    // ── read_translation ─────────────────────────────────────────────────────

    private static Dictionary ReadTranslation(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        var normalized = NormalizeProjectPath(path);
        if (normalized.GetExtension().ToLowerInvariant() != "csv")
            return ErrorInvalidParams("read_translation only supports CSV translation files (path must end with .csv)");
        if (!Godot.FileAccess.FileExists(normalized))
            return ErrorNotFound($"Translation file '{normalized}'");

        using var file = Godot.FileAccess.Open(normalized, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
            return ErrorInternal($"Could not open '{normalized}': {Godot.FileAccess.GetOpenError()}");

        var headers = file.GetCsvLine();
        if (headers.Length < 2)
            return Error(CodeGeneric,
                $"'{normalized}' is not a valid translation CSV (header row needs a key column plus at least one locale column)");

        var locales = new List<string>();
        for (var i = 1; i < headers.Length; i++)
            locales.Add(headers[i].StripEdges());

        var entries = new Godot.Collections.Array();
        while (!file.EofReached())
        {
            var line = file.GetCsvLine();
            if (line.Length == 0)
                continue;
            var key = line[0].StripEdges();
            if (string.IsNullOrEmpty(key))
                continue;

            var translations = new Dictionary();
            for (var i = 1; i < headers.Length; i++)
                translations[locales[i - 1]] = i < line.Length ? line[i] : "";

            entries.Add(new Dictionary
            {
                ["key"] = key,
                ["translations"] = translations,
            });
        }

        var localesArr = new Godot.Collections.Array();
        foreach (var locale in locales)
            localesArr.Add(locale);

        return Success(new Dictionary
        {
            ["path"] = normalized,
            ["locales"] = localesArr,
            ["entries"] = entries,
            ["entry_count"] = entries.Count,
        });
    }

    // ── write_translation ────────────────────────────────────────────────────

    private static Dictionary WriteTranslation(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        var normalized = NormalizeProjectPath(path);
        if (!normalized.StartsWith("res://"))
            return ErrorInvalidParams("Path must be inside the project (res://)");
        if (normalized.GetExtension().ToLowerInvariant() != "csv")
            return ErrorInvalidParams("write_translation only supports CSV files (path must end with .csv)");

        var entriesParam = OptionalArray(@params, "entries");
        if (entriesParam == null)
            return ErrorInvalidParams("Missing required parameter: entries (Array)");

        // Parse entries into rows
        var rows = new List<(string Key, System.Collections.Generic.Dictionary<string, string> Translations)>();
        foreach (var entryVariant in entriesParam)
        {
            if (entryVariant.VariantType != Variant.Type.Dictionary)
                return ErrorInvalidParams("Each entry must be a Dictionary {key, translations}");
            var entry = entryVariant.AsGodotDictionary();

            var key = entry.TryGetValue("key", out var keyVariant) && keyVariant.VariantType == Variant.Type.String
                ? keyVariant.AsString()
                : "";
            if (string.IsNullOrEmpty(key))
                return ErrorInvalidParams("Each entry must have a non-empty 'key' string");

            var translations = new System.Collections.Generic.Dictionary<string, string>();
            if (entry.TryGetValue("translations", out var tVariant) && tVariant.VariantType == Variant.Type.Dictionary)
            {
                var tDict = tVariant.AsGodotDictionary();
                foreach (var tKeyVariant in tDict.Keys)
                {
                    var tValue = tDict[tKeyVariant];
                    translations[tKeyVariant.AsString()] =
                        tValue.VariantType == Variant.Type.String ? tValue.AsString() : tValue.ToString();
                }
            }
            rows.Add((key, translations));
        }

        // Locales: explicit param wins; otherwise union of entry translation keys
        var locales = new List<string>();
        var localesParam = OptionalArray(@params, "locales");
        if (localesParam != null)
        {
            foreach (var locVariant in localesParam)
            {
                if (locVariant.VariantType != Variant.Type.String)
                    continue;
                var locale = locVariant.AsString().StripEdges();
                if (!string.IsNullOrEmpty(locale) && !locales.Contains(locale))
                    locales.Add(locale);
            }
        }
        if (locales.Count == 0)
        {
            foreach (var (_, translations) in rows)
            {
                foreach (var locale in translations.Keys)
                {
                    if (!locales.Contains(locale))
                        locales.Add(locale);
                }
            }
        }
        if (locales.Count == 0)
            return ErrorInvalidParams("No locales specified. Pass locales[] or provide translations in entries.");

        var guard = EnsureParentDir(normalized);
        if (guard.Count > 0)
            return guard;

        var existed = Godot.FileAccess.FileExists(normalized);
        using var file = Godot.FileAccess.Open(normalized, Godot.FileAccess.ModeFlags.Write);
        if (file == null)
            return ErrorInternal($"Could not open '{normalized}' for writing: {Godot.FileAccess.GetOpenError()}");

        var header = new List<string> { "keys" };
        header.AddRange(locales);
        file.StoreCsvLine(header.ToArray());

        foreach (var (key, translations) in rows)
        {
            var row = new List<string> { key };
            foreach (var locale in locales)
                row.Add(translations.TryGetValue(locale, out var text) ? text : "");
            file.StoreCsvLine(row.ToArray());
        }

        // Let the editor pick up the new/changed file
        EditorInterface.Singleton.GetResourceFilesystem().Scan();

        var localesArr = new Godot.Collections.Array();
        foreach (var locale in locales)
            localesArr.Add(locale);

        return Success(new Dictionary
        {
            ["path"] = normalized,
            ["locales"] = localesArr,
            ["entry_count"] = rows.Count,
            ["created"] = !existed,
        });
    }
}
