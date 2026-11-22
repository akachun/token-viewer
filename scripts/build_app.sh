#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
APP_NAME="TokenViewer"
BUILD_CONFIG="${1:-release}"
DIST_DIR="$ROOT_DIR/dist"
APP_BUNDLE="$DIST_DIR/$APP_NAME.app"

echo "🔨 Building $APP_NAME ($BUILD_CONFIG)..."
cd "$ROOT_DIR"
swift build -c "$BUILD_CONFIG"

BIN_PATH="$(swift build -c "$BUILD_CONFIG" --show-bin-path)/$APP_NAME"
if [ ! -f "$BIN_PATH" ]; then
    echo "❌ Binary not found at $BIN_PATH"
    exit 1
fi

echo "📦 Packaging $APP_BUNDLE..."
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

cp "$BIN_PATH" "$APP_BUNDLE/Contents/MacOS/$APP_NAME"
cp "$ROOT_DIR/Resources/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
chmod +x "$APP_BUNDLE/Contents/MacOS/$APP_NAME"

# Ad-hoc code sign so macOS Keychain & notifications work reliably locally
codesign --force --deep --sign - "$APP_BUNDLE" 2>/dev/null || true

echo "✅ Done! App bundle created at:"
echo "   $APP_BUNDLE"
echo ""
echo "실행하려면: open \"$APP_BUNDLE\""
