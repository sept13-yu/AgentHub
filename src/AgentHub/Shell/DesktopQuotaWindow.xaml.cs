using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using AgentHub.Core.ProxyCore;
using AgentHub.Web;
using Microsoft.Web.WebView2.Core;

namespace AgentHub.Shell;

/// <summary>常驻置顶的桌面额度窗。关闭只隐藏并记住，不退出应用。额度页走现有 /api/quotas。</summary>
public partial class DesktopQuotaWindow : Window
{
    public const double CompactWidth = 320;
    public const double CompactHeight = 144;
    public const double ExpandedWidth = 360;
    public const double ExpandedHeight = 340;

    private readonly WebHostService _web;
    private readonly AgentHubConfig _config;
    private string _theme = "dark";
    private bool _coreReady;
    private bool _allowClose;
    private bool _restoring;
    private bool _persistQueued;

    /// <summary>用户点关闭或 Alt+F4：已写入 Visible=false。</summary>
    public event Action? Dismissed;

    public DesktopQuotaWindow(WebHostService web, AgentHubConfig config)
    {
        InitializeComponent();
        _web = web;
        _config = config;
        _theme = IsLight(config.App.Theme) ? "light" : "dark";
        ApplyChrome(_theme);
        RestorePlacement();
        SourceInitialized += (_, _) => ApplyWindowChrome();
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
                "document.documentElement.style.background=th==='light'?'#F5F6F8':'#18181C';" +
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

    private void HandleMessage(string msg)
    {
        if (msg == "dq:close")
        {
            HideFromUser();
            return;
        }
        if (msg == "dq:expanded")
        {
            ApplyLayout(expanded: true, persist: true);
            return;
        }
        if (msg == "dq:compact")
        {
            ApplyLayout(expanded: false, persist: true);
            return;
        }
        const string windowsPrefix = "dq:windows:";
        if (msg.StartsWith(windowsPrefix, StringComparison.Ordinal))
        {
            try
            {
                var ids = JsonSerializer.Deserialize<List<string>>(msg[windowsPrefix.Length..]);
                _config.DesktopQuota.WindowIds = DesktopQuotaSettings.NormalizeWindowIds(ids);
                PersistNow();
            }
            catch (JsonException) { }
        }
    }

    private async System.Threading.Tasks.Task PushStateAsync()
    {
        if (!_coreReady) return;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["expanded"] = _config.DesktopQuota.Expanded,
            ["windowIds"] = _config.DesktopQuota.WindowIds,
            ["theme"] = _theme,
        });
        await Web.CoreWebView2.ExecuteScriptAsync(
            "window.__AGENTHUB_DQ_STATE__=" + payload + ";" +
            "try{var s=window.__AGENTHUB_DQ_STATE__;" +
            "if(s&&s.theme){document.documentElement.setAttribute('data-theme',s.theme);" +
            "window.dispatchEvent(new CustomEvent('agenthub-theme',{detail:'theme:'+s.theme}));}" +
            "window.dispatchEvent(new Event('agenthub-dq-state'));}catch(e){}");
    }

    private void ApplyLayout(bool expanded, bool persist)
    {
        var width = expanded ? ExpandedWidth : CompactWidth;
        var height = expanded ? ExpandedHeight : CompactHeight;
        MinWidth = 0;
        MinHeight = 0;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        Width = width;
        Height = height;
        MinWidth = MaxWidth = width;
        MinHeight = MaxHeight = height;
        _config.DesktopQuota.Expanded = expanded;
        ClampToWorkArea();
        if (persist) PersistSoon();
    }

    private void RestorePlacement()
    {
        _restoring = true;
        try
        {
            ApplyLayout(_config.DesktopQuota.Expanded, persist: false);
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
            ClampToWorkArea();
        }
        finally
        {
            _restoring = false;
        }
    }

    private void ClampToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        var maxLeft = area.Right - Math.Min(Width, area.Width);
        if (maxLeft < area.Left) maxLeft = area.Left;
        Left = Math.Clamp(Left, area.Left, maxLeft);

        var maxTop = area.Bottom - Math.Min(32, Height);
        if (maxTop < area.Top) maxTop = area.Top;
        Top = Math.Clamp(Top, area.Top, maxTop);
    }

    private void PersistSoon()
    {
        if (_restoring || !IsVisible || _persistQueued) return;
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
        var bg = light
            ? System.Windows.Media.Color.FromRgb(0xF5, 0xF6, 0xF8)
            : System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x1C);
        Background = new System.Windows.Media.SolidColorBrush(bg);
        Web.DefaultBackgroundColor = light
            ? System.Drawing.Color.FromArgb(255, 0xF5, 0xF6, 0xF8)
            : System.Drawing.Color.FromArgb(255, 0x18, 0x18, 0x1C);
        try { Icon = AppIcon.CreateImageSource(light); }
        catch (Exception) { }
    }

    private void ApplyWindowChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex.ToInt64() | WS_EX_TOOLWINDOW));
        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
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

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8) return SetWindowLongPtr64(hwnd, index, value);
        return new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
    }
}
