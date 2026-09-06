using System.Windows.Forms;

namespace AgentHub.Shell;

/// <summary>托盘菜单：显示、同步、检查更新、卸载、退出。</summary>
public sealed class TrayShellActions
{
    public required Action ShowMain { get; init; }
    public required Action Exit { get; init; }
    public required Action SyncNow { get; init; }
    public required Action CheckUpdates { get; init; }
    public required Action DownloadAndRestart { get; init; }
    public required Action Uninstall { get; init; }
}

/// <summary>托盘图标（WinForms NotifyIcon）。关主窗藏这里。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly TrayShellActions _actions;
    private bool _balloonShown;

    public TrayIconService(TrayShellActions actions, bool light = false)
    {
        _actions = actions;

        var menu = new ContextMenuStrip { ShowCheckMargin = false, ShowImageMargin = false };
        menu.Items.Add("显示 AgentHub", null, (_, _) => _actions.ShowMain());
        menu.Items.Add("立即同步", null, (_, _) => _actions.SyncNow());
        menu.Items.Add("检查更新", null, (_, _) => _actions.CheckUpdates());
        menu.Items.Add("下载并重启更新", null, (_, _) => _actions.DownloadAndRestart());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("卸载", null, (_, _) => _actions.Uninstall());
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
        Notify("已最小化到托盘。双击图标恢复，右键可退出。", ToolTipIcon.Info);
    }

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
