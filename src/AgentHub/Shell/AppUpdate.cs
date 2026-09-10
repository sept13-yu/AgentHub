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
            ?? throw new InvalidOperationException("Release 里没有 releases.win.json。");
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
                ?? throw new InvalidOperationException("更新源上找不到 " + releaseEntry.FileName + "。");
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
            ?? throw new InvalidOperationException("Release 里没有 releases.win.json。");
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

/// <summary>
/// 走 gitee.com/api/v5 列 Release；上传文件在 attach_files（assets 多为源码包）。
/// 下载直接用 browser_download_url，不走 GitHub 式 assets API。
/// </summary>
public sealed class GiteeApiUpdateSource : IUpdateSource
{
    public const string RepoUrl = "https://gitee.com/sept13-yu/AgentHub";
    const string ApiLatest = "https://gitee.com/api/v5/repos/sept13-yu/AgentHub/releases/latest";
    const string ApiReleases = "https://gitee.com/api/v5/repos/sept13-yu/AgentHub/releases?per_page=20";
    const string ApiAttachFilesFmt = "https://gitee.com/api/v5/repos/sept13-yu/AgentHub/releases/{0}/attach_files";

    static readonly HttpClient Http = CreateClient();
    readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        await RefreshIndexAsync(CancellationToken.None).ConfigureAwait(false);
        var feedName = string.IsNullOrWhiteSpace(channel) ? "releases.win.json" : "releases." + channel + ".json";
        var url = FindUrl(feedName) ?? FindUrl("releases.win.json")
            ?? throw new InvalidOperationException("Release 里没有 releases.win.json。");
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
                ?? throw new InvalidOperationException("更新源上找不到 " + releaseEntry.FileName + "。");
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

