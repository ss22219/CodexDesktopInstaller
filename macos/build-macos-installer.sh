#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REBUILD_DIR="${REBUILD_DIR:-$(cd "$ROOT/.." && pwd)/CodexDesktop-Rebuild}"
ARCH="${1:-arm64}"
PLATFORM="mac-$ARCH"
RID="osx-$ARCH"

if [ "$ARCH" = "x64" ]; then
  RID="osx-x64"
fi

if [ ! -d "$REBUILD_DIR" ]; then
  echo "未找到 CodexDesktop-Rebuild，正在克隆..."
  git clone https://github.com/Haleclipse/CodexDesktop-Rebuild "$REBUILD_DIR"
fi

cd "$REBUILD_DIR"
if [ ! -d node_modules ]; then
  npm ci
fi

if [ ! -d "src/$PLATFORM/_asar" ]; then
  node scripts/sync-upstream.js --skip-win
fi

npm run "build:$PLATFORM"

SOURCE_APP="$REBUILD_DIR/out/$PLATFORM/Codex.app"
VERSION="$(basename "$(ls -t "$REBUILD_DIR"/out/Codex-"$PLATFORM"-*.dmg | head -n 1)" | sed -n "s/Codex-$PLATFORM-\\(.*\\)\\.dmg/\\1/p")"

if [ ! -d "$SOURCE_APP" ]; then
  echo "未找到 Codex.app: $SOURCE_APP"
  exit 1
fi

cd "$ROOT"
PUBLISH_ROOT="$ROOT/artifacts/macos-$ARCH"
LAUNCHER_PUBLISH="$PUBLISH_ROOT/launcher"
PROXY_PUBLISH="$PUBLISH_ROOT/proxy"
APP_BUNDLE="$PUBLISH_ROOT/Codex 启动.app"
DMG_STAGE="$PUBLISH_ROOT/dmg-stage"
DIST="$ROOT/dist-macos/CodexInstaller-$PLATFORM"
DMG="$DIST/CodexInstaller-$PLATFORM-$VERSION.dmg"

rm -rf "$PUBLISH_ROOT" "$DIST"
mkdir -p "$LAUNCHER_PUBLISH" "$PROXY_PUBLISH" "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources" "$DIST"

dotnet publish "$ROOT/CodexLauncher/CodexLauncher.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$LAUNCHER_PUBLISH" \
  /p:PublishAot=true

dotnet publish "$ROOT/CodexApiProxy/CodexApiProxy.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$PROXY_PUBLISH" \
  /p:PublishAot=true

cp "$LAUNCHER_PUBLISH/CodexLauncher" "$APP_BUNDLE/Contents/MacOS/CodexLauncher"
cp "$PROXY_PUBLISH/CodexApiProxy" "$APP_BUNDLE/Contents/MacOS/CodexApiProxy"
chmod +x "$APP_BUNDLE/Contents/MacOS/CodexLauncher" "$APP_BUNDLE/Contents/MacOS/CodexApiProxy"

ditto "$SOURCE_APP" "$APP_BUNDLE/Contents/Resources/Codex.app"

if [ -d "$ROOT/Bundle/Skills" ] || [ -d "$ROOT/Bundle/Plugins" ]; then
  mkdir -p "$APP_BUNDLE/Contents/Resources/Bundle"
  if [ -d "$ROOT/Bundle/Skills" ]; then
    cp -R "$ROOT/Bundle/Skills" "$APP_BUNDLE/Contents/Resources/Bundle/"
  fi
  if [ -d "$ROOT/Bundle/Plugins" ]; then
    cp -R "$ROOT/Bundle/Plugins" "$APP_BUNDLE/Contents/Resources/Bundle/"
  fi
fi

ICON_SOURCE="$SOURCE_APP/Contents/Resources/app.icns"
if [ -f "$ICON_SOURCE" ]; then
  cp "$ICON_SOURCE" "$APP_BUNDLE/Contents/Resources/app.icns"
fi

cat > "$APP_BUNDLE/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>zh_CN</string>
  <key>CFBundleExecutable</key>
  <string>CodexLauncher</string>
  <key>CFBundleIconFile</key>
  <string>app.icns</string>
  <key>CFBundleIdentifier</key>
  <string>com.canghe.codex-launcher</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>Codex 启动</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>26.623.101652</string>
  <key>CFBundleVersion</key>
  <string>26.623.101652</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
EOF

xattr -dr com.apple.quarantine "$APP_BUNDLE" 2>/dev/null || true
codesign --force --deep --sign - "$APP_BUNDLE" >/dev/null

mkdir -p "$DMG_STAGE"
ditto "$APP_BUNDLE" "$DMG_STAGE/Codex 启动.app"
ln -s /Applications "$DMG_STAGE/Applications"
cat > "$DMG_STAGE/使用说明.txt" <<'EOF'
Codex macOS 安装说明

1. 把“Codex 启动.app”拖到 Applications 文件夹
2. 以后从“应用程序”打开“Codex 启动”
3. 在启动器里选择免费模型 / OpenAI 官方 / 自定义 API
4. 点击“保存并启动 Codex”

如果 macOS 提示无法打开:
右键点击“Codex 启动.app”，选择“打开”。
EOF

hdiutil create -volname "Codex Installer" -srcfolder "$DMG_STAGE" -ov -format UDZO "$DMG" >/dev/null

cat > "$DIST/README.txt" <<'EOF'
Codex macOS 一键安装包

使用方法:
1. 打开 .dmg
2. 把“Codex 启动.app”拖到 Applications
3. 从“应用程序”打开“Codex 启动”
4. 选择模式后点击“保存并启动 Codex”

如果 macOS 提示无法打开:
右键点击“Codex 启动.app”，选择“打开”。
EOF

cd "$(dirname "$DIST")"
rm -f "$(basename "$DIST").zip"
zip -qry "$(basename "$DIST").zip" "$(basename "$DIST")"
echo "完成: $DIST.zip"
