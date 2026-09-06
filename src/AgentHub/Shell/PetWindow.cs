using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.TokenCore;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentHub.Shell;

/// <summary>
/// 桌面 clawd 宿主。移植自 TokenTracker <c>PetWindow.cs</c>（MIT）：
/// WPF 分层透明窗 + windowless WebView2，只有精灵不透明像素可见。
/// </summary>
internal sealed class PetWindow : Window
{
    private readonly WebView2CompositionControl _webView = new() { AllowExternalDrop = false };
    private readonly Uri _petUri;
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer;
    private readonly System.Windows.Threading.DispatcherTimer _hoverTimer;
    private readonly System.Windows.Threading.DispatcherTimer _clickThroughTimer;
    private bool _lastHover;
    private bool _clickThrough;
    private bool _typing;
    private long _lastKeyTick;
    private long _typingStreakStart;
    private long _rageUntil;
    private bool _rage;
    private bool _coreReady;
    private bool _exiting;
    private nint _hwnd;
    private POINT _lastMousePos;
    private long _lastMouseActiveTime;
    private bool _mouseIdle;
    private bool _isDragging;
    private double _lastDragLeft;
    private string _size = "medium";
    private string _tokenUnit = "zh";
    private double _bubbleBand = 56;
    private PetSnapshot _stats = new(0, 0, 0, 0, Array.Empty<PetTopModel>());

    private const long TypingLingerMs = 400;
    private const long RageStreakGapMs = 1500;
    private const long RageTriggerMs = 30_000;
    private const long RageShowMs = 5_000;

    public event Action? ContextMenuRequested;
    public event Action? OpenRequested;

    public PetWindow(Uri petUri, string size = "medium")
    {
        _petUri = petUri;
        ApplySizeDims(size);

        Title = "AgentHub Pet";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;

        RestorePlacement();
        Content = _webView;

        _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePlacement(); };
        LocationChanged += (_, _) =>
        {
            UpdateDragDirectionFromWindowMove();
            _saveTimer.Stop();
            _saveTimer.Start();
        };

        _hoverTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverTimer.Tick += (_, _) => { HoverTick(); TypingTick(); };

        _clickThroughTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _clickThroughTimer.Tick += (_, _) => ClickThroughTick();

        Loaded += async (_, _) =>
        {
            try { await InitializeWebViewAsync(); }
            catch (Exception) { /* 已在 Initialize 内兜底；避免 async void 冒泡成未处理异常 */ }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _clickThrough = (GetWindowExStyle(_hwnd).ToInt64() & WS_EX_TRANSPARENT) != 0;
        ClickThroughTick();
    }

    public void ShowPet()
    {
        _lastMouseActiveTime = Environment.TickCount64;
        if (!IsVisible) Show();
        Topmost = true;
        _hoverTimer.Start();
        _clickThroughTimer.Start();
        ClickThroughTick();
    }

    public void HidePet()
    {
        _isDragging = false;
        PushDragState(null);
        _clickThroughTimer.Stop();
        SetClickThrough(false);
        Hide();
        _hoverTimer.Stop();
        _lastHover = false;
        PushHover(false);
        _typing = false;
        _rage = false;
        _typingStreakStart = 0;
    }

    public void ApplyTokenUnit(string unit)
    {
        _tokenUnit = DashboardSettings.NormalizeTokenUnit(unit);
    }

    public void ApplyMode()
    {
        PushContext();
    }

    public void ApplySize(string size)
    {
        var right = Left + Width;
        var bottom = Top + Height;
        ApplySizeDims(size);
        Left = right - Width;
        Top = bottom - Height;
        var wa = SystemParameters.WorkArea;
        if (Left < wa.Left) Left = wa.Left;
        if (Top < wa.Top) Top = wa.Top;
        if (Left + Width > wa.Right) Left = wa.Right - Width;
        if (Top + Height > wa.Bottom) Top = wa.Bottom - Height;
        PushContext();
    }

    private void ApplySizeDims(string size)
    {
        _size = size is "small" or "large" ? size : "medium";
        (Width, Height, _bubbleBand) = _size switch
        {
            "small" => (168d, 140d, 44d),
            "large" => (280d, 220d, 72d),
            _ => (216d, 180d, 56d),
        };
    }

