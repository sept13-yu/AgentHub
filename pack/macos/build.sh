#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
[[ "$(uname -m)" == "arm64" ]] || { echo "需要 Apple Silicon 终端（当前 $(uname -m)）"; exit 1; }
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/Directory.Build.props" | head -1)"
[[ -n "$version" ]] || { echo "Directory.Build.props 缺 Version"; exit 1; }

( cd "$root/src/AgentHub/frontend" && npm ci && npm run build )

rm -rf "$root/dist/macos/backend"
dotnet publish "$root/src/AgentHub.Backend/AgentHub.Backend.csproj" -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$root/dist/macos/backend"
test -x "$root/dist/macos/backend/AgentHub.Backend"
test -f "$root/dist/macos/backend/wwwroot/app/index.html"

cd "$root/src/AgentHub.Mac"
npm ci
[[ -f src-tauri/icons/icon.icns ]] || npx tauri icon "$root/src/AgentHub/wwwroot/icon.png" -o src-tauri/icons
npx tauri build --config "{\"version\":\"$version\"}"

app="src-tauri/target/release/bundle/macos/AgentHub.app"
test -x "$app/Contents/Resources/backend/AgentHub.Backend"
file "$app/Contents/Resources/backend/AgentHub.Backend" | grep -q arm64
test -f "$app/Contents/Resources/backend/wwwroot/app/index.html"
echo "OK: $root/src/AgentHub.Mac/$app"
