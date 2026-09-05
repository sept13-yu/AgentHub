using System.Net.Http;
using System.Text;
using System.Text.Json;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>Trae 用量：本机 JWT 优先，设置 Cookie 换 token 兜底。窗口覆盖上一自然月 1 日至今。</summary>
internal static class TraeUsage
{
    private const string UsageUrl = "https://api.trae.cn/trae/api/v1/pay/query_user_usage_group_by_session";
    private const string TokenUrl = "https://api.trae.cn/cloudide/api/v3/common/GetUserToken";
    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36 Edg/149.0.0.0";
    private const int UsageType = 7;
    private const int PageSize = 50;

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseProxy = true,
        AllowAutoRedirect = false,
    }) { Timeout = TimeSpan.FromSeconds(30) };

    private static string? _jwt;
    private static string? _rejectedJwt;
    private static long _jwtExp;

    public static (List<UsageRecord>? Records, string? Error) Fetch(AgentHubConfig config)
    {
        try
        {
            return FetchAsync(config).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var msg = ex is AggregateException agg && agg.InnerException is not null
                ? agg.InnerException.Message : ex.Message;
            return (null, "Trae 用量请求失败：" + msg);
        }
    }

    private static async Task<(List<UsageRecord>? Records, string? Error)> FetchAsync(AgentHubConfig config)
    {
        var session = TraeAuth.SettingsSession(config);
        var token = await TokenAsync(session).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            return (null, "Trae 会话过期");

        var today = DateTime.Today;
        var (from, _) = UsageRange.Previous("month", today);
        var start = new DateTimeOffset(from).ToUnixTimeSeconds();
        var end = new DateTimeOffset(today.AddDays(1).AddSeconds(-1)).ToUnixTimeSeconds();

        var all = new List<UsageRecord>();
        var total = int.MaxValue;
        for (var page = 1; all.Count < total && page <= 100; page++)
        {
            var (batch, pageTotal, err) = await PageAsync(session, token, start, end, page).ConfigureAwait(false);
            if (err == "auth")
            {
                _rejectedJwt = token;
                _jwt = null;
                _jwtExp = 0;
                token = await TokenAsync(session, forceRefresh: true).ConfigureAwait(false);
                if (string.IsNullOrEmpty(token))
                    return (null, "Trae 会话过期");
                (batch, pageTotal, err) = await PageAsync(session, token, start, end, page).ConfigureAwait(false);
            }
            if (err is not null)
                return (null, err);
            _rejectedJwt = null;
            if (batch is null || batch.Count == 0)
                break;
            total = pageTotal;
            all.AddRange(batch);
            if (batch.Count < PageSize)
                break;
        }
        return (all, null);
    }

    private static async Task<(List<UsageRecord>? Records, int Total, string? Error)> PageAsync(
        string session, string token, long start, long end, int page)
    {
        var body = JsonSerializer.Serialize(new
        {
            start_time = start,
            end_time = end,
            page_size = PageSize,
            page_num = page,
            usage_type = new[] { UsageType },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, UsageUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AddBrowserHeaders(req);
        req.Headers.TryAddWithoutValidation("authorization", "Cloud-IDE-JWT " + token);
        if (!string.IsNullOrEmpty(session))
            req.Headers.TryAddWithoutValidation("cookie", "X-Cloudide-Session=" + session);

        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code is 401 or 403)
            return (null, 0, "auth");
        if (!resp.IsSuccessStatusCode)
            return (null, 0, $"Trae 用量接口返回 HTTP {code}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = Unwrap(doc.RootElement);
        var total = (int)Long(root, "total");
        if (!root.TryGetProperty("user_usage_group_by_sessions", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return ([], total, null);

        var rows = new List<UsageRecord>();
        foreach (var item in arr.EnumerateArray())
            AddRecords(item, rows);
        return (rows, total, null);
    }

    internal static void AddRecords(JsonElement item, List<UsageRecord> rows)
    {
        var sid = Str(item, "session_id");
        if (string.IsNullOrEmpty(sid)) return;
        var ts = UnixTs(item, "usage_time");
        if (item.TryGetProperty("usage_group_details", out var details)
            && details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0)
        {
            var i = 0;
            foreach (var d in details.EnumerateArray())
            {
                var model = FirstNonEmpty(Str(d, "model_display_name"), Str(d, "model_name"), Str(item, "model_name"));
                var rec = RecordFrom(d, item, sid, sid + ":" + i, ts, model);
                if (rec is not null) rows.Add(rec);
                i++;
            }
            return;
        }
        var one = RecordFrom(item, item, sid, sid, ts, Str(item, "model_name"));
        if (one is not null) rows.Add(one);
    }

    private static UsageRecord? RecordFrom(
        JsonElement row, JsonElement fallback, string sid, string key, DateTime ts, string? model)
    {
        using var extra = ExtraInfoDoc(row) ?? ExtraInfoDoc(fallback);
        var src = extra?.RootElement ?? (row.ValueKind == JsonValueKind.Object ? row : fallback);
        return Record(sid, key, ts, src, model);
    }

    private static JsonDocument? ExtraInfoDoc(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("extra_info", out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Object)
            return JsonDocument.Parse(v.GetRawText());
        if (v.ValueKind == JsonValueKind.String)
        {
            var raw = v.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JsonDocument.Parse(raw); }
            catch (JsonException) { return null; }
        }
        return null;
    }

    private static UsageRecord? Record(string sid, string key, DateTime ts, JsonElement extra, string? model)
    {
        var rawIn = Long(extra, "input_token");
        var output = Long(extra, "output_token");
        var cacheRead = Long(extra, "cache_read_token");
        var cacheWrite = Long(extra, "cache_write_token");
        TraeAuth.SplitInput(rawIn, cacheRead, cacheWrite, out var input, out cacheRead, out cacheWrite);
        if (input <= 0 && output <= 0 && cacheRead <= 0 && cacheWrite <= 0) return null;
        if (string.IsNullOrWhiteSpace(model)) model = "unknown";
        return new UsageRecord
        {
            Tool = "trae",
            SessionId = sid,
            RequestKey = key,
            TsUtc = ts,
            InputTokens = input,
            OutputTokens = output,
            CachedInputTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            Model = model.Trim(),
        };
    }

    private static async Task<string?> TokenAsync(string? session, bool forceRefresh = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!forceRefresh && !string.IsNullOrEmpty(_jwt) && _jwtExp > now + 300)
            return _jwt;

        var local = TraeAuth.ReadLocalJwt();
        if (!string.IsNullOrEmpty(local) && local != _rejectedJwt)
        {
            _jwt = local;
            _jwtExp = JwtExp(local);
            return local;
        }

        if (string.IsNullOrEmpty(session))
            return null;
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        AddBrowserHeaders(req);
        req.Headers.TryAddWithoutValidation("cookie", "X-Cloudide-Session=" + session);
        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        var token = NestedStr(doc.RootElement, "Result", "Token")
            ?? NestedStr(doc.RootElement, "result", "token")
            ?? NestedStr(doc.RootElement, "Result", "token");
        if (string.IsNullOrEmpty(token))
            return null;
        _jwt = token;
        _jwtExp = JwtExp(token);
        return token;
    }

    private static void AddBrowserHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("user-agent", BrowserUa);
        req.Headers.TryAddWithoutValidation("origin", "https://www.trae.cn");
        req.Headers.TryAddWithoutValidation("referer", "https://www.trae.cn/");
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
            return data;
        return root;
    }

    private static string? Str(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static string? FirstNonEmpty(params string?[] parts)
    {
        foreach (var p in parts)
            if (!string.IsNullOrWhiteSpace(p)) return p;
        return null;
    }

    private static long Long(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
            return 0;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var n)) return n;
            return (long)v.GetDouble();
        }
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var p) ? p : 0;
    }

    private static DateTime UnixTs(JsonElement el, string name)
    {
        var sec = Long(el, name);
        if (sec <= 0) return DateTime.UtcNow;
        return DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;
    }

    private static string? NestedStr(JsonElement el, string a, string b)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(a, out var inner)
            || inner.ValueKind != JsonValueKind.Object)
            return null;
        return Str(inner, b);
    }

    private static long JwtExp(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return 0;
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            switch (p.Length % 4)
            {
                case 2: p += "=="; break;
                case 3: p += "="; break;
            }
            using var doc = JsonDocument.Parse(Convert.FromBase64String(p));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var n) ? n : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

}
