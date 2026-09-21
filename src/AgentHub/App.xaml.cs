using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Windows;
using AgentHub.Core.CodexConfigCore;
using AgentHub.Core.Platform;
using AgentHub.Core.ProxyCore;
using AgentHub.Hosting;
using AgentHub.Shell;
using Microsoft.Web.WebView2.Core;
// UseWindowsForms 会引入 System.Windows.Forms 全局 using，用别名消解与 WPF 的同名冲突
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AgentHub;

/// <summary>应用入口：单实例 → Runtime 组合根 → 本地 Web 服务 → 主窗（WebView2）→ 托盘。</summary>
public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private TrayIconService? _tray;
    private AgentHubRuntime? _rt;
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

        _rt = AgentHubRuntime.Create(new RuntimeHostOptions
        {
            Autostart = new RegistryAutostartService(),
            AppUpdate = new VelopackAppUpdateService(),
            PickFolder = initial => Dispatcher.Invoke(() =>
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "选择 Agent 文档资料目录",
                    UseDescriptionForTitle = true,
                    SelectedPath = Directory.Exists(initial) ? initial : "",
                    ShowNewFolderButton = true,
                };
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }),
        });
        _rt.Start();

        var win = new MainWindow(_rt.Web, _rt.Config);
        MainWindow = win;
        win.Show();
        _tray = new TrayIconService(BuildTrayActions(), IsLightTheme(_rt.Config.App.Theme));
        _rt.RunInitialScanInBackground();
    }

    private TrayShellActions BuildTrayActions() => new()
    {
        ShowMain = ShowMainWindow,
        Exit = ExitApp,
        SyncNow = SyncNow,
    };

    /// <summary>托盘「立即同步」：后台跑 ScanAll（本地扫描是同步 IO，不能占 UI 线程），扫完由 SSE 刷新页面。</summary>
    private void SyncNow()
    {
        if (_rt is null) return;
        _ = System.Threading.Tasks.Task.Run(() => _rt.SyncNowAsync());
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
        try { _rt?.Stop(); } catch { }
        try { _tray?.Dispose(); } catch { }
        Shutdown();
    }

    private static void Log(string message) => HubLog.Write(message);

    protected override void OnExit(ExitEventArgs e)
    {
        _guard?.Dispose();
        _guard = null;
        base.OnExit(e);
    }
}
