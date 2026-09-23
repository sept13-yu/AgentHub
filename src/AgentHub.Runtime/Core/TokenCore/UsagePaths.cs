using AgentHub.Core.Platform;

namespace AgentHub.Core.TokenCore;

/// <summary>各家用量根目录。Windows 的 OpenCode / Devin 走 %USERPROFILE%\.local\share，不走 %LOCALAPPDATA%。</summary>
internal static class UsagePaths
{
    public static string Home => PlatformPaths.Home;

    public static string XdgDataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg)) return Expand(xdg);
        return Path.Combine(Home, ".local", "share");
    }

    public static string DataHome(string home, string? xdgDataHome) =>
        string.IsNullOrWhiteSpace(xdgDataHome) ? Path.Combine(home, ".local", "share") : xdgDataHome.Trim();

    public static string OpenCodeDataDir(string home, string? xdgDataHome) =>
        Path.Combine(DataHome(home, xdgDataHome), "opencode");

    public static string DevinDbPath(string home, string? xdgDataHome) =>
        Path.Combine(DataHome(home, xdgDataHome), "devin", "cli", "sessions.db");

    public static IEnumerable<string> MiniMaxHomes() =>
        OneOr(Env("TOKENTRACKER_MINIMAX_HOME"), Path.Combine(Home, ".minimax"));

    public static IEnumerable<string> GeminiHomes() =>
        OneOr(Env("GEMINI_HOME"), Path.Combine(Home, ".gemini"));

    public static IEnumerable<string> AntigravityVariantHomes()
    {
        foreach (var gemini in GeminiHomes())
        {
            yield return Path.Combine(gemini, "antigravity");
            yield return Path.Combine(gemini, "antigravity-ide");
            yield return Path.Combine(gemini, "antigravity-cli");
        }
    }

    public static IEnumerable<string> ReasonixHomes()
    {
        var over = Env("TOKENTRACKER_REASONIX_HOME") ?? Env("REASONIX_STATE_HOME");
        if (over is not null)
        {
            yield return over;
            yield break;
        }
        yield return Path.Combine(Home, ".reasonix");
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(PlatformPaths.RoamingAppData, "reasonix");
    }

    public static IEnumerable<string> OpenCodeDataDirs() =>
        OneOr(Env("OPENCODE_HOME"), Path.Combine(XdgDataHome(), "opencode"));

    public static IEnumerable<string> DevinDbCandidates() =>
        OneOr(Env("TOKENTRACKER_DEVIN_DB"), DevinDbPath(Home, Environment.GetEnvironmentVariable("XDG_DATA_HOME")));

    public static IEnumerable<string> DevinProbeRoots()
    {
        foreach (var db in DevinDbCandidates())
        {
            var dir = Path.GetDirectoryName(db);
            if (string.IsNullOrEmpty(dir)) continue;
            var root = string.Equals(Path.GetFileName(dir), "cli", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(dir)
                : dir;
            if (!string.IsNullOrEmpty(root)) yield return root;
        }
    }

    public static string KiroBase(string roamingAppData) =>
        Path.Combine(roamingAppData, "Kiro", "User", "globalStorage", "kiro.kiroagent");

    public static IEnumerable<string> KiroBases() =>
        OneOr(Env("TOKENTRACKER_KIRO_HOME"), KiroBase(PlatformPaths.RoamingAppData));

    public static string HermesStateDb(string home) => Path.Combine(home, ".hermes", "state.db");

    public static IEnumerable<string> HermesHomes()
    {
        var over = Env("TOKENTRACKER_HERMES_HOME");
        if (over is not null)
        {
            yield return over;
            yield break;
        }
        yield return Path.Combine(Home, ".hermes");
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(PlatformPaths.LocalAppData, "hermes");
    }

    public static string CopilotHome(string home) => Path.Combine(home, ".copilot");

    public static string CopilotSessionStore(string copilotHome) => Path.Combine(copilotHome, "session-store.db");

    public static string CopilotAppDb(string copilotHome) => Path.Combine(copilotHome, "data.db");

    public static IEnumerable<string> CopilotHomes() =>
        OneOr(Env("COPILOT_HOME"), CopilotHome(Home));

    public static IEnumerable<string> CopilotProbeRoots()
    {
        foreach (var home in CopilotHomes()) yield return home;
        yield return Path.Combine(Home, ".copilot-otel");
    }

    public static IEnumerable<string> CopilotOtelFiles()
    {
        var explicitFile = Env("COPILOT_OTEL_FILE_EXPORTER_PATH");
        if (explicitFile is not null && File.Exists(explicitFile)) yield return explicitFile;
        foreach (var dir in CopilotHomes().Select(h => Path.Combine(h, "otel")).Append(Path.Combine(Home, ".copilot-otel")))
        {
            foreach (var file in UsageIo.EnumerateFiles(dir, 1, static (path, _) => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
                yield return file;
        }
    }

    public static IEnumerable<string> CopilotSessionStoreDbs() =>
        OneOr(Env("TOKENTRACKER_COPILOT_SESSION_STORE_DB"), CopilotSessionStore(CopilotHomes().First()));

    public static IEnumerable<string> CopilotAppDbs() =>
        OneOr(Env("TOKENTRACKER_COPILOT_APP_DB"), CopilotAppDb(CopilotHomes().First()));

    public static IEnumerable<string> KimiCodeHomes()
    {
        yield return Env("KIMI_CODE_HOME") ?? Path.Combine(Home, ".kimi-code");
        yield return Env("KIMI_HOME") ?? Path.Combine(Home, ".kimi");
    }

    public static IEnumerable<string> CodeBuddyHomes() =>
        OneOr(Env("CODEBUDDY_HOME"), Path.Combine(Home, ".codebuddy"));

    public static IEnumerable<string> CodeBuddyLogRoots()
    {
        var config = PlatformPaths.RoamingAppData;
        var data = PlatformPaths.LocalAppData;
        yield return Path.Combine(config, "CodeBuddy CN", "logs");
        yield return Path.Combine(config, "Code", "logs");
        yield return Path.Combine(data, "CodeBuddyExtension", "Logs", "CodeBuddyIDE");
        yield return Path.Combine(data, "CodeBuddyExtension", "Logs", "VSCode");
    }

    public static string OpenClawHome(string home) => Path.Combine(home, ".openclaw");

    public static IEnumerable<string> OpenClawHomes() =>
        OneOr(Env("TOKENTRACKER_OPENCLAW_HOME") ?? Env("OPENCLAW_HOME") ?? Env("OPENCLAW_STATE_DIR"),
            OpenClawHome(Home));

    public static string EveryCodeHome(string home) => Path.Combine(home, ".code");

    public static IEnumerable<string> EveryCodeHomes() =>
        OneOr(Env("CODE_HOME"), EveryCodeHome(Home));

    public static string AcodeHome(string home) => Path.Combine(home, ".acode");

    public static IEnumerable<string> AcodeHomes() =>
        OneOr(Env("TOKENTRACKER_ACODE_HOME"), AcodeHome(Home));

    public static string OmpAgentDir(string home) => Path.Combine(home, ".omp", "agent");

    public static IEnumerable<string> OmpHomes()
    {
        var omp = Env("OMP_HOME");
        if (omp is not null)
        {
            yield return omp;
            yield break;
        }
        var config = Env("PI_CONFIG_DIR");
        if (config is not null)
        {
            yield return Path.IsPathRooted(config) ? config : Path.Combine(Home, config);
            yield break;
        }
        yield return Path.Combine(Home, ".omp");
    }

    public static IEnumerable<string> OmpAgentDirs()
    {
        var over = Env("TOKENTRACKER_OMP_AGENT_DIR");
        if (over is not null)
        {
            yield return over;
            yield break;
        }
        var shared = Env("PI_CODING_AGENT_DIR");
        if (shared is not null && !PiCodingAgentDirOwnedByPi())
        {
            yield return shared;
            yield break;
        }
        foreach (var home in OmpHomes())
            yield return Path.Combine(home, "agent");
    }

    public static string OmoAgentDir(string home) => Path.Combine(home, ".omo", "agent");

    public static IEnumerable<string> OmoHomes() =>
        OneOr(Env("TOKENTRACKER_OMO_HOME") ?? Env("OMO_HOME"), Path.Combine(Home, ".omo"));

    public static IEnumerable<string> OmoAgentDirs()
    {
        var over = Env("TOKENTRACKER_OMO_AGENT_DIR");
        if (over is not null)
        {
            yield return over;
            yield break;
        }
        foreach (var home in OmoHomes())
            yield return Path.Combine(home, "agent");
    }

    public static string PiAgentDir(string home) => Path.Combine(home, ".pi", "agent");

    public static IEnumerable<string> PiHomes()
    {
        yield return Path.Combine(Home, ".pi");
    }

    public static IEnumerable<string> PiAgentDirs()
    {
        var over = Env("TOKENTRACKER_PI_AGENT_DIR");
        if (over is not null)
        {
            yield return over;
            yield break;
        }
        var shared = Env("PI_CODING_AGENT_DIR");
        if (shared is not null && PiCodingAgentDirOwnedByPi())
        {
            yield return shared;
            yield break;
        }
        yield return Path.Combine(Home, ".pi", "agent");
    }

    /// <summary>~/.pi 是目录时，PI_CODING_AGENT_DIR 归 pi；否则归 oh-my-pi。</summary>
    private static bool PiCodingAgentDirOwnedByPi() =>
        Directory.Exists(Path.Combine(Home, ".pi"));

    public static string PrimeAgentDir(string home) => Path.Combine(home, ".prime", "agent");

    public static IEnumerable<string> PrimeHomes() =>
        OneOr(Env("TOKENTRACKER_PRIME_AGENT_HOME"), Path.Combine(Home, ".prime"));

    public static IEnumerable<string> PrimeAgentDirs()
    {
        var over = Env("TOKENTRACKER_PRIME_AGENT_DIR");
        if (over is not null)
        {
            yield return over;
            yield break;
        }
        foreach (var home in PrimeHomes())
            yield return Path.Combine(home, "agent");
    }

    public static string CraftConfigDir(string home) => Path.Combine(home, ".craft-agent");

    public static IEnumerable<string> CraftConfigDirs() =>
        OneOr(Env("CRAFT_CONFIG_DIR"), CraftConfigDir(Home));

    /// <summary>Linux / macOS 的 XDG 数据根布局。Windows 运行时走 %APPDATA%\kilo\kilo.db。</summary>
    public static string KiloCliDb(string dataHome) => Path.Combine(dataHome, "kilo", "kilo.db");

    public static IEnumerable<string> KiloCliDbs()
    {
        var home = Env("KILO_HOME");
        if (home is not null)
        {
            yield return Path.Combine(home, "kilo.db");
            yield break;
        }
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(PlatformPaths.RoamingAppData, "kilo", "kilo.db");
        else
            yield return KiloCliDb(PlatformPaths.LocalAppData);
    }

    public static IEnumerable<string> KiloCliHomes()
    {
        foreach (var db in KiloCliDbs())
        {
            var dir = Path.GetDirectoryName(db);
            if (!string.IsNullOrEmpty(dir)) yield return dir;
        }
    }

    private static readonly string[] EditorApps =
    [
        "Code", "Code - Insiders", "Cursor", "CodeBuddy", "Windsurf", "VSCodium",
    ];

    /// <summary>VS Code 家族数据根。TOKENTRACKER_KILOCODE_ROOTS 按冒号拆开，和 TokenTracker 一样。</summary>
    public static IEnumerable<string> EditorDataRoots()
    {
        var over = Environment.GetEnvironmentVariable("TOKENTRACKER_KILOCODE_ROOTS");
        if (!string.IsNullOrWhiteSpace(over))
        {
            foreach (var part in over.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var path = Expand(part);
                if (path.Length > 0) yield return path;
            }
            yield break;
        }
        var baseDir = PlatformPaths.RoamingAppData;
        foreach (var name in EditorApps)
            yield return Path.Combine(baseDir, name);
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(baseDir, "Trae");
            yield return Path.Combine(baseDir, "Trae CN");
        }
    }

    public static string KiloCodeTasks(string editor) =>
        Path.Combine(editor, "User", "globalStorage", "kilocode.kilo-code", "tasks");

    public static string RooCodeTasks(string editor) =>
        Path.Combine(editor, "User", "globalStorage", "rooveterinaryinc.roo-cline", "tasks");

    public static IEnumerable<string> KiloCodeStorageDirs()
    {
        foreach (var root in EditorDataRoots())
            yield return Path.GetDirectoryName(KiloCodeTasks(root))!;
    }

    public static IEnumerable<string> RooCodeStorageDirs()
    {
        foreach (var root in EditorDataRoots())
            yield return Path.GetDirectoryName(RooCodeTasks(root))!;
    }

    /// <summary>Linux 小写 zed。macOS / Windows 运行时用 Zed。</summary>
    public static string ZedThreadsDb(string dataHome) =>
        Path.Combine(dataHome, "zed", "threads", "threads.db");

    public static IEnumerable<string> ZedDbCandidates()
    {
        var over = Env("TOKENTRACKER_ZED_DB");
        if (over is not null)
        {
            yield return over;
            yield break;
        }
        var folder = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "Zed" : "zed";
        yield return Path.Combine(PlatformPaths.LocalAppData, folder, "threads", "threads.db");
    }

    public static IEnumerable<string> ZedProbeDirs()
    {
        foreach (var db in ZedDbCandidates())
        {
            var dir = Path.GetDirectoryName(db);
            if (!string.IsNullOrEmpty(dir)) yield return dir;
        }
    }

    public static IEnumerable<string> ClaudeHomes()
    {
        var over = Env("CLAUDE_CONFIG_DIR");
        if (over is not null) yield return over;
        var home = Path.Combine(Home, ".claude");
        if (over is null || !string.Equals(over, home, StringComparison.OrdinalIgnoreCase))
            yield return home;
    }

    public static string Expand(string path)
    {
        path = path.Trim();
        if (path == "~") return Home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Home, path[2..]);
        return path;
    }

    private static string? Env(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? null : Expand(raw);
    }

    private static IEnumerable<string> OneOr(string? overridePath, string fallback)
    {
        yield return overridePath ?? fallback;
    }
}
