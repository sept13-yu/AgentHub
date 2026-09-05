using Microsoft.Win32;

namespace AgentHub.Shell;

/// <summary>开机自启（方案 §0：Run 键名 AgentHub）。
/// 阶段 0 只提供能力不接管——阶段 4「切换自启到 AgentHub」时才启用。</summary>
public static class AutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentHub";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, Environment.ProcessPath
            ?? System.Reflection.Assembly.GetExecutingAssembly().Location);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}