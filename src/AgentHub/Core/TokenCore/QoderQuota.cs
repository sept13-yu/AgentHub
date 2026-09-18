using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// Qoder / Qoder CN 额度：优先本机 IPC（%APPDATA%/Qoder[CN]/SharedClientCache/.info.json
/// → named pipe，JSON-RPC <c>credit/usage</c> + <c>auth/status</c>），失败再 cookie/env、
/// renderer.log 刮取、上次成功缓存。凭据只在内存里用，不落盘不打日志。
/// 口径对齐 TokenTracker <c>src/lib/qoder-limits.js</c>。
/// </summary>
internal static class QoderQuota
{
    internal sealed record Site(
        string Id,
        string AppDir,
        string EnvPrefix,
        string Origin,
        string UsageUrl,
        string ActivityUrl,
        string CacheNamespace);

    internal static readonly Site International = new(
        "international",
        "Qoder",
        "QODER",
        "https://qoder.com",
        "https://qoder.com/api/v2/me/usages/big_model_credits",
        "https://openapi.qoder.sh/algo/api/v2/activity",
        "intl");

    internal static readonly Site China = new(
        "china",
        "QoderCN",
        "QODER_CN",
        "https://qoder.com.cn",
        "https://qoder.com.cn/api/v2/me/usages/big_model_credits",
        "https://openapi.qoder.com.cn/algo/api/v2/activity",
        "cn");

