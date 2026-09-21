using AgentHub.Core.Platform;
using AgentHub.Shell;

namespace AgentHub.Shell;

/// <summary>Windows 开机自启：包装现有静态类。</summary>
public sealed class RegistryAutostartService : IAutostartService
{
    public bool IsSupported => true;
    public bool IsEnabled() => AutostartManager.IsEnabled();
    public void Enable() => AutostartManager.Enable();
    public void Disable() => AutostartManager.Disable();
}
