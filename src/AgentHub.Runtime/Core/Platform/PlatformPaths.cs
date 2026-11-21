namespace AgentHub.Core.Platform;

public static class PlatformPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>用户级配置根。Windows: %APPDATA%；macOS: ~/Library/Application Support；Linux: $XDG_CONFIG_HOME 或 ~/.config。</summary>
    public static string RoamingAppData
    {
        get
        {
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Home, "Library", "Application Support");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(xdg))
                return xdg;
            if (!OperatingSystem.IsWindows())
                return Path.Combine(Home, ".config");
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
    }

    /// <summary>用户级本地数据根。Windows: %LOCALAPPDATA%；macOS: ~/Library/Application Support；Linux: $XDG_DATA_HOME 或 ~/.local/share。</summary>
    public static string LocalAppData
    {
        get
        {
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Home, "Library", "Application Support");
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(xdg))
                return xdg;
            if (!OperatingSystem.IsWindows())
                return Path.Combine(Home, ".local", "share");
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }

    /// <summary>Gemini / Antigravity 数据根：遵循 GEMINI_HOME，否则回退至 ~/.gemini。</summary>
    public static string GeminiHome(string? userProfile = null)
    {
        var baseHome = string.IsNullOrWhiteSpace(userProfile) ? Home : userProfile;
        if (string.Equals(Path.GetFullPath(baseHome).TrimEnd('\\', '/'),
                Path.GetFullPath(Home).TrimEnd('\\', '/'),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            var env = Environment.GetEnvironmentVariable("GEMINI_HOME");
            if (!string.IsNullOrWhiteSpace(env))
                return Environment.ExpandEnvironmentVariables(env.Trim());
        }
        return Path.Combine(baseHome, ".gemini");
    }

    /// <summary>Antigravity 全局自定义配置目录（~/.gemini/config）。</summary>
    public static string AntigravityConfigDir(string? userProfile = null) =>
        Path.Combine(GeminiHome(userProfile), "config");
}
