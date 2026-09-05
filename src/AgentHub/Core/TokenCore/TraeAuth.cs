using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>Trae 本机登录态：读 TRAE SOLO CN 的 storage.json，解开 iCubeAuthInfo JWT。
/// 设置里的 Cookie 只作本机没有登录态时的兜底。凭证不落盘不打日志。</summary>
internal static class TraeAuth
{
    private const string AppDir = "TRAE SOLO CN";
    private const string AuthKey = "iCubeAuthInfo://icube.cloudide";
    private static readonly byte[] Magic = [0x74, 0x63, 0x05, 0x10, 0x00, 0x00];

    // TRAE 客户端 byteCrypto.js 里两段混淆后的 KDF 口令，异或还原。
    private static readonly byte[] Jg =
    [
        82, 9, 106, 213, 48, 54, 165, 56, 191, 64, 163, 158, 129, 243, 215, 251, 124, 227, 57, 130,
        155, 47, 255, 135, 52, 142, 67, 68, 196, 222, 233, 203, 84, 123, 148, 50, 166, 194, 35, 61,
        238, 76, 149, 11, 66, 250, 195, 78, 8, 46, 161, 102, 40, 217, 36, 178, 118, 91, 162, 73,
        109, 139, 209, 37,
    ];
    private static readonly byte[] Kg =
    [
        31, 221, 168, 51, 136, 7, 199, 49, 177, 18, 16, 89, 39, 128, 236, 95, 96, 81, 127, 169, 25,
        181, 74, 13, 45, 229, 122, 159, 147, 201, 156, 239, 160, 224, 59, 77, 174, 42, 245, 176,
        200, 235, 187, 60, 131, 83, 153, 97, 23, 43, 4, 126, 186, 119, 214, 38, 225, 105, 20, 99,
        85, 33, 12, 125,
    ];

    public static string StoragePath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, AppDir, "User", "globalStorage", "storage.json");
        }
    }

    public static bool HasCredentials(AgentHubConfig config) =>
        !string.IsNullOrEmpty(ReadLocalJwt()) || !string.IsNullOrEmpty(SettingsSession(config));

    public static string? SettingsSession(AgentHubConfig config) =>
        CookieValue(Dpapi.Unprotect(config.Credentials.TraeSession), "X-Cloudide-Session");

    /// <summary>本机 JWT 优先；没有再退到设置 Cookie（由调用方拿去换 token）。</summary>
    public static string? ReadLocalJwt()
    {
        try
        {
            using var auth = ReadAuthObject();
            if (auth is null) return null;
            if (!auth.RootElement.TryGetProperty("token", out var tok) || tok.ValueKind != JsonValueKind.String)
                return null;
            var jwt = tok.GetString()?.Trim();
            return string.IsNullOrEmpty(jwt) ? null : jwt;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>官网 extra_info.input_token 含 cache；剥开后三列互不重叠，合计仍是原始 input+output。</summary>
    public static void SplitInput(long rawInput, long cacheRead, long cacheWrite,
        out long input, out long read, out long write)
    {
        rawInput = Math.Max(0, rawInput);
        read = Math.Max(0, cacheRead);
        write = Math.Max(0, cacheWrite);
        read = Math.Min(rawInput, read);
        write = Math.Min(rawInput - read, write);
        input = rawInput - read - write;
    }

    public static string? CookieValue(string? raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Contains('='))
        {
            foreach (var part in raw.Split(';'))
            {
                var item = part.Trim();
                var eq = item.IndexOf('=');
                if (eq <= 0) continue;
                if (item[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return item[(eq + 1)..].Trim();
            }
        }
        return raw;
    }

    private static JsonDocument? ReadAuthObject()
    {
        var path = StoragePath;
        if (!File.Exists(path)) return null;
        using var file = JsonDocument.Parse(File.ReadAllText(path));
        if (!file.RootElement.TryGetProperty(AuthKey, out var value))
            return null;
        return ParseAuthValue(value);
    }

    private static JsonDocument? ParseAuthValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            return JsonDocument.Parse(value.GetRawText());
        if (value.ValueKind != JsonValueKind.String)
            return null;
        var trimmed = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.StartsWith('{'))
            return JsonDocument.Parse(trimmed);
        var json = DecryptBase64(trimmed);
        return json is null ? null : JsonDocument.Parse(json);
    }

    private static string? DecryptBase64(string value)
    {
        byte[] blob;
        try { blob = Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
        return DecryptBlob(blob);
    }

    private static string? DecryptBlob(byte[] blob)
    {
        var min = Magic.Length + 32 + 16;
        if (blob.Length < min) return null;
        if (!blob.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
        var salt = blob.AsSpan(Magic.Length, 32);
        var ciphertext = blob.AsSpan(Magic.Length + 32);
        if (ciphertext.Length % 16 != 0) return null;

        var secret = new byte[64];
        for (var i = 0; i < 64; i++) secret[i] = (byte)(Jg[i] ^ Kg[i]);
        var kdfBuf = new byte[128];
        SHA512.HashData(salt, kdfBuf.AsSpan(0, 64));
        secret.CopyTo(kdfBuf, 64);
        var kdfOut = SHA512.HashData(kdfBuf);
        var key = kdfOut.AsSpan(0, 16).ToArray();
        var iv = kdfOut.AsSpan(16, 16).ToArray();

        byte[] plaintext;
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var dec = aes.CreateDecryptor();
            plaintext = dec.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (plaintext.Length < 64) return null;
        var expected = plaintext.AsSpan(0, 64);
        var data = plaintext.AsSpan(64);
        if (!CryptographicOperations.FixedTimeEquals(expected, SHA512.HashData(data)))
            return null;
        return Encoding.UTF8.GetString(data);
    }
}
