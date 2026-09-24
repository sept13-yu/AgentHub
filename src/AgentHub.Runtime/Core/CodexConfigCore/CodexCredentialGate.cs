using System.Text;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.CodexConfigCore;

/// <summary>
/// `AgentHub.exe codex-credential &lt;connection-id&gt;`：Codex [model_providers.OpenAI].auth.command 入口。
/// 必须在任何单实例/服务启动逻辑之前处理：主实例常驻时本进程是合法的第二个 AgentHub 进程。
/// 只向 stdout 输出明文 Key 后立即退出，不启动 WebView/HTTP/托盘；日志与错误信息禁止包含 Key。
/// </summary>
public static class CodexCredentialGate
{
    public const string Arg = "codex-credential";

    /// <summary>首参命中即认领，避免传参形态差异落入完整 GUI 启动；合法性在 Handle 内严校验。</summary>
    public static bool IsCredentialRequest(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Arg, StringComparison.Ordinal);

    /// <summary>处理凭据请求并结束进程（不返回）。缺参/多参/空 ID 一律非零退出，不进 GUI。</summary>
    public static void Handle(IReadOnlyList<string> args)
    {
        if (args.Count != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            WriteError("codex-credential: usage AgentHub.exe codex-credential <connection-id>");
            HubLog.Write("[codex-credential] invalid args count=" + args.Count);
            Environment.Exit(2);
        }

        try
        {
            var config = AgentHubConfig.Load();
            var service = new CodexConfigService(config);
            var key = service.GetCredentialPlain(args[1]);
            if (string.IsNullOrEmpty(key))
            {
                WriteError("codex-credential: connection not found or key not configured");
                HubLog.Write("[codex-credential] key missing for connection");
                Environment.Exit(2);
            }
            using var stdout = Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(key);
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            HubLog.Write("[codex-credential] ok");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            // 不透出异常细节：可能是配置内容，只报类型
            WriteError("codex-credential: failed");
            HubLog.Write("[codex-credential] failed " + ex.GetType().Name);
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
