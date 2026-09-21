using System.Diagnostics;

namespace AgentHub.Core.Platform;

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
            Process.Start(new ProcessStartInfo("xdg-open",
                [Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath])
            { UseShellExecute = false });
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
