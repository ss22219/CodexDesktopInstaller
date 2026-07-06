#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARCH="${1:-arm64}"
RID="osx-$ARCH"

if [ "$ARCH" = "x64" ]; then
  RID="osx-x64"
fi

cd "$ROOT"

VERSION="26.623.101652"
PUBLISH_ROOT="$ROOT/artifacts/macos-$ARCH"
LAUNCHER_PUBLISH="$PUBLISH_ROOT/launcher"
PROXY_PUBLISH="$PUBLISH_ROOT/proxy"
APP_BUNDLE="$PUBLISH_ROOT/Codex 启动.app"
DMG_STAGE="$PUBLISH_ROOT/dmg-stage"
DIST="$ROOT/dist-macos/CodexInstaller-mac-$ARCH"
DMG="$DIST/CodexLauncher-mac-$ARCH-$VERSION.dmg"

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

cp -R "$LAUNCHER_PUBLISH/." "$APP_BUNDLE/Contents/MacOS/"
cp "$PROXY_PUBLISH/CodexApiProxy" "$APP_BUNDLE/Contents/MacOS/CodexApiProxy"
chmod +x "$APP_BUNDLE/Contents/MacOS/CodexLauncher" "$APP_BUNDLE/Contents/MacOS/CodexApiProxy"
find "$APP_BUNDLE/Contents/MacOS" -name "*.dylib" -type f -exec chmod +x {} \;

if [ -d "$ROOT/Bundle/Skills" ] || [ -d "$ROOT/Bundle/Plugins" ]; then
  mkdir -p "$APP_BUNDLE/Contents/Resources/Bundle"
  if [ -d "$ROOT/Bundle/Skills" ]; then
    cp -R "$ROOT/Bundle/Skills" "$APP_BUNDLE/Contents/Resources/Bundle/"
  fi
  if [ -d "$ROOT/Bundle/Plugins" ]; then
    cp -R "$ROOT/Bundle/Plugins" "$APP_BUNDLE/Contents/Resources/Bundle/"
  fi
fi

ICON_SOURCE="/Applications/Codex.app/Contents/Resources/app.icns"
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
Codex macOS 启动器安装说明

1. 先确认你已经从官网安装 Codex.app，并且它在 Applications 文件夹里
2. 把“Codex 启动.app”拖到 Applications 文件夹
3. 以后从“应用程序”打开“Codex 启动”
4. 在启动器里选择免费模型 / OpenAI 官方 / 自定义 API
5. 点击“保存并启动 Codex”

如果 macOS 提示无法打开:
右键点击“Codex 启动.app”，选择“打开”。
EOF

hdiutil create -volname "Codex Launcher" -srcfolder "$DMG_STAGE" -ov -format UDZO "$DMG" >/dev/null

cat > "$DIST/README.txt" <<'EOF'
Codex macOS 启动器安装包

使用方法:
1. 先确认你已经从官网安装 Codex.app，并且它在 Applications 文件夹里
2. 打开 .dmg
3. 把“Codex 启动.app”拖到 Applications
4. 从“应用程序”打开“Codex 启动”
5. 选择模式后点击“保存并启动 Codex”

如果 macOS 提示无法打开:
右键点击“Codex 启动.app”，选择“打开”。
EOF

cd "$(dirname "$DIST")"
rm -f "$(basename "$DIST").zip"
zip -qry "$(basename "$DIST").zip" "$(basename "$DIST")"
echo "完成: $DIST.zip"
