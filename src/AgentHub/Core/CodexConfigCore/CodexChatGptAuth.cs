using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentHub.Core.CodexConfigCore;

/// <summary>从 Codex auth.json 解析 ChatGPT 登录态的展示信息。只读结构，绝不把 token 带出。</summary>
public sealed class CodexChatGptAuthInfo
{
    public required string RawText { get; init; }
    public string Email { get; init; } = "";
    public string Plan { get; init; } = "";
    public string AccountId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string Identity { get; init; } = "";
}

/// <summary>
/// ChatGPT 官方登录态（auth.json）的结构校验与 JWT 提示字段提取。
/// 档案导入/切换只接受带 refresh_token 的 chatgpt 形态；API Key 形态拒绝归档。
/// </summary>
public static class CodexChatGptAuth
{
    public static bool TryParse(string? text, out CodexChatGptAuthInfo? info, out string? error)
    {
        info = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "auth.json 为空";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            error = "auth.json 不是有效 JSON";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "auth.json 根节点必须是对象";
                return false;
            }

            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            {
                error = "当前不是 ChatGPT 官方登录（缺少 tokens），无法导入为账号档案";
                return false;
            }

            var refresh = ReadString(tokens, "refresh_token");
            var access = ReadString(tokens, "access_token");
            var idToken = ReadString(tokens, "id_token");
            if (refresh.Length == 0)
            {
                error = "当前 auth.json 没有 refresh_token，导入后无法在切换时恢复登录";
                return false;
            }
            if (access.Length == 0 && idToken.Length == 0)
            {
                error = "当前 auth.json 的 tokens 不完整（缺少 access_token / id_token）";
                return false;
            }

            string email = "", plan = "", userId = "", jwtAccountId = "";
            if (idToken.Length > 0)
                ReadJwtHints(idToken, ref email, ref plan, ref userId, ref jwtAccountId);
            if ((email.Length == 0 || plan.Length == 0 || userId.Length == 0 || jwtAccountId.Length == 0)
                && access.Length > 0 && access.Count(c => c == '.') >= 2)
                ReadJwtHints(access, ref email, ref plan, ref userId, ref jwtAccountId);

            var accountId = ReadString(tokens, "account_id");
            if (accountId.Length == 0) accountId = jwtAccountId;

            var identity = accountId.Length > 0 ? "acct:" + accountId
                : userId.Length > 0 ? "user:" + userId
                : email.Length > 0 ? "email:" + email.ToLowerInvariant()
                : "sha:" + Sha256Hex(refresh);

            info = new CodexChatGptAuthInfo
            {
                RawText = text,
                Email = email,
                Plan = plan,
                AccountId = accountId,
                UserId = userId,
                Identity = identity,
            };
            return true;
        }
    }

    private static void ReadJwtHints(string jwt, ref string email, ref string plan, ref string userId, ref string accountId)
    {
        if (!TryDecodeJwtPayload(jwt, out var payload)) return;
        using (payload)
        {
            var root = payload.RootElement;
            if (email.Length == 0)
            {
                email = ReadString(root, "email");
                if (email.Length == 0 && root.TryGetProperty("https://api.openai.com/profile", out var profile)
                    && profile.ValueKind == JsonValueKind.Object)
                    email = ReadString(profile, "email");
            }

            JsonElement auth = default;
            var hasAuth = root.TryGetProperty("https://api.openai.com/auth", out auth)
                && auth.ValueKind == JsonValueKind.Object;
            if (plan.Length == 0)
            {
                if (hasAuth) plan = ReadString(auth, "chatgpt_plan_type");
                if (plan.Length == 0) plan = ReadString(root, "chatgpt_plan_type");
            }
            if (userId.Length == 0)
            {
                if (hasAuth)
                {
                    userId = ReadString(auth, "chatgpt_user_id");
                    if (userId.Length == 0) userId = ReadString(auth, "user_id");
                }
            }
            if (accountId.Length == 0 && hasAuth)
                accountId = ReadString(auth, "chatgpt_account_id");
        }
    }

    private static bool TryDecodeJwtPayload(string jwt, out JsonDocument payload)
    {
        payload = null!;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return false;
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 0: break;
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
            default: return false;
        }
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                return false;
            }
            payload = doc;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return "";
        return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "").Trim() : "";
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
