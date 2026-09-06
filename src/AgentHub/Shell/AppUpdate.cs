using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using AgentHub.Core.ProxyCore;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace AgentHub.Shell;

/// <summary>检查 / 下载更新的结果，给设置页用。</summary>
public sealed class AppUpdateStatus
{
    public bool installed { get; init; }
    public bool busy { get; init; }
    public bool canApply { get; init; }
    public bool needsInstaller { get; init; }
    public string? current { get; init; }
    public string? latest { get; init; }
    public string? releaseUrl { get; init; }
    public string? error { get; init; }
    public string? message { get; init; }
}

/// <summary>走 api.github.com 列 Release，再用 API 资源地址落到 release-assets.githubusercontent.com。</summary>
public sealed class GithubApiUpdateSource : IUpdateSource
{
    public const string RepoUrl = "https://github.com/sept13-yu/AgentHub";
    const string ApiLatest = "https://api.github.com/repos/sept13-yu/AgentHub/releases/latest";
    const string ApiReleases = "https://api.github.com/repos/sept13-yu/AgentHub/releases?per_page=10";

    static readonly HttpClient Http = CreateClient();
    readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        await RefreshIndexAsync(CancellationToken.None).ConfigureAwait(false);
        var feedName = string.IsNullOrWhiteSpace(channel) ? "releases.win.json" : "releases." + channel + ".json";
        var url = FindUrl(feedName) ?? FindUrl("releases.win.json")
            ?? throw new InvalidOperationException("GitHub Release 里没有 releases.win.json。");
        var json = await DownloadStringAsync(url, TimeSpan.FromSeconds(20), CancellationToken.None).ConfigureAwait(false);
        return VelopackAssetFeed.FromJson(json);
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken)
    {
        if (string.IsNullOrWhiteSpace(releaseEntry.FileName))
            throw new InvalidOperationException("更新包没有文件名。");
        var url = FindUrl(releaseEntry.FileName);
        if (url is null)
        {
            await RefreshIndexAsync(cancelToken).ConfigureAwait(false);
            url = FindUrl(releaseEntry.FileName)
                ?? throw new InvalidOperationException("GitHub 上找不到 " + releaseEntry.FileName + "。");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancelToken)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(cancelToken).ConfigureAwait(false);
        await using var dst = File.Create(localFile);
        var buf = new byte[81920];
        long read = 0;
        var last = -1;
        int n;
        while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), cancelToken).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), cancelToken).ConfigureAwait(false);
            read += n;
            if (total is > 0)
            {
                var pct = (int)(read * 100 / total.Value);
                if (pct != last)
                {
                    last = pct;
                    progress?.Invoke(pct);
                }
            }
        }
        progress?.Invoke(100);
    }

    static readonly string[] LatestManifests =
    [
        "https://raw.githubusercontent.com/sept13-yu/AgentHub/main/latest.json",
        "https://cdn.jsdelivr.net/gh/sept13-yu/AgentHub@main/latest.json",
    ];

    public static async Task<string?> FetchLatestTagAsync(CancellationToken ct = default)
    {
        foreach (var url in LatestManifests)
        {
            try
            {
                var version = await ReadVersionManifestAsync(url, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }
            catch (Exception) { /* 下一个地址 */ }
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, ApiLatest);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag)) return null;
        return tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
    }

    static async Task<string?> ReadVersionManifestAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12));
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
        var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        if (string.IsNullOrWhiteSpace(version)) return null;
        return version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;
    }

    public async Task<VelopackAssetFeed> FetchFeedAsync(string? channel, CancellationToken ct = default)
    {
        await RefreshIndexAsync(ct).ConfigureAwait(false);
        var feedName = string.IsNullOrWhiteSpace(channel) ? "releases.win.json" : "releases." + channel + ".json";
        var url = FindUrl(feedName) ?? FindUrl("releases.win.json")
            ?? throw new InvalidOperationException("GitHub Release 里没有 releases.win.json。");
        var json = await DownloadStringAsync(url, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return VelopackAssetFeed.FromJson(json);
    }

    async Task RefreshIndexAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiReleases);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
        lock (_gate)
        {
            _files.Clear();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                    continue;
                if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = a.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    _files.TryAdd(name, url);
                }
            }
        }
    }

    string? FindUrl(string name)
    {
        lock (_gate) return _files.TryGetValue(name, out var url) ? url : null;
    }

    static async Task<string> DownloadStringAsync(string apiUrl, TimeSpan timeout, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AgentHub");
        return http;
    }
}

