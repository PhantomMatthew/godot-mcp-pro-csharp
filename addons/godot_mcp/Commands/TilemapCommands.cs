using Godot;
using Godot.Collections;
using GodotMcpPro.Server;

namespace GodotMcpPro.Commands;

/// <summary>Port of tilemap_commands.gd — supports both TileMapLayer and legacy TileMap.</summary>
[Tool]
public partial class TilemapCommands : BaseCommand
{
    public override IReadOnlyList<ToolDefinition> GetTools()
    {
        var schemaNode = Schema.Str("Scene-relative path to a TileMapLayer or legacy TileMap node.", "");
        return new List<ToolDefinition>
        {
            new()
            {
                Name = "tilemap_set_cell",
                Description = "Set a single tile cell on a TileMapLayer or legacy TileMap, undo-tracked.",
                Category = "tilemap",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                    ["x"] = Schema.Int("Cell X coordinate."),
                    ["y"] = Schema.Int("Cell Y coordinate."),
                    ["source_id"] = Schema.Int("TileSet source id.", 0),
                    ["atlas_x"] = Schema.Int("Atlas column.", 0),
                    ["atlas_y"] = Schema.Int("Atlas row.", 0),
                    ["alternative"] = Schema.Int("Alternative tile id.", 0),
                    ["layer"] = Schema.Int("Legacy TileMap layer index (ignored for TileMapLayer).", 0),
                }, "node_path"),
                Handler = p => Task.FromResult(SetCell(p)),
            },
            new()
            {
                Name = "tilemap_fill_rect",
                Description = "Fill a rectangular region of cells with the same tile, undo-tracked.",
                Category = "tilemap",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                    ["x1"] = Schema.Int("Rect corner X 1."),
                    ["y1"] = Schema.Int("Rect corner Y 1."),
                    ["x2"] = Schema.Int("Rect corner X 2."),
                    ["y2"] = Schema.Int("Rect corner Y 2."),
                    ["source_id"] = Schema.Int("TileSet source id.", 0),
                    ["atlas_x"] = Schema.Int("Atlas column.", 0),
                    ["atlas_y"] = Schema.Int("Atlas row.", 0),
                    ["alternative"] = Schema.Int("Alternative tile id.", 0),
                    ["layer"] = Schema.Int("Legacy TileMap layer index.", 0),
                }, "node_path"),
                Handler = p => Task.FromResult(FillRect(p)),
            },
            new()
            {
                Name = "tilemap_get_cell",
                Description = "Read a single tile cell: source_id, atlas_coords, alternative, empty.",
                Category = "tilemap",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                    ["x"] = Schema.Int("Cell X coordinate."),
                    ["y"] = Schema.Int("Cell Y coordinate."),
                    ["layer"] = Schema.Int("Legacy TileMap layer index.", 0),
                }, "node_path"),
                Handler = p => Task.FromResult(GetCell(p)),
            },
            new()
            {
                Name = "tilemap_clear",
                Description = "Clear tiles on the given layer(s) or the whole map, undo-tracked. Destructive.",
                Category = "tilemap",
                Destructive = true,
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                    ["layer"] = Schema.Int("Legacy TileMap layer index. If omitted, clears every layer.", 0),
                }, "node_path"),
                Handler = p => Task.FromResult(Clear(p)),
            },
            new()
            {
                Name = "tilemap_get_info",
                Description = "Inspect a TileMapLayer/TileMap: layer info, used cell counts, TileSet sources, tile size.",
                Category = "tilemap",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                }, "node_path"),
                Handler = p => Task.FromResult(GetInfo(p)),
            },
            new()
            {
                Name = "tilemap_get_used_cells",
                Description = "List used cells (x, y, layer, source_id) up to max_count.",
                Category = "tilemap",
                InputSchema = Schema.Object(new System.Collections.Generic.Dictionary<string, Dictionary>
                {
                    ["node_path"] = schemaNode,
                    ["layer"] = Schema.Int("Legacy TileMap layer index.", 0),
                    ["max_count"] = Schema.Int("Cap on returned cells.", 500),
                }, "node_path"),
                Handler = p => Task.FromResult(GetUsedCells(p)),
            },
        };
    }

    private static Node? FindTilemapNode(string nodePath)
    {
        var node = FindNodeByPath(nodePath);
        if (node is TileMapLayer) return node;
        if (node != null && node.GetClass() == "TileMap") return node;
        return null;
    }

    private static Dictionary NotFoundResult(string nodePath) =>
        ErrorNotFound($"TileMapLayer or TileMap at '{nodePath}'",
            "Use TileMapLayer for current Godot projects, or pass a deprecated TileMap node with optional layer.");

    private static bool IsLegacyTilemap(Node node) => node != null && node.GetClass() == "TileMap";

    private static int GetTilemapLayerCount(Node tilemap) =>
        tilemap is TileMapLayer ? 1 : (int)tilemap.Call("get_layers_count");

    private static Godot.Collections.Array GetUsedCellsRaw(Node tilemap, int layer)
    {
        if (tilemap is TileMapLayer layerNode) return layerNode.Call("get_used_cells").AsGodotArray();
        return tilemap.Call("get_used_cells", layer).AsGodotArray();
    }

    private static long GetCellSourceId(Node tilemap, int layer, Vector2I coords)
    {
        if (tilemap is TileMapLayer layerNode) return layerNode.Call("get_cell_source_id", coords).AsInt64();
        return tilemap.Call("get_cell_source_id", layer, coords).AsInt64();
    }

    private static Vector2I GetCellAtlasCoords(Node tilemap, int layer, Vector2I coords)
    {
        if (tilemap is TileMapLayer layerNode) return layerNode.Call("get_cell_atlas_coords", coords).AsVector2I();
        return tilemap.Call("get_cell_atlas_coords", layer, coords).AsVector2I();
    }

    private static long GetCellAlternativeTile(Node tilemap, int layer, Vector2I coords)
    {
        if (tilemap is TileMapLayer layerNode) return layerNode.Call("get_cell_alternative_tile", coords).AsInt64();
        return tilemap.Call("get_cell_alternative_tile", layer, coords).AsInt64();
    }

    private static Dictionary ValidateLayer(Node tilemap, int layer)
    {
        if (tilemap is TileMapLayer) return new Dictionary();
        var layerCount = GetTilemapLayerCount(tilemap);
        if (layerCount <= 0) return ErrorInvalidParams("TileMap has no layers");
        if (layer < -layerCount || layer >= layerCount)
            return ErrorInvalidParams($"layer {layer} is out of range for TileMap with {layerCount} layers");
        return new Dictionary();
    }

    /// <summary>Resolve single layer index for cell operations. Returns error dictionary when invalid.</summary>
    private static (int Layer, Dictionary? Error) GetSingleLayer(Node tilemap, Dictionary @params)
    {
        if (tilemap is TileMapLayer)
        {
            if (@params.TryGetValue("layer", out var l) && CoerceInt(l) != 0)
                return (0, ErrorInvalidParams("layer only applies to deprecated TileMap nodes; TileMapLayer has one implicit layer"));
            return (0, null);
        }
        var layer = OptionalInt(@params, "layer", 0);
        var err = ValidateLayer(tilemap, layer);
        if (err.Count > 0) return (layer, err);
        return (layer, null);
    }

    private record CellData(int Layer, Vector2I Coords, long SourceId, Vector2I AtlasCoords, long Alternative);

    private static CellData CaptureCell(Node tilemap, int layer, Vector2I coords) =>
        new(layer, coords, GetCellSourceId(tilemap, layer, coords),
            GetCellAtlasCoords(tilemap, layer, coords),
            GetCellAlternativeTile(tilemap, layer, coords));

    private static void AddDoSetCells(EditorUndoRedoManager ur, Node tilemap, List<CellData> cells)
    {
        foreach (var c in cells)
        {
            if (tilemap is TileMapLayer)
                ur.AddDoMethod(tilemap, "set_cell", c.Coords, c.SourceId, c.AtlasCoords, c.Alternative);
            else
                ur.AddDoMethod(tilemap, "set_cell", c.Layer, c.Coords, c.SourceId, c.AtlasCoords, c.Alternative);
        }
    }

    private static void AddUndoSetCells(EditorUndoRedoManager ur, Node tilemap, List<CellData> cells)
    {
        foreach (var c in cells)
        {
            if (tilemap is TileMapLayer)
                ur.AddUndoMethod(tilemap, "set_cell", c.Coords, c.SourceId, c.AtlasCoords, c.Alternative);
            else
                ur.AddUndoMethod(tilemap, "set_cell", c.Layer, c.Coords, c.SourceId, c.AtlasCoords, c.Alternative);
        }
    }

    private Dictionary SetCell(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return err;

        var tilemap = FindTilemapNode(nodePath);
        if (tilemap == null) return NotFoundResult(nodePath);

        var (layer, lErr) = GetSingleLayer(tilemap, @params);
        if (lErr != null) return lErr;

        var x = OptionalInt(@params, "x", 0);
        var y = OptionalInt(@params, "y", 0);
        var sourceId = OptionalInt(@params, "source_id", 0);
        var atlasX = OptionalInt(@params, "atlas_x", 0);
        var atlasY = OptionalInt(@params, "atlas_y", 0);
        var alternative = OptionalInt(@params, "alternative", 0);

        var coords = new Vector2I(x, y);
        var oldCells = new List<CellData> { CaptureCell(tilemap, layer, coords) };
        var newCells = new List<CellData>
        {
            new(layer, coords, sourceId, new Vector2I(atlasX, atlasY), alternative),
        };

        var ur = GetUndoRedo();
        ur.CreateAction("MCP: Set TileMap cell");
        AddDoSetCells(ur, tilemap, newCells);
        AddUndoSetCells(ur, tilemap, oldCells);
        ur.CommitAction();

        return Success(new Dictionary
        {
            ["x"] = x, ["y"] = y, ["layer"] = layer,
            ["node_class"] = tilemap.GetClass(),
            ["source_id"] = sourceId,
            ["atlas_coords"] = new Godot.Collections.Array { atlasX, atlasY },
        });
    }

    private Dictionary FillRect(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return err;

        var tilemap = FindTilemapNode(nodePath);
        if (tilemap == null) return NotFoundResult(nodePath);

        var (layer, lErr) = GetSingleLayer(tilemap, @params);
        if (lErr != null) return lErr;

        var x1 = OptionalInt(@params, "x1", 0);
        var y1 = OptionalInt(@params, "y1", 0);
        var x2 = OptionalInt(@params, "x2", 0);
        var y2 = OptionalInt(@params, "y2", 0);
        var sourceId = OptionalInt(@params, "source_id", 0);
        var atlasX = OptionalInt(@params, "atlas_x", 0);
        var atlasY = OptionalInt(@params, "atlas_y", 0);
        var alternative = OptionalInt(@params, "alternative", 0);

        var count = 0;
        var oldCells = new List<CellData>();
        var newCells = new List<CellData>();
        for (var cx = Mathf.Min(x1, x2); cx <= Mathf.Max(x1, x2); cx++)
        {
            for (var cy = Mathf.Min(y1, y2); cy <= Mathf.Max(y1, y2); cy++)
            {
                var coords = new Vector2I(cx, cy);
                oldCells.Add(CaptureCell(tilemap, layer, coords));
                newCells.Add(new CellData(layer, coords, sourceId, new Vector2I(atlasX, atlasY), alternative));
                count++;
            }
        }

        var ur = GetUndoRedo();
        ur.CreateAction("MCP: Fill TileMap rect");
        AddDoSetCells(ur, tilemap, newCells);
        AddUndoSetCells(ur, tilemap, oldCells);
        ur.CommitAction();

        return Success(new Dictionary
        {
            ["filled"] = count,
            ["rect"] = new Godot.Collections.Array { x1, y1, x2, y2 },
            ["layer"] = layer,
            ["node_class"] = tilemap.GetClass(),
        });
    }

    private Dictionary GetCell(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return err;

        var tilemap = FindTilemapNode(nodePath);
        if (tilemap == null) return NotFoundResult(nodePath);

        var (layer, lErr) = GetSingleLayer(tilemap, @params);
        if (lErr != null) return lErr;

        var x = OptionalInt(@params, "x", 0);
        var y = OptionalInt(@params, "y", 0);
        var coords = new Vector2I(x, y);

        var sourceId = GetCellSourceId(tilemap, layer, coords);
        var atlasCoords = GetCellAtlasCoords(tilemap, layer, coords);
        var alternative = GetCellAlternativeTile(tilemap, layer, coords);

        return Success(new Dictionary
        {
            ["x"] = x, ["y"] = y, ["layer"] = layer,
            ["node_class"] = tilemap.GetClass(),
            ["source_id"] = sourceId,
            ["atlas_coords"] = new Godot.Collections.Array { atlasCoords.X, atlasCoords.Y },
            ["alternative"] = alternative,
            ["empty"] = sourceId == -1,
        });
    }

    private Dictionary Clear(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return err;

        var tilemap = FindTilemapNode(nodePath);
        if (tilemap == null) return NotFoundResult(nodePath);

        // Resolve which layers to clear
        List<int> layers;
        if (tilemap is TileMapLayer)
        {
            var (l, le) = GetSingleLayer(tilemap, @params);
            if (le != null) return le;
            layers = new List<int> { 0 };
        }
        else if (@params.ContainsKey("layer"))
        {
            var (l, le) = GetSingleLayer(tilemap, @params);
            if (le != null) return le;
            layers = new List<int> { l };
        }
        else
        {
            layers = new List<int>();
            for (var i = 0; i < GetTilemapLayerCount(tilemap); i++) layers.Add(i);
        }

        // Capture old cells for undo
        var oldCells = new List<CellData>();
        foreach (var l in layers)
            foreach (var c in GetUsedCellsRaw(tilemap, l))
                oldCells.Add(CaptureCell(tilemap, l, c.AsVector2I()));

        var ur = GetUndoRedo();
        ur.CreateAction("MCP: Clear TileMap");
        // Do-clear: clear() for TileMapLayer or full clear, else clear_layer per layer
        if (tilemap is TileMapLayer || layers.Count == GetTilemapLayerCount(tilemap))
            ur.AddDoMethod(tilemap, "clear");
        else
            foreach (var l in layers) ur.AddDoMethod(tilemap, "clear_layer", l);
        AddUndoSetCells(ur, tilemap, oldCells);
        ur.CommitAction();

        var layersArr = new Godot.Collections.Array();
        foreach (var l in layers) layersArr.Add(l);
        return Success(new Dictionary
        {
            ["cleared"] = true,
            ["layers"] = layersArr,
            ["node_class"] = tilemap.GetClass(),
        });
    }

    private Dictionary GetInfo(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return err;

        var tilemap = FindTilemapNode(nodePath);
        if (tilemap == null) return NotFoundResult(nodePath);

        var tileSet = tilemap.Get("tile_set").As<TileSet>();
        var sources = new Godot.Collections.Array();
        if (tileSet != null)
        {
            for (var i = 0; i < tileSet.GetSourceCount(); i++)
            {
                var sourceId = tileSet.GetSourceId(i);
                var source = tileSet.GetSource(sourceId);
                var info = new Dictionary { ["id"] = sourceId, ["type"] = source.GetClass() };
                if (source is TileSetAtlasSource atlas)
                {
                    info["texture"] = atlas.Texture != null ? atlas.Texture.ResourcePath : "";
                    info["tile_count"] = atlas.GetTilesCount();
                }
                sources.Add(info);
            }
        }

        var layers = GetLayerInfo(tilemap);
        var usedCells = 0;
        foreach (var layerInfo in layers)
        {
            var liDict = layerInfo.AsGodotDictionary();
            usedCells += liDict.TryGetValue("used_cells", out var uc) ? uc.AsInt32() : 0;
        }

        var tileSize = tileSet != null
            ? new Godot.Collections.Array { tileSet.TileSize.X, tileSet.TileSize.Y }
            : new Godot.Collections.Array { 0, 0 };

        return Success(new Dictionary
        {
            ["node_path"] = nodePath,
            ["node_class"] = tilemap.GetClass(),
            ["layer_count"] = layers.Count,
            ["layers"] = layers,
            ["used_cells"] = usedCells,
            ["tile_set_sources"] = sources,
            ["tile_size"] = tileSize,
        });
    }

    private static Godot.Collections.Array GetLayerInfo(Node tilemap)
    {
        var layers = new Godot.Collections.Array();
        if (tilemap is TileMapLayer layerNode)
        {
            layers.Add(new Dictionary
            {
                ["index"] = 0,
                ["name"] = layerNode.Name.ToString(),
                ["enabled"] = true,
                ["used_cells"] = GetUsedCellsRaw(tilemap, 0).Count,
            });
            return layers;
        }
        for (var layer = 0; layer < GetTilemapLayerCount(tilemap); layer++)
        {
            layers.Add(new Dictionary
            {
                ["index"] = layer,
                ["name"] = tilemap.Call("get_layer_name", layer).AsString(),
                ["enabled"] = tilemap.Call("is_layer_enabled", layer).AsBool(),
                ["used_cells"] = GetUsedCellsRaw(tilemap, layer).Count,
            });
        }
        return layers;
    }

    private Dictionary GetUsedCells(Dictionary @params)
    {
        var (nodePath, err) = RequireString(@params, "node_path");
        if (err != null) return err;

        var tilemap = FindTilemapNode(nodePath);
        if (tilemap == null) return NotFoundResult(nodePath);

        var (layer, lErr) = GetSingleLayer(tilemap, @params);
        if (lErr != null) return lErr;

        var maxCount = Mathf.Max(0, OptionalInt(@params, "max_count", 500));
        var used = GetUsedCellsRaw(tilemap, layer);
        var cells = new Godot.Collections.Array();
        var limit = Mathf.Min(used.Count, maxCount);
        for (var i = 0; i < limit; i++)
        {
            var pos = used[i].AsVector2I();
            cells.Add(new Dictionary
            {
                ["x"] = pos.X, ["y"] = pos.Y, ["layer"] = layer,
                ["source_id"] = GetCellSourceId(tilemap, layer, pos),
            });
        }

        return Success(new Dictionary
        {
            ["cells"] = cells,
            ["total"] = used.Count,
            ["returned"] = cells.Count,
            ["layer"] = layer,
            ["node_class"] = tilemap.GetClass(),
        });
    }
}
