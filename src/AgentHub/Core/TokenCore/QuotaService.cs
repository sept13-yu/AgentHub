using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>额度台账（方案 §5.2）：只画官方接口实际返回的字段。
/// DeepSeek 走 API Key（DPAPI 保护）；Cursor 走 usage-summary（登录态从 vscdb 只读提取，
/// 凭证不落盘不打日志）；Codex 走 ChatGPT backend wham/usage（auth.json OAuth）；
/// Sub2API 走 API Key 的 GET /v1/usage；WorkBuddy / Trae 先本机登录态，设置 Cookie 兜底。拿不到写原因，不写 0。</summary>
public sealed class QuotaService
{
    private readonly AgentHubConfig _config;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private HttpClient _http;
    private bool _retryHttp;

    // 缓存：额度不随区间档重拉（方案 §5.2 节流）
    private Dictionary<string, object?>? _cache;
    private long _cacheAt;
    private bool _cacheUnhealthy;
    private bool _bootstrapped;
    private string? _traeJwt;
    private string? _traeRejectedJwt;
    private long _traeJwtExp;
    private string[] _wbPackageCodes = [];
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ErrorTtl = TimeSpan.FromSeconds(3);

    /// <summary>上次额度结果落盘：下次启动先秒回旧值再后台刷新（stale-while-revalidate）。</summary>
    private static string CacheFile => Path.Combine(AgentHubConfig.Dir, "quota-last.json");

    public QuotaService(AgentHubConfig config)
    {
        _config = config;
        _http = CreateHttp();
        LoadDiskCache();
    }

    /// <summary>丢掉额度缓存。重扫后下次拉取会重建 HTTP 连接并对瞬时失败重试。</summary>
    public void InvalidateCache()
    {
        _cache = null;
        _cacheAt = 0;
        _cacheUnhealthy = true;
        _traeJwt = null;
        _traeRejectedJwt = null;
        _traeJwtExp = 0;
        _wbPackageCodes = [];
    }

    public async Task<Dictionary<string, object?>> GetCreditExpiryAsync(string id)
    {
        if (id == "trae")
        {
            if (!_config.Dashboard.ShowQuotaTrae)
                return ExpiryNone("trae");
            return await TraeExpiryAsync();
        }
        if (!_config.Dashboard.ShowQuotaWorkBuddy)
            return ExpiryNone("workbuddy");
        return await WorkBuddyExpiryAsync();
    }

