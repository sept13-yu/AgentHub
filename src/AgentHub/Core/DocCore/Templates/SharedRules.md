# 各家共用的规则

各家自己的规则文件只写一句：打开并遵守这份。改这里一处，所有 Agent 都生效。

`%USERPROFILE%` 就是你的用户目录。Skill 原文在 `%USERPROFILE%\.agents\skills\`，不要改 Skill 正文。凡 Skill 写死的仓库相对路径，只当「写哪类文档」，最终目录以本文件为准。

这些文件不要放进业务 Git 仓库。不要在业务仓里新建 `docs/plans`、`docs/superpowers`、`CONTEXT.md`、`adr/`、`output/`、`tmp/`。

## 澄清与执行方式

- 任务缺关键信息时先问（最多 3 问，有选项必须标推荐），不要猜着扩大范围。
- 简单、明确的开发任务直接做，不要改写成计划。
- 分析/设计/方案/实施类需求：只写计划，不改业务代码；用户说「确认执行」后再改。业务代码 = 源码、配置、构建脚本；写计划/CONTEXT/ADR 不算。
- 仅当用户明确说 grill / 拷问 / 压力测试 / grill-me / grill-with-docs 时才启用 grilling。日常开发与澄清不升级。架构 Skill 须等用户先选定方案。

## 改代码

- 优先改现有实现，范围最小，跟旁边代码风格。不新增多余抽象。
- 临时文件用完即删。新增业务文件先报路径等确认。
- 测试文件只放 gitignore 目录，不放仓库根。

## Git

- 仅用户明确要求时提交；只提交本次业务文件。
- 不提交计划、CONTEXT、ADR、测试、临时文件、构建产物；不提交凭据、密钥、日志。
- 提交后报 hash、文件、工作区是否干净。

## 其他

- 不编造：数据、路径、结论须有出处；查不到就明说。
- 一律中文回复。

## 外置文档放哪

资料目录：{{libraryRoot}}

- 某个项目的方案、调研：`Plans/<project-slug>/<Agent>/`
- 挂不上项目的杂活：`SandBox/<Agent>/`

`<project-slug>` 用当前仓库文件夹名。工作区不是某个仓库、或就是本资料目录时，写到 `SandBox/<Agent>/`。

`<Agent>` 用当前产品名（忽略大小写）：`Cursor`、`Codex`、`Dsh`、`Trae`、`WorkBuddy`、`ZCode`。`TraeWork` 视为 `Trae`。

文档名用中文。工单号可作前缀。不套日期文件夹，需要时再创建目录。

动手前先找方案：接到某项目的任务，先在 `Plans/<project-slug>/` 下**递归**查找与本任务相关的方案稿——**所有 `<Agent>` 子目录都要找**，不只自家的：方案常是一个 Agent 写、另一个 Agent 执行。找到相关的先读完再动手，避免重复调研或与既定方案打架；没有相关的就跳过。

各家旧文档根不要再写。

## Skill 落盘

- `writing-plans`、方案、实施计划、`research`：`Plans/<project-slug>/<Agent>/<中文名>.md`。不自动 commit，等用户「确认执行」。
- `code-review` 找规格：用户指定 -> 上述目录 -> 仓库里已有 `docs/`、`specs/`；都没有就问。
- `doc` / `pdf` / `spreadsheet` 交付物：同上项目目录；中间文件放系统临时目录，用完即删。
- `playwright` 的 `$CODEX_HOME/skills/playwright/…` → `%USERPROFILE%\.agents\skills\playwright\scripts\playwright_cli.sh`
- 未安装的 `superpowers:*`、`executing-plans`、`using-git-worktrees`、`/tdd`：跳过引用，本会话按步骤做。
