using Godot;
using Godot.Collections;

namespace GodotMcpPro.Utils;

/// <summary>
/// Smart type parsing and JSON-safe serialization for tool parameters.
/// Faithful port of addons/godot_mcp/utils/property_parser.gd.
///
/// Contract:
///  - ParseValue: strings like "Vector2(x,y)", "Vector3(...)", "Rect2(...)",
///    "Color(r,g,b[,a])", "#rrggbb[aa]", "true"/"false", ints/floats and
///    "res://"/"uid://" paths (auto-loaded to Resource) are converted.
///    Dictionaries {x,y[,z]} and arrays are also accepted for vectors.
///  - SerializeValue: Vector2/2i/3/3i => {x,y[,z]}; Rect2 => {x,y,width,height};
///    Color => {r,g,b,a,html}; NodePath => string; Resource => {type,path};
///    Array/Dictionary recursed.
/// </summary>
public static class PropertyParser
{
    /// <summary>Parse a value into the appropriate Godot type.</summary>
    public static Variant ParseValue(Variant value, Variant.Type targetType = Variant.Type.Nil)
    {
        if (value.VariantType == Variant.Type.Nil)
            return default;

        if (targetType == Variant.Type.Nil)
            return AutoParse(value);

        switch (targetType)
        {
            case Variant.Type.Bool:
                if (value.VariantType == Variant.Type.Bool) return value;
                if (value.VariantType == Variant.Type.String)
                {
                    var s = value.AsString().ToLowerInvariant();
                    return s is "true" or "1" or "yes";
                }
                return value.AsBool();
            case Variant.Type.Int:
                return ToInt(value);
            case Variant.Type.Float:
                return ToFloat(value);
            case Variant.Type.String:
                return Stringify(value);
            case Variant.Type.Vector2:
                return ParseVector2(value);
            case Variant.Type.Vector2I:
                return ParseVector2I(value);
            case Variant.Type.Vector3:
                return ParseVector3(value);
            case Variant.Type.Vector3I:
                return ParseVector3I(value);
            case Variant.Type.Rect2:
                return ParseRect2(value);
            case Variant.Type.Color:
                return ParseColor(value);
            case Variant.Type.NodePath:
                return new NodePath(Stringify(value));
            case Variant.Type.Array:
                if (value.VariantType == Variant.Type.Array) return value;
                return new Godot.Collections.Array { value };
            case Variant.Type.Dictionary:
                if (value.VariantType == Variant.Type.Dictionary) return value;
                return new Dictionary();
            case Variant.Type.Object:
                if (value.VariantType == Variant.Type.Object) return value;
                if (value.VariantType == Variant.Type.String)
                {
                    var s = value.AsString();
                    if ((s.StartsWith("res://") || s.StartsWith("uid://")) && ResourceLoader.Exists(s))
                        return ResourceLoader.Load(s);
                    // Unresolvable string for an Object-typed property: return nil so
                    // callers can detect the failure instead of coercing silently.
                    return default;
                }
                return value;
            default:
                return value;
        }
    }

    private static Variant AutoParse(Variant value)
    {
        if (value.VariantType != Variant.Type.String)
            return value;

        var s = value.AsString();

        if (s == "true") return true;
        if (s == "false") return false;

        if (s.IsValidInt()) return s.ToInt();
        if (s.IsValidFloat()) return s.ToFloat();

        if (s.StartsWith("Vector2(") || s.StartsWith("Vector2i("))
            return ParseVector2(s);
        if (s.StartsWith("Vector3(") || s.StartsWith("Vector3i("))
            return ParseVector3(s);
        if (s.StartsWith("Color(") || s.StartsWith("#"))
            return ParseColor(s);
        if (s.StartsWith("Rect2("))
            return ParseRect2(s);

        if ((s.StartsWith("res://") || s.StartsWith("uid://")) && ResourceLoader.Exists(s))
            return ResourceLoader.Load(s);

        return s;
    }

    private static readonly string[] Prefixes =
        { "Vector3i(", "Vector3(", "Vector2i(", "Vector2(", "Rect2(", "Color(", "(" };

