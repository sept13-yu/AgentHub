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
