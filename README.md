# AgentHub

本机桌面控制台：把各家 Agent 的**用量、会话、Skill、MCP、共用规则**收拢到一个窗口。

会话目前覆盖 Codex、DSH、Cursor（含云端）、WorkBuddy、ZCode、MiMo、Qoder CN；用量另含 Trae、Qoder、Grok、DeepSeek、Relay，以及一批只读本地扫描源。具体以当前扫描实现为准。

> Windows 桌面应用（WPF + 同进程 Kestrel + Vue 3）。Mac 为 Tauri 薄壳 + 无界面 Backend。默认只监听 `127.0.0.1`，写操作需要本机令牌，不对外网暴露。

## 能做什么

| 模块 | 作用 |
|---|---|
| **仪表盘** | 汇总各家 Token / 额度卡片，支持费用估算与币种切换 |
| **会话** | 浏览、预览、清理本机会话；支持批量删除 |
| **能力** | 管理 `~/.agents/skills`（启用/停用、安装更新、删除）以及各家 MCP |
| **资料** | 浏览外置方案库（默认不落业务仓） |
| **规则** | 编辑并同步各家 Agent 的共用规则母本 |
| **Codex** | 管理 Codex 连接与 ChatGPT 账号档案 |
| **设置** | 开机自启、检查更新、本机凭据，以及成本估算、Token 单位和扫描间隔 |

## 技术栈

- **Windows 壳**：.NET 10 WinExe / WPF + WebView2 + 托盘
- **Mac 壳**：Tauri 2，子进程拉起 Backend
- **后端**：Kestrel（`127.0.0.1:18780`），业务在 `AgentHub.Runtime`
- **前端**：Vue 3 + Vite + Naive UI（Hash 路由，产物在 `wwwroot/app/`）
- **数据**：配置 %APPDATA%\AgentHub，本机缓存 %LOCALAPPDATA%\AgentHub.Local；密钥走 DPAPI

## 快速开始

### 环境

- Windows 10/11 x64（Mac 壳另需 .NET 10 与 Rust/Tauri 工具链）
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Node.js 20+（仅构建前端需要）

### 开发构建

仓库根目录：

```powershell
.\rebuild.ps1
```

脚本会：结束旧进程 → 强制构建前端 → `dotnet publish` Release + ReadyToRun → 启动应用。

输出目录固定为：

`src/AgentHub/bin/Debug/net10.0-windows10.0.19041.0`

（开机自启注册表指向这里，不要改输出路径。）

只改前端也可：

```powershell
cd src\AgentHub\frontend
npm install
npm run build
```

无界面后端（须先退出桌面壳，端口互斥）：

```powershell
dotnet run --project src/AgentHub.Backend
```

### 发版

Windows 运行 `pack/windows/pack.ps1` 会生成完整 Inno 安装包 `AgentHub-{version}-win-x64.exe` 和便携包 `AgentHub-{version}-win-x64.zip`。安装版可在应用内检查并下载新版安装包；便携版手动下载 ZIP 替换。安装向导允许选择程序目录。

旧版 Velopack 用户需要先在 Windows「已安装的应用」中卸载旧版，再从 [GitHub Releases](https://github.com/sept13-yu/AgentHub/releases) 手动下载新版安装包。不要把新版安装到仍有 `Update.exe` 与 `current` 的旧目录。配置和用量库在 `%APPDATA%\AgentHub`，本机缓存位于 `%LOCALAPPDATA%\AgentHub.Local`，与程序目录分开；卸载前可按需备份这些目录。

Mac 本机打包（Apple Silicon 机器；需 .NET 10 SDK、Rust/Tauri 工具链、Node 20+）：

```bash
bash pack/macos/build.sh        # Apple Silicon（aarch64）
bash pack/macos/build.sh x64    # Intel（x86_64）
```

两个 DMG 统一输出到 `dist/macos/dmg/`，发布名分别为 `AgentHub-{version}-mac-arm64.dmg` 和 `AgentHub-{version}-mac-x64.dmg`。DMG 未做签名与公证，首次打开需右键 →「打开」。开发调试用 `pack/macos/dev.sh`。

CI 发版使用 `vX.Y.Z` tag，且版本号须与 `src/Directory.Build.props`、`latest.json` 一致。Release 工作流并行构建 Windows 安装包、便携 ZIP 和双架构 DMG，核对四个资产的 SHA-256 后公开 GitHub Release，最后把包含版本、文件大小与 SHA-256 的 `update-feed.json` 写入 Gitee 和 GitHub 的 `update-feed` 分支。需要先在 GitHub Actions 配置有 Gitee 仓库 `projects` 权限的 `GITEE_TOKEN` secret。新客户端检查更新时先读 Gitee 静态清单，失败再读 jsDelivr / GitHub Raw 镜像；只有发现新版后才从 GitHub Release 下载安装包。旧版 Gitee Release 不再新增版本。

## 安全口径

- 读接口可本机访问；**写接口**必须带 `X-AgentHub-Token`，且 Host 为 `127.0.0.1`
- 不把 Cookie、API Key、用量库、本机绝对路径提交进 Git
- Skill 的「中文名 / 备注」只存在本机 `skills-state.json`，**不会**写回 `SKILL.md`，更新 Skill 也不会冲掉

## 目录速览

```text
src/
  AgentHub/            Windows 壳、frontend、wwwroot
    Shell/
    frontend/
    wwwroot/
  AgentHub.Runtime/    Core + Web + Hosting
  AgentHub.Backend/    无界面入口
  AgentHub.Mac/        Tauri 壳
  AgentHub.Tests/      用量、额度与牌价测试
```
