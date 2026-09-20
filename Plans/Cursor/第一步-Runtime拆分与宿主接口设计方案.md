# AgentHub 跨平台第一步：Runtime 拆分与宿主接口设计方案

| 项 | 内容 |
|---|---|
| 代码基线 | `c328914`（main） |
| 本步目标 | `Core/**` + `Web/**` 在纯 `net10.0`（非 `-windows` TFM）下编译通过；产出一个无界面的 `AgentHub.Backend` 可执行程序，能在 Windows / macOS / Linux 上启动 Kestrel 并提供现有全部 HTTP API；Windows WPF 壳行为零变化 |
| 不在本步 | Tauri / Photino 壳、Keychain、各第三方客户端（Cursor/Trae/Zcode…）的 Mac 路径适配、Qoder Unix socket、宠物、Mac 打包签名、Windows 迁壳 |
| 交付判定 | 第 6 节验收清单全部通过 |

本文给实施 AI 直接执行。所有文件路径相对仓库根。凡写「保持不变」的地方不要顺手重构。

---

## 1. 现状确证（为什么必须先做这一步）

| 事实 | 位置 |
|---|---|
| 全仓只有一个工程：`WinExe` + `net10.0-windows10.0.19041.0` + `UseWPF` + `UseWindowsForms` + WebView2 + Velopack | `src/AgentHub/AgentHub.csproj` 3–36 |
| 唯一进程入口是 `[STAThread] Main → VelopackApp → new App().Run()`，没有无界面入口 | `src/AgentHub/Program.cs` |
| 所有 Core 服务在 `App.OnStartup` 里组装，`WebHostService.Start()` 只有这一处调用 | `src/AgentHub/App.xaml.cs` 92–124 |
| `WebHostService` 本身是纯 Kestrel，不引用 WPF；已有 `PickFolder` / `UsageScan` / `PetIsRunning` 三个宿主注入点 | `src/AgentHub/Web/WebHostService.cs` |
| Web 层唯一的壳耦合：`UsageEndpoints` 直接调 `AppUpdate.*`（Velopack）与 `AutostartManager.*`（注册表） | `src/AgentHub/Web/UsageEndpoints.cs` 5、66、119、147–156、170、220–221 |
| `Core/**` 不引用 `System.Windows` / `AgentHub.Shell` / `Microsoft.Win32`，但 `Dpapi` 使用 `ProtectedData`（Windows only） | `src/AgentHub/Core/TokenCore/QuotaService.cs` 1361–1383 |
| `Dpapi.Protect/Unprotect` 共 26 处调用、8 个文件：Core（TraeAuth 1、QuotaService 5、SessionService 1、WorkBuddyProvider 1、CursorCloudProvider 1、CodexConfigService 3、CodexAuthProfileStore 2）与 Web（UsageEndpoints 12） | `rg -n "Dpapi\." src/AgentHub` |
| `explorer.exe` 硬编码 3 处 | `Core/DocCore/DocService.cs` 278；`Core/McpCore/McpSyncService.cs` 592、603 |
| 无条件 `Replace('/', '\\')` 会把 `/Users/...` 改坏 | `Core/SessionCore/SessionIndex.cs` 286；`Core/McpCore/McpSyncService.cs` 725 |
| AgentHub 自身数据目录用 `SpecialFolder.ApplicationData / LocalApplicationData`（Mac 上会落到 `~/.config`、`~/.local/share`） | `Core/ProxyCore/AgentHubConfig.cs` 358–366；`Core/SessionCore/TitleOverrideStore.cs` 23 |
| App 直接使用的 `internal` 类型：`ScanScheduler`、`HubLog` | `Core/TokenCore/ScanScheduler.cs` 7；`Core/ProxyCore/HubLog.cs` 7 |
| 嵌入资源按「名称以 `SharedRules.md` 结尾」查找，不依赖精确命名空间 | `Core/DocCore/AgentRuleTemplates.cs` 348–357 |
| 版本号只写在 csproj，`pack.ps1` 与 `release.yml` 用 XML 读取校验 | `AgentHub.csproj` 12；`pack/windows/pack.ps1` 16；`.github/workflows/release.yml` 22–31 |

结论：业务代码已经基本跨平台，缺的是 (a) 一个不带 WPF 的编译目标和进程入口，(b) Web→Shell 的两处静态调用改为接口，(c) `Dpapi` 抽象，(d) 四处路径小 bug。

---

## 2. 目标结构

```
AgentHub.sln                         新增：三工程
AgentHub.CrossPlatform.slnf          新增：只含 Runtime + Backend，供 Mac/Linux 构建
src/
  Directory.Build.props              新增：Version 等公共属性（唯一版本源）
  AgentHub.Runtime/                  新增 classlib，net10.0
    AgentHub.Runtime.csproj
    Core/**                          由 src/AgentHub/Core 整体 git mv 过来，命名空间不变
    Web/**                           由 src/AgentHub/Web  整体 git mv 过来，命名空间不变
    Core/Platform/                   新增：宿主接口 + 平台默认实现（见 §3.4）
    Hosting/AgentHubRuntime.cs       新增：组合根（见 §3.7）
  AgentHub.Backend/                  新增 Exe，net10.0，无界面
    AgentHub.Backend.csproj
    Program.cs
  AgentHub/                          现有 WPF，改为引用 Runtime
    AgentHub.csproj                  删 Core/Web 物理目录后自动不再编译它们
    Program.cs / App.xaml.cs / MainWindow.xaml.cs
    Shell/**                         保留；新增两个适配器（见 §3.9）
    frontend/ wwwroot/ assets/       本步不移动
```

