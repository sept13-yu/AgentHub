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
    /// <summary>checking | downloading | verifying | ready | launching | done | error</summary>
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
    Task<AppUpdateStatus> LaunchAsync();
    void Cancel();
}

/// <summary>可检查新版本、由用户手动下载安装的宿主（Backend / Mac）。</summary>
public sealed class ManualAppUpdateService : IAppUpdateService
{
    private readonly string _version;
    public ManualAppUpdateService(string currentVersion) => _version = currentVersion;
    public bool IsSupported => true;
    public UpdateProgressSnapshot Progress => new();
    public AppUpdateStatus Snapshot() => new() { installed = false, busy = false, current = _version };
    public async Task<AppUpdateStatus> CheckAsync()
    {
        try
        {
            var feed = await UpdateFeed.GetLatestAsync().ConfigureAwait(false);
            var asset = OperatingSystem.IsMacOS()
                ? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.Arm64 => feed.assets.macArm64,
                    System.Runtime.InteropServices.Architecture.X64 => feed.assets.macX64,
                    _ => null,
                }
                : OperatingSystem.IsWindows() &&
                  System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                  System.Runtime.InteropServices.Architecture.X64
                    ? feed.assets.winX64 : null;
            if (asset is null)
                return new AppUpdateStatus { installed = false, current = _version,
                    error = "当前平台暂不支持检查更新。" };

            var newer = UpdateFeed.IsNewer(feed.version, _version);
            return new AppUpdateStatus
            {
                installed = false,
                needsInstaller = newer,
                current = _version,
                latest = feed.version,
                releaseUrl = feed.ReleasePage,
                message = newer ? "有最新版本 " + feed.version : "已是最新版本。",
            };
        }
        catch (Exception ex)
        {
            return new AppUpdateStatus { installed = false, current = _version, error = ex.Message };
        }
    }
    public Task<AppUpdateStatus> ApplyAsync() => Task.FromResult(new AppUpdateStatus
    {
        installed = false,
        current = _version,
        releaseUrl = ProjectLinks.LatestReleaseUrl,
        error = "当前宿主不支持应用内更新",
    });
    public Task<AppUpdateStatus> LaunchAsync() => ApplyAsync();
    public void Cancel() { }
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
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(uri.AbsolutePath, "/sept13-yu/AgentHub/releases", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/sept13-yu/AgentHub/releases/", StringComparison.OrdinalIgnoreCase);
    }
}

public static class RuntimeVersion
{
    /// <summary>Runtime 程序集版本三段式；Directory.Build.props 统一后与壳版本一致。</summary>
    public static string Current =>
        typeof(RuntimeVersion).Assembly.GetName().Version?.ToString(3) ?? "";
}
