using System.Windows.Forms;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.TokenCore;
using AgentHub.Web;

namespace AgentHub.Shell;

/// <summary>桌面宠物：模式永远 usage；右键只有同步和关闭；双击打开主窗。</summary>
internal sealed class PetHost : IDisposable
{
    private readonly WebHostService _web;
    private readonly TokenService _tokens;
    private readonly AgentHubConfig _config;
    private readonly Action _showDashboard;
    private readonly Action _syncNow;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly System.Windows.Threading.DispatcherTimer _poll;
    private readonly ContextMenuStrip _petMenu;
    private PetWindow? _window;

    public PetHost(WebHostService web, TokenService tokens, AgentHubConfig config,
        Action showDashboard, Action syncNow, System.Windows.Threading.Dispatcher dispatcher)
    {
        _web = web;
        _tokens = tokens;
        _config = config;
        _showDashboard = showDashboard;
        _syncNow = syncNow;
        _dispatcher = dispatcher;
        _poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _poll.Tick += (_, _) => PushStats();

        _petMenu = new ContextMenuStrip { ShowCheckMargin = false, ShowImageMargin = false };
        _petMenu.Items.Add("立即同步", null, (_, _) => _syncNow());
        _petMenu.Items.Add("关闭桌面宠物", null, (_, _) => DisablePet());
    }

    public bool IsRunning => _config.App.PetEnabled && _window is { IsVisible: true };

    public void Apply()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(Apply);
            return;
        }

        if (!_config.App.PetEnabled)
        {
            _poll.Stop();
            _window?.HidePet();
            return;
        }

        if (_window is null)
        {
            var uri = new Uri(_web.BaseUri, "pet.html?app=1");
            _window = new PetWindow(uri, _config.App.PetSize);
            _window.ContextMenuRequested += OnContextMenuRequested;
            _window.OpenRequested += OnOpenRequested;
        }
        _window.ApplyTokenUnit(_config.Dashboard.TokenUnit);
        _window.ApplySize(_config.App.PetSize);
        _window.ApplyMode();
        _window.ShowPet();
        PushStats();
        _poll.Start();
    }

    public void PushStats()
    {
        if (_window is null || !_config.App.PetEnabled) return;
        try
        {
            var snap = _tokens.GetPetSnapshot();
            _window.ApplyStats(snap);
        }
        catch { }
    }

    public void Shutdown()
    {
        _poll.Stop();
        if (_window is null)
        {
            _petMenu.Dispose();
            return;
        }
        _window.ContextMenuRequested -= OnContextMenuRequested;
        _window.OpenRequested -= OnOpenRequested;
        _window.Shutdown();
        _window = null;
        _petMenu.Dispose();
    }

    public void Dispose() => Shutdown();

    private void OnContextMenuRequested()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(OnContextMenuRequested);
            return;
        }
        _petMenu.Show(Cursor.Position);
    }

    private void OnOpenRequested()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(OnOpenRequested);
            return;
        }
        _showDashboard();
    }

    private void DisablePet()
    {
        _config.App.PetEnabled = false;
        _config.Save();
        Apply();
    }
}