    private const string UltimateActivityId = "ultimate_200_free_invoke";
    private const string CosyVersion = "1.0.22";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan LimitsCacheMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan UnknownResetTtl = TimeSpan.FromHours(12);
    private const string CosyPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDA8iMH5c02LilrsERw9t6Pv5Nc
        4k6Pz1EaDicBMpdpxKduSZu5OANqUq8er4GM95omAGIOPOh+Nx0spthYA2BqGz+l
        6HRkPJ7S236FZz73In/KVuLnwI8JJ2CbuJap8kvheCCZpmAWpb/cPx/3Vr/J6I17
        XcW+ML9FoCI6AOvOzwIDAQAB
        -----END PUBLIC KEY-----
        """;

    public static Task<Dictionary<string, object?>> FetchInternationalAsync(
        HttpClient http, CancellationToken ct) => FetchAsync(International, http, ct);

    public static Task<Dictionary<string, object?>> FetchChinaAsync(
        HttpClient http, CancellationToken ct) => FetchAsync(China, http, ct);

    internal static async Task<Dictionary<string, object?>> FetchAsync(
        Site site, HttpClient http, CancellationToken ct)
    {
        JsonElement? rpcUsage = null;
        JsonElement? rpcAuth = null;
        Dictionary<string, object?>? activity = null;

        // 本机服务一次只稳接一发；并行会间歇超时。
        try { rpcUsage = await RpcAsync(site, "credit/usage", ct).ConfigureAwait(false); }
        catch (Exception) { /* 下面走缓存 / 兜底 */ }
        try { rpcAuth = await RpcAsync(site, "auth/status", ct).ConfigureAwait(false); }
        catch (Exception) { /* 计划名 / 活动接口可缺 */ }

        if (rpcAuth is { } authEl)
        {
            try
            {
                activity = await FetchActivityAsync(site, authEl, http, ct).ConfigureAwait(false);
                if (activity is not null)
                    WriteActivityCache(site, activity);
            }
            catch (Exception)
            {
                activity = null;
            }
        }
        activity ??= ReadActivityCache(site);

        if (TryNormalizeRpcUsage(rpcUsage, rpcAuth, activity, out var live))
        {
            WriteLimitsCache(site, live);
            return live;
        }

        if (ReadLimitsCache(site) is { } cached)
        {
            if (activity is not null)
                MergeSecondary(cached, activity);
            return cached;
        }

        var cookie = ReadEnv(site.EnvPrefix + "_COOKIE");
        if (!string.IsNullOrEmpty(cookie))
        {
            try
            {
                var httpCard = await FetchCookieUsageAsync(site, cookie, http, ct).ConfigureAwait(false);
                if (httpCard is not null
                    && string.Equals(httpCard.GetValueOrDefault("status") as string, "ok", StringComparison.Ordinal))
                {
                    if (activity is not null)
                        MergeSecondary(httpCard, activity);
                    WriteLimitsCache(site, httpCard);
                    return httpCard;
                }
            }
            catch (Exception)
            {
                // cookie 失效不写原因细节（可能含会话片段）
            }
        }

        if (ReadLocalQuotaLog(site) is { } fromLog)
        {
            if (activity is not null)
                MergeSecondary(fromLog, activity);
            WriteLimitsCache(site, fromLog);
            return fromLog;
        }

        if (activity is not null)
        {
            var plan = PlanFromAuth(rpcAuth);
            return WindowsCard(plan, [activity]);
        }

        return Status("empty", site.Id == "china"
            ? "本机未检测到 Qoder 国内版登录态（需 Qoder 在跑，走 IPC）"
            : "本机未检测到 Qoder 登录态（需 Qoder 在跑，走 IPC）");
    }

    // ------------------------------------------------------------------
    // IPC
    // ------------------------------------------------------------------

    internal static string DataRoot(Site site)
    {
        var homeKey = site.EnvPrefix + "_HOME";
        var overrideHome = Environment.GetEnvironmentVariable(homeKey);
        if (!string.IsNullOrWhiteSpace(overrideHome))
            return Path.GetFullPath(overrideHome.Trim());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", site.AppDir);
        if (OperatingSystem.IsLinux())
            return Path.Combine(home, ".config", site.AppDir);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
            appData = Path.Combine(home, "AppData", "Roaming");
        return Path.Combine(appData, site.AppDir);
    }

    /// <summary>用量库：<c>SharedClientCache/cache/db/local.db</c>。可用 <c>{PREFIX}_DB_PATH</c> 覆盖。
    /// 不是 <c>state.vscdb</c>（那是会话索引，不当作 token 来源）。</summary>
    internal static string LocalDbPath(Site site)
    {
        var dbKey = site.EnvPrefix + "_DB_PATH";
        var overrideDb = Environment.GetEnvironmentVariable(dbKey);
        if (!string.IsNullOrWhiteSpace(overrideDb))
            return Path.GetFullPath(overrideDb.Trim());
        return Path.Combine(DataRoot(site), "SharedClientCache", "cache", "db", "local.db");
    }

    private static string InfoPath(Site site) =>
        Path.Combine(DataRoot(site), "SharedClientCache", ".info.json");

    private static async Task<JsonElement?> RpcAsync(Site site, string method, CancellationToken ct)
    {
        var infoFile = InfoPath(site);
        if (!File.Exists(infoFile))
            throw new IOException("Qoder local service is not running.");
        string ipcPath;
        using (var doc = JsonDocument.Parse(File.ReadAllText(infoFile)))
        {
            ipcPath = (Str(doc.RootElement, "ipcServerPath") ?? "").Trim();
        }
        if (ipcPath.Length == 0)
            throw new IOException("Qoder local service endpoint is unavailable.");

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = new { },
        });
        var body = Encoding.UTF8.GetBytes(payload);
        var frame = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RpcTimeout);

        if (TryNamedPipe(ipcPath, out var pipeName))
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(body, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            return await ReadRpcFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
        }

        throw new IOException("Qoder local service endpoint is unavailable.");
    }

    internal static bool TryNamedPipe(string ipcPath, out string pipeName)
    {
        pipeName = "";
        var path = ipcPath.Trim();
        if (path.Length == 0) return false;
        const string prefix = @"\\.\pipe\";
        const string alt = @"//./pipe/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            pipeName = path[prefix.Length..];
            return pipeName.Length > 0;
        }
        if (path.StartsWith(alt, StringComparison.OrdinalIgnoreCase))
        {
            pipeName = path[alt.Length..].Replace('/', '\\');
            return pipeName.Length > 0;
        }
        if (path.Contains('\\') || path.Contains('/'))
            return false;
        pipeName = path;
        return true;
    }

    private static async Task<JsonElement?> ReadRpcFrameAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (n <= 0) break;
            buffer.Write(chunk, 0, n);
            if (buffer.Length > 4 * 1024 * 1024)
                throw new IOException("Qoder local service response is too large.");
            if (!TryParseFrame(buffer.ToArray(), out var json))
                continue;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out _))
                throw new IOException("Qoder local service request failed.");
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            return result.Clone();
        }
        throw new IOException("Qoder local service returned an invalid response.");
    }

    internal static bool TryParseFrame(byte[] data, out string json)
    {
        json = "";
        var headerEnd = IndexOf(data, "\r\n\r\n"u8);
        if (headerEnd < 0) return false;
        var header = Encoding.ASCII.GetString(data.AsSpan(0, headerEnd));
        var match = System.Text.RegularExpressions.Regex.Match(header, @"Content-Length:\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, out var len) || len < 0 || len > 4 * 1024 * 1024)
            return false;
        var bodyStart = headerEnd + 4;
        if (data.Length < bodyStart + len) return false;
        json = Encoding.UTF8.GetString(data, bodyStart, len);
        return true;
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        var span = haystack.AsSpan();
        for (var i = 0; i <= span.Length - needle.Length; i++)
        {
            if (span.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    // ------------------------------------------------------------------
    // Normalize
    // ------------------------------------------------------------------

    internal static bool TryNormalizeRpcUsage(
        JsonElement? usage, JsonElement? auth, Dictionary<string, object?>? activity,
        out Dictionary<string, object?> card)
    {
        card = [];
        if (usage is not { } root || root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("userQuota", out var quota) || quota.ValueKind != JsonValueKind.Object)
            return false;
        if (!TryDec(quota, "used", out var used)
            || !TryDec(quota, "total", out var total)
            || !TryDec(quota, "remaining", out var remaining))
            return false;
        var exceeded = Bool(root, "isQuotaExceeded");
        var reported = Num(root, "totalUsagePercentage");
        if (reported == 0) reported = Num(quota, "percentage");
        decimal usedPercent = total == 0
            ? 0
            : exceeded
                ? 100
                : reported != 0
                    ? reported
                    : used / total * 100;
        usedPercent = Clamp(usedPercent);
        var resetAt = NormalizeResetAt(root, "expiresAt") ?? NormalizeResetAt(root, "expires_at");
        if (resetAt is not null && DateTimeOffset.TryParse(resetAt, out var exp) && exp.UtcDateTime >= new DateTime(2100, 1, 1))
            resetAt = null;
        var plan = Str(root, "userType") ?? PlanFromAuth(auth);
        var unit = Str(quota, "unit") ?? "credits";
        var windows = new List<Dictionary<string, object?>>
        {
            Window("credits", usedPercent, 100 - usedPercent, resetAt, remaining, unit),
        };
        if (activity is not null) windows.Add(activity);
        card = WindowsCard(plan, windows);
        return true;
    }

    internal static Dictionary<string, object?>? NormalizeHttpUsage(JsonElement root)
    {
        var totalContainer = Obj(root, "totalQuota") ?? Obj(root, "total_quota");
        if (totalContainer is null) return null;
        var summary = Obj(totalContainer, "quotaSummary") ?? Obj(totalContainer, "quota_summary");
        if (summary is null || !TryNormalizeSummary(summary.Value, out var used, out var total, out var remaining, out var pct, out var unit))
            return null;
        var sharedContainer = Obj(root, "sharedQuota") ?? Obj(root, "shared_quota");
        var sharedSummary = sharedContainer is { } sc
            ? Obj(sc, "quotaSummary") ?? Obj(sc, "quota_summary")
            : null;
        if (sharedSummary is { } ss
            && TryNormalizeSummary(ss, out var sUsed, out var sTotal, out var sRemain, out _, out var sUnit))
        {
            used += sUsed;
            total += sTotal;
            remaining += sRemain;
            pct = total > 0 ? used / total * 100 : 100;
            unit ??= sUnit;
        }
        pct = Clamp(pct);
        var reset = NormalizeResetAt(root, "nextResetAt") ?? NormalizeResetAt(root, "next_reset_at");
        return WindowsCard(null, [
            Window("credits", pct, 100 - pct, reset, remaining, unit ?? "credits"),
        ]);
    }

    private static bool TryNormalizeSummary(
        JsonElement summary, out decimal used, out decimal total, out decimal remaining,
        out decimal pct, out string? unit)
    {
        used = total = remaining = pct = 0;
        unit = Str(summary, "unit");
        if (!TryDec(summary, "usedValue", out used) && !TryDec(summary, "used_value", out used))
            return false;
        if (!TryDec(summary, "limitValue", out total) && !TryDec(summary, "limit_value", out total))
            return false;
        if (!TryDec(summary, "remainingValue", out remaining) && !TryDec(summary, "remaining_value", out remaining))
            remaining = Math.Max(0, total - used);
        if (used < 0 || total < 0 || remaining < 0) return false;
        if (total == 0 && (used != 0 || remaining != 0)) return false;
        if (TryDec(summary, "usagePercentage", out pct) || TryDec(summary, "usage_percentage", out pct))
        { }
        else
            pct = total > 0 ? used / total * 100 : 100;
        pct = Clamp(pct);
        return true;
    }

    internal static Dictionary<string, object?>? NormalizeActivity(JsonElement root, DateTimeOffset now)
    {
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var n) && n != 0)
            return null;
        var data = Obj(root, "data");
        if (data is null || !data.Value.TryGetProperty("activities", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(Str(entry, "activityId"), UltimateActivityId, StringComparison.Ordinal))
                continue;
            if (entry.TryGetProperty("eligible", out var elig) && elig.ValueKind == JsonValueKind.False)
                continue;
            var tag = Str(entry, "tagStyle") ?? "";
            if (tag.Equals("EXPIRED", StringComparison.OrdinalIgnoreCase)) continue;
            var endAt = Num(entry, "activityEndAt");
            if (endAt > 0)
            {
                var endMs = endAt > 10_000_000_000m ? endAt : endAt * 1000;
                if (endMs <= now.ToUnixTimeMilliseconds()) continue;
            }
            if (!TryDec(entry, "used", out var used) || !TryDec(entry, "limit", out var total) || total <= 0)
                continue;
            if (!TryDec(entry, "remaining", out var remaining))
                remaining = Math.Max(0, total - used);
            if (used < 0 || remaining < 0) continue;
            var pct = Clamp(used / total * 100);
            return Window("calls", pct, 100 - pct, NormalizeResetAt(entry, "activityEndAt"), remaining, "calls");
        }
        return null;
    }

    internal static Dictionary<string, object?>? ParseQuotaLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var matches = System.Text.RegularExpressions.Regex.Matches(text,
            @"userType=([^,\s]+)[\s\S]*?isQuotaExceeded=(true|false)[\s\S]*?userQuota(?:=|\s+)used=([0-9.]+),\s*total=([0-9.]+),\s*remaining=([0-9.]+),\s*percentage=([0-9.]+),\s*unit=([^,\s]+)");
        if (matches.Count == 0) return null;
        var m = matches[^1];
        if (!decimal.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var used)
            || !decimal.TryParse(m.Groups[4].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var total)
            || !decimal.TryParse(m.Groups[5].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var remaining)
            || !decimal.TryParse(m.Groups[6].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var percentage))
            return null;
        var exceeded = m.Groups[2].Value == "true";
        var usedPercent = total == 0 ? 0 : exceeded ? 100 : Clamp(percentage);
        return WindowsCard(m.Groups[1].Value, [
            Window("credits", usedPercent, 100 - usedPercent, null, remaining, m.Groups[7].Value),
        ]);
    }

    // ------------------------------------------------------------------
    // HTTP fallbacks
    // ------------------------------------------------------------------

    private static async Task<Dictionary<string, object?>?> FetchCookieUsageAsync(
        Site site, string cookie, HttpClient http, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, site.UsageUrl);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Origin", site.Origin);
        req.Headers.TryAddWithoutValidation("Referer", site.Origin + "/account/usage");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Bx-V", "2.5.35");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code is 401 or 403) return Status("error", "Qoder 登录态过期");
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var card = NormalizeHttpUsage(doc.RootElement);
        // 0/0 活跃套餐时 Credits UI 以 renderer.log 为准（TokenTracker：limit_credits===0）。
        if (card is not null && IsZeroTotalQuota(doc.RootElement) && ReadLocalQuotaLog(site) is { } fromLog)
            return fromLog;
        return card;
    }

    private static bool IsZeroTotalQuota(JsonElement root)
    {
        var totalContainer = Obj(root, "totalQuota") ?? Obj(root, "total_quota");
        if (totalContainer is null) return false;
        var summary = Obj(totalContainer.Value, "quotaSummary") ?? Obj(totalContainer.Value, "quota_summary");
        if (summary is null) return false;
        return (TryDec(summary.Value, "limitValue", out var limit) || TryDec(summary.Value, "limit_value", out limit))
            && limit == 0;
    }

    private static async Task<Dictionary<string, object?>?> FetchActivityAsync(
        Site site, JsonElement auth, HttpClient http, CancellationToken ct)
    {
        var headers = BuildActivityHeaders(auth, DateTimeOffset.UtcNow);
        if (headers is null) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, site.ActivityUrl);
        foreach (var (k, v) in headers)
            req.Headers.TryAddWithoutValidation(k, v);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 401 or 403) return null;
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        return NormalizeActivity(doc.RootElement, DateTimeOffset.UtcNow);
    }

    /// <summary>COSY 信封只在内存构造；token 不进日志、不进缓存文件。</summary>
    internal static Dictionary<string, string>? BuildActivityHeaders(JsonElement auth, DateTimeOffset now)
    {
        var userId = (Str(auth, "id") ?? Str(auth, "accountId") ?? "").Trim();
        var accessToken = (Str(auth, "token") ?? "").Trim();
        if (userId.Length == 0 || accessToken.Length == 0) return null;

        var uuid = Guid.NewGuid().ToString("N");
        var temporaryKey = Encoding.ASCII.GetBytes(uuid[..16]);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(CosyPublicKey);
        var cosyKey = Convert.ToBase64String(rsa.Encrypt(temporaryKey, RSAEncryptionPadding.Pkcs1));

        var identity = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["name"] = Str(auth, "name") ?? "",
            ["aid"] = userId,
            ["uid"] = userId,
            ["yx_uid"] = Str(auth, "yxUid") ?? "",
            ["organization_id"] = Str(auth, "orgId") ?? "",
            ["organization_name"] = Str(auth, "orgName") ?? "",
            ["user_type"] = Str(auth, "userType") ?? "personal_standard",
            ["security_oauth_token"] = accessToken,
            ["refresh_token"] = Str(auth, "refreshToken") ?? "",
        });
        using var aes = Aes.Create();
        aes.Key = temporaryKey;
        aes.IV = temporaryKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        var plain = Encoding.UTF8.GetBytes(identity);
        var info = Convert.ToBase64String(enc.TransformFinalBlock(plain, 0, plain.Length));
        var envelope = JsonSerializer.Serialize(new
        {
            version = "v1",
            requestId = Guid.NewGuid().ToString(),
            info,
            cosyVersion = CosyVersion,
            ideVersion = "",
        });
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope));
        var cosyDate = now.ToUnixTimeSeconds().ToString();
        var signature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
            $"{payload}\n{cosyKey}\n{cosyDate}\n\n/api/v2/activity"))).ToLowerInvariant();
        var machineId = Guid.NewGuid().ToString();
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cosy-data-policy"] = "agree",
            ["cosy-machinetype"] = "5",
            ["cosy-clienttype"] = "5",
            ["cosy-date"] = cosyDate,
            ["cosy-user"] = userId,
            ["cosy-key"] = cosyKey,
            ["cache-control"] = "no-cache",
            ["cosy-business-product"] = "cli",
            ["cosy-business-type"] = "agent",
            ["cosy-scene"] = "assistant",
            ["accept"] = "application/json",
            ["authorization"] = $"Bearer COSY.{payload}.{signature}",
            ["accept-encoding"] = "identity",
            ["cosy-version"] = CosyVersion,
            ["cosy-machineid"] = machineId,
            ["cosy-machinetoken"] = machineId,
            ["login-version"] = "v2",
            ["user-agent"] = "Go-http-client/2.0",
        };
    }

    private static Dictionary<string, object?>? ReadLocalQuotaLog(Site site)
    {
        var logRoot = Environment.GetEnvironmentVariable(site.EnvPrefix + "_LOG_ROOT");
        var root = !string.IsNullOrWhiteSpace(logRoot)
            ? Path.GetFullPath(logRoot.Trim())
            : Path.Combine(DataRoot(site), "logs");
        if (!Directory.Exists(root)) return null;
        var files = new List<(string Path, DateTime Mtime)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "renderer.log", SearchOption.AllDirectories))
            {
                try { files.Add((f, File.GetLastWriteTimeUtc(f))); }
                catch (IOException) { }
            }
        }
        catch (IOException) { return null; }
        foreach (var (path, _) in files.OrderByDescending(x => x.Mtime).Take(12))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var len = Math.Min(fs.Length, 512 * 1024);
                if (len <= 0) continue;
                fs.Seek(Math.Max(0, fs.Length - len), SeekOrigin.Begin);
                var buf = new byte[len];
                var read = fs.Read(buf, 0, (int)len);
                var parsed = ParseQuotaLog(Encoding.UTF8.GetString(buf, 0, read));
                if (parsed is not null) return parsed;
            }
            catch (IOException) { }
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Last-good cache（不含凭据）
    // ------------------------------------------------------------------

    private static string LimitsCachePath(Site site) =>
        Path.Combine(AgentHubConfig.Dir, $"qoder-{site.CacheNamespace}-usage-cache.json");

    private static string ActivityCachePath(Site site) =>
        Path.Combine(AgentHubConfig.Dir, $"qoder-{site.CacheNamespace}-activity-cache.json");

    private static void WriteLimitsCache(Site site, Dictionary<string, object?> card)
    {
        if (!string.Equals(card.GetValueOrDefault("status") as string, "ok", StringComparison.Ordinal))
            return;
        try
        {
            Directory.CreateDirectory(AgentHubConfig.Dir);
            var payload = new Dictionary<string, object?>
            {
                ["cached_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["plan"] = card.GetValueOrDefault("plan"),
                ["windows"] = card.GetValueOrDefault("windows"),
            };
            var tmp = LimitsCachePath(site) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload));
            File.Move(tmp, LimitsCachePath(site), overwrite: true);
        }
        catch (Exception) { }
    }

    private static Dictionary<string, object?>? ReadLimitsCache(Site site)
    {
        try
        {
            var path = LimitsCachePath(site);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var cachedAt = DateTimeOffset.TryParse(Str(root, "cached_at"), out var at) ? at : (DateTimeOffset?)null;
            if (cachedAt is null || cachedAt > DateTimeOffset.UtcNow.AddMinutes(1)) return null;
            if (!root.TryGetProperty("windows", out var ws) || ws.ValueKind != JsonValueKind.Array)
                return null;
            var windows = new List<Dictionary<string, object?>>();
            var now = DateTimeOffset.UtcNow;
            var hasDated = false;
            foreach (var w in ws.EnumerateArray())
            {
                var reset = Str(w, "resetAt");
                if (!string.IsNullOrEmpty(reset) && DateTimeOffset.TryParse(reset, out var rs))
                {
                    if (rs <= now) continue;
                    hasDated = true;
                }
                else if (now - cachedAt.Value > UnknownResetTtl)
                    continue;
                if (!TryDec(w, "remainPercent", out var remain)) continue;
                var id = Str(w, "id") ?? "credits";
                windows.Add(Window(id, 100 - remain, remain, reset, TryDec(w, "remaining", out var rem) ? rem : null, Str(w, "unit")));
            }
            if (windows.Count == 0) return null;
            if (now - cachedAt.Value > LimitsCacheMaxAge && !hasDated) return null;
            var card = WindowsCard(Str(root, "plan"), windows);
            card["stale"] = true;
            return card;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteActivityCache(Site site, Dictionary<string, object?> window)
    {
        try
        {
            Directory.CreateDirectory(AgentHubConfig.Dir);
            var payload = new Dictionary<string, object?>
            {
                ["cached_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["window"] = window,
            };
            var tmp = ActivityCachePath(site) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload));
            File.Move(tmp, ActivityCachePath(site), overwrite: true);
        }
        catch (Exception) { }
    }

    private static Dictionary<string, object?>? ReadActivityCache(Site site)
    {
        try
        {
            var path = ActivityCachePath(site);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("window", out var w) || w.ValueKind != JsonValueKind.Object)
                return null;
            var reset = Str(w, "resetAt");
            if (!string.IsNullOrEmpty(reset) && DateTimeOffset.TryParse(reset, out var rs)
                && rs <= DateTimeOffset.UtcNow)
                return null;
            if (!TryDec(w, "remainPercent", out var remain)) return null;
            return Window(Str(w, "id") ?? "calls", 100 - remain, remain, reset,
                TryDec(w, "remaining", out var rem) ? rem : null, Str(w, "unit") ?? "calls");
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Cards
    // ------------------------------------------------------------------

    private static Dictionary<string, object?> WindowsCard(string? plan, List<Dictionary<string, object?>> windows)
    {
        var card = new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["windows"] = windows,
        };
        if (!string.IsNullOrWhiteSpace(plan)) card["plan"] = plan.Trim();
        return card;
    }

    private static Dictionary<string, object?> Window(
        string id, decimal used, decimal remain, string? resetAt, decimal? remaining, string? unit)
    {
        var w = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["usedPercent"] = used,
            ["remainPercent"] = remain,
            ["resetAt"] = resetAt,
        };
        if (remaining is not null) w["remaining"] = remaining.Value;
        if (!string.IsNullOrEmpty(unit)) w["unit"] = unit;
        return w;
    }

    private static void MergeSecondary(Dictionary<string, object?> card, Dictionary<string, object?> activity)
    {
        if (card.TryGetValue("windows", out var raw) && raw is List<Dictionary<string, object?>> list)
        {
            list.RemoveAll(w => string.Equals(w.GetValueOrDefault("id") as string, "calls", StringComparison.Ordinal));
            list.Add(activity);
        }
    }

    private static Dictionary<string, object?> Status(string status, string reason) => new()
    {
        ["status"] = status,
        ["reason"] = reason,
    };

    private static string? PlanFromAuth(JsonElement? auth) =>
        auth is { } a ? Str(a, "userType") : null;

    internal static string? NormalizeResetAt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number)
        {
            var raw = v.TryGetInt64(out var i) ? i : (long)v.GetDouble();
            if (raw <= 0) return null;
            var ms = raw > 10_000_000_000L ? raw : raw * 1000;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                var raw = (long)n;
                if (raw <= 0) return null;
                var ms = raw > 10_000_000_000L ? raw : raw * 1000;
                try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            if (DateTimeOffset.TryParse(s, out var dto))
                return dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        }
        return null;
    }

    private static string? ReadEnv(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static decimal Clamp(decimal v) => v < 0 ? 0 : v > 100 ? 100 : v;

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static JsonElement? Obj(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : null;

    private static bool Bool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static decimal Num(JsonElement el, string name) =>
        TryDec(el, name, out var d) ? d : 0;

    private static bool TryDec(JsonElement el, string name, out decimal value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out value)) return true;
        if (v.ValueKind == JsonValueKind.Number) { value = (decimal)v.GetDouble(); return true; }
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value))
            return true;
        return false;
    }
}
