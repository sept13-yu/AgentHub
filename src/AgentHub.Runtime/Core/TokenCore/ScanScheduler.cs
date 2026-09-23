using AgentHub.Core.ProxyCore;
using AgentHub.Core.SessionCore;

namespace AgentHub.Core.TokenCore;

/// <summary>用量重扫调度：启动/手动/定时都走分阶段扫描——本地源秒回，CSV + 索引后台收尾。</summary>
public sealed class ScanScheduler : IDisposable
{
    private readonly TokenService _tokens;
    private readonly SessionService _sessions;
    private readonly AgentHubConfig _config;
    private readonly Action<string> _log;
    private readonly Action _onCompleted;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private Task<ScanAllResult>? _activeLocal;
    private TaskCompletionSource<ScanAllResult>? _queuedLocal;
    private volatile bool _disposed;

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

    /// <summary>本地入库后返回，Cursor CSV、Trae 和会话索引在后台收尾；
    /// 重叠的手动刷新只补扫一次，并在补扫本地阶段结束后返回。</summary>
    public Task<ScanAllResult> RunAsync(bool rescanIfBusy = false)
    {
        TaskCompletionSource<ScanAllResult> completion;
        lock (_gate)
        {
            if (_disposed)
                return Task.FromException<ScanAllResult>(new ObjectDisposedException(nameof(ScanScheduler)));
            if (_activeLocal is not null)
            {
                if (!rescanIfBusy) return _activeLocal;
                _queuedLocal ??= new TaskCompletionSource<ScanAllResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _queuedLocal.Task;
            }
            completion = new TaskCompletionSource<ScanAllResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeLocal = completion.Task;
        }
        _ = Task.Run(() => RunCycleAsync(completion));
        return completion.Task;
    }

    private async Task RunCycleAsync(TaskCompletionSource<ScanAllResult> completion)
    {
        try
        {
            var result = _tokens.ScanAllLocal();
            _log($"[tokencore] 本地扫描完成：{FormatSources(result)} 入库 {result.Inserted} 条（{result.Seconds:F1}s）");
            completion.TrySetResult(result);
            if (!_disposed) _onCompleted();

            if (!_disposed)
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
            }
            if (!_disposed)
            {
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
            }
            if (!_disposed)
            {
                try
                {
                    await _sessions.EnsureIndexAsync(force: true);
                    _log($"[sessions] 索引已更新：{_sessions.IndexedCount} 条");
                }
                catch (Exception ex)
                {
                    _log($"[sessions] 索引更新失败 {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (!_disposed) _onCompleted();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            _log($"[tokencore] 扫描异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TaskCompletionSource<ScanAllResult>? next;
            lock (_gate)
            {
                next = _disposed ? null : _queuedLocal;
                _queuedLocal = null;
                _activeLocal = next?.Task;
            }
            if (next is not null)
                _ = Task.Run(() => RunCycleAsync(next));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _queuedLocal?.TrySetCanceled();
            _queuedLocal = null;
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