依赖方向（只允许向下）：

```
AgentHub (WPF)  ──►  AgentHub.Runtime
AgentHub.Backend ──►  AgentHub.Runtime
AgentHub.Runtime ──►  Microsoft.AspNetCore.App + NuGet（Sqlite/LevelDB/Tomlyn/Zstd/ProtectedData）
```

`AgentHub.Runtime` 禁止引用：WPF、WinForms、WebView2、Velopack、`Microsoft.Win32.Registry`、`AgentHub.Shell` 命名空间。

---

## 3. 详细修改

### 3.1 工程文件

#### `src/Directory.Build.props`（新增）

```xml
<Project>
  <PropertyGroup>
    <Version>1.1.11</Version>
    <Product>AgentHub</Product>
    <Company>AgentHub</Company>
    <Authors>AgentHub</Authors>
    <Copyright>Copyright (c) 2026 AgentHub</Copyright>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

同时从 `AgentHub.csproj` 删除这些同名属性（`Version`、`Product`、`Company`、`Authors`、`Copyright`、`Nullable`、`ImplicitUsings`、`InvariantGlobalization`），其余保留。

#### `src/AgentHub.Runtime/AgentHub.Runtime.csproj`（新增）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>AgentHub.Runtime</AssemblyName>
    <!-- 必须保持 AgentHub：Core/Web 文件的命名空间是 AgentHub.Core.* / AgentHub.Web -->
    <RootNamespace>AgentHub</RootNamespace>
    <Description>AgentHub cross-platform runtime: core services + local HTTP API</Description>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="LevelDBSharp" Version="1.0.0" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.11" />
    <PackageReference Include="Tomlyn" Version="0.17.0" />
    <PackageReference Include="ZstdSharp.Port" Version="0.8.8" />
    <!-- 纯 net10.0 没有 ProtectedData，需显式引用；仅 Windows 实现使用 -->
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.*" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="Core\DocCore\Templates\SharedRules.md" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="AgentHub" />
    <InternalsVisibleTo Include="AgentHub.Backend" />
  </ItemGroup>
</Project>
```

说明：
- `ProtectedData` 包版本取与本机 SDK 匹配的 10.0.x 稳定版；若 `10.0.*` 通配解析失败就写具体版本。
- `InternalsVisibleTo` 是兜底；§3.3 仍要求把宿主直接用到的类型改为 `public`，不要靠它偷懒。

#### `src/AgentHub.Backend/AgentHub.Backend.csproj`（新增）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>AgentHub.Backend</AssemblyName>
    <RootNamespace>AgentHub.Backend</RootNamespace>
    <Description>AgentHub headless backend (local HTTP API host)</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgentHub.Runtime\AgentHub.Runtime.csproj" />
  </ItemGroup>

  <!-- 前端产物仍由 src/AgentHub 工程构建；这里只复制。构建 Backend 前必须先 npm run build。 -->
  <ItemGroup>
    <Content Include="..\AgentHub\wwwroot\**\*"
             Link="wwwroot\%(RecursiveDir)%(Filename)%(Extension)"
             CopyToOutputDirectory="PreserveNewest"
             CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

#### `src/AgentHub/AgentHub.csproj`（修改）

- 新增 `<ProjectReference Include="..\AgentHub.Runtime\AgentHub.Runtime.csproj" />`。
- 删除 `LevelDBSharp`、`Microsoft.Data.Sqlite`、`Tomlyn`、`ZstdSharp.Port` 四个 PackageReference（随 Runtime 传递）；保留 `Microsoft.Web.WebView2`、`Velopack`。
- 删除 `<EmbeddedResource Include="Core\DocCore\Templates\SharedRules.md" />`（已随文件迁走）。
- 保留 `FrameworkReference Microsoft.AspNetCore.App`（MainWindow/App 直接用到 `WebHostService`）。
- 其余（前端构建 Target、Content、`StartupObject`、图标、RID）保持不变。

#### `AgentHub.sln` / `AgentHub.CrossPlatform.slnf`（新增）

```powershell
dotnet new sln -n AgentHub
dotnet sln AgentHub.sln add src/AgentHub.Runtime/AgentHub.Runtime.csproj src/AgentHub.Backend/AgentHub.Backend.csproj src/AgentHub/AgentHub.csproj
```

`AgentHub.CrossPlatform.slnf`：

```json
{
  "solution": {
    "path": "AgentHub.sln",
    "projects": [
      "src\\AgentHub.Runtime\\AgentHub.Runtime.csproj",
      "src\\AgentHub.Backend\\AgentHub.Backend.csproj"
    ]
  }
}
```

### 3.2 文件迁移清单

用 `git mv` 保留历史：

```
src/AgentHub/Core  →  src/AgentHub.Runtime/Core
src/AgentHub/Web   →  src/AgentHub.Runtime/Web
```

不改任何 `namespace` 声明。迁移后 `src/AgentHub` 下不得再有 `Core/`、`Web/` 目录。

### 3.3 可见性调整（Runtime 内）

| 类型 | 改动 | 原因 |
|---|---|---|
| `AgentHub.Core.TokenCore.ScanScheduler` | `internal sealed class` → `public sealed class` | App 直接 new，且 §3.7 组合根对外暴露 |
| `AgentHub.Core.ProxyCore.HubLog` | `internal static class` → `public static class` | App 与 Backend 都要写日志 |

其他 `internal` 保持。若 Windows 工程编译时再报缺可见性，优先把具体成员改 `public`，并在提交说明里列出来。

### 3.4 平台层 `src/AgentHub.Runtime/Core/Platform/`

命名空间统一 `AgentHub.Core.Platform`。

