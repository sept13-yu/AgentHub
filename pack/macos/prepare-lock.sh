#!/usr/bin/env bash
# 在 Apple Silicon Mac 上生成并检查锁文件（方案要求入库）
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root/src/AgentHub.Mac/src-tauri"
command -v cargo >/dev/null || { echo "需要先安装 Rust（https://rustup.rs）"; exit 1; }
cargo generate-lockfile
test -f Cargo.lock
cd "$root/src/AgentHub.Mac"
npm ci
test -f package-lock.json
echo "已生成/确认："
echo "  $root/src/AgentHub.Mac/src-tauri/Cargo.lock"
echo "  $root/src/AgentHub.Mac/package-lock.json"
echo "请 git add 这两个文件后再跑 pack/macos/dev.sh 或 build.sh"
