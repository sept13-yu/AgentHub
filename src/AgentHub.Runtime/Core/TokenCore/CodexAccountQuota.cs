using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AgentHub.Core.CodexConfigCore;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>多 ChatGPT 账号额度源：live auth.json + 归档档案，只读解密，refresh 后回写本档案。</summary>
public sealed class CodexQuotaAccount
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Email { get; init; } = "";
    public string Plan { get; init; } = "";
    public string Identity { get; init; } = "";
    public bool IsLive { get; init; }
    public string? ProfileId { get; init; }
    public string AuthJson { get; set; } = "";
    public string? ApiKey { get; init; }

    public bool CanRefresh =>
        ApiKey is null
        && AuthJson.Length > 0
        && CodexTokenRefresh.TryGetRefreshToken(AuthJson, out _);
}

public static class CodexAccountQuota
{
    /// <summary>OAuth client_id：优先 id_token.aud，否则 Codex 官方 app。</summary>
    public const string FallbackClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    public static string LiveAuthPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

    /// <summary>live 在前，其后按档案索引；Identity 相同只留 live（标当前）。</summary>
    public static List<CodexQuotaAccount> ListAccounts(CodexAuthProfileStore? store = null)
    {
        store ??= new CodexAuthProfileStore();
        var list = new List<CodexQuotaAccount>();
        var identities = new HashSet<string>(StringComparer.Ordinal);

        var live = LoadLive();
        if (live is not null)
        {
            list.Add(live);
            if (live.Identity.Length > 0) identities.Add(live.Identity);
        }

        try
        {
            var index = store.LoadIndex();
            foreach (var meta in index.Profiles)
            {
                if (meta is null || !CodexAuthProfileStore.IsSafeId(meta.Id)) continue;
                if (meta.Identity.Length > 0 && identities.Contains(meta.Identity)) continue;
                string payload;
                try { payload = store.ReadPayload(meta.Id); }
                catch (InvalidOperationException) { continue; }
                CodexChatGptAuth.TryParse(payload, out var info, out _);
                var identity = info?.Identity ?? meta.Identity;
                if (identity.Length > 0 && identities.Contains(identity)) continue;
                if (identity.Length > 0) identities.Add(identity);
                var email = info?.Email.Length > 0 ? info.Email : meta.Email;
                var plan = info?.Plan.Length > 0 ? info.Plan : meta.Plan;
                list.Add(new CodexQuotaAccount
                {
                    Key = meta.Id,
                    Label = LabelOf(email, meta.Name, plan, isLive: false),
                    Email = email,
                    Plan = plan,
                    Identity = identity,
                    IsLive = false,
                    ProfileId = meta.Id,
                    AuthJson = payload,
                });
            }
        }
        catch (InvalidOperationException)
        {
            // 索引损坏：仍展示 live，不拖垮整卡
        }

        return list;
    }

