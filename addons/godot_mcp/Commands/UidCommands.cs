using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>
/// Project-wide UID maintenance tools. Godot 4.4+ tracks every text-format
/// resource (scripts, scenes, .tres, shaders) by a stable UID persisted in a
/// sidecar '&lt;file&gt;.uid' file next to the resource. These tools locate
/// resources whose sidecar is absent or unparseable and repair them by
/// minting a fresh ResourceUID, registering it, and writing the sidecar.
/// </summary>
[Tool]
public partial class UidCommands : BaseCommand
{
    /// <summary>Text-resource extensions the editor tracks via '.uid' sidecar files.
    /// Binary formats (.scn/.res, imported assets) embed or import-track their UIDs instead.</summary>
    private static readonly string[] UidSidecarExtensions =
        { "gd", "cs", "tscn", "tres", "gdshader", "gdshaderinc" };

    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "batch_update_uids",
            Description = "Scan res:// for text-format resources (scripts, scenes, .tres, shaders) missing " +
                "their '.uid' sidecar file and generate one for each: a fresh UID is minted via " +
                "ResourceUid.CreateId(), registered with ResourceUid.AddId(), written to '<file>.uid', " +
                "and an EditorFileSystem.Scan() is triggered so the editor reconciles its UID cache. " +
                "Files with an existing valid UID are skipped.",
            InputSchema = Schema.Object(),
            Category = "uid",
            Handler = p => Task.FromResult(BatchUpdateUids(p)),
        },
        new()
        {
            Name = "find_missing_uids",
            Description = "Scan res:// for text-format resources without a valid UID: either the '.uid' " +
                "sidecar file is absent (has_uid_file=false), or it exists but its contents do not parse " +
                "to a UID registered in ResourceUID (has_uid_file=true, stale/corrupt sidecar). " +
                "Read-only; use batch_update_uids to repair.",
            InputSchema = Schema.Object(),
            Category = "uid",
            Handler = p => Task.FromResult(FindMissingUids(p)),
        },
    };

    // ── batch_update_uids ────────────────────────────────────────────────────

    private static Dictionary BatchUpdateUids(Dictionary @params)
    {
        var candidates = CollectUidCandidates("res://");
        var scanned = candidates.Count;
        var updated = 0;
        var skipped = 0;
        var failed = new Godot.Collections.Array();

        foreach (var path in candidates)
        {
            if (HasValidUid(path))
            {
                skipped++;
                continue;
            }

            // Mint and persist a fresh UID (also regenerates stale/corrupt sidecars).
            var id = ResourceUid.Singleton.CreateId();
            using (var f = Godot.FileAccess.Open(path + ".uid", Godot.FileAccess.ModeFlags.Write))
            {
                if (f == null)
                {
                    failed.Add(path);
                    continue;
                }
                f.StoreString(ResourceUid.IdToText(id) + "\n");
            }
            ResourceUid.Singleton.AddId(id, path);
            updated++;
        }

        // Let the editor notice the new/updated sidecar files and reconcile its UID cache.
        EditorInterface.Singleton.GetResourceFilesystem().Scan();

        var missingAfter = 0;
        foreach (var path in candidates)
            if (!HasValidUid(path))
                missingAfter++;

        var result = new Dictionary
        {
            ["scanned"] = scanned,
            ["updated"] = updated,
            ["skipped"] = skipped,
            ["missing_after"] = missingAfter,
        };
        if (failed.Count > 0)
            result["failed"] = failed;
        return Success(result);
    }

    // ── find_missing_uids ────────────────────────────────────────────────────

    private static Dictionary FindMissingUids(Dictionary @params)
    {
        var candidates = CollectUidCandidates("res://");
        var missing = new Godot.Collections.Array();

        foreach (var path in candidates)
        {
            if (HasValidUid(path))
                continue;
            missing.Add(new Dictionary
            {
                ["path"] = path,
                ["has_uid_file"] = Godot.FileAccess.FileExists(path + ".uid"),
            });
        }

        return Success(new Dictionary
        {
            ["missing"] = missing,
            ["count"] = missing.Count,
        });
    }

    // ── shared helpers ───────────────────────────────────────────────────────

    /// <summary>True when '<path>.uid' exists AND parses to a UID currently registered in ResourceUid.</summary>
    private static bool HasValidUid(string path)
    {
        var uidPath = path + ".uid";
        if (!Godot.FileAccess.FileExists(uidPath))
            return false;

        string text;
        using (var f = Godot.FileAccess.Open(uidPath, Godot.FileAccess.ModeFlags.Read))
        {
            if (f == null)
                return false;
            text = f.GetAsText().StripEdges();
        }

        var id = ResourceUid.TextToId(text);
        if (id == ResourceUid.InvalidId)
            return false;
        return ResourceUid.Singleton.HasId(id);
    }

    /// <summary>Recursively collect res:// files with UID-sidecar extensions (skips hidden dirs like .godot).</summary>
    private static List<string> CollectUidCandidates(string root)
    {
        var files = new List<string>();
        CollectUidCandidatesRecursive(root, files);
        return files;
    }

    private static void CollectUidCandidatesRecursive(string path, List<string> outFiles)
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
                CollectUidCandidatesRecursive(fullPath, outFiles);
            }
            else
            {
                var ext = fileName.GetExtension().ToLowerInvariant();
                if (System.Array.IndexOf(UidSidecarExtensions, ext) >= 0)
                    outFiles.Add(fullPath);
            }

            fileName = dir.GetNext();
        }

        dir.ListDirEnd();
    }
}
