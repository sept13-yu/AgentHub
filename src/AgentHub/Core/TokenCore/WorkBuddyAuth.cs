using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>WorkBuddy 本机登录态：local_storage、app/session Cookie / Electron Local Storage、safeStorage。
/// 套餐从 account-snapshot 读。拿到的登录态只留在内存，不写日志。</summary>
internal static class WorkBuddyAuth
{
    internal static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".workbuddy");

    public sealed record Probe(
        bool HasSession,
        string? Bearer,
        string? UserId,
        string Plan,
        bool IsPro,
        string Reason);

    /// <summary>本机 Cookie 库 → 设置里的 session → JWT。额度与云端删除共用。</summary>
    public static Probe Read(string? settingsSession = null)
    {
        var (plan, isPro, uid) = ReadAccount();
        if (TryNamedCookie("session", out var cookie) && !string.IsNullOrEmpty(cookie))
            return new Probe(true, cookie, uid, plan, isPro, "ok");
        var fromSettings = CookieValue(settingsSession, "session");
        if (!string.IsNullOrEmpty(fromSettings))
            return new Probe(true, fromSettings, uid, plan, isPro, "ok");
        if (TryCookies(out var jwt) && !string.IsNullOrEmpty(jwt))
            return new Probe(true, jwt, uid, plan, isPro, "ok");
        if (TryElectronLocalStorage(out var lsBearer) && !string.IsNullOrEmpty(lsBearer))
            return new Probe(true, lsBearer, uid, plan, isPro, "ok");
        if (TrySafeStorage(out var safeBearer) && !string.IsNullOrEmpty(safeBearer))
            return new Probe(true, safeBearer, uid, plan, isPro, "ok");

        return new Probe(false, null, uid, plan, isPro,
            "本机没读到登录态，可在设置里先藏掉这张卡");
    }

    /// <summary>积分接口要的是网站 session Cookie：与 <see cref="Read"/> 同一份。</summary>
    public static string? ResolveQuotaSession(string? settingsRaw)
    {
        var probe = Read(settingsRaw);
        return string.IsNullOrEmpty(probe.Bearer) ? null : probe.Bearer;
    }

    internal static string? CookieValue(string? raw, string name)
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

    internal static (string Plan, bool IsPro, string? UserId) ReadAccount()
    {
        try
        {
            var snap = Path.Combine(Root, "storage", "skeleton", "account-snapshot.json");
            if (!File.Exists(snap)) return ("Free", false, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(snap));
            if (!doc.RootElement.TryGetProperty("primary", out var p) || p.ValueKind != JsonValueKind.Object)
                return ("Free", false, null);
            var uid = p.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var isPro = p.TryGetProperty("isPro", out var pro) && pro.ValueKind == JsonValueKind.True;
            var edition = p.TryGetProperty("editionType", out var ed) && ed.ValueKind == JsonValueKind.String
                ? ed.GetString() : null;
            var plan = isPro || string.Equals(edition, "pro", StringComparison.OrdinalIgnoreCase) ? "Pro"
                : string.Equals(edition, "free", StringComparison.OrdinalIgnoreCase) ? "Free"
                : string.IsNullOrEmpty(edition) ? "Free" : edition!;
            return (plan, isPro, uid);
        }
        catch (Exception)
        {
            return ("Free", false, null);
        }
    }

    private static IEnumerable<string> CookieDbPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[]
        {
            Path.Combine(Root, "app", "session", "Network", "Cookies"),
            Path.Combine(Root, "app", "session", "Cookies"),
        })
        {
            if (File.Exists(file) && seen.Add(file))
                yield return file;
        }
        var partitions = Path.Combine(Root, "app", "session", "Partitions");
        if (!Directory.Exists(partitions)) yield break;
        foreach (var file in Directory.EnumerateFiles(partitions, "Cookies", SearchOption.AllDirectories))
        {
            if (seen.Add(file))
                yield return file;
        }
    }

    private static bool TryNamedCookie(string cookieName, out string? value)
    {
        value = null;
        foreach (var db in CookieDbPaths())
        {
            if (TryReadCookie(db, cookieName, out value))
                return true;
        }
        return false;
    }

    private static bool TryReadCookie(string db, string cookieName, out string? value)
    {
        value = null;
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using var conn = new SqliteConnection(cs.ToString());
            conn.Open();
            using var qonly = conn.CreateCommand();
            qonly.CommandText = "PRAGMA query_only=ON";
            qonly.ExecuteNonQuery();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT value FROM cookies
                WHERE name = $name
                  AND (host_key LIKE '%workbuddy.cn' OR host_key LIKE '%codebuddy.cn')
                  AND length(value) > 0
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$name", cookieName);
            var raw = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(raw)) return false;
            value = raw;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryCookies(out string? bearer)
    {
        bearer = null;
        foreach (var db in CookieDbPaths())
        {
            if (TryJwtCookie(db, out bearer))
                return true;
        }
        return false;
    }

    private static bool TryJwtCookie(string db, out string? bearer)
    {
        bearer = null;
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using var conn = new SqliteConnection(cs.ToString());
            conn.Open();
            using var qonly = conn.CreateCommand();
            qonly.CommandText = "PRAGMA query_only=ON";
            qonly.ExecuteNonQuery();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT name, value FROM cookies
                WHERE host_key LIKE '%workbuddy.cn' OR host_key LIKE '%codebuddy.cn'
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.IsDBNull(0) ? "" : r.GetString(0);
                var value = r.IsDBNull(1) ? "" : r.GetString(1);
                if (string.IsNullOrEmpty(value)) continue;
                if (name.Equals("tgw_l7_route", StringComparison.OrdinalIgnoreCase)) continue;
                if (LooksLikeBearer(value))
                {
                    bearer = value;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }
        return false;
    }

    private static bool TryElectronLocalStorage(out string? bearer)
    {
        bearer = null;
        var dir = Path.Combine(Root, "app", "session", "Local Storage", "leveldb");
        if (!Directory.Exists(dir)) return false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (ext is not (".ldb" or ".log") && !Path.GetFileName(file).StartsWith("MANIFEST", StringComparison.Ordinal))
                    continue;
                var data = File.ReadAllBytes(file);
                if (TryExtractBearerNearHint(data, out bearer))
                    return true;
            }
        }
        catch (Exception)
        {
            return false;
        }
        return false;
    }

    private static bool TrySafeStorage(out string? bearer)
    {
        bearer = null;
        // Electron safeStorage 密文未在本机落到可解位置时，这里保持空。
        return false;
    }

    private static bool TryExtractBearerNearHint(byte[] data, out string? bearer)
    {
        bearer = null;
        var hints = new[] { "auth_token", "access_token", "accessToken", "wb_token", "workbuddy" };
        foreach (var hint in hints)
        {
            foreach (var idx in FindAscii(data, hint))
            {
                if (TryJwtAfter(data, idx, out bearer))
                    return true;
            }
            var utf16 = Encoding.Unicode.GetBytes(hint);
            foreach (var idx in FindBytes(data, utf16))
            {
                if (TryJwtAfter(data, idx, out bearer))
                    return true;
            }
        }
        return false;
    }

    private static bool TryJwtAfter(byte[] data, int from, out string? bearer)
    {
        bearer = null;
        var window = data.AsSpan(from, Math.Min(8_000, data.Length - from));
        var ascii = Encoding.ASCII.GetString(window);
        var jwt = ExtractJwt(ascii);
        if (jwt is not null) { bearer = jwt; return true; }
        var uni = Encoding.Unicode.GetString(window.ToArray());
        jwt = ExtractJwt(uni);
        if (jwt is not null) { bearer = jwt; return true; }
        return false;
    }

    private static string? ExtractJwt(string text)
    {
        var i = text.IndexOf("eyJ", StringComparison.Ordinal);
        if (i < 0) return null;
        var sb = new StringBuilder();
        for (var p = i; p < text.Length; p++)
        {
            var c = text[p];
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+' or '/' or '=')
                sb.Append(c);
            else
                break;
        }
        var s = sb.ToString();
        return s.Count(c => c == '.') >= 2 && s.Length is >= 40 and <= 4096 ? s : null;
    }

    private static bool LooksLikeBearer(string value) =>
        value.StartsWith("eyJ", StringComparison.Ordinal) && value.Count(c => c == '.') >= 2;

    private static IEnumerable<int> FindAscii(byte[] data, string needle)
    {
        var n = Encoding.ASCII.GetBytes(needle);
        return FindBytes(data, n, asciiIgnoreCase: true);
    }

    private static IEnumerable<int> FindBytes(byte[] data, byte[] needle, bool asciiIgnoreCase = false)
    {
        if (needle.Length == 0 || data.Length < needle.Length) yield break;
        for (var i = 0; i <= data.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                var a = data[i + j];
                var b = needle[j];
                if (a == b) continue;
                if (asciiIgnoreCase && (a | 32) == (b | 32) && IsAsciiLetter(a) && IsAsciiLetter(b)) continue;
                ok = false;
                break;
            }
            if (ok) yield return i;
        }
    }

    private static bool IsAsciiLetter(byte b) => b is (>= 65 and <= 90) or (>= 97 and <= 122);
}