#### 3.4.1 凭据保护

```csharp
public interface ISecretProtector
{
    /// <summary>平台是否支持加密保存。false 时 Protect 抛 InvalidOperationException，Unprotect 恒返回 null。</summary>
    bool IsSupported { get; }
    /// <summary>明文 → 密文（base64）。空串返回空串。</summary>
    string Protect(string plain);
    /// <summary>密文 → 明文。空/解不开返回 null（视为未配置）。</summary>
    string? Unprotect(string? cipher);
}

/// <summary>Windows DPAPI（CurrentUser）。密文格式与旧 Dpapi 完全一致：原始 ProtectedData 输出的 base64，不加前缀。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretProtector : ISecretProtector { ... }

/// <summary>非 Windows 平台占位（Keychain 在后续步骤接入）。</summary>
public sealed class UnsupportedSecretProtector : ISecretProtector
{
    public static readonly UnsupportedSecretProtector Instance = new();
    public bool IsSupported => false;
    public string Protect(string plain) =>
        string.IsNullOrEmpty(plain) ? "" : throw new InvalidOperationException("当前平台尚未支持凭据加密存储");
    public string? Unprotect(string? cipher) => null;
}

/// <summary>静态门面。替代原 Dpapi。宿主可在启动最早期 Use() 替换实现；不调用则按平台自动选择。</summary>
public static class Secrets
{
    private static ISecretProtector _current = OperatingSystem.IsWindows()
        ? new WindowsDpapiSecretProtector()
        : UnsupportedSecretProtector.Instance;

    public static ISecretProtector Current => _current;
    public static bool IsSupported => _current.IsSupported;
    public static void Use(ISecretProtector protector) => _current = protector;
    public static string Protect(string plain) => _current.Protect(plain);
    public static string? Unprotect(string? cipher) => _current.Unprotect(cipher);
}
```

要求：
- 把 `QuotaService.cs` 1361–1383 的 `Dpapi` 类**删除**，逻辑搬进 `WindowsDpapiSecretProtector`（保持 `CryptographicException → null` 行为）。
- 全仓 `Dpapi.Protect(` → `Secrets.Protect(`，`Dpapi.Unprotect(` → `Secrets.Unprotect(`，共 26 处；调用文件加 `using AgentHub.Core.Platform;`。改完 `rg -n "Dpapi" src/` 只允许剩注释。
- 密文格式不变，现有用户 `config.json` 里的 DPAPI 密文在 Windows 上必须继续可解（验收 W4）。
- 为什么用静态门面而不是全量注入：26 处调用分散在 8 个文件的构造、静态方法与端点闭包里，本步目标是拆分而非重写；门面已经提供了替换点，后续 Keychain 只需 `Secrets.Use(new MacKeychainSecretProtector())`。

#### 3.4.2 开机自启

```csharp
public interface IAutostartService
{
    bool IsSupported { get; }
    bool IsEnabled();
    void Enable();
    void Disable();
}

public sealed class UnsupportedAutostartService : IAutostartService
{
    public static readonly UnsupportedAutostartService Instance = new();
    public bool IsSupported => false;
    public bool IsEnabled() => false;
    public void Enable() { }
    public void Disable() { }
}
```

#### 3.4.3 应用更新

把 `Shell/AppUpdate.cs` 里两个纯 DTO **搬到** Runtime（`Core/Platform/AppUpdateModels.cs`），命名空间改为 `AgentHub.Core.Platform`，属性名保持小写（前端按字段名消费）：

- `AppUpdateStatus`（`installed / busy / canApply / needsInstaller / current / latest / releaseUrl / error / message`）
- `UpdateProgressSnapshot`（`running / percent / phase / message`）

`Shell/AppUpdate.cs` 中删除这两个定义，并加 `using AgentHub.Core.Platform;`。

```csharp
public interface IAppUpdateService
{
    bool IsSupported { get; }
    UpdateProgressSnapshot Progress { get; }
    AppUpdateStatus Snapshot();
    Task<AppUpdateStatus> CheckAsync();
    Task<AppUpdateStatus> ApplyAsync();
}

/// <summary>无应用内更新的宿主（Backend / 未来非 Velopack 壳）。</summary>
public sealed class ManualAppUpdateService : IAppUpdateService
{
    public ManualAppUpdateService(string currentVersion) { ... }
    public bool IsSupported => false;
    public UpdateProgressSnapshot Progress => new();
    public AppUpdateStatus Snapshot() => new() { installed = false, busy = false, current = _version };
    public Task<AppUpdateStatus> CheckAsync() => Task.FromResult(new AppUpdateStatus
    {
        installed = false, current = _version, releaseUrl = ProjectLinks.LatestReleaseUrl,
        message = "当前宿主不支持应用内更新，请前往发布页手动下载",
    });
    public Task<AppUpdateStatus> ApplyAsync() => Task.FromResult(new AppUpdateStatus
    {
        installed = false, current = _version, releaseUrl = ProjectLinks.LatestReleaseUrl,
        error = "当前宿主不支持应用内更新",
    });
}

/// <summary>项目链接与发布页判定（从 AppUpdate.IsReleasePage / GithubApiUpdateSource.RepoUrl 迁出，逻辑逐字保留）。</summary>
public static class ProjectLinks
{
    public const string RepoUrl = "https://github.com/sept13-yu/AgentHub";
    public static string LatestReleaseUrl => RepoUrl + "/releases/latest";
    public static bool IsReleasePage(string? url) { /* 原 AppUpdate.IsReleasePage 逐字搬入 */ }
}

public static class RuntimeVersion
{
    /// <summary>Runtime 程序集版本三段式；Directory.Build.props 统一后与壳版本一致。</summary>
    public static string Current =>
        typeof(RuntimeVersion).Assembly.GetName().Version?.ToString(3) ?? "";
}
```

