using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Core.Platform;
using AgentHub.Core.ProxyCore;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// Qoder / Qoder CN 额度。国际版优先本机 IPC（%APPDATA%/Qoder/SharedClientCache/.info.json
/// → named pipe，JSON-RPC <c>credit/usage</c> + <c>auth/status</c>），失败再 cookie/env、
/// renderer.log 刮取、上次成功缓存。
/// 国内桌面端（product=qodercn）数据根不是 VS Code 叉的 QoderCN：
/// 先 QODER_CN_HOME / QODERCN_CONFIG_DIR，再 ~/.qoder-cn，再 Roaming/com.qodercn.app.stable。
/// 额度优先 IPC（若 CN 暴露了 .info.json / ipc），再读 main.sqlite 的 partner_plan_snapshots
/// 与 .qoder-app-status.json；不读 auth.v1.dat / 凭据库。凭据只在内存里用，不落盘不打日志。
/// 口径对齐 TokenTracker <c>src/lib/qoder-limits.js</c> 的 windows 卡。
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
        "com.qodercn.app.stable",
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

        if (site.Id == "china" && TryReadChinaLocalQuota(out var fromLocal))
        {
            if (activity is not null)
                MergeSecondary(fromLocal, activity);
            WriteLimitsCache(site, fromLocal);
            return fromLocal;
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
            ? ChinaEmptyReason()
            : "本机未检测到 Qoder 登录态（需 Qoder 在跑，走 IPC）");
    }

    // ------------------------------------------------------------------
    // IPC
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Paths：国际版 Qoder/SharedClientCache；国内版 ~/.qoder-cn + com.qodercn.app.stable
    // ------------------------------------------------------------------

    /// <summary>国内 CLI 配置根。QODER_CN_HOME 优先，其次官方 QODERCN_CONFIG_DIR，默认 ~/.qoder-cn。</summary>
    internal static string ChinaConfigHome
    {
        get
        {
            var env = ReadEnv("QODER_CN_HOME") ?? ReadEnv("QODERCN_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(env))
                return Path.GetFullPath(env);
            return Path.Combine(UserHome, ".qoder-cn");
        }
    }

    internal static string UserHome =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal static string AppSupportRoot() => PlatformPaths.RoamingAppData;

    /// <summary>国内数据根探测顺序：环境变量、~/.qoder-cn、Roaming/com.qodercn.app.stable。不含 QoderCN / Qoder。</summary>
    internal static IReadOnlyList<string> ChinaDataRoots()
    {
        var list = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path.Trim()); }
            catch (Exception) { return; }
            foreach (var existing in list)
            {
                if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            list.Add(full);
        }

        Add(ReadEnv("QODER_CN_HOME"));
        Add(ReadEnv("QODERCN_CONFIG_DIR"));
        Add(Path.Combine(UserHome, ".qoder-cn"));
        Add(Path.Combine(AppSupportRoot(), "com.qodercn.app.stable"));
        return list;
    }

    internal static bool ChinaLayoutExists()
    {
        foreach (var root in ChinaDataRoots())
        {
            if (Directory.Exists(root)) return true;
        }
        return File.Exists(Path.Combine(ChinaConfigHome, ".qoder-app-status.json"));
    }

    internal static string DataRoot(Site site)
    {
        if (site.Id == "china")
        {
            foreach (var root in ChinaDataRoots())
            {
                if (Directory.Exists(root)) return root;
            }
            return ChinaConfigHome;
        }

        var homeKey = site.EnvPrefix + "_HOME";
        var overrideHome = Environment.GetEnvironmentVariable(homeKey);
        if (!string.IsNullOrWhiteSpace(overrideHome))
            return Path.GetFullPath(overrideHome.Trim());
        if (OperatingSystem.IsMacOS())
            return Path.Combine(UserHome, "Library", "Application Support", site.AppDir);
        if (OperatingSystem.IsLinux())
            return Path.Combine(UserHome, ".config", site.AppDir);
        return Path.Combine(AppSupportRoot(), site.AppDir);
    }

    /// <summary>国际版用量库：<c>SharedClientCache/cache/db/local.db</c>。可用 <c>QODER_DB_PATH</c> 覆盖。
    /// 国内版不走此路径（jsonl 才是用量源）。不是 <c>state.vscdb</c>。</summary>
    internal static string LocalDbPath(Site site)
    {
        var dbKey = site.EnvPrefix + "_DB_PATH";
        var overrideDb = Environment.GetEnvironmentVariable(dbKey);
        if (!string.IsNullOrWhiteSpace(overrideDb))
            return Path.GetFullPath(overrideDb.Trim());
        return Path.Combine(DataRoot(site), "SharedClientCache", "cache", "db", "local.db");
    }

    private static IEnumerable<string> InfoPaths(Site site)
    {
        if (site.Id != "china")
        {
            yield return Path.Combine(DataRoot(site), "SharedClientCache", ".info.json");
            yield break;
        }

        var rel = new[]
        {
            Path.Combine("SharedClientCache", ".info.json"),
            ".info.json",
            Path.Combine("ipc", ".info.json"),
            Path.Combine("shared_client", ".info.json"),
            Path.Combine("cli", ".info.json"),
        };
        foreach (var root in ChinaDataRoots())
        {
            foreach (var r in rel)
                yield return Path.Combine(root, r);
            var ipcDir = Path.Combine(root, "ipc");
            if (!Directory.Exists(ipcDir)) continue;
            IEnumerable<string> extra;
            try { extra = Directory.EnumerateFiles(ipcDir, "*.json"); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var f in extra)
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith("auth", StringComparison.OrdinalIgnoreCase)) continue;
                yield return f;
            }
        }
    }

    private static async Task<JsonElement?> RpcAsync(Site site, string method, CancellationToken ct)
    {
        Exception? last = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var infoFile in InfoPaths(site))
        {
            if (!seen.Add(infoFile) || !File.Exists(infoFile)) continue;
            try { return await RpcOnceAsync(infoFile, method, ct).ConfigureAwait(false); }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new IOException("Qoder local service is not running.");
    }

    private static async Task<JsonElement?> RpcOnceAsync(string infoFile, string method, CancellationToken ct)
    {
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
        var resetAt = NormalizeResetAt(root, "expiresAt")
            ?? NormalizeResetAt(root, "expires_at")
            ?? NormalizeResetAt(quota, "expiresAt")
            ?? NormalizeResetAt(quota, "expires_at")
            ?? NormalizeResetAt(root, "nextResetAt")
            ?? NormalizeResetAt(root, "next_reset_at");
        // 超远日期表示「无月度重置」：保留哨兵，前端显示「不限期」，避免 period 为空挤短进度条
        if (resetAt is not null && DateTimeOffset.TryParse(resetAt, out var exp) && exp.UtcDateTime >= new DateTime(2100, 1, 1))
            resetAt = "never";
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
        var summary = Obj(totalContainer.Value, "quotaSummary") ?? Obj(totalContainer.Value, "quota_summary");
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
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(logRoot))
            roots.Add(Path.GetFullPath(logRoot.Trim()));
        else if (site.Id == "china")
        {
            foreach (var r in ChinaDataRoots())
                roots.Add(Path.Combine(r, "logs"));
        }
        else
            roots.Add(Path.Combine(DataRoot(site), "logs"));

        var files = new List<(string Path, DateTime Mtime)>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(f);
                    if (!name.Equals("renderer.log", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("main.log", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { files.Add((f, File.GetLastWriteTimeUtc(f))); }
                    catch (IOException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
    // 国内版本地额度：status json + main.sqlite partner_plan_snapshots（不读 auth.v1.dat）
    // ------------------------------------------------------------------

    private static string ChinaEmptyReason()
    {
        if (ChinaLayoutExists())
            return "已检测到 Qoder CN 本机目录（~/.qoder-cn 或 AppData\\com.qodercn.app.stable），但没有可读的 Credits 余额。国内桌面端不是 %APPDATA%\\QoderCN\\SharedClientCache。";
        return "未检测到 Qoder CN。用量读 %USERPROFILE%\\.qoder-cn\\projects\\**\\*.jsonl；额度读 com.qodercn.app.stable。可用 QODER_CN_HOME 覆盖配置目录。";
    }

    internal static bool TryReadChinaLocalQuota(out Dictionary<string, object?> card)
    {
        card = [];
        foreach (var path in ChinaStatusJsonPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (TryQuotaFromJson(doc.RootElement, out card))
                    return true;
            }
            catch (JsonException) { }
            catch (IOException) { }
        }

        foreach (var db in ChinaAppSqlitePaths())
        {
            if (!File.Exists(db)) continue;
            try
            {
                if (TryQuotaFromPartnerSnapshots(db, out card))
                    return true;
            }
            catch (Exception)
            {
                // 加密库 / 锁 / 缺表：跳过，不读 auth.v1.dat
            }
        }
        return false;
    }

    private static IEnumerable<string> ChinaStatusJsonPaths()
    {
        foreach (var root in ChinaDataRoots())
        {
            yield return Path.Combine(root, ".qoder-app-status.json");
            yield return Path.Combine(root, "qoder-app-status.json");
        }
    }

    private static IEnumerable<string> ChinaAppSqlitePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ChinaDataRoots())
        {
            var db = Path.Combine(root, "main.sqlite");
            if (seen.Add(db)) yield return db;
        }
    }

    private static bool TryQuotaFromPartnerSnapshots(string dbPath, out Dictionary<string, object?> card)
    {
        card = [];
        if (!TryCopySqlite(dbPath, "agenthub-qoder-cn-plan-", out var copy, out var tmp))
            return false;
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = copy,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using var conn = new SqliteConnection(cs.ToString());
            conn.Open();
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='partner_plan_snapshots' LIMIT 1";
                if (probe.ExecuteScalar() is null) return false;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM partner_plan_snapshots ORDER BY rowid DESC LIMIT 8";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (TryQuotaFromSnapshotRow(r, out card))
                    return true;
            }
            return false;
        }
        finally
        {
            try { if (!string.IsNullOrEmpty(tmp)) Directory.Delete(tmp, recursive: true); }
            catch (IOException) { }
        }
    }

    private static bool TryQuotaFromSnapshotRow(SqliteDataReader r, out Dictionary<string, object?> card)
    {
        card = [];
        decimal? remaining = null, used = null, total = null;
        string? plan = null;
        string? unit = null;
        for (var i = 0; i < r.FieldCount; i++)
        {
            if (r.IsDBNull(i)) continue;
            var col = r.GetName(i);
            var raw = r.GetValue(i);
            if (raw is string s && s.Length > 0)
            {
                if (LooksLikeJson(s))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(s);
                        if (TryQuotaFromJson(doc.RootElement, out card))
                            return true;
                    }
                    catch (JsonException) { }
                }
                if (IsPlanColumn(col) && !string.IsNullOrWhiteSpace(s))
                    plan ??= s.Trim();
                continue;
            }
            if (raw is not (long or int or double or float or decimal or short)) continue;
            var n = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
            if (n < 0) continue;
            if (IsRemainColumn(col)) remaining = n;
            else if (IsUsedColumn(col)) used = n;
            else if (IsTotalColumn(col)) total = n;
        }
        return TryWindowsFromParts(used, total, remaining, plan, unit, out card);
    }

    internal static bool TryQuotaFromJson(JsonElement el, out Dictionary<string, object?> card)
    {
        card = [];
        return TryQuotaFromJson(el, 0, out card);
    }

    private static bool TryQuotaFromJson(JsonElement el, int depth, out Dictionary<string, object?> card)
    {
        card = [];
        if (depth > 6) return false;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (string.IsNullOrWhiteSpace(s) || !LooksLikeJson(s)) return false;
            try
            {
                using var doc = JsonDocument.Parse(s);
                return TryQuotaFromJson(doc.RootElement, depth + 1, out card);
            }
            catch (JsonException) { return false; }
        }
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (TryQuotaFromJson(item, depth + 1, out card)) return true;
            }
            return false;
        }
        if (el.ValueKind != JsonValueKind.Object) return false;

        if (el.TryGetProperty("userQuota", out var quota) && quota.ValueKind == JsonValueKind.Object)
        {
            var usage = el;
            if (TryNormalizeRpcUsage(usage, el, null, out card))
                return true;
            if (TryWindowsFromQuotaObject(quota, PlanFromAuth(el), out card))
                return true;
        }

        var http = NormalizeHttpUsage(el);
        if (http is not null)
        {
            card = http;
            return true;
        }

        if (TryWindowsFromQuotaObject(el, Str(el, "userType") ?? Str(el, "plan") ?? Str(el, "planName"),
                requireCreditHint: true, out card))
            return true;

        foreach (var prop in el.EnumerateObject())
        {
            if (SkipQuotaWalk(prop.Name)) continue;
            if (TryQuotaFromJson(prop.Value, depth + 1, out card))
                return true;
        }
        return false;
    }

    private static bool SkipQuotaWalk(string name)
    {
        if (name.StartsWith("auth", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || name.Contains("credential", StringComparison.OrdinalIgnoreCase))
            return true;
        var n = name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return n is "chatsessions" or "chatsessionmessages" or "payloadjson" or "payload"
            or "messages" or "context" or "contextusage" or "cwd" or "content";
    }

    private static bool TryWindowsFromQuotaObject(JsonElement quota, string? plan, out Dictionary<string, object?> card)
        => TryWindowsFromQuotaObject(quota, plan, requireCreditHint: false, out card);

    private static bool TryWindowsFromQuotaObject(
        JsonElement quota, string? plan, bool requireCreditHint, out Dictionary<string, object?> card)
    {
        card = [];
        decimal? used = TryDec(quota, "used", out var u) || TryDec(quota, "usedValue", out u)
            || TryDec(quota, "used_value", out u) || TryDec(quota, "usedCredits", out u)
            ? u : null;
        decimal? total = TryDec(quota, "total", out var t) || TryDec(quota, "limitValue", out t)
            || TryDec(quota, "limit_value", out t) || TryDec(quota, "cap", out t)
            || TryDec(quota, "limit", out t) || TryDec(quota, "totalCredits", out t)
            ? t : null;
        decimal? remaining = TryDec(quota, "remaining", out var rem) || TryDec(quota, "remainingValue", out rem)
            || TryDec(quota, "remaining_value", out rem) || TryDec(quota, "remain", out rem)
            || TryDec(quota, "remainingCredits", out rem)
            ? rem : null;
        var unit = Str(quota, "unit");
        if (requireCreditHint && !LooksLikeCreditQuota(quota, plan, unit, used, total, remaining))
            return false;
        return TryWindowsFromParts(used, total, remaining, plan, unit, out card);
    }

    private static bool LooksLikeCreditQuota(
        JsonElement quota, string? plan, string? unit, decimal? used, decimal? total, decimal? remaining)
    {
        if (!string.IsNullOrWhiteSpace(unit)
            && unit.Contains("credit", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(plan)) return true;
        foreach (var name in new[]
                 { "remainingCredits", "totalCredits", "usedCredits", "remaining_credits", "total_credits" })
        {
            if (quota.TryGetProperty(name, out _)) return true;
        }
        return used is not null && total is not null && remaining is not null;
    }

    private static bool TryWindowsFromParts(
        decimal? used, decimal? total, decimal? remaining, string? plan, string? unit,
        out Dictionary<string, object?> card)
    {
        card = [];
        if (remaining is null && used is not null && total is not null)
            remaining = Math.Max(0, total.Value - used.Value);
        if (used is null && remaining is not null && total is not null)
            used = Math.Max(0, total.Value - remaining.Value);
        if (total is null && used is not null && remaining is not null)
            total = used.Value + remaining.Value;
        if (used is null || total is null || remaining is null) return false;
        if (used < 0 || total < 0 || remaining < 0) return false;
        if (total == 0 && used == 0 && remaining == 0) return false;
        var pct = total == 0 ? 0 : Clamp(used.Value / total.Value * 100);
        card = WindowsCard(plan, [
            Window("credits", pct, 100 - pct, null, remaining, unit ?? "credits"),
        ]);
        return true;
    }

    private static bool TryCopySqlite(string src, string tmpPrefix, out string db, out string tmp)
    {
        db = "";
        tmp = "";
        if (!File.Exists(src)) return false;
        tmp = Path.Combine(Path.GetTempPath(), tmpPrefix + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmp);
        try
        {
            db = Path.Combine(tmp, Path.GetFileName(src));
            using (var from = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var to = new FileStream(db, FileMode.Create, FileAccess.Write, FileShare.None))
                from.CopyTo(to);
            CopySidecar(src + "-wal", db + "-wal");
            CopySidecar(src + "-shm", db + "-shm");
            return true;
        }
        catch
        {
            try { Directory.Delete(tmp, recursive: true); }
            catch (IOException) { }
            tmp = "";
            db = "";
            throw;
        }
    }

    private static void CopySidecar(string from, string to)
    {
        if (!File.Exists(from)) return;
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    private static bool IsPlanColumn(string name)
    {
        var n = name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return n is "plan" or "planname" or "usertype" or "partner" or "product";
    }

    private static bool IsRemainColumn(string name)
    {
        var n = name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return n.Contains("remain", StringComparison.Ordinal);
    }

    private static bool IsUsedColumn(string name)
    {
        var n = name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return n.Contains("used", StringComparison.Ordinal) && !n.Contains("user", StringComparison.Ordinal);
    }

    private static bool IsTotalColumn(string name)
    {
        var n = name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return n is "total" or "totalcredits" or "cap" or "limit" or "limitvalue" or "quota";
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
