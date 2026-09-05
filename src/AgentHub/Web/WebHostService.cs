using System.IO;
using System.Net;
using System.Security.Cryptography;
using AgentHub.Core.CodexConfigCore;
using AgentHub.Core.DocCore;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.SessionCore;
using AgentHub.Core.TokenCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentHub.Web;

/// <summary>同进程本地 Web：静态文件 + REST，只绑 127.0.0.1:18780。
/// 写 API 鉴权：壳启动生成随机 token，经 WebView2 初始化脚本注入；
/// 浏览器直连拿不到 token → 只读。同时校验 Host 防 DNS rebinding。</summary>
public sealed class WebHostService
{
    public const int Port = 18780;

    private readonly SessionService? _sessions;
    private readonly DocService? _docs;
    private readonly TokenService? _tokens;
    private readonly QuotaService? _quotas;
    private readonly AgentHubConfig? _config;
    private readonly AgentRuleBootstrapService? _agentRules;
    private readonly CodexConfigService? _codexConfig;
    private WebApplication? _app;
    private Task? _runTask;
    private readonly TaskCompletionSource _ready = new();

    /// <summary>写 API 鉴权 token（每次启动随机；仅注入壳内 WebView2）。
    /// 测试/联调可用环境变量 AGENTHUB_WRITE_TOKEN 固定值。</summary>
    public string WriteToken { get; } =
        Environment.GetEnvironmentVariable("AGENTHUB_WRITE_TOKEN")
        ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    /// <summary>服务完成监听后完成；启动失败（如端口被占）则携带异常。</summary>
    public Task Ready => _ready.Task;

    public event Action? SettingsSaved;

    /// <summary>桌面宠物窗是否正在显示。由 App 在 PetHost 就绪后挂上。</summary>
    public Func<bool>? PetIsRunning { get; set; }

    /// <summary>用量扫描管线（ScanAll + 索引刷新 + 宠物/页面刷新）。由 App 挂上。</summary>
    public Func<Task<ScanAllResult>>? UsageScan { get; set; }
    public Func<string, string?>? PickFolder { get; set; }

    public WebHostService(SessionService? sessions = null, DocService? docs = null,
        TokenService? tokens = null, QuotaService? quotas = null,
        AgentHubConfig? config = null, AgentRuleBootstrapService? agentRules = null,
        CodexConfigService? codexConfig = null)
    {
        _sessions = sessions;
        _docs = docs;
        _tokens = tokens;
        _quotas = quotas;
        _config = config;
        _agentRules = agentRules;
        _codexConfig = codexConfig;
    }

    /// <summary>写接口鉴权：Host 须为 127.0.0.1，防止 DNS rebinding；并校验壳内 token。</summary>
    internal bool WriteAuth(HttpContext ctx)
    {
        if (!ctx.Request.Host.Host.Equals("127.0.0.1", StringComparison.Ordinal))
            return false;
        if (!ctx.Request.Headers.TryGetValue("X-AgentHub-Token", out var v))
            return false;
        return string.Equals(v.ToString(), WriteToken, StringComparison.Ordinal);
    }

    public void Start()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.Logging.ClearProviders();   // 托盘应用无控制台
        builder.Services.Configure<JsonOptions>(o =>
            o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, Port);
            o.AddServerHeader = false;
        });

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path is "/" or "/index.html" or "/app")
            {
                ctx.Response.Redirect("/app/");
                return;
            }
            await next();
        });

        // 开发期禁缓存：前端更新后 WebView2 刷新即见，不落磁盘缓存
        var staticFiles = new StaticFileOptions
        {
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        };
        app.UseDefaultFiles();
        app.UseStaticFiles(staticFiles);
        app.MapGet("/health", () => Results.Json(
            new { status = "ok", service = "AgentHub", port = Port }));

        if (_sessions is not null)
            app.MapSessionEndpoints(_sessions, WriteAuth);
        if (_docs is not null && _sessions is not null)
            app.MapDocEndpoints(_docs, _sessions, WriteAuth);
        if (_agentRules is not null)
            app.MapAgentRuleEndpoints(_agentRules, WriteAuth);
        if (_codexConfig is not null)
            app.MapCodexConfigEndpoints(_codexConfig, WriteAuth);
        if (_tokens is not null && _quotas is not null && _config is not null)
            app.MapUsageEndpoints(_tokens, _quotas, _config, WriteAuth,
                () => SettingsSaved?.Invoke(), () => PetIsRunning?.Invoke() ?? false,
                () => UsageScan is not null ? UsageScan() : Task.FromResult(_tokens.ScanAll()),
                PickFolder);

        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() => _ready.TrySetResult());

        _app = app;
        _runTask = app.RunAsync();
        _ = _runTask.ContinueWith(
            t => _ready.TrySetException(
                t.Exception?.GetBaseException() ?? new InvalidOperationException("Web 服务异常退出")),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Stop()
    {
        try
        {
            _app?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _runTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception) { /* 退出路径尽力而为，不阻塞关机 */ }
    }
}