`Shell/AppUpdate.cs`：`IsReleasePage` 改为转调 `ProjectLinks.IsReleasePage`；`GithubApiUpdateSource.RepoUrl` 改为 `ProjectLinks.RepoUrl` 的别名或直接删除并修正引用。

#### 3.4.4 系统打开 / 定位

```csharp
public static class ShellLauncher
{
    /// <summary>用系统默认程序打开文件、目录或 URL（UseShellExecute 在三平台均可用）。</summary>
    public static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    /// <summary>在文件管理器中定位。Windows: explorer /select；macOS: open -R；Linux: xdg-open 所在目录。</summary>
    public static void Reveal(string fullPath)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + fullPath + "\"") { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo("open", ["-R", fullPath]) { UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo("xdg-open", [Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath]) { UseShellExecute = false });
    }

    /// <summary>打开目录窗口。</summary>
    public static void OpenDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        else
            Open(dir);
    }
}
```

替换点：
- `Core/DocCore/DocService.cs` 278 → `ShellLauncher.Reveal(full)`
- `Core/McpCore/McpSyncService.cs` 590–595 → `ShellLauncher.OpenDirectory(dir)`
- `Core/McpCore/McpSyncService.cs` 601–606 → `ShellLauncher.Reveal(path)`

其他 `Process.Start(new ProcessStartInfo(path){UseShellExecute=true})` 的地方（`DocService.Open`、`SessionService.OpenProjectAsync`、`AgentRuleBootstrapService` 202/223、`UsageEndpoints` open-release/open-config）本身跨平台，可换成 `ShellLauncher.Open` 以统一，但不是必须。

#### 3.4.5 平台目录

```csharp
public static class PlatformPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>用户级配置根。Windows: %APPDATA%；macOS: ~/Library/Application Support；Linux: $XDG_CONFIG_HOME 或 ~/.config。</summary>
    public static string RoamingAppData { get; }

    /// <summary>用户级本地数据根。Windows: %LOCALAPPDATA%；macOS: ~/Library/Application Support；Linux: $XDG_DATA_HOME 或 ~/.local/share。</summary>
    public static string LocalAppData { get; }
}
```

Windows 分支必须直接返回 `Environment.GetFolderPath(ApplicationData / LocalApplicationData)`，保证 Windows 路径与现在**逐字节相同**。

替换点（只改 AgentHub 自身数据，不改第三方客户端目录）：
- `Core/ProxyCore/AgentHubConfig.cs` 358–366：`Dir`、`InstallDir`、`LocalDataDir` 改用 `PlatformPaths.RoamingAppData / LocalAppData`
- `Core/SessionCore/TitleOverrideStore.cs` 23：改用 `PlatformPaths.RoamingAppData`
- `Core/TokenCore/QoderQuota.cs` 186–196 `AppSupportRoot()`：改为 `return PlatformPaths.RoamingAppData;`（语义相同）

**不要动**：`CursorAuth`、`CursorProvider`、`TraeAuth`、`ZcodeDesktopStore`、`JsonServersMcpAdapter` 里的 `ApplicationData` 用法。这些是第三方客户端目录，属于下一步 Mac 适配，需要实机核对。

### 3.5 Web 层改动

#### `Web/WebHostService.cs`

新增两个宿主属性（与现有 `PickFolder` 同风格，非空默认值）：

```csharp
public IAutostartService Autostart { get; set; } = UnsupportedAutostartService.Instance;
public IAppUpdateService AppUpdate { get; set; } = new ManualAppUpdateService(RuntimeVersion.Current);
```

`Start()` 里 `MapUsageEndpoints` 调用追加传参 `Autostart, AppUpdate`。其余不变（端口、鉴权、静态目录、`/health`、就绪信号）。

#### `Web/UsageEndpoints.cs`

- 删除 `using AgentHub.Shell;`，加 `using AgentHub.Core.Platform;`。
- 签名追加 `IAutostartService autostart, IAppUpdateService appUpdate`（放在 `pickFolder` 之后）。
- 逐端点：

| 端点 | 原 | 新 |
|---|---|---|
| `GET /api/settings` | `AppUpdate.Snapshot()`；`autostartActual = AutostartManager.IsEnabled()` | `appUpdate.Snapshot()`；`autostartActual = autostart.IsSupported && autostart.IsEnabled()`；**新增字段** `autostartSupported = autostart.IsSupported`、`updateSupported = appUpdate.IsSupported`、`secretsSupported = Secrets.IsSupported` |
| `PUT /api/settings` app.autostart | `AutostartManager.Enable()/Disable()` | `if (autostart.IsSupported) {...} else notes.Add("当前宿主不支持开机自启");` 响应 `notes` 由 `Array.Empty` 改为实际列表 |
| `PUT /api/settings` credentials | `Dpapi.Protect` | `Secrets.Protect`；新增 `catch (InvalidOperationException ex) → 400 { error = ex.Message }` |
| `GET /api/update` | `AppUpdate.CheckAsync()` | `appUpdate.CheckAsync()` |
| `GET /api/update/progress` | `AppUpdate.Progress` | `appUpdate.Progress` |
| `POST /api/settings/apply-update` | `AppUpdate.ApplyAsync()` | `appUpdate.ApplyAsync()` |
| `POST /api/settings/open-release` | `GithubApiUpdateSource.RepoUrl`、`AppUpdate.IsReleasePage` | `ProjectLinks.LatestReleaseUrl`、`ProjectLinks.IsReleasePage` |

