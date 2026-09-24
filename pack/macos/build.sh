#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
[[ "$(uname -m)" == "arm64" ]] || { echo "需要 Apple Silicon 终端（当前 $(uname -m)）"; exit 1; }
target="${1:-arm64}"
case "$target" in
  arm64) rid=osx-arm64; rust_target=""; expect="arm64"; dmg_arch="aarch64" ;;
  x64) rid=osx-x64; rust_target="x86_64-apple-darwin"; expect="x86_64"; dmg_arch="x64" ;;
  *) echo "用法: $0 [arm64|x64]（默认 arm64）"; exit 1 ;;
esac
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/Directory.Build.props" | head -1)"
[[ -n "$version" ]] || { echo "Directory.Build.props 缺 Version"; exit 1; }

( cd "$root/src/AgentHub/frontend" && npm ci && npm run build )

rm -rf "$root/dist/macos/backend"
dotnet publish "$root/src/AgentHub.Backend/AgentHub.Backend.csproj" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$root/dist/macos/backend"
test -x "$root/dist/macos/backend/AgentHub.Backend"
test -f "$root/dist/macos/backend/wwwroot/app/index.html"

cd "$root/src/AgentHub.Mac"
npm ci
[[ -f src-tauri/icons/icon.icns ]] || npx tauri icon "$root/src/AgentHub/wwwroot/icon.png" -o src-tauri/icons
tauri_args=(build --config "{\"version\":\"$version\"}")
if [[ -n "$rust_target" ]]; then tauri_args+=(--target "$rust_target"); fi
# DMG 默认用 AppleScript 控制 Finder 做图标排版，未授权时失败；自动退回 skip-jenkins 纯 hdiutil 模式
npx tauri "${tauri_args[@]}" \
  || CI=true npx tauri "${tauri_args[@]}"

bundle="src-tauri/target/release/bundle"
if [[ -n "$rust_target" ]]; then bundle="src-tauri/target/$rust_target/release/bundle"; fi
app="$bundle/macos/AgentHub.app"
test -x "$app/Contents/Resources/backend/AgentHub.Backend"
file "$app/Contents/Resources/backend/AgentHub.Backend" | grep -q "$expect"
test -f "$app/Contents/Resources/backend/wwwroot/app/index.html"
dmg="$bundle/dmg/AgentHub_${version}_${dmg_arch}.dmg"
test -f "$dmg"
outdir="$root/dist/macos/dmg"
mkdir -p "$outdir"
cp "$dmg" "$outdir/"
echo "OK: $root/src/AgentHub.Mac/$app"
echo "OK: $outdir/AgentHub_${version}_${dmg_arch}.dmg"
