#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OBSIDIAN_ROOT="$(cd "$ROOT/../.." && pwd)"
REBUILD_DIR="$OBSIDIAN_ROOT/proj/CodexDesktop-Rebuild"
ARCH="${1:-arm64}"
PLATFORM="mac-$ARCH"

if [ ! -d "$REBUILD_DIR" ]; then
  echo "未找到 CodexDesktop-Rebuild: $REBUILD_DIR"
  exit 1
fi

cd "$REBUILD_DIR"

if [ ! -d "src/$PLATFORM/_asar" ]; then
  node scripts/sync-upstream.js --skip-win
fi

npm run "build:$PLATFORM"

DMG="$(ls -t "$REBUILD_DIR"/out/Codex-"$PLATFORM"-*.dmg | head -n 1)"
DIST="$ROOT/dist-macos/CodexInstaller-$PLATFORM"
rm -rf "$DIST"
mkdir -p "$DIST/Bundle"

cp "$DMG" "$DIST/"
cp "$ROOT/macos/install-codex.command" "$DIST/安装 Codex.command"
chmod +x "$DIST/安装 Codex.command"

if [ -d "$ROOT/Bundle/Skills" ]; then
  cp -R "$ROOT/Bundle/Skills" "$DIST/Bundle/"
fi

if [ -d "$ROOT/Bundle/Plugins" ]; then
  cp -R "$ROOT/Bundle/Plugins" "$DIST/Bundle/"
fi

cat > "$DIST/README.txt" <<'EOF'
Codex macOS 一键安装包

使用方法:
1. 双击“安装 Codex.command”
2. 等安装完成后，从“应用程序”打开 Codex

如果 macOS 提示无法打开:
右键点击“安装 Codex.command”，选择“打开”。
EOF

cd "$(dirname "$DIST")"
zip -qry "$(basename "$DIST").zip" "$(basename "$DIST")"
echo "完成: $DIST.zip"
