using System.Text;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.CodexConfigCore;

/// <summary>
/// `AgentHub.exe codex-credential &lt;connection-id&gt;`：Codex [model_providers.OpenAI].auth.command 入口。
/// 必须在任何单实例/服务启动逻辑之前处理（方案 §4.3）：主实例常驻时本进程是合法的第二个 AgentHub
/// 进程，走单实例互斥会误唤醒主窗甚至误杀实例。只向 stdout 输出明文 Key 后立即退出，不启动
/// WebView/HTTP/托盘；日志与错误信息禁止包含 Key。
/// </summary>
public static class CodexCredentialGate
{
    public const string Arg = "codex-credential";

    public static bool IsCredentialRequest(IReadOnlyList<string> args) =>
        args.Count == 2 && string.Equals(args[0], Arg, StringComparison.Ordinal);

    /// <summary>处理凭据请求并结束进程（不返回）。</summary>
    public static void Handle(IReadOnlyList<string> args)
    {
        try
        {
            var config = AgentHubConfig.Load();
            var service = new CodexConfigService(config);
            var key = service.GetCredentialPlain(args[1]);
            if (string.IsNullOrEmpty(key))
            {
                WriteError("codex-credential: connection not found or key not configured");
                Environment.Exit(2);
            }
            using var stdout = Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(key);
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            Environment.Exit(0);
        }
        catch (Exception)
        {
            // 不透出异常细节：可能是配置内容，只报类型
            WriteError("codex-credential: failed");
            Environment.Exit(1);
        }
    }

    private static void WriteError(string message)
    {
        try
        {
            using var stderr = Console.OpenStandardError();
            var bytes = Encoding.UTF8.GetBytes(message + Environment.NewLine);
            stderr.Write(bytes, 0, bytes.Length);
            stderr.Flush();
        }
        catch (Exception)
        {
            // 无标准错误句柄（非常规拉起方式）：忽略
        }
    }
}
