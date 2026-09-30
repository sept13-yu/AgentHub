using System.Windows.Forms;

namespace AgentHub.Shell;

/// <summary>托盘菜单：显示、同步、桌面额度、退出。</summary>
public sealed class TrayShellActions
{
    public required Action ShowMain { get; init; }
    public required Action Exit { get; init; }
    public required Action SyncNow { get; init; }
    public required Action ToggleDesktopQuota { get; init; }
}

/// <summary>托盘图标（WinForms NotifyIcon）。关主窗藏这里。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly TrayShellActions _actions;
    private readonly ToolStripMenuItem _desktopQuotaItem;
    private bool _balloonShown;

    public TrayIconService(TrayShellActions actions, bool light = false, bool desktopQuotaVisible = false)
    {
        _actions = actions;

        var menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        menu.Items.Add("显示 AgentHub", null, (_, _) => _actions.ShowMain());
        menu.Items.Add("立即同步", null, (_, _) => _actions.SyncNow());
        _desktopQuotaItem = new ToolStripMenuItem("桌面额度") { Checked = desktopQuotaVisible };
        _desktopQuotaItem.Click += (_, _) => _actions.ToggleDesktopQuota();
        menu.Items.Add(_desktopQuotaItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _actions.Exit());

        _icon = new NotifyIcon
        {
            Text = "AgentHub",
            Icon = AppIcon.Create(light),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _actions.ShowMain();
    }

    public void ApplyTheme(bool light)
    {
        var next = AppIcon.Create(light);
        var prev = _icon.Icon;
        _icon.Icon = next;
        prev?.Dispose();
    }

    public void NotifyHiddenToTray()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        Notify("已最小化到托盘。双击图标恢复；右键可打开「桌面额度」或退出。", ToolTipIcon.Info);
    }

    public void SetDesktopQuotaChecked(bool visible) => _desktopQuotaItem.Checked = visible;

    public void Notify(string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _icon.ShowBalloonTip(4000, "AgentHub", text, icon);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
