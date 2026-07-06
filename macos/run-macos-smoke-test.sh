#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARCH="${ARCH:-}"
LIVE=0
BUILD=0
PORT="${PORT:-17639}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch)
      ARCH="${2:-}"
      shift 2
      ;;
    --live)
      LIVE=1
      shift
      ;;
    --build)
      BUILD=1
      shift
      ;;
    --port)
      PORT="${2:-}"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if [[ -z "$ARCH" ]]; then
  case "$(uname -m)" in
    arm64) ARCH="arm64" ;;
    x86_64) ARCH="x64" ;;
    *)
      echo "Unsupported local architecture: $(uname -m)" >&2
      exit 2
      ;;
  esac
fi

ZIP="$ROOT/dist-macos/CodexInstaller-mac-$ARCH.zip"
TMPDIR="$(mktemp -d)"
MOUNT=""
PROXY_PID=""

cleanup() {
  if [[ -n "$PROXY_PID" ]]; then
    kill "$PROXY_PID" >/dev/null 2>&1 || true
    wait "$PROXY_PID" >/dev/null 2>&1 || true
  fi
  if [[ -n "$MOUNT" ]]; then
    hdiutil detach "$MOUNT" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMPDIR"
}
trap cleanup EXIT

pass() {
  echo "PASS: $1"
}

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

require_file() {
  [[ -f "$1" ]] || fail "$2 missing: $1"
  pass "$2"
}

require_dir() {
  [[ -d "$1" ]] || fail "$2 missing: $1"
  pass "$2"
}

if [[ "$BUILD" -eq 1 ]]; then
  "$ROOT/macos/build-macos-installer.sh" "$ARCH"
fi

require_file "$ZIP" "macOS zip package"
unzip -q "$ZIP" -d "$TMPDIR/unzip"

DMG="$(find "$TMPDIR/unzip" -name '*.dmg' -print -quit)"
require_file "$DMG" "DMG inside zip"

MOUNT="$(hdiutil attach "$DMG" -nobrowse -readonly | awk '/\/Volumes\// {for (i=3;i<=NF;i++) printf (i==3?$i:" "$i); print ""}' | tail -n 1)"
require_dir "$MOUNT/Codex 启动.app" "launcher app in DMG"
require_dir "$MOUNT/Codex 免费版.app" "modified Codex app in DMG"
[[ -L "$MOUNT/Applications" ]] || fail "Applications shortcut missing"
pass "Applications shortcut"

[[ ! -e "$MOUNT/Codex.app" ]] || fail "official Codex.app must not be bundled"
pass "official Codex.app is not bundled"

APP="$MOUNT/Codex 启动.app"
FREE_APP="$MOUNT/Codex 免费版.app"
LAUNCHER="$APP/Contents/MacOS/CodexLauncher"
PROXY="$APP/Contents/MacOS/CodexApiProxy"
FREE_CODEX="$FREE_APP/Contents/MacOS/Codex"
require_file "$LAUNCHER" "launcher executable"
require_file "$PROXY" "proxy executable"
require_file "$FREE_CODEX" "modified Codex executable"
[[ -x "$LAUNCHER" ]] || fail "launcher executable bit missing"
[[ -x "$PROXY" ]] || fail "proxy executable bit missing"
[[ -x "$FREE_CODEX" ]] || fail "modified Codex executable bit missing"
pass "executables are runnable"

BUNDLE_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$FREE_APP/Contents/Info.plist")"
[[ "$BUNDLE_ID" == "com.canghe.codex-free" ]] || fail "modified Codex bundle id is not isolated: $BUNDLE_ID"
pass "modified Codex bundle id is isolated"

if ! "$PROXY" --self-test >/tmp/codex-proxy-self-test.out 2>/tmp/codex-proxy-self-test.err; then
  if grep -q 'Bad CPU type in executable' /tmp/codex-proxy-self-test.err; then
    pass "runtime checks skipped because this Mac cannot run $ARCH binaries"
    pass "macOS smoke test finished without touching official Codex config"
    exit 0
  fi

  cat /tmp/codex-proxy-self-test.err >&2 || true
  fail "proxy self-test failed"
fi
pass "proxy self-test"

"$PROXY" --port "$PORT" --upstream "https://opencode.ai/zen/v1" >"$TMPDIR/proxy.log" 2>&1 &
PROXY_PID="$!"
sleep 2

curl -fsS "http://127.0.0.1:$PORT/health" | grep -q '"ok":true' || fail "proxy health check failed"
pass "proxy health endpoint"

curl -fsS "http://127.0.0.1:$PORT/v1/models" | grep -q 'deepseek-v4-flash-free' || fail "free model list missing deepseek-v4-flash-free"
pass "free model list endpoint"

if [[ "$LIVE" -eq 1 ]]; then
  BODY='{"model":"mimo-v2.5-free","input":"Say hello in one short sentence.","stream":false}'
  RESPONSE="$(curl -fsS -X POST "http://127.0.0.1:$PORT/v1/responses" -H 'content-type: application/json' -d "$BODY")"
  echo "$RESPONSE" | grep -q '"status":"completed"' || fail "live response did not complete"
  echo "$RESPONSE" | grep -q 'output_text' || fail "live response did not contain output text"
  pass "live chat response"
fi

pass "macOS smoke test finished without touching official Codex config"
