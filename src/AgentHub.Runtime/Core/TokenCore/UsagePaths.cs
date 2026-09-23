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
