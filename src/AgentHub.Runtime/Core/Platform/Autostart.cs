namespace AgentHub.Core.Platform;

public interface IAutostartService
{
    bool IsSupported { get; }
    bool IsEnabled();
    void Enable();
    void Disable();
}

public sealed class UnsupportedAutostartService : IAutostartService
{
    public static readonly UnsupportedAutostartService Instance = new();
    public bool IsSupported => false;
    public bool IsEnabled() => false;
    public void Enable() { }
    public void Disable() { }
}
