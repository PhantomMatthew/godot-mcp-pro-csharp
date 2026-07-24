using Godot;
using Godot.Collections;
using GodotMcpPro.Server;
using GodotMcpPro.Utils;

namespace GodotMcpPro.Commands;

/// <summary>Port of theme_commands.gd — theme overrides, StyleBoxFlat, setup_control, get_theme_info.</summary>
[Tool]
public partial class ThemeCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools()
    {
        var schemaNode = Schema.Str("Scene-relative path to a Control node.", "");
        var schemaName = Schema.Str("Override name (e.g. 'font_color', 'font_size', 'normal').", "");

        return new List<ToolDefinition>
        {
            new()
            {
                Name = "create_theme",
                Description = "Create an empty Theme resource file at the given res:// path.",
                Category = "theme",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["path"] = Schema.Str("res:// path ending in .theme."),
                    ["default_font_size"] = Schema.Int("Default font size for the theme.", 0),
                }, "path"),
                Handler = p => Task.FromResult(CreateTheme(p)),
            },
            new()
            {
                Name = "set_theme_color",
                Description = "Add a theme color override on a Control node, undo-tracked.",
                Category = "theme",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                    ["name"] = schemaName,
                    ["color"] = Schema.Any("Color value: '#rrggbb', 'Color(r,g,b[,a])', or {r,g,b,a} object."),
                    ["theme_type"] = Schema.Str("Theme type. Defaults to the control's class.", ""),
                }, "node_path", "name", "color"),
                Handler = p => Task.FromResult(SetThemeColor(p)),
            },
            new()
            {
                Name = "set_theme_constant",
                Description = "Add a theme constant (integer) override on a Control node, undo-tracked.",
                Category = "theme",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                    ["name"] = schemaName,
                    ["value"] = Schema.Int("Integer value.", 0),
                }, "node_path", "name"),
                Handler = p => Task.FromResult(SetThemeConstant(p)),
            },
            new()
            {
                Name = "set_theme_font_size",
                Description = "Add a theme font_size override on a Control node, undo-tracked.",
                Category = "theme",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                    ["name"] = schemaName,
                    ["size"] = Schema.Int("Font size.", 16),
                }, "node_path", "name"),
                Handler = p => Task.FromResult(SetThemeFontSize(p)),
            },
            new()
            {
                Name = "set_theme_stylebox",
                Description = "Build a StyleBoxFlat (bg_color, border, corner_radius, padding) and apply it as a theme stylebox override, undo-tracked.",
                Category = "theme",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                    ["name"] = schemaName,
                    ["bg_color"] = Schema.Str("Background color string.", ""),
                    ["border_color"] = Schema.Str("Border color string.", ""),
                    ["border_width"] = Schema.Int("Border width on all sides.", 0),
                    ["corner_radius"] = Schema.Int("Corner radius on all corners.", 0),
                    ["padding"] = Schema.Int("Content margin on all sides.", 0),
                }, "node_path", "name"),
                Handler = p => Task.FromResult(SetThemeStylebox(p)),
            },
            new()
            {
                Name = "setup_control",
                Description = "Apply a batch of layout properties to a Control (anchor_preset, min_size, size_flags, margins, separation, grow directions), undo-tracked.",
                Category = "theme",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                    ["anchor_preset"] = Schema.Str("One of: top_left, top_right, bottom_left, bottom_right, center_left, center_top, center_right, center_bottom, center, left_wide, top_wide, right_wide, bottom_wide, vcenter_wide, hcenter_wide, full_rect.", ""),
                    ["min_size"] = Schema.Str("Vector2 expression like 'Vector2(100,50)'.", ""),
                    ["size_flags_h"] = Schema.Str("fill/expand/fill_expand/shrink_center/shrink_end.", ""),
                    ["size_flags_v"] = Schema.Str("fill/expand/fill_expand/shrink_center/shrink_end.", ""),
                    ["margins"] = Schema.Obj("For MarginContainer: {left, top, right, bottom} ints."),
                    ["separation"] = Schema.Int("For BoxContainer: separation constant.", 0),
                    ["grow_h"] = Schema.Str("begin/end/both.", ""),
                    ["grow_v"] = Schema.Str("begin/end/both.", ""),
                }, "node_path"),
                Handler = p => Task.FromResult(SetupControl(p)),
            },
            new()
            {
                Name = "get_theme_info",
                Description = "Inspect a Control's theme (path, type_list) and list all theme_override_colors/constants/font_sizes/styleboxes.",
                Category = "theme",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                }, "node_path"),
                Handler = p => Task.FromResult(GetThemeInfo(p)),
            },
        };
    }

    private Dictionary CreateTheme(Dictionary @params)
    {
        var (path, err) = RequireString(@params, "path");
        if (err != null) return err;

        var theme = new Theme();
        var fontSize = OptionalInt(@params, "default_font_size", 0);
        if (fontSize > 0) theme.DefaultFontSize = fontSize;

        var sceneGuard = GuardOfflineSceneSave(path);
        if (sceneGuard.Count > 0) return sceneGuard;

        var dirGuard = EnsureParentDir(path);
        if (dirGuard.Count > 0) return dirGuard;

        var saveErr = ResourceSaver.Save(theme, path);
        if (saveErr != Godot.Error.Ok)
            return ErrorInternal($"Failed to save theme: {saveErr}");

        EditorInterface.Singleton.GetResourceFilesystem().Scan();
        return Success(new Dictionary { ["path"] = path, ["created"] = true });
    }

    private Dictionary SetThemeColor(Dictionary @params)
    {
        var (nodePath, e1) = RequireString(@params, "node_path");
        if (e1 != null) return e1;
        var (colorName, e2) = RequireString(@params, "name");
        if (e2 != null) return e2;
        var (colorStr, e3) = RequireString(@params, "color");
        if (e3 != null) return e3;

        var node = FindNodeByPath(nodePath);
        if (node is not Control control) return ErrorNotFound($"Control node at '{nodePath}'");

        var color = PropertyParser.ParseColor(colorStr);
        var themeType = OptionalString(@params, "theme_type", "");
        if (string.IsNullOrEmpty(themeType)) themeType = control.GetClass();

        var hadOld = control.HasThemeColorOverride(colorName);
        var oldValue = hadOld ? control.Get($"theme_override_colors/{colorName}") : default;

        var ur = GetUndoRedo();
        ur.CreateAction("MCP: Set theme color override");
        ur.AddDoMethod(control, "add_theme_color_override", colorName, color);
        ur.AddUndoMethod(this, MethodName.RestoreThemeOverride, control, "color", colorName, hadOld, oldValue);
        ur.CommitAction();

        return Success(new Dictionary { ["node_path"] = nodePath, ["name"] = colorName, ["color"] = colorStr });
    }

    private Dictionary SetThemeConstant(Dictionary @params)
    {
        var (nodePath, e1) = RequireString(@params, "node_path");
        if (e1 != null) return e1;
        var (constName, e2) = RequireString(@params, "name");
        if (e2 != null) return e2;

        var node = FindNodeByPath(nodePath);
        if (node is not Control control) return ErrorNotFound($"Control node at '{nodePath}'");

        var value = OptionalInt(@params, "value", 0);
        var hadOld = control.HasThemeConstantOverride(constName);
        var oldValue = hadOld ? control.Get($"theme_override_constants/{constName}") : default;

        var ur = GetUndoRedo();
        ur.CreateAction("MCP: Set theme constant override");
        ur.AddDoMethod(control, "add_theme_constant_override", constName, value);
        ur.AddUndoMethod(this, MethodName.RestoreThemeOverride, control, "constant", constName, hadOld, oldValue);
        ur.CommitAction();

        return Success(new Dictionary { ["node_path"] = nodePath, ["name"] = constName, ["value"] = value });
    }

    private Dictionary SetThemeFontSize(Dictionary @params)
    {
        var (nodePath, e1) = RequireString(@params, "node_path");
        if (e1 != null) return e1;
        var (fontName, e2) = RequireString(@params, "name");
        if (e2 != null) return e2;

        var node = FindNodeByPath(nodePath);
        if (node is not Control control) return ErrorNotFound($"Control node at '{nodePath}'");

        var size = OptionalInt(@params, "size", 16);
        var hadOld = control.HasThemeFontSizeOverride(fontName);
        var oldValue = hadOld ? control.Get($"theme_override_font_sizes/{fontName}") : default;

        var ur = GetUndoRedo();
        ur.CreateAction("MCP: Set theme font size override");
        ur.AddDoMethod(control, "add_theme_font_size_override", fontName, size);
        ur.AddUndoMethod(this, MethodName.RestoreThemeOverride, control, "font_size", fontName, hadOld, oldValue);
        ur.CommitAction();

        return Success(new Dictionary { ["node_path"] = nodePath, ["name"] = fontName, ["size"] = size });
    }

    private Dictionary SetThemeStylebox(Dictionary @params)
    {
        var (nodePath, e1) = RequireString(@params, "node_path");
        if (e1 != null) return e1;
        var (styleName, e2) = RequireString(@params, "name");
        if (e2 != null) return e2;

        var node = FindNodeByPath(nodePath);
        if (node is not Control control) return ErrorNotFound($"Control node at '{nodePath}'");

        var stylebox = new StyleBoxFlat();

        var bgColor = OptionalString(@params, "bg_color", "");
        if (!string.IsNullOrEmpty(bgColor)) stylebox.BgColor = PropertyParser.ParseColor(bgColor);

        var borderColor = OptionalString(@params, "border_color", "");
        if (!string.IsNullOrEmpty(borderColor)) stylebox.BorderColor = PropertyParser.ParseColor(borderColor);

        var borderWidth = OptionalInt(@params, "border_width", 0);
        if (borderWidth > 0)
        {
            stylebox.BorderWidthLeft = borderWidth;
            stylebox.BorderWidthTop = borderWidth;
            stylebox.BorderWidthRight = borderWidth;
            stylebox.BorderWidthBottom = borderWidth;
        }

        var cornerRadius = OptionalInt(@params, "corner_radius", 0);
        if (cornerRadius > 0)
        {
            stylebox.CornerRadiusTopLeft = cornerRadius;
            stylebox.CornerRadiusTopRight = cornerRadius;
            stylebox.CornerRadiusBottomLeft = cornerRadius;
            stylebox.CornerRadiusBottomRight = cornerRadius;
        }

        var padding = OptionalInt(@params, "padding", 0);
        if (padding > 0)
        {
            stylebox.ContentMarginLeft = padding;
            stylebox.ContentMarginTop = padding;
            stylebox.ContentMarginRight = padding;
            stylebox.ContentMarginBottom = padding;
        }

        var hadOld = control.HasThemeStyleboxOverride(styleName);
        var oldValue = hadOld ? control.Get($"theme_override_styles/{styleName}") : default;

        var ur = GetUndoRedo();
        ur.CreateAction("MCP: Set theme stylebox override");
        ur.AddDoMethod(control, "add_theme_stylebox_override", styleName, stylebox);
        ur.AddDoReference(stylebox);
        ur.AddUndoMethod(this, MethodName.RestoreThemeOverride, control, "stylebox", styleName, hadOld, oldValue);
        if (oldValue.VariantType == Variant.Type.Object && oldValue.AsGodotObject() is Resource oldRes)
            ur.AddUndoReference(oldRes);
        ur.CommitAction();

        return Success(new Dictionary { ["node_path"] = nodePath, ["name"] = styleName, ["type"] = "StyleBoxFlat" });
    }

    /// <summary>Undo helper invoked by EditorUndoRedoManager. Signature must match the AddUndoMethod calls.</summary>
    public void RestoreThemeOverride(Control control, string kind, string overrideName, bool hadOld, Variant oldValue)
    {
        switch (kind)
        {
            case "color":
                if (hadOld) control.AddThemeColorOverride(overrideName, oldValue.AsColor());
                else control.RemoveThemeColorOverride(overrideName);
                break;
            case "constant":
                if (hadOld) control.AddThemeConstantOverride(overrideName, oldValue.AsInt32());
                else control.RemoveThemeConstantOverride(overrideName);
                break;
            case "font_size":
                if (hadOld) control.AddThemeFontSizeOverride(overrideName, oldValue.AsInt32());
                else control.RemoveThemeFontSizeOverride(overrideName);
                break;
            case "stylebox":
                if (hadOld) control.AddThemeStyleboxOverride(overrideName, oldValue.As<StyleBox>());
                else control.RemoveThemeStyleboxOverride(overrideName);
                break;
        }
    }

    private static readonly System.Collections.Generic.Dictionary<string, Control.LayoutPreset> PresetMap = new()
    {
        ["top_left"] = Control.LayoutPreset.TopLeft,
        ["top_right"] = Control.LayoutPreset.TopRight,
        ["bottom_left"] = Control.LayoutPreset.BottomLeft,
        ["bottom_right"] = Control.LayoutPreset.BottomRight,
        ["center_left"] = Control.LayoutPreset.CenterLeft,
        ["center_top"] = Control.LayoutPreset.CenterTop,
        ["center_right"] = Control.LayoutPreset.CenterRight,
        ["center_bottom"] = Control.LayoutPreset.CenterBottom,
        ["center"] = Control.LayoutPreset.Center,
        ["left_wide"] = Control.LayoutPreset.LeftWide,
        ["top_wide"] = Control.LayoutPreset.TopWide,
        ["right_wide"] = Control.LayoutPreset.RightWide,
        ["bottom_wide"] = Control.LayoutPreset.BottomWide,
        ["vcenter_wide"] = Control.LayoutPreset.VcenterWide,
        ["hcenter_wide"] = Control.LayoutPreset.HcenterWide,
        ["full_rect"] = Control.LayoutPreset.FullRect,
    };

    private static readonly System.Collections.Generic.Dictionary<string, Control.SizeFlags> SizeFlagsMap = new()
    {
        ["fill"] = Control.SizeFlags.Fill,
        ["expand"] = Control.SizeFlags.Expand,
        ["fill_expand"] = Control.SizeFlags.ExpandFill,
        ["shrink_center"] = Control.SizeFlags.ShrinkCenter,
        ["shrink_end"] = Control.SizeFlags.ShrinkEnd,
    };

    private static readonly System.Collections.Generic.Dictionary<string, Control.GrowDirection> GrowMap = new()
    {
        ["begin"] = Control.GrowDirection.Begin,
        ["end"] = Control.GrowDirection.End,
        ["both"] = Control.GrowDirection.Both,
    };

    private static readonly string[] LayoutProperties =
    {
        "anchor_left", "anchor_top", "anchor_right", "anchor_bottom",
        "offset_left", "offset_top", "offset_right", "offset_bottom",
        "custom_minimum_size", "size_flags_horizontal", "size_flags_vertical",
        "grow_horizontal", "grow_vertical",
    };

    private static readonly string[] ThemeConstantNames =
        { "margin_left", "margin_top", "margin_right", "margin_bottom", "separation" };

    private static Dictionary CaptureControlSetupState(Control control)
    {
        var props = new Dictionary();
        foreach (var p in LayoutProperties) props[p] = control.Get(p);
        var themeConstants = new Dictionary();
        foreach (var name in ThemeConstantNames)
        {
            var had = control.HasThemeConstantOverride(name);
            themeConstants[name] = new Dictionary
            {
                ["had"] = had,
                ["value"] = had ? control.Get($"theme_override_constants/{name}") : default,
            };
        }
        return new Dictionary { ["properties"] = props, ["theme_constants"] = themeConstants };
    }

    private Dictionary SetupControl(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return err;

        var node = FindNodeByPath(nodePath);
        if (node is not Control control) return ErrorNotFound($"Control node at '{nodePath}'");

        var applied = new Godot.Collections.Array();
        var oldState = CaptureControlSetupState(control);
        var target = (Control)control.Duplicate();

        var anchorPreset = OptionalString(@params, "anchor_preset", "");
        if (!string.IsNullOrEmpty(anchorPreset) && PresetMap.TryGetValue(anchorPreset, out var preset))
        {
            target.SetAnchorsAndOffsetsPreset(preset);
            applied.Add($"anchor_preset={anchorPreset}");
        }

        var minSizeStr = OptionalString(@params, "min_size", "");
        if (!string.IsNullOrEmpty(minSizeStr))
        {
            var expr = new Expression();
            if (expr.Parse(minSizeStr) == Godot.Error.Ok)
            {
                var val = expr.Execute();
                if (val.VariantType == Variant.Type.Vector2)
                {
                    target.CustomMinimumSize = val.AsVector2();
                    applied.Add($"min_size={minSizeStr}");
                }
            }
        }

        var sfH = OptionalString(@params, "size_flags_h", "");
        if (!string.IsNullOrEmpty(sfH) && SizeFlagsMap.TryGetValue(sfH, out var sf))
        {
            target.SizeFlagsHorizontal = sf;
            applied.Add($"size_flags_h={sfH}");
        }

        var sfV = OptionalString(@params, "size_flags_v", "");
        if (!string.IsNullOrEmpty(sfV) && SizeFlagsMap.TryGetValue(sfV, out var sf2))
        {
            target.SizeFlagsVertical = sf2;
            applied.Add($"size_flags_v={sfV}");
        }

        if (@params.TryGetValue("margins", out var mVar) && mVar.VariantType == Variant.Type.Dictionary && target is MarginContainer mc)
        {
            var margins = mVar.AsGodotDictionary();
            if (margins.TryGetValue("left", out var ml)) mc.AddThemeConstantOverride("margin_left", CoerceInt(ml));
            if (margins.TryGetValue("top", out var mt)) mc.AddThemeConstantOverride("margin_top", CoerceInt(mt));
            if (margins.TryGetValue("right", out var mr)) mc.AddThemeConstantOverride("margin_right", CoerceInt(mr));
            if (margins.TryGetValue("bottom", out var mb)) mc.AddThemeConstantOverride("margin_bottom", CoerceInt(mb));
            applied.Add($"margins={margins}");
        }

        if (@params.TryGetValue("separation", out var sepVar) && target is BoxContainer box)
        {
            var sep = CoerceInt(sepVar);
            box.AddThemeConstantOverride("separation", sep);
            applied.Add($"separation={sep}");
        }

        var growH = OptionalString(@params, "grow_h", "");
        if (!string.IsNullOrEmpty(growH) && GrowMap.TryGetValue(growH, out var gh))
        {
            target.GrowHorizontal = gh;
            applied.Add($"grow_h={growH}");
        }

        var growV = OptionalString(@params, "grow_v", "");
        if (!string.IsNullOrEmpty(growV) && GrowMap.TryGetValue(growV, out var gv))
        {
            target.GrowVertical = gv;
            applied.Add($"grow_v={growV}");
        }

        if (applied.Count > 0)
        {
            var newState = CaptureControlSetupState(target);
            RegisterControlSetupUndo(control, oldState, newState);
        }
        target.QueueFree();

        return Success(new Dictionary { ["node_path"] = nodePath, ["applied"] = applied, ["count"] = applied.Count });
    }

    private void RegisterControlSetupUndo(Control control, Dictionary oldState, Dictionary newState)
    {
        var ur = GetUndoRedo();
        ur.CreateAction("MCP: Setup Control");
        var newProps = newState["properties"].AsGodotDictionary();
        var oldProps = oldState["properties"].AsGodotDictionary();
        foreach (var property in newProps.Keys)
        {
            ur.AddDoProperty(control, property.AsString(), newProps[property]);
            ur.AddUndoProperty(control, property.AsString(), oldProps[property]);
        }
        var newConstants = newState["theme_constants"].AsGodotDictionary();
        var oldConstants = oldState["theme_constants"].AsGodotDictionary();
        foreach (var name in newConstants.Keys)
        {
            var nameStr = name.AsString();
            var newConstant = newConstants[name].AsGodotDictionary();
            var oldConstant = oldConstants[name].AsGodotDictionary();
            ur.AddDoMethod(this, MethodName.RestoreThemeOverride, control, "constant", nameStr,
                newConstant["had"].AsBool(), newConstant["value"]);
            ur.AddUndoMethod(this, MethodName.RestoreThemeOverride, control, "constant", nameStr,
                oldConstant["had"].AsBool(), oldConstant["value"]);
        }
        ur.CommitAction();
    }

    private Dictionary GetThemeInfo(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return err;

        var node = FindNodeByPath(nodePath);
        if (node is not Control control) return ErrorNotFound($"Control node at '{nodePath}'");

        var info = new Dictionary { ["node_path"] = nodePath, ["class"] = control.GetClass() };

        var theme = control.Theme;
        if (theme != null)
        {
            info["theme_path"] = theme.ResourcePath;
            info["type_list"] = theme.GetTypeList();
        }

        var overrides = new Dictionary
        {
            ["colors"] = new Dictionary(),
            ["constants"] = new Dictionary(),
            ["font_sizes"] = new Dictionary(),
            ["styleboxes"] = new Dictionary(),
        };
        var colors = overrides["colors"].AsGodotDictionary();
        var constants = overrides["constants"].AsGodotDictionary();
        var fontSizes = overrides["font_sizes"].AsGodotDictionary();
        var styleboxes = overrides["styleboxes"].AsGodotDictionary();

        foreach (var prop in control.GetPropertyList())
        {
            var pname = prop["name"].AsString();
            if (pname.StartsWith("theme_override_colors/"))
            {
                var key = pname["theme_override_colors/".Length..];
                colors[key] = "#" + control.Get(pname).AsColor().ToHtml();
            }
            else if (pname.StartsWith("theme_override_constants/"))
            {
                var key = pname["theme_override_constants/".Length..];
                constants[key] = control.Get(pname);
            }
            else if (pname.StartsWith("theme_override_font_sizes/"))
            {
                var key = pname["theme_override_font_sizes/".Length..];
                fontSizes[key] = control.Get(pname);
            }
            else if (pname.StartsWith("theme_override_styles/"))
            {
                var key = pname["theme_override_styles/".Length..];
                var style = control.Get(pname);
                styleboxes[key] = style.VariantType == Variant.Type.Object
                    ? style.AsGodotObject()?.GetClass() ?? ""
                    : "";
            }
        }

        info["overrides"] = overrides;
        return Success(info);
    }
}
