using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>ZCode 本机用量与 Coding Plan Key。读库前拷三件套，避开宿主占用 WAL 时主文件为空。</summary>
internal static class ZcodeLocal
{
    private static string Home => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode");

    public static string DbPath => Path.Combine(Home, "cli", "db", "db.sqlite");
    public static string ConfigPath => Path.Combine(Home, "v2", "config.json");
    public static string CredentialsPath => Path.Combine(Home, "v2", "credentials.json");
    public static string CachePath => Path.Combine(Home, "v2", "coding-plan-cache.json");

    public static bool DbExists => File.Exists(DbPath);

    /// <summary>拷主文件 + WAL/SHM 到临时目录。调用方用完必须 <see cref="DeleteSnapshot"/>。</summary>
    public static bool TrySnapshot(out string db, out string tmp) =>
        UsageIo.TrySnapshot(DbExists ? DbPath : null, "agenthub-zcode-", "db.sqlite", out db, out tmp);

    public static void DeleteSnapshot(string? tmp) => UsageIo.DeleteSnapshot(tmp);

    public static IReadOnlyList<UsageRecord> ReadUsage()
    {
        if (!TrySnapshot(out var db, out var tmp)) return [];
        try { return ReadCopied(db); }
        finally { DeleteSnapshot(tmp); }
    }