    private static double[] ExtractNumbers(string s)
    {
        var cleaned = s;
        foreach (var prefix in Prefixes)
        {
            if (cleaned.StartsWith(prefix))
            {
                cleaned = cleaned[prefix.Length..];
                break;
            }
        }
        cleaned = cleaned.TrimSuffix(")").StripEdges();

        var parts = cleaned.Split(',');
        var numbers = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            numbers[i] = parts[i].StripEdges().ToFloat();
        return numbers;
    }

    public static Vector2 ParseVector2(Variant value)
    {
        if (value.VariantType == Variant.Type.Vector2) return value.AsVector2();
        if (value.VariantType == Variant.Type.Vector2I) return value.AsVector2I();
        if (value.VariantType == Variant.Type.Dictionary)
        {
            var d = value.AsGodotDictionary();
            return new Vector2(
                (float)GetNum(d, "x"),
                (float)GetNum(d, "y"));
        }
        var s = Stringify(value);
        if (s.Length > 0 && s[0] == '{')
        {
            var reparsed = Json.ParseString(s);
            if (reparsed.VariantType == Variant.Type.Dictionary)
                return ParseVector2(reparsed);
        }
        var nums = ExtractNumbers(s);
        if (nums.Length >= 2)
            return new Vector2((float)nums[0], (float)nums[1]);
        return Vector2.Zero;
    }

    public static Vector2I ParseVector2I(Variant value)
    {
        var v = ParseVector2(value);
        return new Vector2I((int)v.X, (int)v.Y);
    }

    public static Vector3 ParseVector3(Variant value)
    {
        if (value.VariantType == Variant.Type.Vector3) return value.AsVector3();
        if (value.VariantType == Variant.Type.Vector3I) return value.AsVector3I();
        if (value.VariantType == Variant.Type.Dictionary)
        {
            var d = value.AsGodotDictionary();
            return new Vector3(
                (float)GetNum(d, "x"),
                (float)GetNum(d, "y"),
                (float)GetNum(d, "z"));
        }
        // Some MCP clients serialize a {x,y,z} object to a JSON string when the schema
        // omits "type". Re-parse such strings back to a dict before falling back to
        // ExtractNumbers (which throws FormatException on "{").
        var s = Stringify(value);
        if (s.Length > 0 && s[0] == '{')
        {
            var reparsed = Json.ParseString(s);
            if (reparsed.VariantType == Variant.Type.Dictionary)
                return ParseVector3(reparsed);
        }
        var nums = ExtractNumbers(s);
        if (nums.Length >= 3)
            return new Vector3((float)nums[0], (float)nums[1], (float)nums[2]);
        return Vector3.Zero;
    }

    public static Vector3I ParseVector3I(Variant value)
    {
        var v = ParseVector3(value);
        return new Vector3I((int)v.X, (int)v.Y, (int)v.Z);
    }

    public static Rect2 ParseRect2(Variant value)
    {
        if (value.VariantType == Variant.Type.Rect2) return value.AsRect2();
        if (value.VariantType == Variant.Type.Dictionary)
        {
            var d = value.AsGodotDictionary();
            var w = d.ContainsKey("w") ? GetNum(d, "w") : GetNum(d, "width");
            var h = d.ContainsKey("h") ? GetNum(d, "h") : GetNum(d, "height");
            return new Rect2((float)GetNum(d, "x"), (float)GetNum(d, "y"), (float)w, (float)h);
        }
        var nums = ExtractNumbers(Stringify(value));
        if (nums.Length >= 4)
            return new Rect2((float)nums[0], (float)nums[1], (float)nums[2], (float)nums[3]);
        return new Rect2();
    }