    public void ApplyStats(PetSnapshot stats)
    {
        var prev = _stats.TodayTokens;
        _stats = stats;
        if (prev > 0 && stats.TodayTokens > prev && _coreReady)
        {
            var delta = stats.TodayTokens - prev;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                "window.dispatchEvent(new CustomEvent('pet:model-status',{detail:{modelName:'token',tokensDelta:"
                + delta.ToString(CultureInfo.InvariantCulture) + ",costDelta:0}}));");
        }
        PushContext();
    }

    public void Shutdown()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            HidePet();
            return;
        }
        SavePlacement();
        _saveTimer.Stop();
        _hoverTimer.Stop();
        _clickThroughTimer.Stop();
        base.OnClosing(e);
    }

    private async Task InitializeWebViewAsync()
    {
        if (_coreReady) return;
        try
        {
            var userDataFolder = Path.Combine(AgentHubConfig.LocalDataDir, "WebView2Pet");
            Directory.CreateDirectory(userDataFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "0");

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            await _webView.EnsureCoreWebView2Async(env);
            _coreReady = true;
            _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "try{var s=document.createElement('style');" +
                "s.textContent='html,body,#pet-root{background:transparent!important}';" +
                "(document.head||document.documentElement).appendChild(s);}catch(e){}");

            core.WebMessageReceived += (_, e) =>
            {
                string msg;
                try { msg = e.TryGetWebMessageAsString(); }
                catch { return; }

                switch (msg)
                {
                    case "pet:drag":
                    case "pet:drag-left":
                    case "pet:drag-right":
                        _isDragging = true;
                        _lastDragLeft = Left;
                        PushDragState(msg == "pet:drag-left" ? "running-left" : "running-right");
                        try
                        {
                            ReleaseCapture();
                            SendMessage(_hwnd, WM_NCLBUTTONDOWN, (nint)HTCAPTION, nint.Zero);
                        }
                        finally
                        {
                            _isDragging = false;
                            PushDragState(null);
                        }
                        _saveTimer.Stop();
                        SavePlacement();
                        break;
                    case "pet:context-menu":
                        ContextMenuRequested?.Invoke();
                        break;
                    case "pet:open":
                        OpenRequested?.Invoke();
                        break;
                }
            };

            core.NavigationCompleted += (_, _) => PushContext();
            core.Navigate(_petUri.ToString());
        }
        catch (Exception)
        {
            // 宠物窗初始化失败不拖垮主应用；保持隐藏即可
        }
    }

    private bool IsPointOnPet(System.Windows.Point screenPoint)
    {
        try
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return true;
            var p = PointFromScreen(screenPoint);
            if (p.X < 0 || p.Y < 0 || p.X >= ActualWidth || p.Y >= ActualHeight) return false;

            double spriteSize = Math.Max(40, Math.Min(ActualWidth, ActualHeight - _bubbleBand) - 8);
            double pad = Math.Max(8, spriteSize * 0.08);
            double left = (ActualWidth - spriteSize) / 2 - pad;
            double right = left + spriteSize + (pad * 2);
            double top = _bubbleBand - pad;
            double bottom = Math.Min(ActualHeight, _bubbleBand + spriteSize + pad);
            return p.X >= left && p.X <= right && p.Y >= top && p.Y <= bottom;
        }
        catch { return true; }
    }

    private void ClickThroughTick()
    {
        if (!IsVisible || _hwnd == nint.Zero || _isDragging || !GetCursorPos(out var cursor)) return;
        SetClickThrough(!IsPointOnPet(new System.Windows.Point(cursor.X, cursor.Y)));
    }

    private void SetClickThrough(bool enabled)
    {
        if (_hwnd == nint.Zero || enabled == _clickThrough) return;
        long style = GetWindowExStyle(_hwnd).ToInt64();
        long updatedStyle = enabled ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowExStyle(_hwnd, (nint)updatedStyle);
        long appliedStyle = GetWindowExStyle(_hwnd).ToInt64();
        if (((appliedStyle & WS_EX_TRANSPARENT) != 0) != enabled) return;
        _clickThrough = enabled;
        SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void HoverTick()
    {
        if (!IsVisible || !_coreReady || _isDragging) return;
        if (!GetCursorPos(out var p)) return;

        long now = Environment.TickCount64;
        bool moved = p.X != _lastMousePos.X || p.Y != _lastMousePos.Y;
        if (moved)
        {
            _lastMousePos = p;
            _lastMouseActiveTime = now;
            if (_mouseIdle)
            {
                _mouseIdle = false;
                _ = _webView.CoreWebView2.ExecuteScriptAsync(
                    "window.dispatchEvent(new CustomEvent('pet:wake'));");
            }
        }
        else if (!_mouseIdle && now - _lastMouseActiveTime >= 60_000)
        {
            _mouseIdle = true;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                "window.dispatchEvent(new CustomEvent('pet:sleep',{detail:{phase:'sleeping'}}));");
        }

        bool inside;
        try
        {
            var tl = PointToScreen(new System.Windows.Point(0, 0));
            double spriteSize = Math.Max(40, Math.Min(Width, Height - _bubbleBand) - 8);
            double pad = Math.Max(8, spriteSize * 0.08);
            double spriteLeft = tl.X + (Width - spriteSize) / 2 - pad;
            double spriteRight = spriteLeft + spriteSize + pad * 2;
            double spriteTop = tl.Y + _bubbleBand - pad;
            double spriteBottom = tl.Y + _bubbleBand + spriteSize + pad;
            inside = p.X >= spriteLeft && p.X < spriteRight && p.Y >= spriteTop && p.Y < spriteBottom;
        }
        catch { return; }

        if (inside == _lastHover) return;
        _lastHover = inside;
        PushHover(inside);
    }

    private void PushHover(bool hovering)
    {
        if (!_coreReady) return;
        try
        {
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__ttPetHover={(hovering ? "true" : "false")};" +
                "window.dispatchEvent(new Event('pet:hover'));");
        }
        catch { }
    }

    private void UpdateDragDirectionFromWindowMove()
    {
        if (!_isDragging || double.IsNaN(Left)) return;
        var deltaX = Left - _lastDragLeft;
        if (Math.Abs(deltaX) < 0.5) return;
        _lastDragLeft = Left;
        PushDragState(deltaX < 0 ? "running-left" : "running-right");
    }

    private void PushDragState(string? state)
    {
        if (!_coreReady) return;
        try
        {
            var value = JsonSerializer.Serialize(state);
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__ttPetDragState={value};" +
                "window.dispatchEvent(new Event('pet:drag-state'));" +
                (state is null ? "window.dispatchEvent(new Event('pet:drag-end'));" : ""));
        }
        catch { }
    }

    private void TypingTick()
    {
        long now = Environment.TickCount64;
        if (AnyTypingKeyPressed()) _lastKeyTick = now;
        bool typing = now - _lastKeyTick < TypingLingerMs;
        if (typing != _typing)
        {
            _typing = typing;
            PushFlag("Typing", typing);
        }

        if (_rage)
        {
            if (now >= _rageUntil) { _rage = false; _typingStreakStart = 0; PushFlag("Rage", false); }
        }
        else if (now - _lastKeyTick < RageStreakGapMs)
        {
            if (_typingStreakStart == 0) _typingStreakStart = now;
            else if (now - _typingStreakStart >= RageTriggerMs)
            {
                _rage = true;
                _rageUntil = now + RageShowMs;
                PushFlag("Rage", true);
            }
        }
        else _typingStreakStart = 0;
    }

    private void PushFlag(string name, bool value)
    {
        if (!_coreReady) return;
        try
        {
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__ttPet{name}={(value ? "true" : "false")};" +
                $"window.dispatchEvent(new Event('pet:{name.ToLowerInvariant()}'));");
        }
        catch { }
    }

    private void PushContext()
    {
        if (!_coreReady) return;
        var inv = CultureInfo.InvariantCulture;
        var statsJson = JsonSerializer.Serialize(new
        {
            todayTokens = _stats.TodayTokens,
            todayCostUsd = 0,
            conversations = _stats.Conversations,
            last7dTokens = _stats.Last7dTokens,
            last7dActiveDays = 0,
            last30dTokens = 0,
            last30dAvgPerDay = 0,
            streakDays = _stats.StreakDays,
            activeDaysAllTime = 0,
            topModels = _stats.TopModels.Select(m => new { name = m.Name, percent = m.Percent, source = "agenthub" }),
        });
        try
        {
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                "window.__ttPetCurrency={symbol:'',rate:1};" +
                "window.__ttPetLocale='zh-CN';" +
                "window.__ttPetCharacter='clawd';" +
                "window.__ttPetDark=true;" +
                "window.__ttPetSyncing=false;" +
                "window.__ttPetConnected=true;" +
                "window.__ttPetMiniMode=false;" +
                $"window.__ttPetTokenUnit={JsonSerializer.Serialize(DashboardSettings.NormalizeTokenUnit(_tokenUnit))};" +
                $"window.__ttPetTokens={_stats.TodayTokens.ToString(inv)};" +
                "window.__ttPetCostUsd=0;" +
                $"window.__ttPetStats={statsJson};" +
                $"window.__ahPetSize={JsonSerializer.Serialize(_size)};" +
                "try{document.documentElement.dataset.size=" + JsonSerializer.Serialize(_size) + ";}catch(e){}" +
                "window.dispatchEvent(new Event('pet:usage'));" +
                "window.dispatchEvent(new Event('pet:connected'));" +
                "window.dispatchEvent(new Event('pet:mode'));");
        }
        catch { }
    }

    private static readonly string PlacementPath = Path.Combine(
        AgentHubConfig.LocalDataDir, "pet-placement.json");

    private void RestorePlacement()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var wa = SystemParameters.WorkArea;
        double left = wa.Right - Width - 24;
        double top = wa.Bottom - Height - 24;
        try
        {
            if (File.Exists(PlacementPath)
                && JsonNode.Parse(File.ReadAllText(PlacementPath))?.AsObject() is { } s
                && s["x"]?.GetValue<double>() is { } x
                && s["y"]?.GetValue<double>() is { } y
                && IsOnScreen(x, y, Width, Height))
            {
                left = Clamp(x, SystemParameters.VirtualScreenLeft,
                    SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
                top = Clamp(y, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
            }
        }
        catch { }
        Left = left;
        Top = top;
    }

    private void SavePlacement()
    {
        if (_isDragging) return;
        if (WindowState != WindowState.Normal) return;
        var x = Left;
        var y = Top;
        if (double.IsNaN(x) || double.IsNaN(y)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementPath)!);
            File.WriteAllText(PlacementPath, JsonSerializer.Serialize(new { x, y }));
        }
        catch { }
    }

    private static bool IsOnScreen(double x, double y, double width, double height)
    {
        double minX = SystemParameters.VirtualScreenLeft;
        double minY = SystemParameters.VirtualScreenTop;
        double maxX = minX + SystemParameters.VirtualScreenWidth;
        double maxY = minY + SystemParameters.VirtualScreenHeight;
        return x + width >= minX + 32 && y + height >= minY + 32
            && x <= maxX - 32 && y <= maxY - 32;
    }

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(v, Math.Max(min, max)));

    private static bool AnyTypingKeyPressed()
    {
        static bool Hit(int vk) => (GetAsyncKeyState(vk) & 0x0001) != 0;
        for (int vk = 0x41; vk <= 0x5A; vk++) if (Hit(vk)) return true;
        for (int vk = 0x30; vk <= 0x39; vk++) if (Hit(vk)) return true;
        for (int vk = 0x60; vk <= 0x6F; vk++) if (Hit(vk)) return true;
        foreach (int vk in TypingKeys) if (Hit(vk)) return true;
        return false;
    }

    private static readonly int[] TypingKeys =
        [0x20, 0x0D, 0x08, 0x09, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE];

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 2;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private static nint GetWindowExStyle(nint hWnd) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE)
        : (nint)GetWindowLong32(hWnd, GWL_EXSTYLE);

    private static nint SetWindowExStyle(nint hWnd, nint style) => IntPtr.Size == 8
        ? SetWindowLongPtr64(hWnd, GWL_EXSTYLE, style)
        : (nint)SetWindowLong32(hWnd, GWL_EXSTYLE, style.ToInt32());

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
