using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AgentHub.Shell;

/// <summary>应用图标：优先读打包的 ICO/PNG，没有文件时才回退自绘。</summary>
public static class AppIcon
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public static string? FilePath
    {
        get
        {
            foreach (var p in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "assets", "agenthub.ico"),
                Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico"),
            })
                if (File.Exists(p)) return p;
            return null;
        }
    }

    public static Icon Create(bool light = false)
    {
        if (FilePath is { } path)
        {
            try { return new Icon(path, 32, 32); }
            catch (Exception) { /* 文件坏了再自绘 */ }
        }
        using var bmp = Draw(light);
        IntPtr handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public static BitmapSource CreateImageSource(bool light)
    {
        foreach (var p in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "assets", "agenthub.png"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "icon.png"),
        })
        {
            if (!File.Exists(p)) continue;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(Path.GetFullPath(p));
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception) { /* 下一份 */ }
        }

        using var drawn = Draw(light);
        var hbitmap = drawn.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(hbitmap); }
    }

    private static Bitmap Draw(bool light)
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var mint = Color.FromArgb(0x2E, 0xC4, 0xB6);
        using var pathBg = Rounded(1.5f, 1.5f, 29, 29, 8);
        using var bg = new SolidBrush(light
            ? Color.FromArgb(0xEF, 0xF4, 0xF1)
            : Color.FromArgb(0x18, 0x18, 0x1C));
        g.FillPath(bg, pathBg);
        using var rim = new Pen(mint, 1.6f);
        g.DrawPath(rim, pathBg);
        return bmp;
    }

    private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, r, r, 180, 90);
        p.AddArc(x + w - r, y, r, r, 270, 90);
        p.AddArc(x + w - r, y + h - r, r, r, 0, 90);
        p.AddArc(x, y + h - r, r, r, 90, 90);
        p.CloseFigure();
        return p;
    }
}
