using System.Text.Json;
using System.Windows;
using AgentHub.Core.ProxyCore;
using AgentHub.Web;
using Microsoft.Web.WebView2.Core;

namespace AgentHub.Shell;

/// <summary>常驻置顶的桌面额度窗。关闭只隐藏并记住，不退出应用。额度页走现有 /api/quotas。</summary>
public partial class DesktopQuotaWindow : Window
{
    private const int ExpandedMinimumWidth = 360;
    // 与前端 margin、Mac hit_test 同一套：设置板高 250，底边 12，上方按表盘尺寸收回 10%。
    private const double SettingsTuck = 0.10;
    private const double SettingsBody = 250;
    private const double SettingsBottom = 12;

    private readonly WebHostService _web;
    private readonly AgentHubConfig _config;
    private string _theme = "dark";
    private bool _coreReady;
    private bool _allowClose;
    private bool _restoring;
    private bool _persistQueued;
    private int _layoutGeneration;
    private int _layoutLock;

    /// <summary>用户点关闭或 Alt+F4：已写入 Visible=false。</summary>
    public event Action? Dismissed;

    public DesktopQuotaWindow(WebHostService web, AgentHubConfig config)
    {
        InitializeComponent();
        _web = web;
        _config = config;
        config.DesktopQuota.NormalizeDialAppearance();
        _theme = IsLight(config.App.Theme) ? "light" : "dark";
        ApplyChrome(_theme);
        RestorePlacement();
        InitializeChrome();
        Loaded += OnLoaded;
        LocationChanged += (_, _) => PersistSoon();
    }

    public void Present(bool activate)
    {
        if (!IsVisible) Show();
        if (activate)
        {
            Activate();
            Topmost = true;
        }
    }