前端本步**不改**。新增字段是给下一步壳层隐藏开关用的，现有 `SettingsPage.vue` 忽略未知字段。

### 3.6 Core 路径 bug 修复

#### `Core/SessionCore/SessionIndex.cs` 283–293 `NormalizeProjectPath`

```csharp
internal static string NormalizeProjectPath(string? path)
{
    if (string.IsNullOrWhiteSpace(path)) return "";
    var s = path.Trim();
    if (OperatingSystem.IsWindows())
    {
        s = s.Replace('/', '\\');
        while (s.Length > 0 && s.EndsWith('\\'))
        {
            if (s.Length == 3 && char.IsLetter(s[0]) && s[1] == ':') break;   // 保留 C:\
            s = s[..^1];
        }
        return s;
    }
    while (s.Length > 1 && s.EndsWith('/')) s = s[..^1];                        // 保留 /
    return s;
}
```

#### `Core/McpCore/McpSyncService.cs` 722–726 `NormPath`

```csharp
private static string NormPath(string? s)
{
    if (string.IsNullOrEmpty(s)) return "";
    s = s.Trim();
    return OperatingSystem.IsWindows() ? s.Replace('/', '\\') : s;
}
```

同函数所在的比较仍用 `OrdinalIgnoreCase`，本步不改（Mac 大小写敏感属于下一步）。

`Core/DocCore/AgentRuleTemplates.cs` 241/250 的 `Replace('/', '\\')` 是对规则**文本**做匹配（`%USERPROFILE%\.agents\AGENTS.md`），不是文件系统路径，本步**不改**；共用规则引用在 Mac 上该写成什么属于下一步。

### 3.7 组合根 `src/AgentHub.Runtime/Hosting/AgentHubRuntime.cs`

目的：`App.OnStartup` 92–124 那段装配只保留一份，WPF 和 Backend 都用它，避免两份漂移。

```csharp
namespace AgentHub.Hosting;

public sealed class RuntimeHostOptions
{
    public IAutostartService Autostart { get; set; } = UnsupportedAutostartService.Instance;
    public IAppUpdateService AppUpdate { get; set; } = new ManualAppUpdateService(RuntimeVersion.Current);
    public Func<string, string?>? PickFolder { get; set; }
    public Func<bool>? PetIsRunning { get; set; }
    /// <summary>为空则按平台默认（Windows DPAPI / 其他 Unsupported）。</summary>
    public ISecretProtector? SecretProtector { get; set; }
    public Action<string>? Log { get; set; }
}

public sealed class AgentHubRuntime : IDisposable
{
    public static AgentHubRuntime Create(RuntimeHostOptions options);

    public AgentHubConfig Config { get; }
    public TitleOverrideStore Titles { get; }
    public SessionService Sessions { get; }
    public SkillManager Skills { get; }
    public DocService Docs { get; }
    public AgentRuleBootstrapService AgentRules { get; }
    public TokenService Tokens { get; }
    public QuotaService Quotas { get; }
    public ScanScheduler Scan { get; }
    public CodexConfigService CodexConfig { get; }
    public McpSyncService Mcp { get; }
    public WebHostService Web { get; }

    /// <summary>价格基线变化或用量扫描完成后触发；壳层用来刷新页面 / 宠物。可能来自任意线程。</summary>
    public event Action? DashboardRefreshRequested;
    /// <summary>用量扫描完成（一次扫描一次）。可能来自任意线程。</summary>
    public event Action? UsageScanCompleted;
    /// <summary>设置保存后触发（Runtime 已自行 Scan.Reconfigure()）。可能来自任意线程。</summary>
    public event Action? SettingsSaved;

    public Task Ready => Web.Ready;
    public void Start();                         // Web.Start(); Scan.Reconfigure(); PriceSyncService.RefreshInBackground();
    public void RunInitialScanInBackground();    // Task.Run(Scan.RunAsync) + 日志
    public Task SyncNowAsync();                  // Scan.RunAsync()，异常记日志不抛
    public void Stop();                          // Scan.Dispose(); Web.Stop();
    public void Dispose() => Stop();
}
```

`Create` 内部顺序严格照搬 `App.OnStartup` 92–124：

1. `if (options.SecretProtector is not null) Secrets.Use(options.SecretProtector);`
2. `if (OperatingSystem.IsWindows()) AgentHubConfig.RelocateLocalDataFromInstallDir();`
3. `Config = AgentHubConfig.Load(); PriceSyncService.TryLoadCache(); PriceSyncService.OnBaselineChanged = () => DashboardRefreshRequested?.Invoke();`
4. `Titles`、`Sessions`、`Skills`（`RecoverStaging()`）、`Docs`、`AgentRules`（`RecoverPendingTransaction()`）、`Tokens`、`Quotas`、`Scan`（onCompleted → `UsageScanCompleted` + `DashboardRefreshRequested`）、`CodexConfig`、`Mcp`、`CodexConfig.EnsureSeeded()`
5. `Web = new WebHostService(...)`；`Web.UsageScan = () => Scan.RunAsync()`；`Web.PickFolder / PetIsRunning / Autostart / AppUpdate` 取自 options；`Web.SettingsSaved += () => { Scan.Reconfigure(); SettingsSaved?.Invoke(); }`

`Log` 默认 `HubLog.Write`。

#### `src/AgentHub/App.xaml.cs` 改造

- 字段：删 `_web/_config/_codexConfig/_mcp/_titles/_sessions/_docs/_skills/_agentRules/_tokens/_quotas/_scan`，改为 `private AgentHubRuntime? _rt;`
- `OnStartup`：credential gate、WinForms 初始化、WebView2 检测、异常兜底、单实例守卫**原样保留**；随后：

