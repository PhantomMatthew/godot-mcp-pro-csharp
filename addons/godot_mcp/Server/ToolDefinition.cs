using Godot.Collections;

namespace GodotMcpPro.Server;

/// <summary>
/// Metadata + handler for a single MCP tool.
/// </summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON Schema (as a Godot dictionary) describing the tool input.</summary>
    public required Dictionary InputSchema { get; init; }
    /// <summary>Executes on the main thread. Returns {"result": {...}} or {"error": {...}}.</summary>
    public required Func<Dictionary, Task<Dictionary>> Handler { get; init; }
    /// <summary>Category label shown in the UI / used by mode filtering (e.g. "scene", "node").</summary>
    public string Category { get; init; } = "general";
    /// <summary>Destructive tools may be gated behind confirmation by clients.</summary>
    public bool Destructive { get; init; }
}

/// <summary>
/// Terse builders for JSON-Schema fragments (as Godot dictionaries) used in tool inputSchema.
/// </summary>
public static class Schema
{
    public static Dictionary Object(
        System.Collections.Generic.Dictionary<string, Dictionary>? properties = null,
        params string[] required)
    {
        var props = new Dictionary();
        if (properties != null)
            foreach (var (key, value) in properties)
                props[key] = value;
        var schema = new Dictionary
        {
            ["type"] = "object",
            ["properties"] = props,
        };
        if (required.Length > 0)
        {
            var req = new Godot.Collections.Array();
            foreach (var r in required) req.Add(r);
            schema["required"] = req;
        }
        return schema;
    }

    public static Dictionary Str(string description, string? @default = null)
    {
        var d = new Dictionary { ["type"] = "string", ["description"] = description };
        if (@default != null) d["default"] = @default;
        return d;
    }

    public static Dictionary Enum(string description, params string[] values)
    {
        var vals = new Godot.Collections.Array();
        foreach (var v in values) vals.Add(v);
        return new Dictionary { ["type"] = "string", ["description"] = description, ["enum"] = vals };
    }

    public static Dictionary Int(string description, long? @default = null)
    {
        var d = new Dictionary { ["type"] = "integer", ["description"] = description };
        if (@default.HasValue) d["default"] = @default.Value;
        return d;
    }

    public static Dictionary Num(string description, double? @default = null)
    {
        var d = new Dictionary { ["type"] = "number", ["description"] = description };
        if (@default.HasValue) d["default"] = @default.Value;
        return d;
    }

    public static Dictionary Bool(string description, bool? @default = null)
    {
        var d = new Dictionary { ["type"] = "boolean", ["description"] = description };
        if (@default.HasValue) d["default"] = @default.Value;
        return d;
    }

    public static Dictionary Arr(string description, Dictionary? items = null)
    {
        var d = new Dictionary { ["type"] = "array", ["description"] = description };
        if (items != null) d["items"] = items;
        return d;
    }

    public static Dictionary Obj(string description) =>
        new() { ["type"] = "object", ["description"] = description };

    /// <summary>Value of any JSON type (string/number/bool/object/array) — used for smart-parsed values.
    /// NOTE: omitting "type" makes strict MCP clients serialize object values to a JSON string.
    /// Prefer <see cref="Vec3"/>/<see cref="Obj"/> for structured params.</summary>
    public static Dictionary Any(string description) =>
        new() { ["description"] = description };

    /// <summary>Vector3 parameter declared as a {x,y,z} object so strict MCP clients pass an
    /// object (not a stringified dict). The server still accepts a "Vector3(x,y,z)" string too.</summary>
    public static Dictionary Vec3(string description) => new()
    {
        ["type"] = "object",
        ["description"] = description,
        ["properties"] = new Dictionary
        {
            ["x"] = new Dictionary { ["type"] = "number" },
            ["y"] = new Dictionary { ["type"] = "number" },
            ["z"] = new Dictionary { ["type"] = "number" },
        },
    };
}
