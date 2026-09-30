using System.Security.Cryptography;
using System.Text;

namespace AgentHub.Core.Platform;

/// <summary>
/// 非 Windows 平台的凭据保护：AES-256-GCM + 本机随机密钥文件（Unix 下 600）。
/// 密文格式 <c>aghs1:</c> + base64(nonce(12) ‖ tag(16) ‖ ciphertext)，与 Windows DPAPI 的裸 base64 可区分。
///
/// 安全边界（有意为之，不再加防护）：只防配置文件被单独拷走与其他账号读取；
/// 密钥文件与 config.json 同目录，整个数据目录被拷到别的机器可以离线解密，同用户进程也能读到密钥。
/// 手动换/删密钥文件后需重启 AgentHub 并重填凭据。
/// </summary>
public sealed class AesGcmFileSecretProtector : ISecretProtector
{
    private const string Prefix = "aghs1:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _keyPath;
    private readonly object _gate = new();
    private byte[]? _key;

    public AesGcmFileSecretProtector(string keyPath) => _keyPath = keyPath;

    public bool IsSupported => true;

    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var key = GetOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plainBytes, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>读路径绝不创建密钥：密钥文件缺失、损坏或密文不认，一律当作「未配置」返回 null，不上抛。</summary>
    public string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        // 无前缀的按 Windows DPAPI 密文处理：本平台解不开，视为未配置。
        if (!cipher.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(cipher[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }
        if (blob.Length < NonceSize + TagSize) return null;

        byte[]? key;
        try
        {
            key = ReadKeyFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
        if (key is null) return null;

        try
        {
            var plain = new byte[blob.Length - NonceSize - TagSize];
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(
                blob.AsSpan(0, NonceSize),
                blob.AsSpan(NonceSize + TagSize),
                blob.AsSpan(NonceSize, TagSize),
                plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>加密用密钥。单进程只取一次；失败一律转成调用端能展示的 InvalidOperationException，消息不含凭据。</summary>
    private byte[] GetOrCreateKey()
    {
        lock (_gate)
        {
            if (_key is not null) return _key;
            try
            {
                var existing = ReadKeyFile();
                if (existing is not null) return _key = existing;
                return _key = CreateKeyFile();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("凭据密钥文件不可用：" + _keyPath, ex);
            }
        }
    }

    /// <summary>新建密钥：先写同目录临时文件，完整写入并关闭流后再改名发布，避免并发首次使用时读到半截密钥。</summary>
    private byte[] CreateKeyFile()
    {
        var fresh = RandomNumberGenerator.GetBytes(KeySize);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_keyPath))!);
        var tmp = _keyPath + "." + Environment.ProcessId + ".tmp";
        try
        {
            WriteKeyFile(tmp, fresh);
            try
            {
                // File.Move 目标已存在会抛 IOException，天然不覆盖。
                File.Move(tmp, _keyPath);
                return fresh;
            }
            catch (IOException) when (File.Exists(_keyPath))
            {
                // 竞争失败：用对方已发布的那把；本进程还没用它加密过任何东西，直接丢掉自己生成的。
                return ReadKeyFile() ?? throw new InvalidOperationException("凭据密钥文件不可用：" + _keyPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时文件清不掉不影响正确性
            }
        }
    }

    /// <summary>返回 null 表示文件不存在；长度不对说明密钥文件被破坏，明确报错而不是重建覆盖。</summary>
    private byte[]? ReadKeyFile()
    {
        if (!File.Exists(_keyPath)) return null;
        var bytes = File.ReadAllBytes(_keyPath);
        if (bytes.Length != KeySize)
            throw new InvalidOperationException(
                $"凭据密钥文件损坏（{bytes.Length} 字节，应为 {KeySize}）：{_keyPath}。删除该文件后可以重新填写凭据，已保存的凭据会失效。");
        return bytes;
    }

    private static void WriteKeyFile(string path, byte[] bytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        // UnixCreateMode 的 setter 在 Windows 会抛 PlatformNotSupportedException，只能在 Unix 分支设置。
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var fs = new FileStream(path, options);
        fs.Write(bytes);
        fs.Flush(flushToDisk: true);
    }
}
