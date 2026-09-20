using AgentHub.Core.Platform;

namespace AgentHub.Shell;

/// <summary>Windows Velopack 更新：包装现有静态类。</summary>
public sealed class VelopackAppUpdateService : IAppUpdateService
{
    public bool IsSupported => true;
    public UpdateProgressSnapshot Progress => AppUpdate.Progress;
    public AppUpdateStatus Snapshot() => AppUpdate.Snapshot();
    public Task<AppUpdateStatus> CheckAsync() => AppUpdate.CheckAsync();
    public Task<AppUpdateStatus> ApplyAsync() => AppUpdate.ApplyAsync();
}