    public static Color ParseColor(Variant value)
    {
        if (value.VariantType == Variant.Type.Color) return value.AsColor();
        var s = Stringify(value);
        // Tolerate stringified {r,g,b,a} dicts from strict clients.
        if (s.Length > 0 && s[0] == '{')
        {
            var reparsed = Json.ParseString(s);
            if (reparsed.VariantType == Variant.Type.Dictionary)
                return ParseColor(reparsed);
        }
        if (s.StartsWith("#"))
            return Color.FromHtml(s);
        if (s.StartsWith("Color("))
        {
            var nums = ExtractNumbers(s);
            switch (nums.Length)
            {
                case 3: return new Color((float)nums[0], (float)nums[1], (float)nums[2]);
                case 4: return new Color((float)nums[0], (float)nums[1], (float)nums[2], (float)nums[3]);
            }
        }
        if (Color.HtmlIsValid(s))
            return Color.FromHtml(s);
        return Colors.White;
    }

    /// <summary>Serialize a Variant to a JSON-safe representation.</summary>
    public static Variant SerializeValue(Variant value)
    {
        switch (value.VariantType)
        {
            case Variant.Type.Nil:
                return default;
            case Variant.Type.Vector2:
            {
                var v = value.AsVector2();
                return new Dictionary { ["x"] = v.X, ["y"] = v.Y };
            }
            case Variant.Type.Vector2I:
            {
                var v = value.AsVector2I();
                return new Dictionary { ["x"] = v.X, ["y"] = v.Y };
            }
            case Variant.Type.Vector3:
            {
                var v = value.AsVector3();
                return new Dictionary { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
            }
            case Variant.Type.Vector3I:
            {
                var v = value.AsVector3I();
                return new Dictionary { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
            }
            case Variant.Type.Rect2:
            {
                var r = value.AsRect2();
                return new Dictionary
                {
                    ["x"] = r.Position.X, ["y"] = r.Position.Y,
                    ["width"] = r.Size.X, ["height"] = r.Size.Y
                };
            }
            case Variant.Type.Color:
            {
                var c = value.AsColor();
                return new Dictionary
                {
                    ["r"] = c.R, ["g"] = c.G, ["b"] = c.B, ["a"] = c.A,
                    ["html"] = "#" + c.ToHtml()
                };
            }
            case Variant.Type.NodePath:
                return value.AsNodePath().ToString();
            case Variant.Type.Object:
            {
                var obj = value.AsGodotObject();
                if (obj is Resource res)
                    return new Dictionary { ["type"] = res.GetClass(), ["path"] = res.ResourcePath };
                return obj?.ToString() ?? "";
            }
            case Variant.Type.Array:
            {
                var arr = value.AsGodotArray();
                var result = new Godot.Collections.Array();
                foreach (var item in arr)
                    result.Add(SerializeValue(item));
                return result;
            }
            case Variant.Type.Dictionary:
            {
                var dict = value.AsGodotDictionary();
                var result = new Dictionary();
                foreach (var key in dict.Keys)
                    result[Stringify(key)] = SerializeValue(dict[key]);
                return result;
            }
            default:
                return value;
        }
    }

    // -- helpers --------------------------------------------------------

    private static string Stringify(Variant value) =>
        value.VariantType == Variant.Type.String ? value.AsString() : value.ToString();

    private static double GetNum(Dictionary d, string key)
    {
        if (!d.TryGetValue(key, out var v)) return 0;
        return v.VariantType switch
        {
            Variant.Type.Int => v.AsInt64(),
            Variant.Type.Float => v.AsDouble(),
            Variant.Type.String => v.AsString().ToFloat(),
            Variant.Type.Bool => v.AsBool() ? 1 : 0,
            _ => 0,
        };
    }

    private static long ToInt(Variant value) => value.VariantType switch
    {
        Variant.Type.Int => value.AsInt64(),
        Variant.Type.Float => (long)value.AsDouble(),
        Variant.Type.String => value.AsString().ToInt(),
        Variant.Type.Bool => value.AsBool() ? 1 : 0,
        _ => 0,
    };

    private static double ToFloat(Variant value) => value.VariantType switch
    {
        Variant.Type.Int => value.AsInt64(),
        Variant.Type.Float => value.AsDouble(),
        Variant.Type.String => value.AsString().ToFloat(),
        Variant.Type.Bool => value.AsBool() ? 1 : 0,
        _ => 0,
    };
}