```csharp
_rt = AgentHubRuntime.Create(new RuntimeHostOptions
{
    Autostart = new RegistryAutostartService(),
    AppUpdate = new VelopackAppUpdateService(),
    PickFolder = initial => Dispatcher.Invoke(() => { /* 原 FolderBrowserDialog 代码 */ }),
    PetIsRunning = () => _pet?.IsRunning == true,
});
_rt.DashboardRefreshRequested += () => Dispatcher.BeginInvoke(RequestDashboardRefresh);
_rt.UsageScanCompleted += () => Dispatcher.BeginInvoke(() => _pet?.PushStats());
_rt.SettingsSaved += () => Dispatcher.BeginInvoke(() => _pet?.Apply());
_rt.Start();

var win = new MainWindow(_rt.Web, _rt.Config);
MainWindow = win; win.Show();
_pet = new PetHost(_rt.Web, _rt.Tokens, _rt.Config, ShowMainWindow, SyncNow, Dispatcher);
_tray = new TrayIconService(BuildTrayActions(), IsLightTheme(_rt.Config.App.Theme));
_ = _rt.Ready.ContinueWith(_ => Dispatcher.BeginInvoke(() => _pet?.Apply()), TaskContinuationOptions.OnlyOnRanToCompletion);
_rt.RunInitialScanInBackground();
```

- `SyncNow` → `_ = _rt?.SyncNowAsync();`
- `ExitApp`：`_scan?.Dispose()` + `_web?.Stop()` 合并为 `_rt?.Stop()`，其余顺序不变（先挂 8 秒硬退出、关窗、宠物、托盘）。
- 原 `OnUsageScanCompleted` 中「宠物 PushStats + RequestDashboardRefresh」语义由上面两个事件覆盖，注意 `Scan` 的 onCompleted 在 Runtime 里同时触发 `UsageScanCompleted` 与 `DashboardRefreshRequested`，不要在 App 里重复刷新两次。

`MainWindow.xaml.cs` 不改（构造参数类型仍是 `WebHostService, AgentHubConfig`）。

### 3.8 `src/AgentHub.Backend/Program.cs`

```
用法：
  AgentHub.Backend [--parent-pid <pid>]
  AgentHub.Backend codex-credential <connection-id>

环境变量：
  AGENTHUB_WRITE_TOKEN   壳生成的写鉴权 token（WebHostService 已支持）。不设则随机生成，
                         此时无人能拿到 token，页面只读——仅适合联调 GET。
                         Backend 永不向 stdout/日志输出 token。
```

启动序：

1. `if (CodexCredentialGate.IsCredentialRequest(args)) { CodexCredentialGate.Handle(args); return; }`（该方法自行 `Environment.Exit`）
2. `Console.OutputEncoding = UTF8`
3. `var rt = AgentHubRuntime.Create(new RuntimeHostOptions { Log = m => { HubLog.Write(m); Console.Error.WriteLine(m); } });`
   - 不传 `Autostart / AppUpdate / PickFolder / PetIsRunning`，全部使用默认「不支持」实现（即使在 Windows 上也如此：Backend 的 exe 路径不是用户装的 AgentHub.exe，注册自启会指错）
4. `rt.Start(); await rt.Ready;`
   - 失败（典型是 18780 被占）：stderr 输出 `AGENTHUB_START_FAILED <message>`，退出码 **3**
5. stdout 输出一行就绪信号（唯一的 stdout 输出）：`AGENTHUB_READY {"port":18780,"pid":<pid>,"version":"<RuntimeVersion.Current>"}`
6. `rt.RunInitialScanInBackground();`
7. 等待退出：`Console.CancelKeyPress` 与 `AppDomain.ProcessExit`（SIGTERM）→ `rt.Stop()`；若给了 `--parent-pid`，每 2 秒 `Process.GetProcessById` 探测一次，父进程消失则 `rt.Stop()` 并退出码 0
8. 正常退出码 0；未处理异常记日志，退出码 1

注意：
- Backend 不使用 `SingleInstanceGuard`（user32），端口独占就是单实例。
- `codex-credential` 子命令在 Backend 里可用，但 Windows 上 WPF 仍是 `~/.codex/config.toml` 里 `auth.command` 指向的进程；本步不要在同一台 Windows 上同时用 WPF 与 Backend 去「应用」Codex 配置（`CodexToml` 会校验 `auth.command` 必须等于当前进程路径，两者会互相覆盖）。Backend 在 Windows 上只作联调。

### 3.9 Windows 壳适配器 `src/AgentHub/Shell/`

```csharp
// RegistryAutostartService.cs —— 包装现有静态类，不改 AutostartManager 本体
public sealed class RegistryAutostartService : IAutostartService
{
    public bool IsSupported => true;
    public bool IsEnabled() => AutostartManager.IsEnabled();
    public void Enable() => AutostartManager.Enable();
    public void Disable() => AutostartManager.Disable();
}

// VelopackAppUpdateService.cs —— 包装现有静态类，不改 AppUpdate 本体
public sealed class VelopackAppUpdateService : IAppUpdateService
{
    public bool IsSupported => true;
    public UpdateProgressSnapshot Progress => AppUpdate.Progress;
    public AppUpdateStatus Snapshot() => AppUpdate.Snapshot();
    public Task<AppUpdateStatus> CheckAsync() => AppUpdate.CheckAsync();
    public Task<AppUpdateStatus> ApplyAsync() => AppUpdate.ApplyAsync();
}
```

