using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Windows;
using AgentHub.Core.CodexConfigCore;
using AgentHub.Core.DocCore;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.SessionCore;
using AgentHub.Core.TokenCore;
using AgentHub.Shell;
using AgentHub.Web;
using Microsoft.Web.WebView2.Core;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;
// UseWindowsForms 会引入 System.Windows.Forms 全局 using，用别名消解与 WPF 的同名冲突
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AgentHub;

/// <summary>应用入口：单实例 → 配置/各 Core → 本地 Web 服务 → 主窗（WebView2）→ 托盘。</summary>
public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private WebHostService? _web;
    private TrayIconService? _tray;
    private PetHost? _pet;
    private AgentHubConfig? _config;
    private CodexConfigService? _codexConfig;
    private TitleOverrideStore? _titles;
    private SessionService? _sessions;
    private DocService? _docs;
    private SkillManager? _skills;
    private AgentRuleBootstrapService? _agentRules;
    private TokenService? _tokens;
    private QuotaService? _quotas;
    private ScanScheduler? _scan;
    private bool _refreshPending;
    private int _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        // codex-credential 认证子命令必须抢在单实例守卫之前（方案 §4.3）：
        // 主实例常驻时 TryAcquire 会唤醒主窗甚至误杀实例，而 Codex auth.command 需要随时拉起本进程。
        if (CodexCredentialGate.IsCredentialRequest(e.Args))
        {
            CodexCredentialGate.Handle(e.Args);
            return;
        }

        try
        {
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.EnableVisualStyles();
        }
        catch (InvalidOperationException) { }

        base.OnStartup(e);

        if (!HasWebView2())
        {
            MessageBox.Show(
                "AgentHub 需要 Microsoft Edge WebView2 Runtime。安装程序将打开官方下载页，安装完成后请重新运行本安装包。",
                "AgentHub", MessageBoxButton.OK, MessageBoxImage.Error);
            try
            {
                Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception) { }
            Shutdown();
            return;
        }

        // 未处理异常兜底：托盘常驻应用静默崩溃比弹窗更糟
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show("未处理异常：" + ex.Exception.Message, "AgentHub",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        _guard = new SingleInstanceGuard();
        if (!_guard.TryAcquire())
        {
            Shutdown();
            return;
        }
        _guard.Activated += () => Dispatcher.Invoke(ShowMainWindow);

        _config = AgentHubConfig.Load();
        PriceSyncService.TryLoadCache();
        PriceSyncService.OnBaselineChanged = () => Dispatcher.BeginInvoke(RequestDashboardRefresh);
        _titles = new TitleOverrideStore();
        _sessions = new SessionService(_titles, _config, Log);
        _skills = new SkillManager(log: Log);
        _skills.RecoverStaging();
        _docs = new DocService(_config, _skills);
        _agentRules = new AgentRuleBootstrapService(_config, log: Log);
        _agentRules.RecoverPendingTransaction();
        _tokens = new TokenService(_config, Log);
        _quotas = new QuotaService(_config);
        _scan = new ScanScheduler(_tokens, _sessions, _config, Log, OnUsageScanCompleted);
        _codexConfig = new CodexConfigService(_config);
        _codexConfig.EnsureSeeded();

        _web = new WebHostService(_sessions, _docs, _tokens, _quotas, _config, _agentRules, _codexConfig);
        _web.UsageScan = () => _scan.RunAsync();
        _web.PickFolder = initial => Dispatcher.Invoke(() =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 Agent 文档资料目录",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(initial) ? initial : "",
                ShowNewFolderButton = true,
            };
            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
        });
        _web.Start();
        _web.SettingsSaved += () => Dispatcher.BeginInvoke(() =>
        {
            _pet?.Apply();
            _scan?.Reconfigure();
        });

        var win = new MainWindow(_web, _config);
        MainWindow = win;
        win.Show();

        _pet = new PetHost(_web, _tokens, _config, ShowMainWindow, SyncNow, Dispatcher);
        _web.PetIsRunning = () => _pet?.IsRunning == true;
        _tray = new TrayIconService(BuildTrayActions(), IsLightTheme(_config.App.Theme));
        _ = _web.Ready.ContinueWith(_ => Dispatcher.BeginInvoke(() => _pet?.Apply()),
            TaskContinuationOptions.OnlyOnRanToCompletion);

        _scan.Reconfigure();
        PriceSyncService.RefreshInBackground();
        System.Threading.Tasks.Task.Run(async () =>
        {
            try { await _scan.RunAsync(); }
            catch (Exception ex)
            {
                Log("[tokencore] 首扫失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        });
        System.Threading.Tasks.Task.Run(() => CheckForUpdatesAsync(promptIfNone: false));
    }

    private TrayShellActions BuildTrayActions() => new()
    {
        ShowMain = ShowMainWindow,
        Exit = ExitApp,
        SyncNow = SyncNow,
        CheckUpdates = () => System.Threading.Tasks.Task.Run(() => CheckForUpdatesAsync(promptIfNone: true)),
        DownloadAndRestart = () => System.Threading.Tasks.Task.Run(DownloadAndRestartAsync),
    };

    /// <summary>托盘 / 宠物「立即同步」：走同一把 ScanAll，扫完推宠物并派发页面刷新。</summary>
    private void SyncNow()
    {
        if (_scan is null) return;
        System.Threading.Tasks.Task.Run(async () =>
        {
            try { await _scan.RunAsync(); }
            catch (Exception ex)
            {
                Log("[tokencore] 同步失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        });
    }

    private void OnUsageScanCompleted()
    {
        // 额度作废只在手动刷新接口入口做（随后前端就带 force 拉取）；
        // 这里若再作废，会把刚拉到的结果和启动盘缓存一起抹掉，引发二次全量拉取。
        Dispatcher.BeginInvoke(() =>
        {
            _pet?.PushStats();
            RequestDashboardRefresh();
        });
    }

    private void RequestDashboardRefresh()
    {
        var hidden = MainWindow is not { IsVisible: true };
        var ok = MainWindow is MainWindow win && win.TryDispatchRefresh();
        if (hidden || !ok) _refreshPending = true;
    }

    internal void FlushPendingDashboardRefresh()
    {
        if (!_refreshPending) return;
        if (MainWindow is MainWindow win && win.TryDispatchRefresh() && win.IsVisible)
            _refreshPending = false;
    }

    internal void NotifyHiddenToTray() => _tray?.NotifyHiddenToTray();

    internal void ApplyTrayTheme(bool light) => _tray?.ApplyTheme(light);

    private static bool IsLightTheme(string? theme) =>
        string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);

    private static bool HasWebView2()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static UpdateManager CreateUpdateManager() =>
        new(new GithubSource("https://github.com/sept13-yu/AgentHub", accessToken: null, prerelease: false));

    private async Task CheckForUpdatesAsync(bool promptIfNone)
    {
        try
        {
            var upd = await CreateUpdateManager().CheckForUpdatesAsync().ConfigureAwait(false);
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (upd is null)
                {
                    if (promptIfNone) _tray?.Notify("已是最新版本。");
                    return;
                }
                _tray?.Notify($"发现新版本 {upd.TargetFullRelease.Version}。右键选「下载并重启更新」。");
            });
        }
        catch (NotInstalledException)
        {
            if (promptIfNone)
                _ = Dispatcher.BeginInvoke(() => _tray?.Notify("当前不是 Velopack 安装，无法在线更新。"));
        }
        catch (Exception)
        {
            if (promptIfNone)
                _ = Dispatcher.BeginInvoke(() => _tray?.Notify("检查更新失败。"));
        }
    }

    private async Task DownloadAndRestartAsync()
    {
        try
        {
            var mgr = CreateUpdateManager();
            var upd = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (upd is null)
            {
                _ = Dispatcher.BeginInvoke(() => _tray?.Notify("已是最新版本。"));
                return;
            }
            await mgr.DownloadUpdatesAsync(upd).ConfigureAwait(false);
            mgr.ApplyUpdatesAndRestart(upd);
        }
        catch (NotInstalledException)
        {
            _ = Dispatcher.BeginInvoke(() => _tray?.Notify("当前不是 Velopack 安装，无法在线更新。"));
        }
        catch (Exception ex)
        {
            _ = Dispatcher.BeginInvoke(() => _tray?.Notify("更新失败：" + ex.Message));
        }
    }

    /// <summary>WebView 初始化失败时真正退出，避免空窗藏进托盘后互斥锁把二次启动静默吃掉。</summary>
    internal void RequestExit() => ExitApp();

    private void ShowMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
        if (_refreshPending)
        {
            _refreshPending = false;
            if (MainWindow is MainWindow win)
                win.TryDispatchRefresh();
        }
    }

    /// <summary>托盘「退出」：停止本地服务；若退出路径卡住，约 8 秒后强制结束。</summary>
    private void ExitApp()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0) return;

        // 先挂硬退出，再拆服务，避免 Close / Kestrel 堵住 UI 线程。
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            Thread.Sleep(8000);
            Environment.Exit(0);
        });

        if (MainWindow is MainWindow win) win.ReallyExit = true;
        try { MainWindow?.Close(); } catch { }
        try { _scan?.Dispose(); } catch { }
        try { _pet?.Shutdown(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _web?.Stop(); } catch { }
        Shutdown();
    }

    private static void Log(string message) =>
        System.Diagnostics.Debug.WriteLine("[AgentHub] " + message);

    protected override void OnExit(ExitEventArgs e)
    {
        _guard?.Dispose();
        _guard = null;
        base.OnExit(e);
    }
}