    private static CodexQuotaAccount? LoadLive()
    {
        try
        {
            if (!File.Exists(LiveAuthPath)) return null;
            var text = File.ReadAllText(LiveAuthPath);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("OPENAI_API_KEY", out var k) && k.ValueKind == JsonValueKind.String)
            {
                var key = k.GetString();
                if (!string.IsNullOrEmpty(key) && key != "null")
                {
                    return new CodexQuotaAccount
                    {
                        Key = "live",
                        Label = "API Key",
                        IsLive = true,
                        ApiKey = key,
                        AuthJson = text,
                    };
                }
            }

            if (!root.TryGetProperty("tokens", out var t) || t.ValueKind != JsonValueKind.Object)
                return null;
            CodexChatGptAuth.TryParse(text, out var info, out _);
            var email = info?.Email ?? "";
            return new CodexQuotaAccount
            {
                Key = "live",
                Label = LabelOf(email, "当前", info?.Plan ?? "", isLive: true),
                Email = email,
                Plan = info?.Plan ?? "",
                Identity = info?.Identity ?? "",
                IsLive = true,
                AuthJson = text,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string LabelOf(string email, string name, string plan, bool isLive)
    {
        if (email.Length > 0)
        {
            var at = email.IndexOf('@');
            var shortName = at > 0 ? email[..at] : email;
            return isLive ? shortName + " · 当前" : shortName;
        }
        if (name.Length > 0) return isLive ? name + " · 当前" : name;
        if (plan.Length > 0) return isLive ? plan + " · 当前" : plan;
        return isLive ? "当前登录" : "ChatGPT 账号";
    }

    public static string? GetAccessToken(CodexQuotaAccount account)
    {
        if (account.ApiKey is not null) return account.ApiKey;
        return CodexTokenRefresh.TryGetAccessToken(account.AuthJson, out var at) ? at : null;
    }

    /// <summary>token 快过期或接口 401 时刷新；成功则更新 account.AuthJson 并回写 live / 档案。</summary>
    public static async Task<bool> TryRefreshAsync(
        CodexQuotaAccount account,
        Func<Func<HttpRequestMessage>, Task<HttpResponseMessage>> send,
        CodexAuthProfileStore store)
    {
        if (!account.CanRefresh) return false;
        if (!CodexTokenRefresh.TryGetRefreshToken(account.AuthJson, out var refresh)) return false;
        var clientId = CodexTokenRefresh.ClientIdFromAuth(account.AuthJson) ?? FallbackClientId;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = clientId,
        };
        try
        {
            using var resp = await send(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "https://auth.openai.com/oauth/token")
                {
                    Content = new FormUrlEncodedContent(form),
                };
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                return req;
            });
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;
            var access = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("access_token", out var a)
                && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return false;
            var newRefresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : refresh;
            var newId = root.TryGetProperty("id_token", out var i) && i.ValueKind == JsonValueKind.String
                ? i.GetString() : null;
            var next = CodexTokenRefresh.ApplyTokens(account.AuthJson, access, newRefresh, newId);
            if (next is null) return false;
            account.AuthJson = next;
            if (account.IsLive)
            {
                WriteLiveAuth(next);
                // 当前号若已归档，同步档案，避免下次读到旧票
                SyncProfileByIdentity(store, account.Identity, next);
            }
            else if (account.ProfileId is not null)
            {
                store.WritePayload(account.ProfileId, next);
                if (account.IsLive) WriteLiveAuth(next);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SyncProfileByIdentity(CodexAuthProfileStore store, string identity, string authJson)
    {
        if (identity.Length == 0) return;
        try
        {
            var index = store.LoadIndex();
            foreach (var meta in index.Profiles)
            {
                if (meta.Identity.Length > 0 && string.Equals(meta.Identity, identity, StringComparison.Ordinal))
                    store.WritePayload(meta.Id, authJson);
            }
        }
        catch (Exception)
        {
            // 回写失败不影响本次额度
        }
    }

    private static void WriteLiveAuth(string authJson)
    {
        try
        {
            var dir = Path.GetDirectoryName(LiveAuthPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = LiveAuthPath + ".agenthub-tmp";
            var bytes = Encoding.UTF8.GetBytes(authJson);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(LiveAuthPath)) File.Replace(tmp, LiveAuthPath, destinationBackupFileName: null);
            else File.Move(tmp, LiveAuthPath);
        }
        catch (Exception)
        {
            // 本地写失败：内存里已是新票，仍可查额度
        }
    }

    /// <summary>解析 wham/usage。窗口 id 只有 5h / 7d。</summary>
    public static List<Dictionary<string, object?>> ParseWindows(JsonElement root)
    {
        var scope = root;

        long WindowSecs(JsonElement w)
        {
            var secs = (long)Num(w, "window_length_seconds");
            if (secs <= 0) secs = (long)Num(w, "limit_window_seconds");
            return secs;
        }

        string? ResetAt(JsonElement w)
        {
            var s = Str(w, "resets_at") ?? Str(w, "reset_at");
            if (!string.IsNullOrEmpty(s)) return s;
            if (w.ValueKind == JsonValueKind.Object && w.TryGetProperty("reset_at", out var ra)
                && ra.ValueKind == JsonValueKind.Number)
            {
                long unix = ra.TryGetInt64(out var i) ? i : (long)ra.GetDouble();
                if (unix > 10_000_000_000L) unix /= 1000;
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("o");
            }
            return null;
        }

        string Classify(JsonElement w, string fallback)
        {
            long secs = WindowSecs(w);
            if (secs >= 86_400 * 2) return "7d";
            if (secs > 0) return "5h";
            return fallback;
        }

        var windows = new List<Dictionary<string, object?>>();
        void AddWindow(JsonElement w, string id)
        {
            decimal used = Num(w, "used_percent");
            windows.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["usedPercent"] = used,
                ["remainPercent"] = 100 - used,
                ["resetAt"] = ResetAt(w),
                ["windowSeconds"] = WindowSecs(w),
            });
        }

        var sawRateLimit = false;
        if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            scope = rateLimit;
            sawRateLimit = true;
        }

        if (scope.TryGetProperty("primary_window", out var pw)) AddWindow(pw, Classify(pw, "5h"));
        if (scope.TryGetProperty("secondary_window", out var sw)) AddWindow(sw, Classify(sw, "7d"));
        if (windows.Count == 0 && sawRateLimit)
        {
            if (root.TryGetProperty("primary_window", out pw)) AddWindow(pw, Classify(pw, "5h"));
            if (root.TryGetProperty("secondary_window", out sw)) AddWindow(sw, Classify(sw, "7d"));
        }
        if (windows.Count == 0 && root.TryGetProperty("windows", out var ws) && ws.ValueKind == JsonValueKind.Array)
            foreach (var w in ws.EnumerateArray())
                AddWindow(w, Classify(w, "5h"));
        return windows;
    }

    public static string? ReadPlan(JsonElement root) =>
        Str(root, "plan_type") ?? Str(root, "planType") ?? Str(root, "plan_name");

    private static decimal Num(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.Number) return (decimal)v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p))
            return p;
        return 0;
    }

    private static string? Str(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        var s = v.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
