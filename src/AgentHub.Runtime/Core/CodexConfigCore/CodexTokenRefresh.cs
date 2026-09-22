using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHub.Core.CodexConfigCore;

/// <summary>ChatGPT OAuth 票：只读提取 + refresh 后改写 tokens 节。不把 token 写进日志。</summary>
public static class CodexTokenRefresh
{
    public static bool TryGetAccessToken(string authJson, out string accessToken)
    {
        accessToken = "";
        if (!TryTokens(authJson, out var tokens)) return false;
        var s = tokens["access_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(s)) return false;
        accessToken = s;
        return true;
    }

    public static bool TryGetRefreshToken(string authJson, out string refreshToken)
    {
        refreshToken = "";
        if (!TryTokens(authJson, out var tokens)) return false;
        var s = tokens["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(s)) return false;
        refreshToken = s;
        return true;
    }

    /// <summary>client_id 取 id_token / access_token 的 aud（Codex 为 app_*）。</summary>
    public static string? ClientIdFromAuth(string authJson)
    {
        if (!TryTokens(authJson, out var tokens)) return null;
        foreach (var key in new[] { "id_token", "access_token" })
        {
            var jwt = tokens[key]?.GetValue<string>();
            var aud = JwtAud(jwt);
            if (!string.IsNullOrEmpty(aud)) return aud;
        }
        return null;
    }

    /// <summary>access_token 是否已过期（60s 时钟偏移）。非 JWT 视为未过期，交给 401 兜底。</summary>
    public static bool IsAccessExpired(string authJson)
    {
        if (!TryTokens(authJson, out var tokens)) return true;
        var at = tokens["access_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(at)) return true;
        var exp = JwtExp(at);
        if (exp <= 0) return false;
        return exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
    }

    /// <summary>替换 tokens 里的 access/refresh/id_token，其余字段（含 account_id）原样保留。</summary>
    public static string? ApplyTokens(string authJson, string accessToken, string? refreshToken, string? idToken)
    {
        try
        {
            var node = JsonNode.Parse(authJson) as JsonObject;
            if (node is null) return null;
            if (node["tokens"] is not JsonObject tokens) return null;
            tokens["access_token"] = accessToken;
            if (!string.IsNullOrEmpty(refreshToken)) tokens["refresh_token"] = refreshToken;
            if (!string.IsNullOrEmpty(idToken)) tokens["id_token"] = idToken;
            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryTokens(string authJson, out JsonObject tokens)
    {
        tokens = null!;
        if (string.IsNullOrWhiteSpace(authJson)) return false;
        try
        {
            if (JsonNode.Parse(authJson) is not JsonObject root) return false;
            if (root["tokens"] is not JsonObject t) return false;
            tokens = t;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? JwtAud(string? jwt) => JwtStringClaim(jwt, "aud");

    private static long JwtExp(string? jwt)
    {
        var payload = JwtPayload(jwt);
        if (payload is null) return 0;
        try
        {
            if (payload.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var n))
                return n;
        }
        catch (Exception)
        {
            // ignore
        }
        return 0;
    }

    private static string? JwtStringClaim(string? jwt, string claim)
    {
        var payload = JwtPayload(jwt);
        if (payload is null) return null;
        try
        {
            var root = payload.RootElement;
            if (!root.TryGetProperty(claim, out var el)) return null;
            if (el.ValueKind == JsonValueKind.String) return el.GetString();
            if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0)
            {
                var first = el[0];
                return first.ValueKind == JsonValueKind.String ? first.GetString() : null;
            }
        }
        catch (Exception)
        {
            // ignore
        }
        return null;
    }

    private static JsonDocument? JwtPayload(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt)) return null;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 0: break;
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
            default: return null;
        }
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                return null;
            }
            return doc;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
