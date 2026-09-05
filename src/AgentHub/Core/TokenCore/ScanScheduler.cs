using AgentHub.Core.ProxyCore;
using AgentHub.Core.SessionCore;

namespace AgentHub.Core.TokenCore;

/// <summary>用量重扫调度：启动/手动/定时都走分阶段扫描——本地源秒回，CSV + 索引后台收尾。</summary>
internal sealed class ScanScheduler : IDisposable
{
    private readonly TokenService _tokens;
    private readonly SessionService _sessions;
    private readonly AgentHubConfig _config;
    private readonly Action<string> _log;
    private readonly Action _onCompleted;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;

    public ScanScheduler(
        TokenService tokens,
        SessionService sessions,
        AgentHubConfig config,
        Action<string> log,
        Action onCompleted)
    {
        _tokens = tokens;
        _sessions = sessions;
        _config = config;
        _log = log;
        _onCompleted = onCompleted;
    }

    public void Reconfigure()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            var minutes = Math.Clamp(_config.Dashboard.ScanIntervalMinutes, 0, 1440);
            if (minutes <= 0) return;
            var period = TimeSpan.FromMinutes(minutes);
            _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, period, period);
        }
    }

    /// <summary>分两阶段：本地源入库（亚秒级）后立即通知页面/宠物并返回——刷新按钮不等网络；
    /// Cursor CSV、Trae 用量、会话索引转后台收尾，完成后再通知一次，前端经 agenthub-refresh 自动补数据。</summary>
    public Task<ScanAllResult> RunAsync()
    {
        var result = _tokens.ScanAllLocal();
        _log($"[tokencore] 本地扫描完成：{FormatSources(result)} 入库 {result.Inserted} 条（{result.Seconds:F1}s）");
        _onCompleted();

        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var cursor = _tokens.ScanCursorCsv();
                if (cursor.Skipped == 0)
                    _log($"[tokencore] cursor 入库 {cursor.Inserted} 条");
            }
            catch (Exception ex)
            {
                _log($"[tokencore] cursor 收尾异常 {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                var trae = _tokens.ScanTraeUsage();
                if (trae.Files > 0 && trae.Skipped == 0)
                    _log($"[tokencore] trae 入库 {trae.Inserted} 条");
            }
            catch (Exception ex)
            {
                _log($"[tokencore] trae 收尾异常 {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                await _sessions.EnsureIndexAsync(force: true);
                _log($"[sessions] 索引已更新：{_sessions.IndexedCount} 条");
            }
            catch (Exception ex)
            {
                _log($"[sessions] 索引更新失败 {ex.GetType().Name}: {ex.Message}");
            }
            _onCompleted();
        });
        return System.Threading.Tasks.Task.FromResult(result);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private async Task TickAsync()
    {
        try { await RunAsync(); }
        catch (Exception ex)
        {
            _log($"[tokencore] 定时扫描失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static string FormatSources(ScanAllResult result) =>
        string.Join("；", result.Sources.Select(kv =>
            $"{kv.Key} {kv.Value.Files} 文件 {kv.Value.Inserted} 条 {kv.Value.Skipped} 跳过"));
}
