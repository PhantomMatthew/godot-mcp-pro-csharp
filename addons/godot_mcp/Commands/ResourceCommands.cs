using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>
/// Resource read/edit/create/preview tools.
/// Faithful port of addons/godot_mcp/commands/resource_commands.gd.
/// </summary>
[Tool]
public partial class ResourceCommands : BaseCommand
{
    private static readonly HashSet<string> ImageExtensions = new()
    {
        "png", "jpg", "jpeg", "bmp", "webp", "svg",
    };

    public override IReadOnlyList<ToolDefinition> GetTools() => new List<ToolDefinition>
    {
        new()
        {
            Name = "read_resource",
            Description = "Load a .tres/.res (or other) resource file and return its editor-visible " +
                "properties, serialized to JSON-safe values.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the resource to read."),
            }, "path"),
            Category = "resource",
            Handler = p => Task.FromResult(ReadResource(p)),
        },
        new()
        {
            Name = "edit_resource",
            Description = "Load a resource, apply new values to one or more properties (smart-parsed to " +
                "match each property's existing type), save it back to disk, and report old/new values " +
                "for every changed property.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the resource to edit."),
                ["properties"] = Schema.Obj("Map of property name to new value."),
            }, "path", "properties"),
            Category = "resource",
            Handler = p => Task.FromResult(EditResource(p)),
        },
        new()
        {
            Name = "create_resource",
            Description = "Instantiate a new Resource of the given ClassDB type, optionally set initial " +
                "properties, and save it to a new .tres/.res file. Refuses to overwrite an existing file " +
                "unless overwrite=true.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path to save the new resource to."),
                ["type"] = Schema.Str("ClassDB class name of the Resource to instantiate, e.g. 'StyleBoxFlat'."),
                ["properties"] = Schema.Obj("Optional map of initial property values to apply."),
                ["overwrite"] = Schema.Bool("Overwrite the file if it already exists.", false),
            }, "path", "type"),
            Category = "resource",
            Destructive = true,
            Handler = p => Task.FromResult(CreateResource(p)),
        },
        new()
        {
            Name = "get_resource_preview",
            Description = "Generate a PNG preview (base64-encoded) of an image file or a resource with an " +
                "extractable image (Texture2D, Image), resized to fit within max_size while preserving " +
                "aspect ratio.",
            InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
            {
                ["path"] = Schema.Str("res:// path of the image or image-bearing resource."),
                ["max_size"] = Schema.Int("Maximum width/height of the preview in pixels.", 256),
            }, "path"),
            Category = "resource",
            Handler = p => Task.FromResult(GetResourcePreview(p)),
        },
    };

    // ── read_resource ────────────────────────────────────────────────────────

    private static Dictionary ReadResource(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Resource '{path}'");

        var guard = GuardOfflineSceneSave(path);
        if (guard.Count > 0)
            return guard;

        var resource = ResourceLoader.Load(path);
        if (resource == null)
            return ErrorInternal($"Failed to load resource: {path}");

        var props = new Dictionary();
        foreach (var propInfo in resource.GetPropertyList())
        {
            var propName = propInfo["name"].AsString();
            var usage = propInfo["usage"].AsInt32();
            if ((usage & (int)PropertyUsageFlags.Editor) == 0)
                continue;
            if (propName.StartsWith('_') || propName == "script" || propName == "resource_local_to_scene"
                || propName == "resource_name" || propName == "resource_path")
                continue;
            props[propName] = PropertyParser.SerializeValue(resource.Get(propName));
        }

        return Success(new Dictionary
        {
            ["path"] = path,
            ["type"] = resource.GetClass(),
            ["resource_name"] = resource.ResourceName,
            ["properties"] = props,
        });
    }

    // ── edit_resource ────────────────────────────────────────────────────────

    private static Dictionary EditResource(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        var newProps = OptionalDict(@params, "properties");
        if (newProps == null)
            return ErrorInvalidParams("'properties' dictionary is required");

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Resource '{path}'");

        var guard = GuardOfflineSceneSave(path);
        if (guard.Count > 0)
            return guard;

        var resource = ResourceLoader.Load(path);
        if (resource == null)
            return ErrorInternal($"Failed to load resource: {path}");

        var changed = new Dictionary();
        foreach (var propName in newProps.Keys)
        {
            var propNameStr = propName.AsString();
            if (!HasProperty(resource, propNameStr))
                continue;
            var oldValue = resource.Get(propNameStr);
            var targetType = oldValue.VariantType;
            var newValue = PropertyParser.ParseValue(newProps[propName], targetType);
            resource.Set(propNameStr, newValue);
            changed[propNameStr] = new Dictionary
            {
                ["old"] = PropertyParser.SerializeValue(oldValue),
                ["new"] = PropertyParser.SerializeValue(resource.Get(propNameStr)),
            };
        }

        if (changed.Count == 0)
            return Success(new Dictionary { ["path"] = path, ["changed"] = new Dictionary(), ["message"] = "No properties were changed" });

        var saveErr = ResourceSaver.Save(resource, path);
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save resource: {saveErr}");

        return Success(new Dictionary
        {
            ["path"] = path,
            ["type"] = resource.GetClass(),
            ["changed"] = changed,
        });
    }

    // ── create_resource ──────────────────────────────────────────────────────

    private static Dictionary CreateResource(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        var (resourceType, err2) = RequireString(@params, "type");
        if (err2 != null)
            return err2;

        if (!ClassDB.ClassExists(resourceType))
            return ErrorInvalidParams($"Unknown resource type: {resourceType}");
        if (!ClassDB.IsParentClass(resourceType, "Resource"))
            return ErrorInvalidParams($"'{resourceType}' is not a Resource type");

        var overwrite = OptionalBool(@params, "overwrite", false);
        if (Godot.FileAccess.FileExists(path) && !overwrite)
            return Error(-32000, $"Resource already exists: {path}",
                new Dictionary { ["suggestion"] = "Set overwrite=true to replace" });

        var guard = GuardOfflineSceneSave(path);
        if (guard.Count > 0)
            return guard;

        var resourceVariant = ClassDB.Instantiate(resourceType);
        if (resourceVariant.VariantType != Variant.Type.Object || resourceVariant.AsGodotObject() is not Resource resource)
            return ErrorInternal($"Failed to instantiate: {resourceType}");

        // Apply properties
        var properties = OptionalDict(@params, "properties") ?? new Dictionary();
        foreach (var propName in properties.Keys)
        {
            var propNameStr = propName.AsString();
            if (HasProperty(resource, propNameStr))
            {
                var current = resource.Get(propNameStr);
                resource.Set(propNameStr, PropertyParser.ParseValue(properties[propName], current.VariantType));
            }
        }

        var dirGuard = EnsureParentDir(path);
        if (dirGuard.Count > 0)
            return dirGuard;

        var saveErr = ResourceSaver.Save(resource, path);
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save resource '{path}': {saveErr}");

        // Rescan filesystem
        EditorInterface.Singleton.GetResourceFilesystem().Scan();

        var propertiesSet = new Godot.Collections.Array();
        foreach (var k in properties.Keys)
            propertiesSet.Add(k);

        return Success(new Dictionary
        {
            ["path"] = path,
            ["type"] = resourceType,
            ["properties_set"] = propertiesSet,
        });
    }

    // ── get_resource_preview ─────────────────────────────────────────────────

    private static Dictionary GetResourcePreview(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null)
            return err;

        if (!Godot.FileAccess.FileExists(path))
            return ErrorNotFound($"Resource '{path}'");

        var maxSize = OptionalInt(@params, "max_size", 256);
        Image? image = null;

        // Try loading as image file directly
        var ext = path.GetExtension().ToLowerInvariant();
        if (ImageExtensions.Contains(ext))
        {
            image = new Image();
            var loadErr = image.Load(path);
            if (loadErr != Godot.Error.Ok)
                return ErrorInternal($"Failed to load image: {loadErr}");
        }
        else
        {
            // Try loading as resource and extracting image
            var resource = ResourceLoader.Load(path);
            if (resource == null)
                return ErrorInternal($"Failed to load resource: {path}");

            if (resource is Texture2D texture)
                image = texture.GetImage();
            else if (resource is Image resImage)
                image = resImage;
            else
                return ErrorInvalidParams($"Resource type '{resource.GetClass()}' does not have an image preview");
        }

        if (image == null)
            return ErrorInternal("Could not extract image from resource");

        // Resize if needed
        if (image.GetWidth() > maxSize || image.GetHeight() > maxSize)
        {
            var scaleX = (float)maxSize / image.GetWidth();
            var scaleY = (float)maxSize / image.GetHeight();
            var scale = Mathf.Min(scaleX, scaleY);
            var newW = (int)(image.GetWidth() * scale);
            var newH = (int)(image.GetHeight() * scale);
            image.Resize(newW, newH, Image.Interpolation.Lanczos);
        }

        var pngBuffer = image.SavePngToBuffer();
        var base64 = Marshalls.RawToBase64(pngBuffer);

        return Success(new Dictionary
        {
            ["image_base64"] = base64,
            ["width"] = image.GetWidth(),
            ["height"] = image.GetHeight(),
            ["format"] = "png",
            ["path"] = path,
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Equivalent to GDScript's `prop_name in resource`.</summary>
    private static bool HasProperty(GodotObject obj, string propName)
    {
        foreach (var propInfo in obj.GetPropertyList())
        {
            if (propInfo["name"].AsString() == propName)
                return true;
        }
        return false;
    }
}