/// <summary>设置页的检查 / 下载更新。</summary>
public static class AppUpdate
{
    static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(25);
    static readonly TimeSpan LatestTtl = TimeSpan.FromMinutes(30);
    static readonly string LatestCachePath = Path.Combine(AgentHubConfig.Dir, "update.latest.json");
    static readonly object CacheGate = new();
    static string? _cachedLatest;
    static DateTimeOffset _cachedAt;
    static int _busy;

    public static bool Busy => Volatile.Read(ref _busy) != 0;

    public static string CurrentVersion
    {
        get
        {
            try
            {
                var v = CreateManager().CurrentVersion;
                if (v is not null) return v.ToString();
            }
            catch (Exception) { /* 调试运行没有安装定位 */ }

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        }
    }

    public static UpdateManager CreateManager() => new(new GithubApiUpdateSource());

    public static AppUpdateStatus Snapshot()
    {
        try
        {
            var mgr = CreateManager();
            return new AppUpdateStatus
            {
                installed = mgr.IsInstalled,
                busy = Busy,
                current = mgr.CurrentVersion?.ToString() ?? CurrentVersion,
            };
        }
        catch (Exception)
        {
            return new AppUpdateStatus { installed = false, busy = Busy, current = CurrentVersion };
        }
    }

    public static async Task<AppUpdateStatus> CheckAsync()
    {
        if (!TryBegin()) return AlreadyBusy();
        try
        {
            var snap = Snapshot();
            var (latest, probeError) = await ProbeLatestAsync().ConfigureAwait(false);
            if (latest is null)
                return Fail(probeError ?? "没有查到可用版本。", snap.installed, snap.current);

            return Compose(snap.installed, snap.current, latest);
        }
        catch (Exception ex)
        {
            var snap = Snapshot();
            return Fail(Humanize(ex), snap.installed, snap.current);
        }
        finally
        {
            End();
        }
    }

    public static async Task<AppUpdateStatus> ApplyAsync(Action<int>? progress = null, CancellationToken ct = default)
    {
        if (!TryBegin()) return AlreadyBusy();
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled)
            {
                var (found, probeError) = await ProbeLatestAsync().ConfigureAwait(false);
                if (found is null)
                    return Fail(probeError ?? "没有查到可用版本。", installed: false, CurrentVersion);
                return Compose(installed: false, CurrentVersion, found);
            }

            var info = await AwaitTimeout(mgr.CheckForUpdatesAsync(), CheckTimeout).ConfigureAwait(false);
            if (info is null)
                return new AppUpdateStatus { installed = true, current = CurrentVersion, message = "已是最新版本。" };

            var latest = info.TargetFullRelease.Version.ToString();

