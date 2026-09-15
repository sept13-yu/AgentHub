using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using AgentHub.Core.ProxyCore;
using AgentHub.Shell;
using AgentHub.Web;
using Microsoft.Web.WebView2.Core;
// UseWindowsForms 会引入 System.Windows.Forms 全局 using，用别名消解与 WPF 的同名冲突
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AgentHub;

/// <summary>主窗：WebView2 容器，加载 /app/。
/// 启动时注入写 API token：壳内可写，浏览器直连只读。</summary>
public partial class MainWindow : Window
{
    private readonly WebHostService _web;
    private readonly AgentHubConfig _config;

    /// <summary>当前壳层主题（dark | light）。首帧取 AppSettings.Theme，页面切换经 theme: 消息写回。</summary>
    private string _theme = "dark";

    /// <summary>关闭按钮默认隐藏到托盘；托盘「退出」置 true 才真正关窗。</summary>
    public bool ReallyExit { get; set; }

    private bool _pageReady;

    public MainWindow(WebHostService web, AgentHubConfig config)
    {
        InitializeComponent();
        _web = web;
        _config = config;

        // 首帧主题来自配置而非 localStorage（UI_RULES §7.2）：窗体底、启动层、WebView 底在显示前就位
        _theme = NormalizeTheme(_config.App.Theme);
        ApplyShellTheme(_theme);
        RestoreWindowBounds();
        SourceInitialized += (_, _) => ApplyTitleBar(dark: !IsLight(_theme));
        Loaded += OnLoaded;
        SizeChanged += (_, _) => PersistWindowBounds();
        LocationChanged += (_, _) => PersistWindowBounds();
        StateChanged += (_, _) => PersistWindowBounds();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _web.Ready;   // 先等服务监听就绪，避免 WebView2 首帧吃到连接拒绝

            var dataFolder = Path.Combine(AgentHubConfig.LocalDataDir, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await Web.EnsureCoreWebView2Async(env);

            // 桌面壳不是浏览器：关掉右键网页菜单（后退/刷新/检查等）和底栏链接预览
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // 写 token 注入 + fetch 自动带头：壳内 = 可写；浏览器 = 只读。
            // 主题首帧以配置为准：写 data-theme 和 localStorage，再回报壳层（UI_RULES §7.2）
            var tokenJson = JsonSerializer.Serialize(_web.WriteToken);
            var themeJson = JsonSerializer.Serialize(_theme);
            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__AGENTHUB_SHELL__=true;" +
                "window.__AGENTHUB_TOKEN__=" + tokenJson + ";" +
                "window.__AGENTHUB_THEME__=" + themeJson + ";" +
                "(function(){var t=window.__AGENTHUB_TOKEN__;" +
                "if(!t||!window.fetch)return;" +
                "var of=window.fetch;" +
                "window.fetch=function(input,init){init=init||{};" +
                "init.headers=new Headers(init.headers||{});" +
                "init.headers.set('X-AgentHub-Token',t);" +
                "return of.call(window,input,init);};})();" +
                "(function(){try{var th=window.__AGENTHUB_THEME__||'dark';" +
                "document.documentElement.setAttribute('data-theme',th);" +
                "localStorage.setItem('agenthub-theme',th);" +
                "if(window.chrome&&chrome.webview)chrome.webview.postMessage('theme:'+th);}catch(e){}})();");

            Web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                string msg;
                try { msg = e.TryGetWebMessageAsString(); }
                catch { return; }
                if (msg.StartsWith("theme:", StringComparison.Ordinal))
                {
                    var th = NormalizeTheme(msg["theme:".Length..]);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (th == _theme) return;
                        _theme = th;
                        _config.App.Theme = th;
                        _config.Save();
                        ApplyShellTheme(th);
                    });
                }
            };

            Web.NavigationCompleted += OnFirstNavigationCompleted;
            Web.Source = new Uri(_web.BaseUri, "app/");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "初始化失败：" + ex.GetBaseException().Message
                + "\n\n若提示端口被占用，请先在托盘退出已运行的 AgentHub 再打开。",
                "AgentHub", MessageBoxButton.OK, MessageBoxImage.Error);
            ReallyExit = true;
            (Application.Current as App)?.RequestExit();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        PersistWindowBounds();
        if (!ReallyExit)
        {
            e.Cancel = true;
            Hide();
            (Application.Current as App)?.NotifyHiddenToTray();
        }
        base.OnClosing(e);
    }

    private void OnFirstNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Web.NavigationCompleted -= OnFirstNavigationCompleted;
        Dispatcher.BeginInvoke(RevealWebView);
    }

    /// <summary>页面首航完成后再显示 WebView2，并揭掉壳层 loading。</summary>
    private void RevealWebView()
    {
        if (_pageReady) return;
        _pageReady = true;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        (Application.Current as App)?.FlushPendingDashboardRefresh();
    }

    /// <summary>CoreWebView2 已就绪则派发 agenthub-refresh（主窗隐藏也可以）。</summary>
    public bool TryDispatchRefresh()
    {
        if (Web.CoreWebView2 is null) return false;
        try
        {
            _ = Web.CoreWebView2.ExecuteScriptAsync(
                "window.dispatchEvent(new CustomEvent('agenthub-refresh'));");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLight(string? theme) =>
        string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTheme(string? theme) => IsLight(theme) ? "light" : "dark";

    /// <summary>壳层五面同值：窗体底、启动层、WebView 默认底、标题栏（UI_RULES §8）。</summary>
    private void ApplyShellTheme(string theme)
    {
        var light = IsLight(theme);
        Web.DefaultBackgroundColor = light
            ? System.Drawing.Color.FromArgb(255, 0xEF, 0xF4, 0xF1)
            : System.Drawing.Color.FromArgb(255, 0x18, 0x18, 0x1C);
        var bg = light
            ? System.Windows.Media.Color.FromRgb(0xEF, 0xF4, 0xF1)
            : System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x1C);
        var brush = new System.Windows.Media.SolidColorBrush(bg);
        Background = brush;
        LoadingOverlay.Background = brush;
        LoadingTitle.Foreground = new System.Windows.Media.SolidColorBrush(
            light ? System.Windows.Media.Colors.Black : System.Windows.Media.Colors.White);
        LoadingSub.Foreground = new System.Windows.Media.SolidColorBrush(light
            ? System.Windows.Media.Color.FromRgb(0x5A, 0x63, 0x5C)
            : System.Windows.Media.Color.FromRgb(0xA8, 0xAE, 0xA9));
        LoadingBar.Background = new System.Windows.Media.SolidColorBrush(light
            ? System.Windows.Media.Color.FromRgb(0xE4, 0xEA, 0xE6)
            : System.Windows.Media.Color.FromRgb(0x10, 0x10, 0x14));
        LoadingBar.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x2E, 0xC4, 0xB6));
        Icon = AppIcon.CreateImageSource(light);
        (Application.Current as App)?.ApplyTrayTheme(light);
        ApplyTitleBar(dark: !light);
    }


    private bool _restoringBounds;
    private bool _persistQueued;

    /// <summary>从 AppSettings 恢复窗口位置/尺寸/最大化，并钳到当前工作区。</summary>
    private void RestoreWindowBounds()
    {
        var app = _config.App;
        var wa = SystemParameters.WorkArea;
        _restoringBounds = true;
        try
        {
            var w = app.WindowWidth is > 0 ? app.WindowWidth.Value : Width;
            var h = app.WindowHeight is > 0 ? app.WindowHeight.Value : Height;
            w = Math.Clamp(w, MinWidth, Math.Max(MinWidth, wa.Width));
            h = Math.Clamp(h, MinHeight, Math.Max(MinHeight, wa.Height));
            Width = w;
            Height = h;

            if (app.WindowLeft is double left && app.WindowTop is double top)
            {
                // 至少露出一截标题栏，避免完全跑出屏幕
                var minLeft = wa.Left - Math.Max(0, w - 80);
                var maxLeft = Math.Max(minLeft, wa.Right - Math.Min(w, 80));
                var minTop = wa.Top - 8;
                var maxTop = Math.Max(minTop, wa.Bottom - Math.Min(h, 40));
                Left = Math.Clamp(left, minLeft, maxLeft);
                Top = Math.Clamp(top, minTop, maxTop);
                WindowStartupLocation = WindowStartupLocation.Manual;
            }

            if (app.WindowMaximized)
                WindowState = WindowState.Maximized;
        }
        finally
        {
            _restoringBounds = false;
        }
    }

    /// <summary>把当前窗口几何写入 AppSettings（最小化时跳过；最大化时存 RestoreBounds）。</summary>
    private void PersistWindowBounds()
    {
        if (_restoringBounds || WindowState == WindowState.Minimized) return;
        if (_persistQueued) return;
        _persistQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _persistQueued = false;
            if (_restoringBounds || WindowState == WindowState.Minimized) return;
            var app = _config.App;
            var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            app.WindowWidth = bounds.Width;
            app.WindowHeight = bounds.Height;
            app.WindowLeft = bounds.Left;
            app.WindowTop = bounds.Top;
            app.WindowMaximized = WindowState == WindowState.Maximized;
            try { _config.Save(); }
            catch (Exception) { /* 配置占用时下次再写 */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ApplyTitleBar(bool dark)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int v = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