### 3.10 脚本与 CI（版本源改到 `Directory.Build.props`）

| 文件 | 改动 |
|---|---|
| `pack/windows/pack.ps1` 16–18 | `[xml]$project = Get-Content -Raw src\Directory.Build.props`；`$version = [string]$project.Project.PropertyGroup.Version`（其余不变） |
| `.github/workflows/release.yml` 25–28 | 同上改为读取 `src/Directory.Build.props`；`latest.json` 校验保留 |
| `rebuild.ps1` | 不改。`OutDir` 仍是 `src\AgentHub\bin\Debug\net10.0-windows10.0.19041.0`（开机自启注册表指向这里） |
| `README.md` | 若有构建说明，补一行 Backend 构建命令（可选） |

`.gitignore` 已忽略 `bin/ obj/ *.dll *.exe`，新工程无需追加。

---

## 4. 实施顺序（每步 Windows 编译必须为绿，按此拆 commit）

| # | 内容 | 完成判定 |
|---|---|---|
| 1 | 在**现有工程内**新增 `Core/Platform/*`（§3.4 全部）、迁 DTO、`Dpapi → Secrets` 26 处替换、`UsageEndpoints` 改接口（§3.5）、新增两个 Windows 适配器（§3.9）、App 里 `_web.Autostart = new RegistryAutostartService(); _web.AppUpdate = new VelopackAppUpdateService();` | `dotnet build src/AgentHub -c Release` 通过；设置页自启/更新/凭据行为与之前一致 |
| 2 | `Directory.Build.props`、`AgentHub.Runtime.csproj`、`git mv Core Web`、可见性调整（§3.3）、WPF 改 ProjectReference、`AgentHub.sln` | Windows 全量编译通过；`rebuild.ps1` 启动正常 |
| 3 | 路径修复（§3.6）与 `PlatformPaths`（§3.4.5）、`ShellLauncher` 替换 3 处 | Windows 上配置路径、会话项目分组、定位文件与之前一致 |
| 4 | `AgentHubRuntime` 组合根 + `App.xaml.cs` 改造（§3.7） | 托盘同步、宠物推送、设置保存后重配扫描、退出流程与之前一致 |
| 5 | `AgentHub.Backend` + `AgentHub.CrossPlatform.slnf`（§3.8） | Windows 上 Backend 能起、`/health` 与 `/app/` 正常；Mac/Linux 上 `dotnet build AgentHub.CrossPlatform.slnf` 通过 |
| 6 | 脚本与 CI（§3.10） | `pack/windows/pack.ps1 -PublishOnly` 正常读到版本 |

第 1 步先于第 2 步的原因：`UsageEndpoints` 仍 `using AgentHub.Shell` 时，把 `Web/` 挪进 classlib 会直接编不过。

---

## 5. 明确不做（留给下一步，实施 AI 不要顺手做）

- 不新增 Tauri / Photino / 任何壳工程；不改 `frontend/`、`wwwroot/` 位置；不改 Vue 代码。
- 不实现 Keychain / 文件主密钥；非 Windows 平台凭据就是「不支持」。
- 不改第三方客户端目录探测（Cursor、Trae、Zcode、WorkBuddy、Mimocode、JsonServersMcpAdapter）。
- 不改 `AgentRuleTemplates.SharedReference` 的 `%USERPROFILE%` 写法。
- 不改 Qoder 国际版 Named Pipe 通信、进程名检测（`codex` / `ChatGPT` / `Cursor`）。
- 不删 WPF、不动 Velopack 渠道、不改 `latest.json` / `releases.win.json` 机制。
- 不做 SSE / WebSocket 推送（Backend 阶段页面刷新靠用户手动）。
- 不把端口做成可配置。

---

## 6. 验收清单

### 6.1 Windows 回归（必须全部通过，任何一项失败即回退该 commit）

| # | 用例 | 期望 |
|---|---|---|
| W1 | `dotnet build AgentHub.sln -c Release` | 0 error；不新增 warning 类别（允许 CA1416 以外的既有 warning） |
| W2 | `.\rebuild.ps1` | 构建成功，AgentHub 启动，托盘出现，主窗显示看板 |
| W3 | 设置页：切换开机自启开关 | `HKCU\...\Run\AgentHub` 值相应写入/删除；重新打开设置页 `autostartActual` 正确 |
| W4 | 升级前已在设置页保存过 DeepSeek Key / 中转 Key 的 `config.json` | 升级后设置页显示「已设置」；额度砖正常请求（说明旧密文可解） |
| W5 | 设置页重新保存一个 Key，再重启 | 仍显示已设置；`config.json` 中密文仍为 base64 无前缀 |
| W6 | 设置页「检查更新」「立即更新」 | 行为与之前一致（走 Velopack） |
| W7 | Codex 配置页：切换连接、导入/切换 ChatGPT 档案 | 与之前一致；`config.toml` 中 `auth.command` 仍指向 `AgentHub.exe` |
| W8 | 资料页「定位文件」、MCP 页「打开配置所在目录」 | explorer 正确定位 |
| W9 | 会话页项目分组 | 分组数量与升级前一致（`C:/a/b` 与 `C:\a\b` 仍归一组） |
| W10 | 托盘「立即同步」、宠物统计推送、设置保存后扫描周期变化 | 与之前一致 |
| W11 | 托盘「退出」 | 8 秒内进程结束，无残留 |
| W12 | `pack/windows/pack.ps1 -PublishOnly` | 正常读到版本并发布到 `dist/win-x64` |
| W13 | `dotnet run --project src/AgentHub.Backend`（先退出 WPF 版） | stdout 出现 `AGENTHUB_READY {...}`；`GET http://127.0.0.1:18780/health` 200；`/app/` 返回页面；`/api/settings` 中 `autostartSupported=false, updateSupported=false, secretsSupported=true` |
| W14 | 设 `AGENTHUB_WRITE_TOKEN=test` 启动 Backend，`PUT /api/settings` 带 `X-AgentHub-Token: test`、`Host: 127.0.0.1:18780`、body `{"app":{"autostart":true}}` | 200，响应 `notes` 含「当前宿主不支持开机自启」，注册表未被写 |
| W15 | 在 Backend 运行时启动 WPF 版 | WPF 主窗提示端口占用（原有行为），不崩溃 |