    public async Task<Dictionary<string, object?>> GetQuotasAsync(bool force = false)
    {
        if (!force && TryServeCache())
            return _cache!;

        // 启动首查且盘上有上次结果：立即返回旧值（带 stale 标记），后台刷新完由前端补拉换新
        if (!force && !_bootstrapped)
        {
            _bootstrapped = true;
            if (_cache is not null)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await GetQuotasAsync(); }
                    catch (Exception) { }
                });
                return new Dictionary<string, object?>(_cache, StringComparer.Ordinal)
                {
                    ["stale"] = true,
                };
            }
        }

        await _fetchGate.WaitAsync();
        try
        {
            if (!force && TryServeCache())
                return _cache!;

            var reconnect = force || _cacheUnhealthy;
            _retryHttp = reconnect;
            if (reconnect)
                RecycleHttp();

            var dash = _config.Dashboard;
            var jobs = new List<(string Id, Task<Dictionary<string, object?>> Task)>();
            if (dash.ShowQuotaDeepSeek) jobs.Add(("deepseek", DeepSeekAsync()));
            if (dash.ShowQuotaCursor) jobs.Add(("cursor", CursorAsync()));
            if (dash.ShowQuotaCodex) jobs.Add(("codex", CodexAsync()));
            if (dash.ShowQuotaRelay) jobs.Add(("relay", RelayAsync()));
            if (dash.ShowQuotaWorkBuddy) jobs.Add(("workbuddy", WorkBuddyAsync()));
            if (dash.ShowQuotaTrae) jobs.Add(("trae", TraeAsync()));
            if (dash.ShowQuotaZcode) jobs.Add(("zcode", ZcodeAsync()));
            if (jobs.Count > 0)
                await Task.WhenAll(jobs.Select(j => j.Task));

            var sources = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            foreach (var (id, task) in jobs)
                sources[id] = task.Result;

            _cache = new Dictionary<string, object?>
            {
                ["updatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["items"] = QuotaPresenter.Flatten(sources, dash.DeriveQuotaOrder()),
            };
            _cacheAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _cacheUnhealthy = IsUnhealthy(sources);
            WriteDiskCache();
            return _cache;
        }
        finally
        {
            _retryHttp = false;
            _fetchGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // DeepSeek：GET api.deepseek.com/user/balance（无总额度 → 不画进度条）
    // ------------------------------------------------------------------

    private async Task<Dictionary<string, object?>> DeepSeekAsync()
    {
        var key = Dpapi.Unprotect(_config.Credentials.DeepSeekKey);
        if (string.IsNullOrEmpty(key))
            return Status("empty", "API Key 未配置（设置页可填，DPAPI 加密存储）");
        try
        {
            using var resp = await SendQuotaAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                return req;
            });
            if (!resp.IsSuccessStatusCode)
                return Status("error", $"接口返回 HTTP {(int)resp.StatusCode}" + ((int)resp.StatusCode == 401 ? "（Key 无效）" : ""));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            var balance = "";
            var currency = "";
            if (doc.RootElement.TryGetProperty("balance_infos", out var infos) && infos.GetArrayLength() > 0)
            {
                var info = infos[0];
                if (info.TryGetProperty("total_balance", out var b) && b.ValueKind == JsonValueKind.String) balance = b.GetString()!;
                if (info.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String) currency = c.GetString()!;
            }
            decimal? parsed = decimal.TryParse(balance, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
            return AmountOk("deepseek", parsed, currency, hasTotal: false, plan: null, unit: currency);
        }
        catch (Exception ex)
        {
            return Status("error", "请求失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Cursor：vscdb 只读取 accessToken → GET cursor.com/api/usage-summary
    // 契约探针与失效信号区分（方案 §5.1 Cursor 开关约定）
    // ------------------------------------------------------------------

    private async Task<Dictionary<string, object?>> CursorAsync()
    {
        try
        {
            if (!CursorAuth.TryCookie(out var cookie, out var authErr))
                return Status("empty", authErr ?? "未在 state.vscdb 找到登录态");

            using var resp = await SendQuotaAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary");
                req.Headers.TryAddWithoutValidation("Cookie", cookie);
                req.Headers.TryAddWithoutValidation("Referer", "https://cursor.com/settings");
                req.Headers.TryAddWithoutValidation("User-Agent", CursorAuth.UserAgent);
                return req;
            });
            if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                return Status("error", "会话过期（401/403）：打开 Cursor 重新登录");
            if (!resp.IsSuccessStatusCode)
                return Status("error", $"接口返回 HTTP {(int)resp.StatusCode}");
            var body = await resp.Content.ReadAsStringAsync();
            if (body.TrimStart().StartsWith("<"))
                return Status("error", "返回 HTML（登录墙或改版）");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var plan = root;
            if (root.TryGetProperty("individualUsage", out var iu)
                && iu.ValueKind == JsonValueKind.Object
                && iu.TryGetProperty("plan", out var p)
                && p.ValueKind == JsonValueKind.Object)
                plan = p;
            decimal used = GetNum(plan, "totalPercentUsed");
            var membership = CursorAuth.ReadMembershipType();
            var card = new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["usedPercent"] = used,
                ["remainPercent"] = 100 - used,
                ["autoPercent"] = GetNum(plan, "autoPercentUsed"),
                ["apiPercent"] = GetNum(plan, "apiPercentUsed"),
                ["cycleStart"] = GetStr(root, "billingCycleStart"),
                ["cycleEnd"] = GetStr(root, "billingCycleEnd"),
                ["note"] = GetStr(root, "autoModelSelectedDisplayMessage"),
                ["updatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            };
            if (!string.IsNullOrEmpty(membership))
                card["plan"] = membership;
            return card;
        }
        catch (Exception ex)
        {
            return Status("error", "请求失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Codex：auth.json OAuth → chatgpt.com/backend-api/wham/usage（TokenTracker usage-limits.js 移植）
    // ------------------------------------------------------------------

    private async Task<Dictionary<string, object?>> CodexAsync()
    {
        try
        {
            var token = ReadCodexToken();
            if (token is null)
                return Status("empty", "未找到 ~/.codex/auth.json 登录态");

            using var resp = await SendQuotaAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                return req;
            });
            if ((int)resp.StatusCode == 401)
                return Status("error", "登录态过期（401）：codex login 后重试");
            if (!resp.IsSuccessStatusCode)
                return Status("error", $"接口返回 HTTP {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());

            // 窗口按秒数识别（Free 档可能只有周窗，不要按字段名把周窗标成 5h，方案 §5.2）
            string Classify(JsonElement w, string fallback)
            {
                long secs = (long)GetNum(w, "window_length_seconds");
                if (secs >= 86_400 * 2) return "7d";
                if (secs > 0) return "5h";
                return fallback;
            }
            var windows = new List<Dictionary<string, object?>>();
            void AddWindow(JsonElement w, string id)
            {
                decimal used = GetNum(w, "used_percent");
                windows.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["usedPercent"] = used,
                    ["remainPercent"] = 100 - used,
                    ["resetAt"] = GetStr(w, "resets_at") ?? GetStr(w, "reset_at"),
                    ["windowSeconds"] = (long)GetNum(w, "window_length_seconds"),
                });
            }
            if (doc.RootElement.TryGetProperty("primary_window", out var pw)) AddWindow(pw, Classify(pw, "5h"));
            if (doc.RootElement.TryGetProperty("secondary_window", out var sw)) AddWindow(sw, Classify(sw, "7d"));
            if (windows.Count == 0 && doc.RootElement.TryGetProperty("windows", out var ws) && ws.ValueKind == JsonValueKind.Array)
                foreach (var w in ws.EnumerateArray())
                    AddWindow(w, Classify(w, "5h"));
            if (windows.Count == 0)
                return Status("error", "接口响应不含可识别的窗口字段");
            var codexPlan = GetStr(doc.RootElement, "plan_type")
                ?? GetStr(doc.RootElement, "planType")
                ?? GetStr(doc.RootElement, "plan_name");
            var codexCard = new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["windows"] = windows,
            };
            if (!string.IsNullOrEmpty(codexPlan))
                codexCard["plan"] = codexPlan;
            return codexCard;
        }
        catch (Exception ex)
        {
            return Status("error", "请求失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // ZCode：config.json 的 Coding Plan Key → GET /api/monitor/usage/quota/limit
    // ------------------------------------------------------------------

    private async Task<Dictionary<string, object?>> ZcodeAsync()
    {
        if (!ZcodeLocal.CodingPlanAvailable())
            return Status("empty", "Coding Plan 未开通");
        var key = ZcodeLocal.ReadCodingPlanKey();
        if (string.IsNullOrEmpty(key))
            return Status("empty", "未找到 Coding Plan API Key");
        try
        {
            using var resp = await SendQuotaAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://open.bigmodel.cn/api/monitor/usage/quota/limit");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                req.Headers.TryAddWithoutValidation("User-Agent", "AgentHub/1.0");
                return req;
            });
            if ((int)resp.StatusCode is 401 or 403)
                return Status("error", "Coding Plan Key 无效");
            if (!resp.IsSuccessStatusCode)
                return Status("error", $"接口返回 HTTP {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;
            if (!data.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
                return Status("error", "接口响应不含可识别的窗口字段");

            var windows = new List<Dictionary<string, object?>>();
            foreach (var w in limits.EnumerateArray())
            {
                var id = ClassifyZcode(w);
                if (id is null) continue;
                decimal used = GetNum(w, "percentage");
                windows.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["usedPercent"] = used,
                    ["remainPercent"] = 100 - used,
                    ["resetAt"] = ResetIso(w),
                });
            }
            if (windows.Count == 0)
                return Status("error", "接口响应不含可识别的窗口字段");
            var zcodePlan = GetStr(data, "level");
            var zcodeCard = new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["windows"] = windows,
            };
            if (!string.IsNullOrEmpty(zcodePlan))
                zcodeCard["plan"] = zcodePlan;
            return zcodeCard;
        }
        catch (Exception ex)
        {
            return Status("error", "请求失败：" + ex.Message);
        }
    }

    private static string? ClassifyZcode(JsonElement w)
    {
        var unit = (long)GetNum(w, "unit");
        var number = (long)GetNum(w, "number");
        if (unit == 3 && number == 5) return "5h";
        if (unit == 6 && number == 1) return "week";
        return null;
    }

    private static string? ResetIso(JsonElement w)
    {
        var ms = (long)GetNum(w, "nextResetTime");
        if (ms <= 0) return GetStr(w, "nextResetTime") ?? GetStr(w, "resets_at");
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ------------------------------------------------------------------
    // Sub2API：sk + GET /v1/usage
    // ------------------------------------------------------------------

    private async Task<Dictionary<string, object?>> RelayAsync()
    {
        var key = Dpapi.Unprotect(_config.Credentials.RelayKey);
        if (string.IsNullOrEmpty(key))
            return Status("empty", "填 Sub2API API Key 即可查余额");
        if (ResolveRelayUsageBase() is null)
            return Status("empty", "再填上游地址（用来拼 /v1/usage）");
        var byKey = await TryRelayKeyUsageAsync();
        return byKey ?? Status("error", "读不到 Sub2API 余额");
    }

    private async Task<Dictionary<string, object?>?> TryRelayKeyUsageAsync()
    {
        var key = Dpapi.Unprotect(_config.Credentials.RelayKey);
        if (string.IsNullOrEmpty(key)) return null;
        var baseUrl = ResolveRelayUsageBase();
        if (baseUrl is null) return null;
        try
        {
            using var resp = await SendQuotaAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/usage");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                req.Headers.TryAddWithoutValidation("User-Agent", "AgentHub/1.0");
                return req;
            });
            var code = (int)resp.StatusCode;
            if (code is 401 or 403) return Status("error", "Sub2API API Key 无效");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out _))
            {
                var msg = GetStr(root, "message") ?? GetStr(root, "error");
                return Status("error", string.IsNullOrEmpty(msg) ? "Sub2API 用量接口返回错误" : msg);
            }
            var payload = root;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                payload = data;
            if (!TryRelayRemain(payload, out var remain) && !TryRelayRemain(root, out remain))
                return null;
            var unit = GetStr(payload, "unit") ?? GetStr(root, "unit");
            var plan = GetStr(payload, "planName") ?? GetStr(root, "planName");
            return AmountOk("relay", remain, unit, hasTotal: false, plan, unit);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryRelayRemain(JsonElement el, out decimal remain)
    {
        remain = 0;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[] { "remaining", "remain", "total_available", "balance", "quota_remaining", "remaining_quota" })
        {
            if (TryGetDecimal(el, name, out remain)) return true;
        }
        if (el.TryGetProperty("quota", out var quota))
        {
            if (quota.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
                if (TryGetDecimal(el, "quota", out remain)) return true;
            }
            else if (quota.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "remaining", "remain", "available" })
                {
                    if (TryGetDecimal(quota, name, out remain)) return true;
                }
            }
        }
        if (TryGetDecimal(el, "total_granted", out var granted) && TryGetDecimal(el, "total_used", out var used))
        {
            remain = granted - used;
            return true;
        }
        return false;
    }

    private string? ResolveRelayUsageBase()
    {
        var s = (_config.Credentials.RelayPanelBaseUrl ?? "").Trim().TrimEnd('/');
        if (s.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
            s = s[..^"/v1/responses".Length].TrimEnd('/');
        else if (s.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            s = s[..^3].TrimEnd('/');
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? s
            : null;
    }


    // ------------------------------------------------------------------
    // WorkBuddy / Trae：本机登录态优先，设置 Cookie 兜底
    // ------------------------------------------------------------------

    // WorkBuddy 网关认 Edge 形态 UA；缺 Edg/ 会 401。Trae 同一串即可。
    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36 Edg/149.0.0.0";

    private const string LocalLoginHint = "本机未登录，可在设置里填 Cookie 兜底";

    private async Task<Dictionary<string, object?>> WorkBuddyAsync()
    {
        var session = WorkBuddyAuth.ResolveQuotaSession(Dpapi.Unprotect(_config.Credentials.WorkBuddySession));
        if (string.IsNullOrEmpty(session))
            return Status("empty", LocalLoginHint);
        try
        {
            using var resp = await SendQuotaAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post,
                    "https://www.workbuddy.cn/billing/meter/get-user-resource-summary")
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("accept", "application/json");
                req.Headers.TryAddWithoutValidation("user-agent", BrowserUa);
                req.Headers.TryAddWithoutValidation("x-client-platform", "web");
                req.Headers.TryAddWithoutValidation("origin", "https://www.workbuddy.cn");
                req.Headers.TryAddWithoutValidation("referer", "https://www.workbuddy.cn/");
                req.Headers.TryAddWithoutValidation("cookie", "session=" + session);
                return req;
            });
            var code = (int)resp.StatusCode;
            if (code is 401 or 403)
                return Status("error", "会话过期");
            if (!resp.IsSuccessStatusCode)
                return Status("error", $"接口返回 HTTP {code}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            if (doc.RootElement.TryGetProperty("code", out var biz) && biz.ValueKind == JsonValueKind.Number
                && biz.TryGetInt32(out var n) && n != 0)
                return Status("error", GetStr(doc.RootElement, "msg") ?? "官网返回错误");
            _wbPackageCodes = ReadPackageCodes(doc.RootElement);
            if (TrySumWorkBuddyRemain(doc.RootElement, out var remain))
                return AmountOk("workbuddy", remain, currency: null, hasTotal: false,
                    plan: WorkBuddyPlanName(doc.RootElement), unit: "积分");
            return Status("empty", "官网用量接口未返回剩余积分");
        }
        catch (Exception ex)
        {
            return Status("error", "请求失败：" + ex.Message);
        }
    }

    private async Task<Dictionary<string, object?>> TraeAsync()
    {
        if (!TraeAuth.HasCredentials(_config))
            return Status("empty", LocalLoginHint);
        try
        {
            var token = await TraeTokenAsync();
            if (string.IsNullOrEmpty(token))
                return Status("error", "会话过期");
            using var resp = await SendQuotaAsync(() => TraeEntitlementRequest(token));
            var code = (int)resp.StatusCode;
            if (code is 401 or 403)
            {
                _traeRejectedJwt = token;
                _traeJwt = null;
                _traeJwtExp = 0;
                token = await TraeTokenAsync(forceRefresh: true);
                if (string.IsNullOrEmpty(token))
                    return Status("error", "会话过期");
                using var retry = await SendQuotaAsync(() => TraeEntitlementRequest(token));
                return ParseTraeEntitlement(retry);
            }
            return ParseTraeEntitlement(resp);
        }
        catch (Exception ex)
        {
            return Status("error", "请求失败：" + ex.Message);
        }
    }

    private HttpRequestMessage TraeEntitlementRequest(string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.trae.cn/trae/api/v2/pay/user_current_entitlement_list")
        {
            Content = new StringContent(
                """{"require_usage":true,"full_data":true}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("user-agent", BrowserUa);
        req.Headers.TryAddWithoutValidation("origin", "https://www.trae.cn");
        req.Headers.TryAddWithoutValidation("referer", "https://www.trae.cn/");
        req.Headers.TryAddWithoutValidation("authorization", "Cloud-IDE-JWT " + token);
        return req;
    }

    private static Dictionary<string, object?> ParseTraeEntitlement(HttpResponseMessage resp)
    {
        var code = (int)resp.StatusCode;
        if (code is 401 or 403)
            return Status("error", "会话过期");
        if (!resp.IsSuccessStatusCode)
            return Status("error", $"接口返回 HTTP {code}");
        using var doc = JsonDocument.Parse(resp.Content.ReadAsStream());
        if (TryTraeRemain(doc.RootElement, out var remain))
            return AmountOk("trae", remain, currency: null, hasTotal: false,
                plan: DeepFirstStr(doc.RootElement, "entitlement_name", "plan_name", "product_name", "package_name"),
                unit: "积分");
        return Status("empty", "官网用量接口未返回剩余积分");
    }

    private async Task<string?> TraeTokenAsync(bool forceRefresh = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!forceRefresh && !string.IsNullOrEmpty(_traeJwt) && _traeJwtExp > now + 300)
            return _traeJwt;

        var local = TraeAuth.ReadLocalJwt();
        if (!string.IsNullOrEmpty(local) && local != _traeRejectedJwt)
        {
            _traeJwt = local;
            _traeJwtExp = JwtExp(local);
            return local;
        }

        var session = TraeAuth.SettingsSession(_config);
        if (string.IsNullOrEmpty(session))
            return null;
        using var resp = await SendQuotaAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.trae.cn/cloudide/api/v3/common/GetUserToken")
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
            req.Headers.TryAddWithoutValidation("accept", "application/json");
            req.Headers.TryAddWithoutValidation("user-agent", BrowserUa);
            req.Headers.TryAddWithoutValidation("origin", "https://www.trae.cn");
            req.Headers.TryAddWithoutValidation("referer", "https://www.trae.cn/");
            req.Headers.TryAddWithoutValidation("cookie", "X-Cloudide-Session=" + session);
            return req;
        });
        if (!resp.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        var token = GetNestedStr(doc.RootElement, "Result", "Token")
            ?? GetNestedStr(doc.RootElement, "result", "token")
            ?? GetNestedStr(doc.RootElement, "Result", "token");
        if (string.IsNullOrEmpty(token))
            return null;
        _traeJwt = token;
        _traeJwtExp = JwtExp(token);
        return token;
    }

    private static bool TrySumWorkBuddyRemain(JsonElement root, out decimal remain)
    {
        remain = 0;
        var data = root;
        if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            data = d;
        if (!data.TryGetProperty("Packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var p in pkgs.EnumerateArray())
        {
            if (TryGetDecimal(p, "CycleRemainCapacity", out var v))
                remain += v;
        }
        return true;
    }

    private static string[] ReadPackageCodes(JsonElement root)
    {
        var data = root;
        if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            data = d;
        if (!data.TryGetProperty("Packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var p in pkgs.EnumerateArray())
        {
            var code = GetStr(p, "PackageCode");
            if (!string.IsNullOrEmpty(code) && !list.Contains(code, StringComparer.Ordinal))
                list.Add(code);
        }
        return [.. list];
    }

    private async Task<Dictionary<string, object?>> WorkBuddyExpiryAsync()
    {
        var session = WorkBuddyAuth.ResolveQuotaSession(Dpapi.Unprotect(_config.Credentials.WorkBuddySession));
        if (string.IsNullOrEmpty(session))
            return ExpiryErr("workbuddy", LocalLoginHint);
        try
        {
            var codes = _wbPackageCodes;
            if (codes.Length == 0)
            {
                using var sum = await SendQuotaAsync(() => WorkBuddyRequest(
                    "/billing/meter/get-user-resource-summary", session, "{}"));
                if ((int)sum.StatusCode is 401 or 403)
                    return ExpiryErr("workbuddy", "会话过期");
                if (!sum.IsSuccessStatusCode)
                    return ExpiryErr("workbuddy", $"接口返回 HTTP {(int)sum.StatusCode}");
                using var sumDoc = JsonDocument.Parse(await sum.Content.ReadAsStreamAsync());
                codes = ReadPackageCodes(sumDoc.RootElement);
                _wbPackageCodes = codes;
            }
            if (codes.Length == 0)
                return ExpiryNone("workbuddy");

            var today = DateTime.Today;
            var payload = JsonSerializer.Serialize(new
            {
                PageNumber = 1,
                PageSize = 200,
                Status = new[] { 0 },
                SlicePeriodStartTime = today.ToString("yyyy-MM-dd") + " 00:00:00",
                SlicePeriodEndTime = today.ToString("yyyy-MM-dd") + " 23:59:59",
                PackageCodes = codes,
            });
            var rows = new List<(DateOnly Date, decimal Amount)>();
            foreach (var path in new[]
            {
                "/billing/meter/get-user-resource-free-packages",
                "/billing/meter/get-user-resource-paid-packages",
            })
            {
                using var resp = await SendQuotaAsync(() => WorkBuddyRequest(path, session, payload));
                if ((int)resp.StatusCode is 401 or 403)
                    return ExpiryErr("workbuddy", "会话过期");
                if (!resp.IsSuccessStatusCode)
                    continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
                CollectWorkBuddyDue(doc.RootElement, rows);
            }
            return ExpiryFromRows("workbuddy", rows);
        }
        catch (Exception ex)
        {
            return ExpiryErr("workbuddy", "请求失败：" + ex.Message);
        }
    }

    private HttpRequestMessage WorkBuddyRequest(string path, string session, string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "https://www.workbuddy.cn" + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("accept", "application/json");
        req.Headers.TryAddWithoutValidation("user-agent", BrowserUa);
        req.Headers.TryAddWithoutValidation("x-client-platform", "web");
        req.Headers.TryAddWithoutValidation("origin", "https://www.workbuddy.cn");
        req.Headers.TryAddWithoutValidation("referer", "https://www.workbuddy.cn/profile/plans-usage");
        req.Headers.TryAddWithoutValidation("cookie", "session=" + session);
        return req;
    }

    private static void CollectWorkBuddyDue(JsonElement root, List<(DateOnly Date, decimal Amount)> rows)
    {
        var data = root;
        if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            data = d;
        if (!data.TryGetProperty("Accounts", out var accs) || accs.ValueKind != JsonValueKind.Array)
            return;
        foreach (var a in accs.EnumerateArray())
        {
            if (a.TryGetProperty("Status", out var st) && st.ValueKind == JsonValueKind.Number
                && st.TryGetInt32(out var n) && n != 0)
                continue;
            if (!TryGetDecimal(a, "CycleCapacityRemainPrecise", out var remain)
                && !TryGetDecimal(a, "CycleCapacityRemain", out remain))
                continue;
            var raw = GetStr(a, "CycleEndTime");
            if (string.IsNullOrEmpty(raw)
                || !DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
                continue;
            rows.Add((DateOnly.FromDateTime(dt), remain));
        }
    }

    private async Task<Dictionary<string, object?>> TraeExpiryAsync()
    {
        if (!TraeAuth.HasCredentials(_config))
            return ExpiryErr("trae", LocalLoginHint);
        try
        {
            var token = await TraeTokenAsync();
            if (string.IsNullOrEmpty(token))
                return ExpiryErr("trae", "会话过期");
            using var resp = await SendQuotaAsync(() => TraeEntitlementRequest(token));
            if ((int)resp.StatusCode is 401 or 403)
            {
                _traeRejectedJwt = token;
                _traeJwt = null;
                _traeJwtExp = 0;
                token = await TraeTokenAsync(forceRefresh: true);
                if (string.IsNullOrEmpty(token))
                    return ExpiryErr("trae", "会话过期");
                using var retry = await SendQuotaAsync(() => TraeEntitlementRequest(token));
                return ParseTraeExpiry(retry);
            }
            return ParseTraeExpiry(resp);
        }
        catch (Exception ex)
        {
            return ExpiryErr("trae", "请求失败：" + ex.Message);
        }
    }

    private static Dictionary<string, object?> ParseTraeExpiry(HttpResponseMessage resp)
    {
        if ((int)resp.StatusCode is 401 or 403)
            return ExpiryErr("trae", "会话过期");
        if (!resp.IsSuccessStatusCode)
            return ExpiryErr("trae", $"接口返回 HTTP {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(resp.Content.ReadAsStream());
        var rows = new List<(DateOnly Date, decimal Amount)>();
        CollectTraeDue(doc.RootElement, rows);
        return ExpiryFromRows("trae", rows);
    }

    private static void CollectTraeDue(JsonElement el, List<(DateOnly Date, decimal Amount)> rows)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("user_entitlement_pack_list", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in list.EnumerateArray())
                    AddTraePack(p, rows);
                return;
            }
            foreach (var prop in el.EnumerateObject())
                CollectTraeDue(prop.Value, rows);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                CollectTraeDue(item, rows);
        }
    }

    private static void AddTraePack(JsonElement p, List<(DateOnly Date, decimal Amount)> rows)
    {
        if (p.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number
            && st.TryGetInt32(out var n) && n != 0)
            return;
        decimal limit = 0, used = 0;
        if (p.TryGetProperty("entitlement_base_info", out var baseInfo)
            && baseInfo.ValueKind == JsonValueKind.Object)
        {
            if (baseInfo.TryGetProperty("quota", out var q) && q.ValueKind == JsonValueKind.Object)
                TryGetDecimal(q, "credits_limit", out limit);
            if (limit == 0 && baseInfo.TryGetProperty("product_extra", out var extra)
                && extra.ValueKind == JsonValueKind.Object
                && extra.TryGetProperty("package_extra", out var pkg)
                && pkg.ValueKind == JsonValueKind.Object
                && pkg.TryGetProperty("quota", out var q2) && q2.ValueKind == JsonValueKind.Object)
                TryGetDecimal(q2, "credits_limit", out limit);
        }
        if (p.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            TryGetDecimal(usage, "credits_amount", out used);
        var remain = limit - used;
        if (remain <= 0) return;
        long exp = 0;
        if (p.TryGetProperty("expire_time", out var et) && et.TryGetInt64(out var e1) && e1 > 0)
            exp = e1;
        else if (p.TryGetProperty("entitlement_base_info", out var bi)
            && bi.ValueKind == JsonValueKind.Object
            && bi.TryGetProperty("end_time", out var end) && end.TryGetInt64(out var e2))
            exp = e2;
        if (exp <= 0) return;
        var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime().DateTime);
        rows.Add((day, remain));
    }

    private static Dictionary<string, object?> ExpiryFromRows(string id, List<(DateOnly Date, decimal Amount)> rows)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var bag = new Dictionary<DateOnly, decimal>();
        foreach (var (d, a) in rows)
        {
            if (a <= 0 || d < today) continue;
            bag[d] = bag.GetValueOrDefault(d) + a;
        }
        if (bag.Count == 0)
            return ExpiryNone(id);
        var day = bag.Keys.Min();
        return ExpiryOk(id, day, bag[day]);
    }

    private static Dictionary<string, object?> ExpiryOk(string id, DateOnly date, decimal amount) => new()
    {
        ["id"] = id,
        ["date"] = date.ToString("yyyy-MM-dd"),
        ["amount"] = amount,
    };

    private static Dictionary<string, object?> ExpiryNone(string id) => new()
    {
        ["id"] = id,
        ["date"] = null,
        ["amount"] = null,
    };

    private static Dictionary<string, object?> ExpiryErr(string id, string error) => new()
    {
        ["id"] = id,
        ["error"] = error,
    };

    private static bool TryTraeRemain(JsonElement root, out decimal remain)
    {
        remain = 0;
        if (FindUsageSummary(root) is not { } us)
            return false;
        if (!TryGetDecimal(us, "total_amount", out var total))
            return false;
        TryGetDecimal(us, "consumed_amount", out var used);
        remain = total - used;
        return true;
    }

    private static JsonElement? FindUsageSummary(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("usage_summary", out var us) && us.ValueKind == JsonValueKind.Object)
                return us;
            foreach (var p in el.EnumerateObject())
            {
                var found = FindUsageSummary(p.Value);
                if (found is not null) return found;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var found = FindUsageSummary(item);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static string? GetNestedStr(JsonElement el, string a, string b)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(a, out var inner)
            || inner.ValueKind != JsonValueKind.Object)
            return null;
        return GetStr(inner, b);
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
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var n))
                return n;
        }
        catch (Exception)
        {
            return 0;
        }
        return 0;
    }

    private static Dictionary<string, object?> AmountOk(
        string key, decimal? balance, string? currency, bool hasTotal, string? plan, string? unit, decimal? total = null)
    {
        if (balance is null)
            return Status("error", "读不到余额");
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var d = new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["balance"] = balance.Value,
            ["hasTotal"] = hasTotal && total is not null,
            ["updatedAt"] = now,
        };
        if (!string.IsNullOrEmpty(currency)) d["currency"] = currency;
        if (!string.IsNullOrEmpty(unit)) d["unit"] = unit;
        if (!string.IsNullOrEmpty(plan)) d["plan"] = plan;
        if (hasTotal && total is not null) d["total"] = total.Value;
        return d;
    }

    private static bool TryGetDecimal(JsonElement el, string name, out decimal value)
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

    private static string? ReadCodexToken()
    {
        try
        {
            var authFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
            if (!File.Exists(authFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(authFile));
            if (doc.RootElement.TryGetProperty("OPENAI_API_KEY", out var k) && k.ValueKind == JsonValueKind.String)
            {
                var key = k.GetString();
                if (!string.IsNullOrEmpty(key) && key != "null") return key;
            }
            if (doc.RootElement.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Object
                && t.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String)
                return at.GetString();
            return null;
        }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------
    // 助手
    // ------------------------------------------------------------------

    private bool TryServeCache()
    {
        if (_cache is null) return false;
        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _cacheAt;
        var ttl = _cacheUnhealthy ? ErrorTtl.TotalMilliseconds : CacheTtl.TotalMilliseconds;
        return age < ttl;
    }

    // ------------------------------------------------------------------
    // 磁盘缓存（stale-while-revalidate）
    // ------------------------------------------------------------------

    private void LoadDiskCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            var disk = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(CacheFile));
            if (disk is { Count: > 0 } && disk.ContainsKey("items"))
            {
                _cache = disk;
                _cacheAt = 0;   // 视为已过期：首次请求触发后台刷新
            }
        }
        catch (Exception) { }
    }

    private void WriteDiskCache()
    {
        try
        {
            Directory.CreateDirectory(AgentHubConfig.Dir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(_cache));
        }
        catch (Exception) { }
    }

    private static bool IsUnhealthy(IReadOnlyDictionary<string, Dictionary<string, object?>> sources)
    {
        foreach (var key in new[] { "cursor", "deepseek", "codex", "relay", "trae", "workbuddy" })
        {
            if (!sources.TryGetValue(key, out var card))
                continue;
            if (card.TryGetValue("status", out var st) && st is string { } status && status == "error"
                && card.TryGetValue("reason", out var r) && r is string reason
                && IsTransientReason(reason))
                return true;
        }
        return false;
    }

    private static bool IsTransientReason(string reason) =>
        reason.Contains("请求失败", StringComparison.Ordinal)
        || reason.Contains("积极拒绝", StringComparison.Ordinal)
        || reason.Contains("无法连接", StringComparison.Ordinal)
        || reason.Contains("connection", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("timeout", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttp() => new(new SocketsHttpHandler
    {
        // 需要访问外部服务，遵循 Windows 系统代理。
        UseProxy = true,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    private void RecycleHttp()
    {
        var next = CreateHttp();
        var old = _http;
        _http = next;
        old.Dispose();
    }

    private async Task<HttpResponseMessage> SendQuotaAsync(Func<HttpRequestMessage> factory)
    {
        var attempts = _retryHttp ? 3 : 1;
        Exception? last = null;
        for (var i = 1; i <= attempts; i++)
        {
            try
            {
                using var req = factory();
                return await _http.SendAsync(req);
            }
            catch (Exception ex) when (IsTransientHttp(ex) && i < attempts)
            {
                last = ex;
                await Task.Delay(300 * i);
            }
        }
        throw last!;
    }

    private static bool IsTransientHttp(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or SocketException or IOException)
                return true;
            if (e is TaskCanceledException or OperationCanceledException)
                return true;
        }
        return false;
    }

    private static Dictionary<string, object?> Status(string status, string reason) => new()
    {
        ["status"] = status,
        ["reason"] = reason,
    };

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> a, Dictionary<string, object?> b)
    {
        foreach (var (k, v) in b) a[k] = v;
        return a;
    }

    private static decimal GetNum(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            if (v.ValueKind == JsonValueKind.Number) return (decimal)v.GetDouble();
        }
        return 0;
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>WorkBuddy 套餐名：summary 响应 data.Packages[] 里的名称字段（PascalCase，实样字段名未知，防御性挨个试）。</summary>
    private static string? WorkBuddyPlanName(JsonElement root)
    {
        var data = root;
        if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            data = d;
        if (!data.TryGetProperty("Packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var p in pkgs.EnumerateArray())
        {
            var name = GetStr(p, "PackageName") ?? GetStr(p, "ProductName")
                ?? GetStr(p, "PlanName") ?? GetStr(p, "Title");
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        return null;
    }

    /// <summary>递归深度优先找第一个命中的字符串字段（Trae entitlement 结构未知，防御性读取）。</summary>
    private static string? DeepFirstStr(JsonElement el, params string[] names)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var n in names)
            {
                var s = GetStr(el, n);
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            foreach (var p in el.EnumerateObject())
            {
                var found = DeepFirstStr(p.Value, names);
                if (found is not null) return found;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var found = DeepFirstStr(item, names);
                if (found is not null) return found;
            }
        }
        return null;
    }
}

/// <summary>DPAPI 凭据保护（方案 §4.3 / §5.2：Key 只加密落盘，日志禁止出现片段）。</summary>
public static class Dpapi
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;   // 换机器/换用户解不开：当作未配置
        }
    }
}
