using AgentHub.Core.CodexConfigCore;
using AgentHub.Core.DocCore;
using AgentHub.Core.McpCore;
using AgentHub.Core.Platform;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.SessionCore;
using AgentHub.Core.TokenCore;
using AgentHub.Web;

namespace AgentHub.Hosting;

public sealed class RuntimeHostOptions
{
    public IAutostartService Autostart { get; set; } = UnsupportedAutostartService.Instance;
    public IAppUpdateService AppUpdate { get; set; } = new ManualAppUpdateService(RuntimeVersion.Current);
    public Func<string, string?>? PickFolder { get; set; }
    /// <summary>为空则按平台默认（Windows DPAPI / 其他 Unsupported）。</summary>
    public ISecretProtector? SecretProtector { get; set; }
    public Action<string>? Log { get; set; }
}

/// <summary>共享组合根：Core/Web 服务只装配一次，WPF 与 Backend 共用。</summary>
public sealed class AgentHubRuntime : IDisposable
{
    private readonly Action<string> _log;

    private AgentHubRuntime(RuntimeHostOptions options)
    {
        _log = options.Log ?? HubLog.Write;
        if (options.SecretProtector is not null)
            Secrets.Use(options.SecretProtector);

        if (OperatingSystem.IsWindows())
            AgentHubConfig.RelocateLocalDataFromInstallDir();

        Config = AgentHubConfig.Load();
        PriceSyncService.TryLoadCache();
        PriceSyncService.OnBaselineChanged = () => DashboardRefreshRequested?.Invoke();

        Titles = new TitleOverrideStore();
        Sessions = new SessionService(Titles, Config, _log);
        Skills = new SkillManager(log: _log);
        Skills.RecoverStaging();
        Docs = new DocService(Config, Skills);
        AgentRules = new AgentRuleBootstrapService(Config, log: _log);
        AgentRules.RecoverPendingTransaction();
        Tokens = new TokenService(Config, _log);
        Quotas = new QuotaService(Config);
        Scan = new ScanScheduler(Tokens, Sessions, Config, _log, OnUsageScanCompleted);
        CodexConfig = new CodexConfigService(Config);
        Mcp = new McpSyncService(log: _log);
        CodexConfig.EnsureSeeded();

        Web = new WebHostService(Sessions, Docs, Tokens, Quotas, Config, AgentRules, CodexConfig, Mcp)
        {
            UsageScan = () => Scan.RunAsync(),
            PickFolder = options.PickFolder,
            Autostart = options.Autostart,
            AppUpdate = options.AppUpdate,
        };
        Web.SettingsSaved += () =>
        {
            Scan.Reconfigure();
            SettingsSaved?.Invoke();
        };
        DashboardRefreshRequested += () => Web.Events.Publish("dashboard-refresh");
        SettingsSaved += () => Web.Events.Publish("settings-saved", new { theme = Config.App.Theme });
    }

    public static AgentHubRuntime Create(RuntimeHostOptions options) => new(options);

    public AgentHubConfig Config { get; }
    public TitleOverrideStore Titles { get; }
    public SessionService Sessions { get; }
    public SkillManager Skills { get; }
    public DocService Docs { get; }
    public AgentRuleBootstrapService AgentRules { get; }
    public TokenService Tokens { get; }
    public QuotaService Quotas { get; }
    public ScanScheduler Scan { get; }
    public CodexConfigService CodexConfig { get; }
    public McpSyncService Mcp { get; }
    public WebHostService Web { get; }

    /// <summary>价格基线变化或用量扫描完成后触发；壳层用来刷新页面。可能来自任意线程。</summary>
    public event Action? DashboardRefreshRequested;
    /// <summary>用量扫描完成（一次扫描一次）。可能来自任意线程。</summary>
    public event Action? UsageScanCompleted;
    /// <summary>设置保存后触发（Runtime 已自行 Scan.Reconfigure()）。可能来自任意线程。</summary>
    public event Action? SettingsSaved;

    public Task Ready => Web.Ready;

    private void OnUsageScanCompleted()
    {
        UsageScanCompleted?.Invoke();
        DashboardRefreshRequested?.Invoke();
    }

    public void Start()
    {
        Web.Start();
        Scan.Reconfigure();
        PriceSyncService.RefreshInBackground();
    }

    public void RunInitialScanInBackground()
    {
        System.Threading.Tasks.Task.Run(async () =>
        {
            try { await Scan.RunAsync(); }
            catch (Exception ex)
            {
                _log("[tokencore] 首扫失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        });
    }

    public Task SyncNowAsync() => Task.Run(async () =>
    {
        try { await Scan.RunAsync(); }
        catch (Exception ex)
        {
            _log("[tokencore] 同步失败: " + ex.GetType().Name + ": " + ex.Message);
        }
    });

    public void Stop()
    {
        try { Scan.Dispose(); } catch { }
        try { Web.Stop(); } catch { }
    }

    public void Dispose() => Stop();
}
