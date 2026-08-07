# Quick Start — Godot MCP Pro CSharp

5-minute guide from zero to first AI-driven Godot edit.

## Prerequisites

- Godot 4.4+ **mono** (the .NET build) — [download](https://godotengine.org/download/)
- .NET 8.0 SDK — `dotnet --version` should show 8.x
- An MCP-compatible AI client (Claude Code, Cursor, Cline, etc.)

## Step 1: Add the plugin to your project

```bash
cd /your/godot/project
cp -r /path/to/godot-mcp-pro-csharp/addons/godot_mcp addons/
```

Open the project in Godot (mono). The C# project compiles on first load.

Go to **Project → Project Settings → Plugins** and enable **Godot MCP Pro CSharp**.

A **"MCP Pro"** tab appears at the editor bottom panel. It should show `● connected` with a port number (65001–65005).

## Step 2: Build the stdio bridge (if using Claude Code / Cline)

```bash
cd /path/to/godot-mcp-pro-csharp/bridge
dotnet build -c Release
```

## Step 3: Configure your AI client

### Claude Code

Create or edit `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "godot-mcp-pro-csharp": {
      "command": "dotnet",
      "args": ["/absolute/path/to/godot-mcp-pro-csharp/bridge/bin/Release/net8.0/godot-mcp-bridge.dll"],
      "env": {}
    }
  }
}
```

Optional: Copy `settings.local.json` to `~/.claude/settings.local.json` for auto-approve of read/safe tools.

### Cursor / Windsurf (HTTP)

Point your client at `http://127.0.0.1:65001/mcp`.

## Step 4: Test

In your AI client, try:

```
What scenes exist in my Godot project?
```

The AI will call `get_filesystem_tree` or `get_scene_tree` and report back.

```
Create a new scene called "TestScene.tscn" with a Node2D root and a Sprite2D child.
```

The AI calls `create_scene` + `add_node`. Check Godot — the new scene appears in the FileSystem dock. Ctrl+Z undoes it.

## What works

| Action | Example prompt |
|--------|---------------|
| Inspect project | "List all scripts" |
| Edit scenes | "Add a Button to the main scene" |
| Write scripts | "Create a player movement script" |
| Run game + inspect | "Play the scene and tell me the player position" |
| Take screenshots | "Take a screenshot of the game" |
| Batch operations | "Set position on all Enemy nodes" |
| Testing | "Run a test: press Space and check if the player jumps" |
