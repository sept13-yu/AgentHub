# AgentHub

本机桌面控制台：WPF 壳 + 同进程 Kestrel（`127.0.0.1:18780`）+ Vue 3。单工程 `src/AgentHub/AgentHub.csproj`（.NET 10 WinExe），前端 `src/AgentHub/frontend`（Vite + Naive UI + Hash 路由）。`App.xaml.cs` 是组合根。

**落盘任何方案文档前，先读规则中的外置文档节。**

规则是 `%USERPROFILE%\.agents\AGENTS.md`（Cursor 入口 `.cursor/rules/AGENTS.mdc` 只指向它）。方案、调研、CONTEXT、ADR 按那一节写，不进本仓，不在本仓新建 `docs/`、`docs/plans`、`CONTEXT.md`、`adr/`。本仓文件夹名是 `AgentHub`，新稿写 `Plans/AgentHub/<Agent>/`；不要因为产品是枢纽就写到 `Sandbox/`。

动手前先在 `C:\MyPc\Agents\Plans\AgentHub\` **递归**查找相关稿（`docs/` 与所有 `<Agent>` 子目录都要找）。已有需求、探测、对照稿多在 `Plans/AgentHub/docs/`。

## 分层

| 层 | 目录 | 职责 |
|---|---|---|
| Shell | `src/AgentHub/Shell`、`MainWindow`、`App.xaml.cs` | 单实例、托盘、主窗 WebView2、宠物窗、自启、更新 |
| Web | `src/AgentHub/Web` | HTTP、写鉴权、静态文件。主 UI `/app/` |
| Core | `src/AgentHub/Core` | 用量/额度、会话、资料与共用规则、配置、Codex 连接 |
| 前端 | `src/AgentHub/frontend` | `#/dashboard`、`#/sessions`、`#/docs`、`#/rules`、`#/codex-config`、`#/settings` |

Core 不引用 ASP.NET、WPF、WinForms。端点按资源拆文件。API 响应用 `record`，不把 `Dictionary<string, object?>` 或匿名对象当合同。过 300 行就按职责拆。注释写不变量和 Windows 坑。

## 硬口径

- 读接口公开。写接口必须 `X-AgentHub-Token` + Host `127.0.0.1`。浏览器直连只有只读。
- 配置 `%APPDATA%\AgentHub\config.json`，用量库 `tokens.db`，日期用本机 `local_date`。密钥走 DPAPI。本机缓存 `%LOCALAPPDATA%\AgentHub.Local`，禁止写回安装目录 `%LOCALAPPDATA%\AgentHub`。
- Vite `outDir` 只许 `wwwroot/app/`（`base` 为 `/app/`）。不得改 `wwwroot/pet.html`、`wwwroot/clawd`、壳层图标。宠物窗独立打开 `pet.html?app=1`。
- 色源只有 `frontend/src/tokens.ts`。页面 CSS 只消费 `var(--*)`，不写新 hex，不引用 `--n-*`。Naive `themeOverrides` 只收已解析颜色，不传 `var(--*)`。
- 本文件只管**改本仓库**。`%USERPROFILE%\.agents\AGENTS.md` 是各家共用规则（设置页「共用规则」编的就是它）。`Core/DocCore/Templates/SharedRules.md` 是嵌入模板，改它等于改所有 Agent 的共用规则正文；各家本机规则文件由 DocCore 注入指针，不要当本仓文档改。
- `InvariantGlobalization` 必须为 `false`（zh-CN 日期/数字）。发布 tag、`AgentHub.csproj` 的 `Version`、`latest.json` 必须同一版本。

## 命令

日常改完验证用仓库根 `rebuild.ps1`：停进程 → 强制打前端 → `dotnet publish` Release + ReadyToRun。输出目录必须仍是 `src/AgentHub/bin/Debug/net10.0-windows10.0.19041.0`（开机自启指向那里，不能换）。桌面壳吃的是打出来的 `wwwroot\app`，不是 `localhost:5173`。要在壳里验证时 Agent 自己跑这个脚本并拉起进程，不要让用户手动重建。

```text
dotnet build src/AgentHub/AgentHub.csproj
dotnet build src/AgentHub/AgentHub.csproj -p:SkipFrontendBuild=true
```

只改前端也可在 `frontend` 里 `npm run build`。发版走 `pack/windows/pack.ps1` 与 tag `v*`。没有仓库内单测；改 UI 要在壳里把相关页走一遍（含空态/写接口 403、浅色深色）。

## 检索

C# / Vue / TS 定义与调用链优先 `codebase-memory-mcp`（项目名 `C-MyPc-IdeaProjects-AgentHub`）。`rg` 补模板正文、`wwwroot` 宠物脚本、注释里的本机路径。不把仓库外旧对照稿当现行实现；以当前代码和 `Plans/AgentHub/` 里仍有效的需求为准。

## Git / 安全

仅用户明确要求时提交。不提交 `docs/`、`wwwroot/app`、`bin`/`obj`、`config.json`、用量库、凭据、日志、探针。不要把各家 Cookie、API Key、本机绝对路径写进仓库。提交信息短、单点、中文。