    /// <summary>
    /// Coding Plan API Key：3.13 及更早读 v2/config.json；
    /// 3.14+ 改为 v2/credentials.json（enc:v1 AES-GCM，与 ZCode/TokenTracker 同款派生密钥）。
    /// 额度接口走 bigmodel，优先取 bigmodel 的 coding-plan key。
    /// </summary>
    public static string? ReadCodingPlanKey()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                var legacy = FindCodingPlanKey(doc.RootElement);
                if (!string.IsNullOrEmpty(legacy)) return legacy;
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return ReadCodingPlanKeyFromCredentials();
    }

    public static bool CodingPlanAvailable()
    {
        if (File.Exists(CachePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
                return !IsUnavailable(doc.RootElement, "builtin:bigmodel-coding-plan");
            }
            catch (JsonException)
            {
                return true;
            }
            catch (IOException)
            {
                return true;
            }
        }
        // 3.14+ 通常不再写 cache：有可用 key 才视为已开通
        return !string.IsNullOrEmpty(ReadCodingPlanKey());
    }

    private static string? ReadCodingPlanKeyFromCredentials()
    {
        if (!File.Exists(CredentialsPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            string? bigmodel = null;
            string? any = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // account-provider:coding-plan:account:<family>-individual-coding-plan:account:<id>:api-key
                if (!prop.Name.StartsWith("account-provider:coding-plan:", StringComparison.Ordinal))
                    continue;
                if (!prop.Name.EndsWith(":api-key", StringComparison.Ordinal))
                    continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var plain = DecryptCredential(prop.Value.GetString());
                if (string.IsNullOrWhiteSpace(plain)) continue;
                any ??= plain.Trim();
                if (prop.Name.Contains("bigmodel", StringComparison.OrdinalIgnoreCase))
                    bigmodel ??= plain.Trim();
            }
            return bigmodel ?? any;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>ZCode enc:v1：base64url(iv).base64url(tag).base64url(ciphertext)，AES-256-GCM。</summary>
    private static string? DecryptCredential(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!value.StartsWith("enc:v1:", StringComparison.Ordinal)) return value;
        var encoded = value["enc:v1:".Length..];
        var parts = encoded.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var iv = Base64UrlDecode(parts[0]);
            var tag = Base64UrlDecode(parts[1]);
            var cipher = Base64UrlDecode(parts[2]);
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(CreateCredentialSecret()));
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(iv, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string CreateCredentialSecret()
    {
        var env = Environment.GetEnvironmentVariable("ZCODE_CREDENTIAL_SECRET");
        if (!string.IsNullOrEmpty(env)) return env;
        var username = Environment.UserName ?? "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // 与 Node process.platform 对齐：win32 / darwin / linux
        var platform = OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsMacOS() ? "darwin"
            : "linux";
        return $"zcode-credential-fallback:{platform}:{home}:{username}";
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static List<UsageRecord> ReadCopied(string db)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        try { return ReadUsageRows(conn, withProvider: true); }
        catch (SqliteException)
        {
            return ReadUsageRows(conn, withProvider: false);
        }
    }

    private static List<UsageRecord> ReadUsageRows(SqliteConnection conn, bool withProvider)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = withProvider
            ? """
            SELECT logical_request_id, session_id, model_id, started_at, completed_at,
                   input_tokens, output_tokens, cache_read_input_tokens, cache_creation_input_tokens,
                   reasoning_tokens, provider_id
            FROM model_usage
            WHERE status = 'completed'
            """
            : """
            SELECT logical_request_id, session_id, model_id, started_at, completed_at,
                   input_tokens, output_tokens, cache_read_input_tokens, cache_creation_input_tokens,
                   reasoning_tokens
            FROM model_usage
            WHERE status = 'completed'
            """;
        var list = new List<UsageRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var requestKey = r.IsDBNull(0) ? "" : r.GetString(0);
            if (string.IsNullOrEmpty(requestKey)) continue;
            var session = r.IsDBNull(1) ? requestKey : r.GetString(1);
            var model = r.IsDBNull(2) || string.IsNullOrWhiteSpace(r.GetString(2))
                ? "unknown" : r.GetString(2);
            var started = ReadMs(r, 3);
            var completed = ReadMs(r, 4);
            var ts = completed ?? started;
            if (ts is null) continue;

            var input = ReadLong(r, 5);
            var output = ReadLong(r, 6);
            var cacheRead = ReadLong(r, 7);
            var cacheWrite = ReadLong(r, 8);
            var reasoning = ReadLong(r, 9);
            var providerId = withProvider && r.FieldCount > 10 && !r.IsDBNull(10) ? r.GetString(10) : "";
            // 捆绑的 Claude/Codex/Gemini 子代理已由对应解析器记账，这里丢掉避免双计。
            if (IsBundledProvider(providerId)) continue;

            // TokenTracker v0.96：ZCode 的 input 含 cache 读/写，output 含 reasoning。
            UsageParsers.SplitInclusiveTokens(input, output, cacheRead, cacheWrite, reasoning,
                out var netIn, out var netOut);

            list.Add(new UsageRecord
            {
                Tool = "zcode",
                SessionId = session,
                RequestKey = requestKey,
                TsUtc = ts.Value,
                InputTokens = netIn,
                OutputTokens = netOut,
                CachedInputTokens = cacheRead,
                CacheWriteTokens = cacheWrite,
                ReasoningTokens = reasoning,
                Model = model,
            });
        }
        return list;
    }

    private static bool IsBundledProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        var p = providerId.Trim().ToLowerInvariant();
        return p.Contains("anthropic", StringComparison.Ordinal)
            || p.Contains("openai", StringComparison.Ordinal)
            || p.Contains("google", StringComparison.Ordinal);
    }

    private static string? FindCodingPlanKey(JsonElement root)
    {
        foreach (var name in new[] { "provider", "providers" })
        {
            if (!root.TryGetProperty(name, out var prov)) continue;
            if (prov.ValueKind == JsonValueKind.Object
                && prov.TryGetProperty("builtin:bigmodel-coding-plan", out var one))
            {
                var key = KeyFromProvider(one);
                if (!string.IsNullOrEmpty(key)) return key;
            }
            if (prov.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in prov.EnumerateArray())
                {
                    var id = Str(p, "id") ?? Str(p, "providerId") ?? Str(p, "name");
                    if (id != "builtin:bigmodel-coding-plan") continue;
                    var key = KeyFromProvider(p);
                    if (!string.IsNullOrEmpty(key)) return key;
                }
            }
        }
        return null;
    }

    private static string? KeyFromProvider(JsonElement p)
    {
        if (p.TryGetProperty("options", out var opt) && opt.ValueKind == JsonValueKind.Object)
        {
            var nested = Str(opt, "apiKey") ?? Str(opt, "api_key");
            if (!string.IsNullOrEmpty(nested)) return nested;
        }
        return Str(p, "apiKey") ?? Str(p, "api_key");
    }

    private static bool IsUnavailable(JsonElement el, string id)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty(id, out var node))
                return StatusUnavailable(node);
            if (el.TryGetProperty("providers", out var providers) && providers.ValueKind == JsonValueKind.Object
                && providers.TryGetProperty(id, out var p))
                return StatusUnavailable(p);
            foreach (var prop in el.EnumerateObject())
            {
                if (StatusUnavailable(prop.Value) && (prop.Name.Contains("coding-plan", StringComparison.OrdinalIgnoreCase)
                    || prop.Name == id))
                    return true;
            }
        }
        return false;
    }

    private static bool StatusUnavailable(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String)
            return string.Equals(node.GetString(), "unavailable", StringComparison.OrdinalIgnoreCase);
        if (node.ValueKind != JsonValueKind.Object) return false;
        var status = Str(node, "status") ?? Str(node, "state") ?? Str(node, "availability");
        return string.Equals(status, "unavailable", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? EmptyToNull(v.GetString()) : null;

    private static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static long ReadLong(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return 0;
        return r.GetFieldType(i) == typeof(long) ? Math.Max(0, r.GetInt64(i)) : Math.Max(0, Convert.ToInt64(r.GetValue(i)));
    }

    private static DateTime? ReadMs(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        var ms = r.GetFieldType(i) == typeof(long) ? r.GetInt64(i) : Convert.ToInt64(r.GetValue(i));
        return UsageParsers.ParseMs(ms);
    }

}
