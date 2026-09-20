using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AgentHub.Core.Platform;

public interface ISecretProtector
{
    /// <summary>平台是否支持加密保存。false 时 Protect 抛 InvalidOperationException，Unprotect 恒返回 null。</summary>
    bool IsSupported { get; }
    /// <summary>明文 → 密文（base64）。空串返回空串。</summary>
    string Protect(string plain);
    /// <summary>密文 → 明文。空/解不开返回 null（视为未配置）。</summary>
    string? Unprotect(string? cipher);
}

/// <summary>Windows DPAPI（CurrentUser）。密文格式与旧 Dpapi 完全一致：原始 ProtectedData 输出的 base64，不加前缀。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    public bool IsSupported => true;

    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>非 Windows 平台占位（Keychain 在后续步骤接入）。</summary>
public sealed class UnsupportedSecretProtector : ISecretProtector
{
    public static readonly UnsupportedSecretProtector Instance = new();
    public bool IsSupported => false;
    public string Protect(string plain) =>
        string.IsNullOrEmpty(plain) ? "" : throw new InvalidOperationException("当前平台尚未支持凭据加密存储");
    public string? Unprotect(string? cipher) => null;
}

/// <summary>静态门面。宿主可在启动最早期 Use() 替换实现；不调用则按平台自动选择。</summary>
public static class Secrets
{
    private static ISecretProtector _current = OperatingSystem.IsWindows()
        ? new WindowsDpapiSecretProtector()
        : UnsupportedSecretProtector.Instance;

    public static ISecretProtector Current => _current;
    public static bool IsSupported => _current.IsSupported;
    public static void Use(ISecretProtector protector) => _current = protector;
    public static string Protect(string plain) => _current.Protect(plain);
    public static string? Unprotect(string? cipher) => _current.Unprotect(cipher);
}
