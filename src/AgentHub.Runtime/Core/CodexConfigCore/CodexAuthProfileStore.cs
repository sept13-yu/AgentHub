using System.IO;
using System.Text.Json;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.TokenCore;

using AgentHub.Core.Platform;

namespace AgentHub.Core.CodexConfigCore;

/// <summary>
/// ChatGPT 登录档案：索引 + 每条 DPAPI 密文，落 %APPDATA%\AgentHub\codex-auth-profiles。
/// 索引不含 token；payload 文件只有 AuthCipher，与中转 Key 同一套本机 DPAPI。
/// </summary>
public sealed class CodexAuthProfileStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;

    public CodexAuthProfileStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(AgentHubConfig.Dir, "codex-auth-profiles");
    }

    public string Dir => _dir;
    public string IndexPath => Path.Combine(_dir, "index.json");

    public CodexAuthProfileIndex LoadIndex()
    {
        if (!File.Exists(IndexPath)) return new CodexAuthProfileIndex();
        try
        {
            var doc = JsonSerializer.Deserialize<CodexAuthProfileIndex>(File.ReadAllText(IndexPath), JsonOpts);
            if (doc is null) return new CodexAuthProfileIndex();
            doc.Profiles ??= [];
            return doc;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "账号档案索引损坏，请检查 AgentHub 数据目录中的 codex-auth-profiles", ex);
        }
    }

    public void SaveIndex(CodexAuthProfileIndex index)
    {
        Directory.CreateDirectory(_dir);
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(index, JsonOpts));
        if (File.Exists(IndexPath)) File.Replace(tmp, IndexPath, destinationBackupFileName: null);
        else File.Move(tmp, IndexPath);
    }

    public void WritePayload(string id, string authJsonText)
    {
        if (!IsSafeId(id)) throw new InvalidOperationException("账号档案不存在");
        if (string.IsNullOrEmpty(authJsonText))
            throw new InvalidOperationException("登录态内容为空");
        Directory.CreateDirectory(_dir);
        var path = PayloadPath(id);
        var tmp = path + ".tmp";
        var file = new CodexAuthProfilePayloadFile { AuthCipher = Secrets.Protect(authJsonText) };
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
    }

    public string ReadPayload(string id)
    {
        if (!IsSafeId(id)) throw new InvalidOperationException("账号档案不存在");
        var path = PayloadPath(id);
        if (!File.Exists(path))
            throw new InvalidOperationException("档案内容缺失，请删除后重新导入");
        CodexAuthProfilePayloadFile? file;
        try
        {
            file = JsonSerializer.Deserialize<CodexAuthProfilePayloadFile>(File.ReadAllText(path), JsonOpts);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("档案内容损坏，请删除后重新导入");
        }
        var plain = Secrets.Unprotect(file?.AuthCipher);
        if (string.IsNullOrEmpty(plain))
            throw new InvalidOperationException("档案凭据无法解密（可能换过 Windows 用户），请重新导入");
        return plain;
    }

    public void DeletePayload(string id)
    {
        if (!IsSafeId(id)) throw new InvalidOperationException("账号档案不存在");
        var path = PayloadPath(id);
        if (File.Exists(path)) File.Delete(path);
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
    }

    private string PayloadPath(string id)
    {
        var path = Path.GetFullPath(Path.Combine(_dir, id + ".json"));
        var root = Path.GetFullPath(_dir);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("账号档案不存在");
        return path;
    }

    /// <summary>档案 Id 只允许 auth- + 十六进制，避免路径穿越。</summary>
    public static bool IsSafeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("auth-", StringComparison.Ordinal) || id.Length != 13)
            return false;
        for (var i = 5; i < id.Length; i++)
        {
            var c = id[i];
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        }
        return true;
    }
}

internal sealed class CodexAuthProfilePayloadFile
{
    public string AuthCipher { get; set; } = "";
}