    public static async Task<string?> FetchLatestTagAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiLatest);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
                var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (!string.IsNullOrWhiteSpace(tag))
                    return tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
            }
        }
        catch (Exception) { /* 回落到列表首个非预发布 */ }

        using var listReq = new HttpRequestMessage(HttpMethod.Get, ApiReleases);
        using var listCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        listCts.CancelAfter(TimeSpan.FromSeconds(15));
        using var listResp = await Http.SendAsync(listReq, listCts.Token).ConfigureAwait(false);
        listResp.EnsureSuccessStatusCode();
        await using var listStream = await listResp.Content.ReadAsStreamAsync(listCts.Token).ConfigureAwait(false);
        using var listDoc = await JsonDocument.ParseAsync(listStream, cancellationToken: listCts.Token).ConfigureAwait(false);
        foreach (var rel in listDoc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                continue;
            var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) continue;
            return tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        }
        return null;
    }

    public async Task<VelopackAssetFeed> FetchFeedAsync(string? channel, CancellationToken ct = default)
    {
        await RefreshIndexAsync(ct).ConfigureAwait(false);
        var feedName = string.IsNullOrWhiteSpace(channel) ? "releases.win.json" : "releases." + channel + ".json";
        var url = FindUrl(feedName) ?? FindUrl("releases.win.json")
            ?? throw new InvalidOperationException("Release 里没有 releases.win.json。");
        var json = await DownloadStringAsync(url, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return VelopackAssetFeed.FromJson(json);
    }

    async Task RefreshIndexAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiReleases);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                continue;
            if (!rel.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;

            // Gitee：用户上传文件在 attach_files；assets 常只有源码 zip/tar。优先 attach_files。
            try
            {
                await IndexAttachFilesAsync(id, next, cts.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* 单个 release 附件失败不阻断其它 */ }

            if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                    TryAddAsset(a, next, preferOverwrite: false);
            }
        }

        lock (_gate)
        {
            _files.Clear();
            foreach (var kv in next) _files[kv.Key] = kv.Value;
        }
    }

    static async Task IndexAttachFilesAsync(string releaseId, Dictionary<string, string> into, CancellationToken ct)
    {
        var url = string.Format(ApiAttachFilesFmt, releaseId);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        foreach (var a in doc.RootElement.EnumerateArray())
            TryAddAsset(a, into, preferOverwrite: true);
    }

    static void TryAddAsset(JsonElement a, Dictionary<string, string> into, bool preferOverwrite)
    {
        var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
        // 优先 browser_download_url；没有再退 url（源码包等）
        string? url = null;
        if (a.TryGetProperty("browser_download_url", out var b) && b.ValueKind == JsonValueKind.String)
            url = b.GetString();
        if (string.IsNullOrWhiteSpace(url) && a.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            url = u.GetString();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return;
        if (preferOverwrite)
            into[name] = url;
        else
            into.TryAdd(name, url);
    }

    string? FindUrl(string name)
    {
        lock (_gate) return _files.TryGetValue(name, out var url) ? url : null;
    }

    static async Task<string> DownloadStringAsync(string downloadUrl, TimeSpan timeout, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
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

/// <summary>立即更新的进度快照，设置页轮询用。</summary>
public sealed record UpdateProgressSnapshot
{
    public bool running { get; init; }
    public int percent { get; init; }
    /// <summary>checking | downloading | applying | done | error</summary>
    public string phase { get; init; } = "";
    public string? message { get; init; }
}

/// <summary>设置页的检查 / 下载更新。</summary>
public static class AppUpdate
{
    static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(25);
    static readonly TimeSpan LatestTtl = TimeSpan.FromMinutes(30);
    static readonly string LatestCachePath = Path.Combine(AgentHubConfig.Dir, "update.latest.json");
    static readonly object CacheGate = new();
    static readonly object ProgressGate = new();
    static string? _cachedLatest;
    static DateTimeOffset _cachedAt;
    static int _busy;
    static UpdateProgressSnapshot _progress = new();

    public static bool Busy => Volatile.Read(ref _busy) != 0;

    public static UpdateProgressSnapshot Progress
    {
        get { lock (ProgressGate) return _progress; }
    }

    static void SetProgress(bool running, int percent, string phase, string? message = null)
    {
        lock (ProgressGate)
            _progress = new UpdateProgressSnapshot
            {
                running = running,
                percent = Math.Clamp(percent, 0, 100),
                phase = phase,
                message = message,
            };
    }

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

    public static UpdateManager CreateManager() => new(new GiteeApiUpdateSource());

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
        SetProgress(true, 0, "checking", "正在检查更新…");
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled)
            {
                var (found, probeError) = await ProbeLatestAsync().ConfigureAwait(false);
                if (found is null)
                {
                    var err = probeError ?? "没有查到可用版本。";
                    SetProgress(false, 0, "error", err);
                    return Fail(err, installed: false, CurrentVersion);
                }
                SetProgress(false, 0, "done", "当前不是安装版，请手动下载。");
                return Compose(installed: false, CurrentVersion, found);
            }

            var info = await AwaitTimeout(mgr.CheckForUpdatesAsync(), CheckTimeout).ConfigureAwait(false);
            if (info is null)
            {
                SetProgress(false, 0, "done", "已是最新版本。");
                return new AppUpdateStatus { installed = true, current = CurrentVersion, message = "已是最新版本。" };
            }

            var latest = info.TargetFullRelease.Version.ToString();
            SetProgress(true, 0, "downloading", "正在下载更新…");

            await mgr.DownloadUpdatesAsync(info, p =>
            {
                progress?.Invoke(p);
                SetProgress(true, p, "downloading", $"正在下载更新… {p}%");
            }, ct).ConfigureAwait(false);

            SetProgress(true, 100, "applying", "正在应用更新…");
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
            var err = Humanize(ex);
            SetProgress(false, 0, "error", err);
            return Fail(err, installed: true, CurrentVersion);
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
            var latest = await AwaitTimeout(GiteeApiUpdateSource.FetchLatestTagAsync(), CheckTimeout)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(latest))
            {
                RememberLatest(latest);
                return (latest, null);
            }
        }
        catch (Exception)
        {
            /* Gitee 失败时弱回落到 GitHub */
        }

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
        {
            // 便携/调试运行：只有真有更新才提示去下载安装包
            if (!newer)
                return new AppUpdateStatus
                {
                    installed = false,
                    current = current,
                    latest = latest,
                    releaseUrl = page,
                    message = "已是最新版本。",
                };
            return new AppUpdateStatus
            {
                installed = false,
                needsInstaller = true,
                current = current,
                latest = latest,
                releaseUrl = page,
                message = "有最新版本 " + latest,
            };
        }

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
            return "更新源请求较频繁，请稍后再检查更新。";
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