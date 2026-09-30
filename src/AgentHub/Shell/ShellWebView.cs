using System.IO;
using AgentHub.Web;
using Microsoft.Web.WebView2.Core;

namespace AgentHub.Shell;

/// <summary>主窗和桌面额度窗共用一个 WebView2 用户数据目录，避免同时初始化时互相锁住。</summary>
internal static class ShellWebView
{
    private static readonly object Gate = new();
    private static Task<CoreWebView2Environment>? _env;

    public static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        lock (Gate)
        {
            if (_env is { IsFaulted: false, IsCanceled: false }) return _env;
            var folder = Path.Combine(AgentHub.Core.ProxyCore.AgentHubConfig.LocalDataDir, "WebView2");
            _env = CoreWebView2Environment.CreateAsync(null, folder);
            return _env;
        }
    }

    /// <summary>壳内只允许本机 Kestrel（含 about:blank 首帧）。</summary>
    public static bool IsLocalShellUri(string? uriText)
    {
        if (string.IsNullOrEmpty(uriText)) return false;
        if (uriText.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp
            && uri.Host.Equals("127.0.0.1", StringComparison.Ordinal)
            && uri.Port == WebHostService.Port;
    }
}
