#!/bin/bash
set -euo pipefail

cd "$(dirname "$0")"

APP_NAME="Codex"
DMG_PATH=""
if ls Codex-mac-*.dmg >/dev/null 2>&1; then
  DMG_PATH="$(ls Codex-mac-*.dmg | head -n 1)"
fi

if [ -z "$DMG_PATH" ]; then
  echo "未找到 Codex macOS 安装镜像。"
  echo "请确认 Codex-mac-*.dmg 和本文件在同一个文件夹。"
  read -r -p "按回车退出..."
  exit 1
fi

echo "正在安装 Codex..."

MOUNT_POINT=""
cleanup() {
  if [ -n "$MOUNT_POINT" ] && mount | grep -q "$MOUNT_POINT"; then
    hdiutil detach "$MOUNT_POINT" -quiet || true
  fi
}
trap cleanup EXIT

MOUNT_POINT="$(hdiutil attach "$DMG_PATH" -nobrowse | sed -n 's#.*\(/Volumes/.*\)#\1#p' | head -n 1)"
if [ -z "$MOUNT_POINT" ] || [ ! -d "$MOUNT_POINT/Codex.app" ]; then
  echo "无法打开安装镜像。"
  read -r -p "按回车退出..."
  exit 1
fi

if [ -d "/Applications/Codex.app" ]; then
  rm -rf "/Applications/Codex.app"
fi
ditto "$MOUNT_POINT/Codex.app" "/Applications/Codex.app"
xattr -dr com.apple.quarantine "/Applications/Codex.app" 2>/dev/null || true

CODEX_HOME="$HOME/.codex"
mkdir -p "$CODEX_HOME/skills" "$CODEX_HOME/plugins/cache"

if [ -d "Bundle/Skills" ]; then
  ditto "Bundle/Skills" "$CODEX_HOME/skills"
fi

if [ -d "Bundle/Plugins" ]; then
  ditto "Bundle/Plugins" "$CODEX_HOME/plugins/cache"
fi

OPENAI_PLUGIN_ROOT="/Applications/Codex.app/Contents/Resources/plugins/openai-bundled/plugins"
if [ -d "$OPENAI_PLUGIN_ROOT" ]; then
  mkdir -p "$CODEX_HOME/plugins/cache/openai-bundled"
  for plugin_dir in "$OPENAI_PLUGIN_ROOT"/*; do
    [ -d "$plugin_dir" ] || continue
    plugin_name="$(basename "$plugin_dir")"
    manifest="$plugin_dir/.codex-plugin/plugin.json"
    version=""
    if [ -f "$manifest" ]; then
      version="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -n 1)"
    fi
    [ -n "$version" ] || continue
    mkdir -p "$CODEX_HOME/plugins/cache/openai-bundled/$plugin_name"
    ditto "$plugin_dir" "$CODEX_HOME/plugins/cache/openai-bundled/$plugin_name/$version"
  done
fi

CONFIG_PATH="$CODEX_HOME/config.toml"
touch "$CONFIG_PATH"
cp "$CONFIG_PATH" "$CONFIG_PATH.bak.$(date +%Y%m%d%H%M%S)"

if ! grep -q '^\[features\]' "$CONFIG_PATH"; then
  {
    echo ""
    echo "[features]"
    echo "js_repl = true"
    echo "tool_search = true"
  } >> "$CONFIG_PATH"
else
  grep -q '^js_repl[[:space:]]*=' "$CONFIG_PATH" || perl -0pi -e 's/(\[features\]\n)/$1js_repl = true\n/' "$CONFIG_PATH"
  grep -q '^tool_search[[:space:]]*=' "$CONFIG_PATH" || perl -0pi -e 's/(\[features\]\n)/$1tool_search = true\n/' "$CONFIG_PATH"
fi

NODE_REPL="/Applications/Codex.app/Contents/Resources/cua_node/bin/node_repl"
NODE_BIN="/Applications/Codex.app/Contents/Resources/cua_node/bin/node"
NODE_MODULES="/Applications/Codex.app/Contents/Resources/cua_node/bin/node_modules"
if [ -x "$NODE_REPL" ] && ! grep -q '^\[mcp_servers\.node_repl\]' "$CONFIG_PATH"; then
  {
    echo ""
    echo "[mcp_servers.node_repl]"
    echo "args = []"
    echo "command = \"$NODE_REPL\""
    echo "startup_timeout_sec = 120"
    echo ""
    echo "[mcp_servers.node_repl.env]"
    echo "NODE_REPL_NATIVE_PIPE_CONNECT_TIMEOUT_MS = \"1000\""
    echo "NODE_REPL_NODE_PATH = \"$NODE_BIN\""
    echo "NODE_REPL_NODE_MODULE_DIRS = \"$NODE_MODULES\""
    echo "NODE_REPL_TRUSTED_CODE_PATHS = \"/Applications/Codex.app:$CODEX_HOME\""
    echo "CODEX_HOME = \"$CODEX_HOME\""
    echo "BROWSER_USE_AVAILABLE_BACKENDS = \"chrome,iab\""
    echo "BROWSER_USE_CODEX_APP_BUILD_FLAVOR = \"prod\""
    echo "BROWSER_USE_CODEX_APP_VERSION = \"26.623.101652\""
    echo "SKY_CUA_NATIVE_PIPE = \"1\""
  } >> "$CONFIG_PATH"
fi

for section in \
  'plugins."browser@openai-bundled"' \
  'plugins."computer-use@openai-bundled"' \
  'plugins."hyperframes@openai-curated-remote"'
do
  if ! grep -Fq "[$section]" "$CONFIG_PATH"; then
    {
      echo ""
      echo "[$section]"
      echo "enabled = true"
    } >> "$CONFIG_PATH"
  fi
done

find "$CODEX_HOME/skills" "$CODEX_HOME/plugins/cache" -name "SKILL.md" -type f -print0 2>/dev/null |
  while IFS= read -r -d '' file; do
    perl -i -pe 'BEGIN { binmode STDIN; binmode STDOUT } s/^\xEF\xBB\xBF//' "$file" 2>/dev/null || true
  done

echo ""
echo "Codex 已安装完成。"
echo "你可以在“应用程序”里打开 Codex。"
echo ""
read -r -p "按回车退出..."
