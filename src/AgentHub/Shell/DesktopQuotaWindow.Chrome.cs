using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Shell;

public partial class DesktopQuotaWindow
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int RGN_OR = 2;
    private const int WM_DPICHANGED = 0x02E0;

    private HwndSource? _windowSource;

    private void InitializeChrome()
    {
        SourceInitialized += (_, _) => ApplyWindowChrome();
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        SizeChanged += (_, _) => UpdateWindowHitRegion();
    }

    private void ApplyWindowChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex.ToInt64() | WS_EX_TOOLWINDOW));
        _windowSource = HwndSource.FromHwnd(hwnd);
        _windowSource?.AddHook(OnWindowMessage);
        UpdateWindowHitRegion();
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_DPICHANGED)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateWindowHitRegion));
        }
        return IntPtr.Zero;
    }

    /// <summary>将系统命中区限制在圆盘、皇冠和展开面板内。</summary>
    private void UpdateWindowHitRegion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (GetClientRect(hwnd, out var client) == 0 || client.Right <= 0 || client.Bottom <= 0) return;

        var scaleX = client.Right / ActualWidth;
        var scaleY = client.Bottom / ActualHeight;
        int PxX(double x) => (int)Math.Round(x * scaleX);
        int PxY(double y) => (int)Math.Round(y * scaleY);
        const double viewBox = 440;
        var dialSize = (double)_config.DesktopQuota.DialSize;
        var offsetX = (ActualWidth - dialSize) / 2;
        const double centerX = 205;
        const double centerY = 211;
        const double hitRadius = 174;
        var scale = dialSize / viewBox;
        var radius = hitRadius * scale;

        var region = CreateEllipticRgn(
            PxX(offsetX + centerX * scale - radius),
            PxY(centerY * scale - radius),
            PxX(offsetX + centerX * scale + radius),
            PxY(centerY * scale + radius));
        if (region == IntPtr.Zero) return;

        AddRoundedRegion(region, PxX(offsetX + 368 * scale), PxY(176 * scale),
            PxX(offsetX + 410 * scale), PxY(251 * scale), PxX(22 * scale), PxY(22 * scale));
        if (_config.DesktopQuota.Expanded)
        {
            var panelTop = dialSize * (1 - SettingsTuck);
            AddRoundedRegion(region, PxX(12), PxY(panelTop), PxX(ActualWidth - 12),
                PxY(panelTop + SettingsBody), PxX(28), PxY(28));
        }

        if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }

    private static void AddRoundedRegion(IntPtr destination, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight)
    {
        var extra = CreateRoundRectRgn(left, top, right, bottom, ellipseWidth, ellipseHeight);
        if (extra == IntPtr.Zero) return;
        _ = CombineRgn(destination, destination, extra, RGN_OR);
        DeleteObject(extra);
    }

    private void OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        var dialSize = (double)_config.DesktopQuota.DialSize;
        var scale = dialSize / 440d;
        var offsetX = (ActualWidth - dialSize) / 2;
        var centerX = offsetX + 205d * scale;
        var centerY = 211d * scale;
        var radius = 173d * scale;
        var dx = point.X - centerX;
        var dy = point.Y - centerY;
        if (InRefreshSlot(point, offsetX, scale) || InSettingsPlate(point, dialSize)) return;

        var inDial = dx * dx + dy * dy <= radius * radius;
        var inCrown = point.X >= offsetX + 368d * scale && point.X <= offsetX + 410d * scale &&
                      point.Y >= 176d * scale && point.Y <= 251d * scale;
        if (!inDial || inCrown) return;

        e.Handled = true;
        try { DragMove(); }
        catch (InvalidOperationException) { /* 鼠标可能已在拖动开始前松开。 */ }
    }

    /// <summary>12 点方向的刷新键在表盘圆内，拖动逻辑必须让开，否则点不到。</summary>
    private static bool InRefreshSlot(System.Windows.Point point, double offsetX, double scale)
    {
        if (scale <= 0) return false;
        var x = (point.X - offsetX) / scale;
        var y = point.Y / scale;
        return x is >= 176 and <= 236 && y is >= 48 and <= 96;
    }

    private bool InSettingsPlate(System.Windows.Point point, double dialSize)
    {
        if (!_config.DesktopQuota.Expanded) return false;
        var top = dialSize * (1 - SettingsTuck);
        return point.X >= 12 && point.X <= ActualWidth - 12
            && point.Y >= top && point.Y <= top + SettingsBody;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// 工作区用窗口当前显示器的物理像素。SystemParameters.WorkArea 只有主屏，
    /// 副屏上展开设置会被夹回第一块屏。
    /// </summary>
    private bool TryGetPlacement(out RECT window, out RECT work)
    {
        window = default;
        work = default;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out window)) return false;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        work = info.rcWork;
        return work.Right > work.Left && work.Bottom > work.Top;
    }

    private void HoldPhysicalPosition(RECT anchor, RECT work)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var now)) return;
        var width = now.Right - now.Left;
        var height = now.Bottom - now.Top;
        var workWidth = work.Right - work.Left;
        var workHeight = work.Bottom - work.Top;
        var maxLeft = work.Right - Math.Min(width, workWidth);
        var maxTop = work.Bottom - Math.Min(height, workHeight);
        if (maxLeft < work.Left) maxLeft = work.Left;
        if (maxTop < work.Top) maxTop = work.Top;
        var x = Math.Clamp(anchor.Left, work.Left, maxLeft);
        var y = Math.Clamp(anchor.Top, work.Top, maxTop);
        if (x == now.Left && y == now.Top) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern int GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8) return SetWindowLongPtr64(hwnd, index, value);
        return new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
    }

}
