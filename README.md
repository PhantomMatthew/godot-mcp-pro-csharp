# Godot MCP Pro CSharp

Self-contained MCP (Model Context Protocol) server for the Godot 4 editor, written in C#.

C# rewrite of [godot-mcp-pro](https://github.com/PhantomMatthew/godot-mcp-pro) (GDScript). No Node.js runtime required — the MCP server runs entirely inside the Godot editor process.

## Architecture

```
AI Assistant ──stdio──> Bridge Process ──HTTP──> Godot Editor Plugin (C#)
AI Assistant ──HTTP──> Godot Editor Plugin (C#)   (direct, no bridge needed)
```

- **Streamable HTTP** transport built into the editor plugin (default port 65001–65005)
- **stdio bridge** for MCP clients that only speak stdio (`bridge/` console app)
- **File-IPC** between editor and running game (3 autoload services)
- **239 tools** across 39 categories

## Requirements

- Godot 4.4+ with .NET support (tested on 4.7 mono)
- .NET 8.0 SDK

## Installation

### 1. Plugin

1. Copy `addons/godot_mcp/` into your project's `addons/` directory.
2. Open the project in **Godot 4.4+ (.NET)**. The C# project builds automatically on first load.
3. Enable **Godot MCP Pro CSharp** in Project Settings → Plugins.
4. Check the editor bottom panel — a **"MCP Pro"** tab appears showing `● connected` and the HTTP port.

> **Note:** Godot's *Attach Script* / *New Script* dialog defaults to the project's **last used language**, stored in per-project editor metadata (`.godot/editor/project_metadata.cfg` → `[script_setup] last_selected_language`). The engine only writes that key after you create a script through the dialog — which is why a project that never did so keeps offering **GDScript** even after *Reload Current Project*, while a project where you already created a C# script once opens with **C#** preselected. This plugin seeds that key to **C#** on startup, so the dialog opens with C# preselected from the first run. The *Create New Plugin* dialog (Project Settings → Plugins), in contrast, is hardcoded to GDScript by the engine and ignores that key — switch it to C# manually there. Neither default affects loading/reloading existing plugins: the plugin's language is determined solely by the `script` path in `plugin.cfg` (here `GodotMcpPlugin.cs`).

### 2. MCP Client Setup

#### Option A: stdio (Claude Code, Cline, Cursor)

```bash
cd bridge/
dotnet build -c Release
```

Copy `.mcp.json.example` to your project root as `.mcp.json`, then edit the bridge DLL path:

```json
{
  "mcpServers": {
    "godot-mcp-pro-csharp": {
      "command": "dotnet",
      "args": ["/absolute/path/to/bridge/bin/Release/net8.0/godot-mcp-bridge.dll"],
      "env": { "GODOT_MCP_HTTP_PORT": "65001" }
    }
  }
}
```

#### Option B: Direct HTTP (Cursor, Windsurf, custom clients)

Point your MCP client at `http://127.0.0.1:65001/mcp`. The port auto-scans 65001–65005.

### 3. Claude Code Permission Presets (optional)

Copy `settings.local.json` (conservative: read + safe-write auto-approved) or `settings.local.permissive.json` (all tools auto-approved, destructive tools denied) to `~/.claude/settings.local.json`.

## Verification

After installation, verify the server is running:

1. **Editor bottom panel**: Look for the "MCP Pro" tab. Status shows `● connected` with the port number.
2. **curl test**:
   ```bash
   curl -X POST http://127.0.0.1:65001/mcp \
     -H "Content-Type: application/json" \
     -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'
   ```
   Expected: JSON response with `serverInfo.name = "godot-mcp-pro-csharp"`.
3. **List tools**:
   ```bash
   curl -X POST http://127.0.0.1:65001/mcp \
     -H "Content-Type: application/json" \
     -d '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
   ```

## Troubleshooting

### Plugin won't enable / C# build errors

```
cd /path/to/your/godot/project
dotnet build *.csproj
```
Fix any compile errors, then restart Godot. Common issues:
- **SDK version**: Ensure .NET 8.0 SDK is installed (`dotnet --version`).
- **Godot version**: Must be the **mono** build, not standard Godot.

### MCP client can't connect

1. Verify the editor bottom panel shows `● connected`.
2. Check the port file: `cat $(godot --editor --path . -e 2>/dev/null; echo)` or look for `user://mcp_http_port` in your project's user data dir.
3. The bridge auto-discovers ports 65001–65005. If you changed the port, set `GODOT_MCP_HTTP_PORT`.
4. Firewall: the server binds `127.0.0.1` only — no external access.