### 6.2 非 Windows（macOS arm64 优先，Linux/WSL 可作替代）

| # | 用例 | 期望 |
|---|---|---|
| X1 | `dotnet build AgentHub.CrossPlatform.slnf -c Release` | 0 error |
| X2 | `dotnet run --project src/AgentHub.Backend` | `AGENTHUB_READY` 输出；`/health` 200 |
| X3 | `GET /app/` | 返回 Vue 首页 HTML（需先在 Windows 或本机 `npm run build` 生成 `wwwroot/app`） |
| X4 | `GET /api/settings` | 200；`secretsSupported=false`；所有 `*Set` 为 false；`configPath` 位于 `~/Library/Application Support/AgentHub/config.json`（macOS）或 `~/.config/AgentHub/config.json`（Linux） |
| X5 | `GET /api/usage`、`GET /api/sessions`、`GET /api/quotas` | 200（内容可为空；不得 500） |
| X6 | 带 token `PUT /api/settings` body `{"credentials":{"deepseekKey":"x"}}` | 400，`error` 为「当前平台尚未支持凭据加密存储」；`config.json` 未写入密文 |
| X7 | 造一批 `Project` 为 `/Users/a/b/`、`/Users/a/b` 的会话摘要调用 `SessionIndex.NormalizeProjectPath`（可用临时测试工程或 `dotnet script`） | 两者归一为 `/Users/a/b`；不出现反斜杠 |
| X8 | `dotnet publish src/AgentHub.Backend -c Release -r osx-arm64 --self-contained -o dist/osx-arm64` | 产出 `AgentHub.Backend` 可执行文件；目录内含 `wwwroot/app/index.html`、`libe_sqlite3.dylib`（或 `runtimes/osx-arm64/native/`）、LevelDB 的 osx 原生库 |
| X9 | 运行 X8 产物 | 与 X2–X5 相同结果 |
| X10 | `kill -TERM <pid>` | 进程 3 秒内退出，退出码 0 |
| X11 | 用 `--parent-pid` 指向一个随后被杀的进程 | Backend 随之退出 |

Linux 上若 `InvariantGlobalization=false` 报缺 ICU，安装 `libicu` 即可，不要改成 `true`。

---

## 7. 风险与注意点

| 风险 | 处理 |
|---|---|
| `System.Security.Cryptography.ProtectedData` 在非 Windows 调用抛 `PlatformNotSupportedException` | 只在 `WindowsDpapiSecretProtector` 内调用，且类标 `[SupportedOSPlatform("windows")]`，`Secrets` 静态初始化按 `OperatingSystem.IsWindows()` 选择 |
| CA1416 分析器警告 | 上一条已覆盖；不要用 `#pragma` 全局压制 |
| 嵌入资源名变化 | `AgentRuleTemplates.ReadEmbedded` 按后缀匹配，且 Runtime 的 `RootNamespace` 仍为 `AgentHub`，资源名不变；X1 后可用 `GetManifestResourceNames()` 打印确认 |
| WinForms 隐式全局 using 消失后 Core 编不过 | §1 已 grep 确认 Core/Web 未用 `System.Drawing` / `System.Windows.Forms`；若出现，按需加显式 `using`，不要给 Runtime 加 `UseWindowsForms` |
| `Directory.Build.props` 改动使 `pack.ps1` / `release.yml` 读不到版本 | §3.10 两处必须同一 commit 修改；W12 验证 |
| `AppUpdateStatus` 序列化字段名 | 类搬迁时保持小写属性名不动，前端 `SettingsPage.vue` 按 `current / installed / canApply ...` 读取 |
| Backend 与 WPF 在 Windows 上同时运行 | 端口互斥，第二个起不来，属预期（W15） |
| `Scan` 完成事件触发两次刷新 | Runtime 的 onCompleted 同时触发 `UsageScanCompleted` 与 `DashboardRefreshRequested`；App 只在后者里 `RequestDashboardRefresh`，前者只推宠物 |
| 组合根重构漏掉某一步 | 以 `App.OnStartup` 92–124 为对照逐行核对；`_codexConfig.EnsureSeeded()` 与 `_skills.RecoverStaging()`、`_agentRules.RecoverPendingTransaction()` 容易漏 |

---

## 8. 本步完成后的下一步（仅作方向，不属于本方案）

1. 壳工程：一份双平台 Tauri（或 Photino.NET）工程，sidecar 拉起 `AgentHub.Backend`，通过 `AGENTHUB_WRITE_TOKEN` 注入 token，等待 `AGENTHUB_READY` 后导航到 `http://127.0.0.1:18780/app/`；先只打 Mac 包。
2. `MacKeychainSecretProtector` 实现 `ISecretProtector`，`Secrets.Use()` 接入。
3. 第三方客户端 Mac 目录、Qoder Unix socket、进程名核对、`SharedReference` 的 Mac 写法。
4. 页面刷新改为 Runtime 侧 SSE，去掉对壳 `ExecuteScript` 的依赖。
