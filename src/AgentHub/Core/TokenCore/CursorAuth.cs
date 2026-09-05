using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>Cursor 登录态只从本机 state.vscdb 读 accessToken，拼 Cookie，不落盘不打日志。</summary>
internal static class CursorAuth
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    public static string? ReadAccessToken() => ReadItem("cursorAuth/accessToken");

    /// <summary>本机订阅档位（stripeMembershipType：ultra / pro / free …），零网络。</summary>
    public static string? ReadMembershipType() => ReadItem("cursorAuth/stripeMembershipType");

    private static string? ReadItem(string key)
    {
        try
        {
            var db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "User", "globalStorage", "state.vscdb");
            if (!File.Exists(db)) return null;
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            using var conn = new SqliteConnection(cs.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT value FROM ItemTable WHERE key = '{key}'";
            return cmd.ExecuteScalar() as string;
        }
        catch (Exception) { return null; }
    }

    public static string? ExtractJwtSub(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
        }
        catch (Exception) { return null; }
    }

    public static bool TryCookie(out string cookie, out string? error)
    {
        cookie = "";
        var token = ReadAccessToken();
        if (string.IsNullOrEmpty(token))
        {
            error = "未在 state.vscdb 找到登录态（打开 Cursor 登录后重试）";
            return false;
        }
        var userId = ExtractJwtSub(token) ?? "";
        cookie = $"WorkosCursorSessionToken={Uri.EscapeDataString(userId + "::" + token)}";
        error = null;
        return true;
    }
}