            await mgr.DownloadUpdatesAsync(info, progress, ct).ConfigureAwait(false);
            mgr.ApplyUpdatesAndRestart(info);
            return new AppUpdateStatus
            {
                installed = true,
                current = CurrentVersion,
                latest = latest,
                message = "正在重启以完成更新。",
            };
        }
        catch (Exception ex)
        {
            return Fail(Humanize(ex), installed: true, CurrentVersion);
        }
        finally
        {
            End();
        }
    }

    public static bool IsReleasePage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.AbsolutePath.StartsWith("/sept13-yu/AgentHub", StringComparison.OrdinalIgnoreCase);
    }

    static async Task<(string? latest, string? error)> ProbeLatestAsync()
    {
        if (TryReadCache(out var cached, stale: false))
            return (cached, null);

        try
        {
            var latest = await AwaitTimeout(GithubApiUpdateSource.FetchLatestTagAsync(), CheckTimeout)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(latest))
                return TryReadCache(out cached, stale: true) ? (cached, null) : (null, "没有查到可用版本。");
            RememberLatest(latest);
            return (latest, null);
        }
        catch (Exception ex)
        {
            return TryReadCache(out cached, stale: true)
                ? (cached, null)
                : (null, Humanize(ex));
        }
    }

    static bool TryReadCache(out string? latest, bool stale)
    {
        lock (CacheGate)
        {
            if (_cachedLatest is not null && (stale || DateTimeOffset.UtcNow - _cachedAt < LatestTtl))
            {
                latest = _cachedLatest;
                return true;
            }
        }

        try
        {
            if (!File.Exists(LatestCachePath)) { latest = null; return false; }
            using var doc = JsonDocument.Parse(File.ReadAllText(LatestCachePath));
            var tag = doc.RootElement.TryGetProperty("latest", out var l) ? l.GetString() : null;
            var at = doc.RootElement.TryGetProperty("fetchedAt", out var t) &&
                     DateTimeOffset.TryParse(t.GetString(), out var parsed)
                ? parsed : DateTimeOffset.MinValue;
            if (string.IsNullOrWhiteSpace(tag)) { latest = null; return false; }
            lock (CacheGate)
            {
                _cachedLatest = tag;
                _cachedAt = at;
            }
            if (!stale && DateTimeOffset.UtcNow - at >= LatestTtl) { latest = null; return false; }
            latest = tag;
            return true;
        }
        catch (Exception)
        {
            latest = null;
            return false;
        }
    }

    static void RememberLatest(string latest)
    {
        var now = DateTimeOffset.UtcNow;
        lock (CacheGate)
        {
            _cachedLatest = latest;
            _cachedAt = now;
        }
        try
        {
            Directory.CreateDirectory(AgentHubConfig.Dir);
            File.WriteAllText(LatestCachePath,
                "{\"latest\":\"" + latest + "\",\"fetchedAt\":\"" + now.ToString("o") + "\"}");
        }
        catch (Exception) { /* 缓存写失败不影响检查结果 */ }
    }

    static AppUpdateStatus Compose(bool installed, string? current, string latest)
    {
        var newer = IsNewer(latest, current);
        var page = GithubApiUpdateSource.RepoUrl + "/releases/tag/v" + latest;
        if (!installed)
            return new AppUpdateStatus
            {
                installed = false,
                needsInstaller = true,
                current = current,
                latest = latest,
                releaseUrl = page,
                message = "有最新版本 " + latest,
            };

        if (!newer)
            return new AppUpdateStatus
            {
                installed = true,
                current = current,
                latest = latest,
                releaseUrl = page,
                message = "已是最新版本。",
            };

        return new AppUpdateStatus
        {
            installed = true,
            canApply = true,
            current = current,
            latest = latest,
            releaseUrl = page,
            message = "有最新版本 " + latest,
        };
    }

    static bool IsNewer(string latest, string? current)
    {
        if (string.IsNullOrWhiteSpace(current)) return true;
        if (!Version.TryParse(TrimVersion(latest), out var l)) return true;
        if (!Version.TryParse(TrimVersion(current), out var c)) return true;
        return l > c;
    }

    static string TrimVersion(string value)
    {
        var core = value.Trim();
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0) core = core[..cut];
        return core;
    }

    static AppUpdateStatus AlreadyBusy() => new()
    {
        installed = Snapshot().installed,
        busy = true,
        current = CurrentVersion,
        message = "正在处理更新，请稍候。",
    };

    static AppUpdateStatus Fail(string error, bool installed, string? current) => new()
    {
        installed = installed,
        current = current ?? CurrentVersion,
        error = error,
    };

    static string Humanize(Exception ex)
    {
        if (ex is TimeoutException or TaskCanceledException or OperationCanceledException)
            return "检查更新超时。请稍后重试。";
        var msg = ex.Message;
        if (string.IsNullOrWhiteSpace(msg)) return "更新失败。";
        if (msg.Contains("403", StringComparison.Ordinal) &&
            msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return "GitHub 请求较频繁，请稍后再检查更新。";
        return msg.Contains("更新失败", StringComparison.Ordinal) ? msg : "更新失败：" + msg;
    }

    static bool TryBegin() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    static void End() => Interlocked.Exchange(ref _busy, 0);

    static async Task<T> AwaitTimeout<T>(Task<T> task, TimeSpan timeout)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != task) throw new TimeoutException();
        return await task.ConfigureAwait(false);
    }
}
