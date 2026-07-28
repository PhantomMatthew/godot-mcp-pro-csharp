#!/bin/bash
# MCP Smoke Test — verifies the plugin HTTP server responds correctly.
# Run this while Godot editor is open with the plugin enabled.
# Usage: bash test/mcp_smoke_test.sh [port]

set -e

PORT="${1:-65001}"
URL="http://127.0.0.1:${PORT}/mcp"
PASS=0
FAIL=0

ok() { echo "  ✅ $1"; PASS=$((PASS+1)); }
no() { echo "  ❌ $1"; FAIL=$((FAIL+1)); }

call() {
  local id=$1 method=$2 params=$3
  curl -s -m 10 -X POST "$URL" \
    -H "Content-Type: application/json" \
    -d "{\"jsonrpc\":\"2.0\",\"id\":${id},\"method\":\"${method}\",\"params\":${params}}"
}

echo "=== Godot MCP Pro CSharp Smoke Test (port ${PORT}) ==="
echo ""

# 1. Initialize
echo "[1] Initialize"
RESP=$(call 1 initialize '{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}')
if echo "$RESP" | grep -q "godot-mcp-pro-csharp"; then
  ok "Server info returned"
else
  no "Initialize failed: $RESP"
  echo "Server not reachable. Check: Godot editor open? Plugin enabled? Port correct?"
  exit 1
fi

# 2. Ping
echo "[2] Ping"
RESP=$(call 2 ping '{}')
if echo "$RESP" | grep -q '"result"'; then
  ok "Ping responded"
else
  no "Ping failed: $RESP"
fi

# 3. Tools list
echo "[3] Tools List"
RESP=$(call 3 tools/list '{}')
TOOL_COUNT=$(echo "$RESP" | grep -o '"name"' | wc -l | tr -d ' ')
if [ "$TOOL_COUNT" -gt 100 ]; then
  ok "Tools list returned ($TOOL_COUNT tools)"
else
  no "Tools list returned too few tools ($TOOL_COUNT)"
fi

# 4. Call get_project_info
echo "[4] Tool Call: get_project_info"
RESP=$(call 4 tools/call '{"name":"get_project_info","arguments":{}}')
if echo "$RESP" | grep -q "project_name\|godot_version\|isError.*false"; then
  ok "get_project_info returned project data"
else
  no "get_project_info failed: $RESP"
fi

# 5. Call get_filesystem_tree
echo "[5] Tool Call: get_filesystem_tree"
RESP=$(call 5 tools/call '{"name":"get_filesystem_tree","arguments":{"path":"res://","max_depth":1}}')
if echo "$RESP" | grep -q "tree\|isError.*false"; then
  ok "get_filesystem_tree returned file tree"
else
  no "get_filesystem_tree failed: $RESP"
fi

# 6. Call get_scene_tree (may return error if no scene open — that's OK)
echo "[6] Tool Call: get_scene_tree"
RESP=$(call 6 tools/call '{"name":"get_scene_tree","arguments":{}}')
if echo "$RESP" | grep -q "scene_path\|tree\|No scene\|isError"; then
  ok "get_scene_tree responded (scene may or may not be open)"
else
  no "get_scene_tree failed: $RESP"
fi

# 7. Call get_editor_performance
echo "[7] Tool Call: get_editor_performance"
RESP=$(call 7 tools/call '{"name":"get_editor_performance","arguments":{}}')
if echo "$RESP" | grep -q "fps\|isError.*false"; then
  ok "get_editor_performance returned metrics"
else
  no "get_editor_performance failed: $RESP"
fi

# 8. Call list_scripts
echo "[8] Tool Call: list_scripts"
RESP=$(call 8 tools/call '{"name":"list_scripts","arguments":{}}')
if echo "$RESP" | grep -q "scripts\|count\|isError"; then
  ok "list_scripts responded"
else
  no "list_scripts failed: $RESP"
fi

echo ""
echo "=== Results: ${PASS} passed, ${FAIL} failed ==="
exit $FAIL
