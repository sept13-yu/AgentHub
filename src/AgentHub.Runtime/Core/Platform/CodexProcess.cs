using System.Diagnostics;

namespace AgentHub.Core.Platform;

/// <summary>Codex 是否在跑：CLI 进程名 codex，或桌面包 ChatGPT.exe（路径含 OpenAI.Codex）。</summary>
public static class CodexProcess
{
    public static bool IsRunning()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    if (name.Equals("codex", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (!name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* 受保护进程读不到路径 */ }
                    if (path is not null && path.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // 枚举失败按未运行处理，不能挡状态接口
        }
        return false;
    }
}
