using System.Net.Http;
using System.Text.Json;

namespace AgentHub.Core.Platform;

/// <summary>完整发行版发布后写入的轻量清单；检查更新不访问 GitHub API。</summary>
public sealed record UpdateFeedManifest(
    string version,
    string releaseUrl,
    UpdateFeedAssets assets)
{
    public string ReleasePage => "https://github.com/sept13-yu/AgentHub/releases/tag/v" + version;

    public Uri DownloadUri(UpdateFeedAsset asset) =>
        new("https://github.com/sept13-yu/AgentHub/releases/download/v" + version + "/" +
            Uri.EscapeDataString(asset.fileName));
}

public sealed record UpdateFeedAssets(
    UpdateFeedAsset winX64,
    UpdateFeedAsset macArm64,
    UpdateFeedAsset macX64);

public sealed record UpdateFeedAsset(string fileName, long size, string sha256);

/// <summary>依次读取 Gitee 静态清单与镜像，短时复用结果，避免重复请求远端。</summary>
public static class UpdateFeed
{
    static readonly string[] Sources =
    [
        "https://gitee.com/sept13-yu/AgentHub/raw/update-feed/update-feed.json",
        "https://cdn.jsdelivr.net/gh/sept13-yu/AgentHub@update-feed/update-feed.json",
        "https://raw.githubusercontent.com/sept13-yu/AgentHub/update-feed/update-feed.json",
    ];

    static readonly HttpClient Http = new();
    static readonly SemaphoreSlim Gate = new(1, 1);
    static UpdateFeedManifest? _cached;
    static DateTimeOffset _cachedAt;
    static DateTimeOffset _failedAt;

    public static async Task<UpdateFeedManifest> GetLatestAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromMinutes(5))
                return _cached;
            if (DateTimeOffset.UtcNow - _failedAt < TimeSpan.FromSeconds(30))
                throw new InvalidOperationException("暂时无法检查更新，请稍后重试。");

            foreach (var source in Sources)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    using var response = await Http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await response.Content.LoadIntoBufferAsync(64 * 1024, timeout.Token).ConfigureAwait(false);
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    var manifest = await JsonSerializer.DeserializeAsync<UpdateFeedManifest>(stream,
                        cancellationToken: timeout.Token).ConfigureAwait(false);
                    if (!IsValid(manifest)) continue;

                    _cached = manifest;
                    _cachedAt = DateTimeOffset.UtcNow;
                    _failedAt = default;
                    return manifest!;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 当前源不可用或内容无效，尝试下一份同内容镜像。
                }
            }

            _failedAt = DateTimeOffset.UtcNow;
            throw new InvalidOperationException("暂时无法检查更新，请稍后重试。");
        }
        finally
        {
            Gate.Release();
        }
    }

    public static bool IsNewer(string latest, string? current)
    {
        if (!Version.TryParse(latest, out var remote)) return false;
        return !Version.TryParse(current, out var local) || remote > local;
    }

    static bool IsValid(UpdateFeedManifest? manifest)
    {
        if (manifest is null || !Version.TryParse(manifest.version, out _)) return false;
        if (!string.Equals(manifest.releaseUrl.TrimEnd('/'), manifest.ReleasePage,
                StringComparison.OrdinalIgnoreCase)) return false;

        var version = manifest.version;
        return ValidAsset(manifest.assets?.winX64, $"AgentHub-{version}-win-x64.exe")
            && ValidAsset(manifest.assets?.macArm64, $"AgentHub-{version}-mac-arm64.dmg")
            && ValidAsset(manifest.assets?.macX64, $"AgentHub-{version}-mac-x64.dmg");
    }

    static bool ValidAsset(UpdateFeedAsset? asset, string expectedName)
    {
        if (asset is null || asset.size <= 0 ||
            !string.Equals(asset.fileName, expectedName, StringComparison.Ordinal)) return false;
        return asset.sha256 is { Length: 64 } &&
            asset.sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
