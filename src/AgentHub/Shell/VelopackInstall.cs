using System.Diagnostics;
using System.IO;
using Velopack;
using Velopack.Sources;

namespace AgentHub.Shell;

/// <summary>定位 Velopack 安装并启动官方卸载（Update.exe uninstall）。</summary>
public static class VelopackInstall
{
    public static bool CanUninstall()
    {
        try
        {
            var mgr = CreateManager();
            var exe = mgr.Locator.UpdateExePath;
            return mgr.IsInstalled && !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryStartUninstall(out string? error)
    {
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled)
            {
                error = "当前不是安装版，无法卸载。";
                return false;
            }

            var exe = mgr.Locator.UpdateExePath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                error = "找不到卸载程序。";
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "uninstall",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static UpdateManager CreateManager() =>
        new(new GithubSource("https://github.com/sept13-yu/AgentHub", accessToken: null, prerelease: false));
}
