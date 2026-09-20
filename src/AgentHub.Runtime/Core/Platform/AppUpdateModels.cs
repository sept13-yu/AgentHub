namespace AgentHub.Core.Platform;

/// <summary>检查 / 下载更新的结果，给设置页用。属性名保持小写（前端按字段名消费）。</summary>
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

/// <summary>立即更新的进度快照，设置页轮询用。</summary>
public sealed record UpdateProgressSnapshot
{
    public bool running { get; init; }
    public int percent { get; init; }
    /// <summary>checking | downloading | applying | done | error</summary>
    public string phase { get; init; } = "";
    public string? message { get; init; }
}

public interface IAppUpdateService
{
    bool IsSupported { get; }
    UpdateProgressSnapshot Progress { get; }
    AppUpdateStatus Snapshot();
    Task<AppUpdateStatus> CheckAsync();
    Task<AppUpdateStatus> ApplyAsync();
}

/// <summary>无应用内更新的宿主（Backend / 未来非 Velopack 壳）。</summary>
public sealed class ManualAppUpdateService : IAppUpdateService
{
    private readonly string _version;
    public ManualAppUpdateService(string currentVersion) => _version = currentVersion;
    public bool IsSupported => false;
    public UpdateProgressSnapshot Progress => new();
    public AppUpdateStatus Snapshot() => new() { installed = false, busy = false, current = _version };
    public Task<AppUpdateStatus> CheckAsync() => Task.FromResult(new AppUpdateStatus
    {
        installed = false,
        current = _version,
        releaseUrl = ProjectLinks.LatestReleaseUrl,
        message = "当前宿主不支持应用内更新，请前往发布页手动下载",
    });
    public Task<AppUpdateStatus> ApplyAsync() => Task.FromResult(new AppUpdateStatus
    {
        installed = false,
        current = _version,
        releaseUrl = ProjectLinks.LatestReleaseUrl,
        error = "当前宿主不支持应用内更新",
    });
}

/// <summary>项目链接与发布页判定。</summary>
public static class ProjectLinks
{
    public const string RepoUrl = "https://github.com/sept13-yu/AgentHub";
    public static string LatestReleaseUrl => RepoUrl + "/releases/latest";

    public static bool IsReleasePage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/sept13-yu/AgentHub", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(uri.Host, "gitee.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/sept13-yu/AgentHub", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}

public static class RuntimeVersion
{
    /// <summary>Runtime 程序集版本三段式；Directory.Build.props 统一后与壳版本一致。</summary>
    public static string Current =>
        typeof(RuntimeVersion).Assembly.GetName().Version?.ToString(3) ?? "";
}
