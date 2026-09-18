using System.IO;

namespace AgentHub.Core.ProxyCore;

/// <summary>本机诊断日志：%LOCALAPPDATA%\AgentHub.Local\agenthub.log。
/// Debug.WriteLine 在 Release 托盘下看不到；不写密钥。</summary>
internal static class HubLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static void Write(string message)
    {
        System.Diagnostics.Debug.WriteLine("[AgentHub] " + message);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AgentHubConfig.LocalDataDir);
                var path = Path.Combine(AgentHubConfig.LocalDataDir, "agenthub.log");
                RotateIfNeeded(path);
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // 日志失败不能挡业务
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length < MaxBytes) return;
        var bak = path + ".1";
        try { File.Delete(bak); } catch (IOException) { }
        try { File.Move(path, bak); } catch (IOException) { }
    }
}
