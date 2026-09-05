using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.TokenCore;

namespace AgentHub.Core.CodexConfigCore;

/// <summary>应用结果。ok=false 时 error 给出原因；restartRequired 表示 Codex 正在运行需重启生效。</summary>
public sealed class CodexApplyResult
{
    public bool Ok { get; init; }
    public bool RestartRequired { get; init; }
    public string? BackupPath { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Codex 连接管理（方案 §6）：连接存 AgentHub config.json，live config.toml 只是当前连接的投影。
/// AgentHub 不管理 auth.json、会话与其它用户配置；切换只重写受管字段并原子替换。
/// </summary>
public sealed class CodexConfigService
{
    private readonly AgentHubConfig _config;
    private readonly string _codexHome;
    private readonly string _exePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CodexConfigService(AgentHubConfig config, string? codexHome = null, string? exePath = null)
    {
        _config = config;
        _codexHome = codexHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _exePath = exePath ?? Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? "AgentHub.exe";
    }

    public string ConfigPath => Path.Combine(_codexHome, "config.toml");
    public string AuthJsonPath => Path.Combine(_codexHome, "auth.json");
    public static string BackupDir => Path.Combine(AgentHubConfig.Dir, "backups", "codex");
    public string CredentialExePath => _exePath;

    private CodexConfigSettings State => _config.Codex;
    private List<CodexConnection> Connections => State.Connections;

    // ---------------- 连接 CRUD ----------------

    public IEnumerable<object> ListConnections() => Connections.Select(ToView);

    private object ToView(CodexConnection c) => new
    {
        c.Id, c.Name, kind = c.IsOfficial ? "official" : "relay",
        baseUrl = c.IsOfficial ? "" : c.BaseUrl,
        defaultModel = c.DefaultModel,
        supportsWebSockets = c.SupportsWebSockets,
        userAgent = c.UserAgent,
        originator = c.Originator,
        keySet = !string.IsNullOrEmpty(Dpapi.Unprotect(c.ApiKeyCipher)),
        usageBaseUrl = c.UsageBaseUrl,
        active = c.Id == State.ActiveConnectionId,
    };

    /// <summary>新增或更新中转连接。apiKey 为空表示保留原 Key。返回连接 Id。</summary>
    public string SaveRelay(string? id, string? name, string? baseUrl, string? defaultModel,
        bool supportsWebSockets, string? userAgent, string? originator, string? apiKey, string? usageBaseUrl)
    {
        var trimmedName = (name ?? "").Trim();
        if (trimmedName.Length == 0) throw new InvalidOperationException("连接名称不能为空");
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Responses 地址必须是绝对 http/https 地址");
        if (uri.Fragment.Length > 0) throw new InvalidOperationException("Responses 地址不能包含 # 片段");
        var usage = (usageBaseUrl ?? "").Trim().TrimEnd('/');
        if (usage.Length > 0 && (!Uri.TryCreate(usage, UriKind.Absolute, out var usageUri)
            || usageUri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("余额查询地址必须是绝对 http/https 地址");

        CodexConnection conn;
        if (id is null)
        {
            conn = new CodexConnection { Id = NewId() };
            Connections.Add(conn);
        }
        else
        {
            conn = Connections.FirstOrDefault(c => c.Id == id)
                ?? throw new InvalidOperationException("连接不存在");
            if (conn.IsOfficial) throw new InvalidOperationException("官方订阅连接不可编辑");
        }
        conn.Name = trimmedName;
        conn.Kind = CodexConnectionKind.ResponsesRelay;
        conn.BaseUrl = url;
        conn.DefaultModel = (defaultModel ?? "").Trim();
        conn.SupportsWebSockets = supportsWebSockets;
        conn.UserAgent = (userAgent ?? "").Trim();
        conn.Originator = (originator ?? "").Trim();
        conn.UsageBaseUrl = usage;
        var plainKey = (apiKey ?? "").Trim();
        if (plainKey.Length > 0) conn.ApiKeyCipher = Dpapi.Protect(plainKey);
        _config.Save();
        return conn.Id;
    }

    public void Delete(string id)
    {
        var conn = Connections.FirstOrDefault(c => c.Id == id)
            ?? throw new InvalidOperationException("连接不存在");
        if (conn.IsOfficial) throw new InvalidOperationException("内置官方订阅不可删除");
        if (conn.Id == State.ActiveConnectionId)
            throw new InvalidOperationException("当前生效的连接不可删除，请先切换到其它连接");
        Connections.Remove(conn);
        _config.Save();
    }

    // ---------------- 导入与凭据 ----------------

    /// <summary>把 live [model_providers.OpenAI] 表导入为新中转连接；Key 复用已保存的 Sub2API Key 密文。</summary>
    public CodexConnection ImportCurrent()
    {
        var live = CodexToml.Read(ReadLiveText());
        if (!live.TableExists || string.IsNullOrEmpty(live.BaseUrl))
            throw new InvalidOperationException(
                "当前 config.toml 没有 [model_providers.OpenAI].base_url，无需或无法导入为中转连接");
        var existing = Connections.FirstOrDefault(c => !c.IsOfficial
            && string.Equals(c.BaseUrl, live.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.UserAgent, live.UserAgent ?? "", StringComparison.Ordinal)
            && string.Equals(c.Originator, live.Originator ?? "", StringComparison.Ordinal));
        if (existing is not null) return existing;
        var conn = new CodexConnection
        {
            Id = NewId(),
            Name = "导入的中转配置",
            BaseUrl = live.BaseUrl.Trim().TrimEnd('/'),
            SupportsWebSockets = live.SupportsWebSockets == true,
            UserAgent = live.UserAgent ?? "",
            Originator = live.Originator ?? "",
            ApiKeyCipher = _config.Credentials.RelayKey,
            UsageBaseUrl = _config.Credentials.RelayPanelBaseUrl,
        };
        Connections.Add(conn);
        _config.Save();
        return conn;
    }

    /// <summary>启动初始化：确保内置官方连接存在；无中转连接且 live 是中转形态时导入第一条（不改 live）。</summary>
    public void EnsureSeeded()
    {
        var changed = false;
        if (Connections.FirstOrDefault(c => c.Id == CodexConnection.OfficialId) is null)
        {
            Connections.Add(CodexConnection.CreateOfficial());
            changed = true;
        }
        if (!Connections.Any(c => !c.IsOfficial))
        {
            try
            {
                var before = Connections.Count;
                ImportCurrent();
                changed |= Connections.Count > before;
            }
            catch (InvalidOperationException) { /* live 非中转形态：保持空列表 */ }
        }
        if (changed) _config.Save();
    }

    /// <summary>codex-credential 专用：取连接的明文 Key。找不到或解不开返回 null。禁止进日志。</summary>
    public string? GetCredentialPlain(string id)
    {
        var conn = Connections.FirstOrDefault(c => c.Id == id);
        if (conn is null || conn.IsOfficial) return null;
        return Dpapi.Unprotect(conn.ApiKeyCipher);
    }

    // ---------------- 应用与切换 ----------------

    public async Task<CodexApplyResult> ApplyAsync(string id)
    {
        var conn = Connections.FirstOrDefault(c => c.Id == id);
        if (conn is null)
            return new CodexApplyResult { Ok = false, Error = "连接不存在" };
        if (conn.IsOfficial && conn.Id != CodexConnection.OfficialId)
            return new CodexApplyResult { Ok = false, Error = "连接类型无效" };

        await _writeLock.WaitAsync();
        try
        {
            // live 缺失（新机器/首次使用）时按空文件投影，产出一份最小可用 config.toml
            string liveText;
            try
            {
                liveText = ReadLiveText() ?? "";
            }
            catch (InvalidOperationException ex)
            {
                return new CodexApplyResult { Ok = false, Error = ex.Message };
            }

            string backupPath = "";
            if (File.Exists(ConfigPath))
            {
                backupPath = BackupLive();
                if (backupPath.StartsWith("error:", StringComparison.Ordinal))
                    return new CodexApplyResult { Ok = false, Error = backupPath["error:".Length..] };
            }

            var candidate = CodexToml.Project(liveText, conn, _exePath);
            var invalid = CodexToml.Validate(candidate, conn, _exePath);
            if (invalid is not null)
                return new CodexApplyResult { Ok = false, Error = "候选配置校验失败：" + invalid };

            var tempPath = ConfigPath + ".agenthub-tmp";
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(candidate);
                await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await fs.WriteAsync(bytes);
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(ConfigPath)) File.Replace(tempPath, ConfigPath, destinationBackupFileName: null);
                else File.Move(tempPath, ConfigPath);
            }
            catch (Exception ex)
            {
                try { File.Delete(tempPath); } catch (IOException) { }
                return new CodexApplyResult
                {
                    Ok = false,
                    Error = "写入 config.toml 失败：" + ex.Message,
                    BackupPath = backupPath.Length > 0 ? backupPath : null,
                };
            }

            // 复核：重读并校验受管字段，通过后才前移 active 标记
            string verifyText;
            try
            {
                verifyText = File.ReadAllText(ConfigPath);
            }
            catch (Exception ex)
            {
                return new CodexApplyResult { Ok = false, Error = "写后复核读取失败：" + ex.Message, BackupPath = backupPath };
            }
            invalid = CodexToml.Validate(verifyText, conn, _exePath);
            if (invalid is not null)
                return new CodexApplyResult
                {
                    Ok = false,
                    Error = "写后复核失败（可用备份恢复）：" + invalid,
                    BackupPath = backupPath,
                };

            State.ActiveConnectionId = id;
            State.LiveSha256 = Sha256Hex(verifyText);
            _config.Save();
            return new CodexApplyResult { Ok = true, RestartRequired = IsCodexRunning(), BackupPath = backupPath };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string BackupLive()
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            var path = Path.Combine(BackupDir,
                "config.toml-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak");
            File.Copy(ConfigPath, path, overwrite: true);
            foreach (var old in Directory.GetFiles(BackupDir, "config.toml-*.bak")
                .OrderByDescending(f => f, StringComparer.Ordinal).Skip(10))
                File.Delete(old);
            return path;
        }
        catch (Exception ex)
        {
            return "error:" + ex.Message;
        }
    }

    // ---------------- 状态与 diff ----------------

    public CodexStatus GetStatus()
    {
        string? liveText = null;
        var parseBroken = false;
        try { liveText = ReadLiveText(); }
        catch (InvalidOperationException) { parseBroken = true; }
        var live = CodexToml.Read(liveText);
        var currentSha = File.Exists(ConfigPath) ? Sha256Hex(File.ReadAllText(ConfigPath)) : null;
        return new CodexStatus
        {
            ProviderId = CodexConnection.FixedProviderId,
            ConfigPath = ConfigPath,
            ConfigExists = live.FileExists,
            ConfigBroken = parseBroken,
            LiveProvider = live.ModelProvider,
            LiveProviderMatches = live.ProviderMatches,
            LiveModel = live.Model,
            Live = live,
            ExternalChanged = State.LiveSha256 is not null && currentSha is not null
                && !string.Equals(State.LiveSha256, currentSha, StringComparison.Ordinal),
            AuthType = DetectAuthType(),
            CodexRunning = IsCodexRunning(),
            ActiveConnectionId = State.ActiveConnectionId,
            CredentialExePath = _exePath,
            ProviderBuckets = CountProviderBuckets(),
        };
    }

    public object Diff(string id)
    {
        var conn = Connections.FirstOrDefault(c => c.Id == id)
            ?? throw new InvalidOperationException("连接不存在");
        var liveText = ReadLiveText();
        var live = CodexToml.Read(liveText);
        return new
        {
            rows = CodexToml.Diff(liveText, conn, _exePath),
            liveProviderMatches = live.ProviderMatches,
            liveHybrid = live.IsHybridForm,
        };
    }

    private string? ReadLiveText()
    {
        if (!File.Exists(ConfigPath)) return null;
        var text = File.ReadAllText(ConfigPath);
        try
        {
            CodexToml.Read(text);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("当前 config.toml 语法损坏，拒绝继续：" + ex.Message, ex);
        }
        return text;
    }

    /// <summary>auth.json 只看结构判类型，绝不返回内容。apikey | chatgpt | none | unknown。</summary>
    private string DetectAuthType()
    {
        try
        {
            if (!File.Exists(AuthJsonPath)) return "none";
            using var doc = JsonDocument.Parse(File.ReadAllText(AuthJsonPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "unknown";
            if (root.TryGetProperty("tokens", out var tokens)
                && tokens.ValueKind == JsonValueKind.Object
                && tokens.EnumerateObject().Any()) return "chatgpt";
            if (root.TryGetProperty("OPENAI_API_KEY", out var key)
                && key.ValueKind == JsonValueKind.String
                && key.GetString() is { Length: > 0 }) return "apikey";
            return "none";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static bool IsCodexRunning()
    {
        try
        {
            var list = Process.GetProcessesByName("codex");
            foreach (var p in list) p.Dispose();
            return list.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>扫描会话 JSONL 首行的 payload.model_provider，统计历史桶分布（诊断用，非精确保证）。</summary>
    private Dictionary<string, int> CountProviderBuckets()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var roots = new[]
        {
            Path.Combine(_codexHome, "sessions"),
            Path.Combine(_codexHome, "archived_sessions"),
        };
        var scanned = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
            }
            catch (Exception) { continue; }
            foreach (var file in files)
            {
                if (scanned >= 5000) return counts;
                scanned++;
                try
                {
                    using var reader = new StreamReader(file);
                    var first = reader.ReadLine();
                    if (string.IsNullOrEmpty(first)) continue;
                    using var doc = JsonDocument.Parse(first);
                    if (doc.RootElement.TryGetProperty("payload", out var payload)
                        && payload.TryGetProperty("model_provider", out var provider)
                        && provider.ValueKind == JsonValueKind.String)
                    {
                        var name = provider.GetString() ?? "";
                        counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
                    }
                }
                catch (Exception) { /* 单个会话文件读不了不影响统计 */ }
            }
        }
        return counts;
    }

    private static string NewId()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        return "relay-" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

public sealed class CodexStatus
{
    public string ProviderId { get; init; } = "";
    public string ConfigPath { get; init; } = "";
    public bool ConfigExists { get; init; }
    public bool ConfigBroken { get; init; }
    public string? LiveProvider { get; init; }
    public bool LiveProviderMatches { get; init; }
    public string? LiveModel { get; init; }
    public CodexLiveInfo? Live { get; init; }
    public bool ExternalChanged { get; init; }
    public string AuthType { get; init; } = "unknown";
    public bool CodexRunning { get; init; }
    public string? ActiveConnectionId { get; init; }
    public string CredentialExePath { get; init; } = "";
    public Dictionary<string, int> ProviderBuckets { get; init; } = [];
}
