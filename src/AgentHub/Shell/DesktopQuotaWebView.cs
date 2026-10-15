using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AgentHub.Core.ProxyCore;
using Microsoft.Web.WebView2.Wpf;
using Image = System.Windows.Controls.Image;

namespace AgentHub.Shell;

/// <summary>保持桌面圆盘的合成画面与当前显示器像素对齐。</summary>
public sealed class DesktopQuotaWebView : WebView2CompositionControl
{
    private bool _dpiSyncQueued;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // 浏览器已经抗锯齿，合成到 WPF 时不要再平滑一次。
        if (GetTemplateChild("PART_image") is Image image)
        {
            image.UseLayoutRounding = true;
            image.SnapsToDevicePixels = true;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_dpiSyncQueued) return;
        _dpiSyncQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SyncDpi));
    }

    private void SyncDpi()
    {
        _dpiSyncQueued = false;
        if (!IsLoaded || CoreWebView2 is null) return;
        try
        {
            // SDK 1.0.4191 的 _dpiScale、捕获缓冲区、鼠标坐标比例都在 SizeChanged
            // 更新。跨屏不改变 DIP 尺寸时，走 WPF 公开的尺寸通知入口刷新物理尺寸，
            // 不反射私有控制器，也不临时改变窗口大小或网页缩放。
            OnRenderSizeChanged(new SizeChangedInfo(this, RenderSize, true, true));
            InvalidateVisual();
            var dpi = VisualTreeHelper.GetDpi(this);
            HubLog.Write($"[desktop-quota] DPI画布同步 scale={dpi.DpiScaleX:0.##} size={ActualWidth:0.#}x{ActualHeight:0.#}");
        }
        catch (Exception ex)
        {
            HubLog.Write("[desktop-quota] DPI画布同步失败 " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