    public void ApplyTheme(bool light)
    {
        var theme = light ? "light" : "dark";
        _theme = theme;
        ApplyChrome(theme);
        if (!_coreReady) return;
        try
        {
            Web.CoreWebView2.Profile.PreferredColorScheme = light
                ? CoreWebView2PreferredColorScheme.Light
                : CoreWebView2PreferredColorScheme.Dark;
            Web.CoreWebView2.PostWebMessageAsString("theme:" + theme);
        }
        catch (Exception ex)
        {
            HubLog.Write("[desktop-quota] 主题同步失败 " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>用户关闭：记住隐藏。进程退出走 <see cref="Shutdown"/>，不改可见性。</summary>
    public void HideFromUser()
    {
        _config.DesktopQuota.Visible = false;
        PersistNow();
        Hide();
        Dismissed?.Invoke();
    }

    public void Shutdown()
    {
        _allowClose = true;
        Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 句柄已经落在用户放下的那块屏上，只在这块屏里把露出的部分收回来。
        if (TryGetPlacement(out var anchor, out var work))
            HoldPhysicalPosition(anchor, work);
        try
        {
            await _web.Ready;
            var env = await ShellWebView.EnvironmentAsync();
            await Web.EnsureCoreWebView2Async(env);

            var settings = Web.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsNonClientRegionSupportEnabled = true;

            var themeJson = JsonSerializer.Serialize(_theme);
            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__AGENTHUB_HOST__='wpf';" +
                "window.__AGENTHUB_DESKTOP_QUOTA__=true;" +
                "window.__AGENTHUB_THEME__=" + themeJson + ";" +
                "(function(){try{var th=window.__AGENTHUB_THEME__||'dark';" +
                "document.documentElement.setAttribute('data-theme',th);" +
                "document.documentElement.style.setProperty('background','transparent','important');" +
                "var s=document.createElement('style');s.textContent='html,body,#app,.n-config-provider{background:transparent!important}';" +
                "(document.head||document.documentElement).appendChild(s);" +
                "}catch(e){}})();");

            Web.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                string msg;
                try { msg = args.TryGetWebMessageAsString(); }
                catch { return; }
                Dispatcher.BeginInvoke(() => HandleMessage(msg));
            };
            Web.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!ShellWebView.IsLocalShellUri(args.Uri)) args.Cancel = true;
            };
            Web.CoreWebView2.NewWindowRequested += (_, args) => { args.Handled = true; };
            Web.CoreWebView2.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    HubLog.Write("[desktop-quota] 页面加载失败 " + args.WebErrorStatus);
                    return;
                }
                try { await PushStateAsync(); }
                catch (Exception ex)
                {
                    HubLog.Write("[desktop-quota] 同步状态失败 " + ex.GetType().Name + ": " + ex.Message);
                }
            };

            _coreReady = true;
            ApplyTheme(IsLight(_theme));
            Web.Source = new Uri($"http://127.0.0.1:{WebHostService.Port}/app/#/desktop-quota");
        }
        catch (Exception ex)
        {
            HubLog.Write("[desktop-quota] 初始化失败 " + ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message);
        }
    }

    private void ApplyLayout(bool expanded, bool persist)
    {
        // 改尺寸前记住窗口所在显示器。WPF 改 Width/Height 时会把副屏窗口甩回主屏。
        var anchored = TryGetPlacement(out var anchor, out var work);
        var generation = ++_layoutGeneration;
        _layoutLock++;
        SetFixedSize(expanded);
        if (anchored) HoldPhysicalPosition(anchor, work);
        UpdateWindowHitRegion();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            try
            {
                if (generation != _layoutGeneration) return;
                if (anchored) HoldPhysicalPosition(anchor, work);
                UpdateWindowHitRegion();
            }
            finally
            {
                _layoutLock = Math.Max(0, _layoutLock - 1);
                if (generation == _layoutGeneration && persist)
                    PersistSoon();
            }
        });
    }

    private void SetFixedSize(bool expanded)
    {
        var size = _config.DesktopQuota.DialSize;
        var width = expanded ? Math.Max(ExpandedMinimumWidth, size) : size;
        var height = expanded ? size * (1 - SettingsTuck) + SettingsBody + SettingsBottom : size;
        MinWidth = 0;
        MinHeight = 0;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        Width = width;
        Height = height;
        MinWidth = MaxWidth = width;
        MinHeight = MaxHeight = height;
        _config.DesktopQuota.Expanded = expanded;
    }

    private void RestorePlacement()
    {
        _restoring = true;
        try
        {
            SetFixedSize(_config.DesktopQuota.Expanded);
            var area = SystemParameters.WorkArea;
            var dq = _config.DesktopQuota;
            if (IsFinite(dq.Left) && IsFinite(dq.Top))
            {
                Left = dq.Left!.Value;
                Top = dq.Top!.Value;
            }
            else
            {
                Left = area.Right - Width - 24;
                Top = area.Bottom - Height - 24;
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void PersistSoon()
    {
        if (_restoring || _layoutLock > 0 || !IsVisible || _persistQueued) return;
        _persistQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _persistQueued = false;
            if (_restoring) return;
            RememberPosition();
            PersistNow();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void PersistNow()
    {
        if (_restoring) return;
        _persistQueued = false;
        RememberPosition();
        try { _config.Save(); }
        catch (Exception) { /* 配置占用时下次再写 */ }
    }

    private void RememberPosition()
    {
        if (!IsVisible) return;
        _config.DesktopQuota.Left = Left;
        _config.DesktopQuota.Top = Top;
    }

    private void ApplyChrome(string theme)
    {
        var light = IsLight(theme);
        Background = System.Windows.Media.Brushes.Transparent;
        Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        try { Icon = AppIcon.CreateImageSource(light); }
        catch (Exception) { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideFromUser();
            return;
        }
        PersistNow();
        base.OnClosing(e);
    }

    private static bool IsLight(string? theme) =>
        string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinite(double? value) => value is double number && double.IsFinite(number);

}