### Runtime tools fail ("No scene is currently playing")

Runtime tools (get_game_scene_tree, move_to, etc.) require a running game. Use `play_scene` first. The 3 autoloads (MCPScreenshot, MCPInputService, MCPGameInspector) must be active — check Project Settings → Autoload.

### Error code -32009 (conflict)

A file you're trying to write is open in the editor. Either close it in the editor, or pass `force=true` to the tool.

### Error code -32000 (timeout) with game running

The game is likely paused on a runtime error. The plugin auto-presses the debugger Continue button. Check `get_editor_errors` for the actual error.

### Bridge process exits immediately

```bash
dotnet /path/to/bridge/bin/Release/net8.0/godot-mcp-bridge.dll --url http://127.0.0.1:65001/mcp
```
Run manually to see stderr output. The bridge writes diagnostics to stderr only (stdout is reserved for protocol).

## Tool Categories

| Category | Count | Examples |
|---|---|---|
| project | 12 | get_project_info, search_files, set_project_setting |
| scene | 10 | create_scene, open_scene, save_scene |
| node | 19 | add_node, update_property, connect_signal |
| script | 7 | create_script, edit_script, validate_script |
| editor | 24 | execute_editor_script, get_editor_screenshot, editor_undo |
| input | 5 | simulate_key, simulate_mouse_click, simulate_action |
| input_map | 2 | get_input_actions, set_input_action |
| resource | 7 | read_resource, create_resource, create_gradient_resource |
| shader | 6 | create_shader, edit_shader, assign_shader_material |
| runtime | 19 | get_game_scene_tree, execute_game_script, move_to |
| test | 5 | run_test_scenario, assert_node_state |
| profiling | 2 | get_performance_monitors, get_editor_performance |
| animation | 6 | create_animation, set_animation_keyframe |
| animation_tree | 8 | add_state_machine_state, set_blend_tree_node |
| tilemap | 6 | tilemap_set_cell, tilemap_fill_rect |
| theme | 7 | set_theme_color, set_theme_stylebox, setup_control |
| batch | 7 | batch_set_property, cross_scene_set_property |
| analysis | 6 | find_unused_resources, detect_circular_dependencies |
| scene_3d | 6 | add_mesh_instance, setup_environment |
| rendering_3d | 5 | create_decal, create_reflection_probe, setup_voxel_gi |
| geometry_3d | 4 | create_csg, configure_csg, setup_multimesh |
| particle | 5 | create_particles, apply_particle_preset |
| physics | 6 | setup_collision, setup_physics_body |
| navigation | 5 | bake_navigation_mesh, setup_navigation_agent |
| audio | 6 | add_audio_bus, add_audio_player |
| light_2d | 3 | setup_light_2d, setup_light_occluder_2d, setup_parallax |
| skeleton | 3 | get_skeleton_info, set_bone_pose, get_bone_pose |
| joints | 3 | create_joint, configure_joint, list_joints |
| curve | 4 | get_curve_points, add_curve_point, set_curve_point |
| visual_shader | 6 | create_visual_shader, add_visual_shader_node |
| advanced_nodes | 5 | setup_animated_sprite, setup_vehicle, setup_sky |
| translation | 3 | list_translations, read_translation, write_translation |
| import | 3 | get_import_settings, set_import_settings, reimport_assets |
| uid | 2 | batch_update_uids, find_missing_uids |
| network | 2 | setup_multiplayer, setup_http_request |
| diff | 2 | diff_scenes, diff_resources |
| misc | 2 | editor_focus, set_time_scale |
| export | 3 | export_project, get_export_info |
| android | 3 | list_android_devices, deploy_to_android |

## Modes

Set `GODOT_MCP_MODE` env var or pass `--mode` to the bridge:

| Mode | Tools | Target |
|---|---|---|
| `full` | 239 | All clients |
| `3d` | 210 | Clients with 100-tool limits needing 3D |
| `lite` | 124 | Windsurf, Junie |
| `minimal` | 86 | OpenCode, local LLMs |

## Safety Model (v1.14+)

- **Open-scene write guard**: Tools refuse to write scene/script files currently open in the editor (`-32009` conflict) unless `force=true`.
- **UndoRedo integration**: All mutations go through `EditorUndoRedoManager` — Ctrl+Z works.
- **Dry-run by default**: `cross_scene_set_property` requires `dry_run=false` AND `force=true` for real writes.

## License

MIT
