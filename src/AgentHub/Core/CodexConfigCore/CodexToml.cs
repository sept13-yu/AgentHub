using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace AgentHub.Core.CodexConfigCore;

/// <summary>live config.toml 中受管区域的可读快照（只含方案授权读写的字段）。属性供 API 序列化。</summary>
public sealed class CodexLiveInfo
{
    public bool FileExists { get; set; }
    public string? ModelProvider { get; set; }
    public string? Model { get; set; }
    public bool TableExists { get; set; }
    public string? Name { get; set; }
    public string? BaseUrl { get; set; }
    public string? WireApi { get; set; }
    public bool RequiresOpenaiAuth { get; set; }
    public bool? SupportsWebSockets { get; set; }
    public bool? SupportsStandaloneWebSearch { get; set; }
    public string? UserAgent { get; set; }
    public string? Originator { get; set; }
    public bool HasAuthCommand { get; set; }
    public string? AuthCommand { get; set; }
    public List<string> AuthArgs { get; } = [];
    public long? RefreshIntervalMs { get; set; }
    public bool HasEnvKey { get; set; }
    public bool HasBearerToken { get; set; }
    public List<string> ForeignKeys { get; } = [];

    public bool ProviderMatches => string.Equals(ModelProvider, CodexConnection.FixedProviderId, StringComparison.Ordinal);
    public bool IsHybridForm => TableExists && RequiresOpenaiAuth && !string.IsNullOrEmpty(BaseUrl);
}

/// <summary>
/// Codex config.toml 的受管读写。
/// 编辑用文本手术：只替换顶层 model_provider/model 行与 [model_providers.OpenAI] 表块，
/// 其余字节原样保留；每次产出后用 Tomlyn 完整解析做语法与语义校验，失败即拒绝写入。
/// </summary>
public static partial class CodexToml
{
    private const string TableHeader = $"[model_providers.{CodexConnection.FixedProviderId}]";
    private const string AuthHeader = $"[model_providers.{CodexConnection.FixedProviderId}.auth]";

    [GeneratedRegex(@"^\[model_providers\.OpenAI\]\s*(#.*)?$")]
    private static partial Regex TableHeaderLine();
    [GeneratedRegex(@"^\[model_providers\.OpenAI\..+\]\s*(#.*)?$")]
    private static partial Regex TableSubHeaderLine();
    [GeneratedRegex(@"^\s*(\w[\w-]*)\s*=\s*(.*?)\s*(#.*)?$")]
    private static partial Regex KeyValueLine();
    [GeneratedRegex(@"^\[.+\]\s*(#.*)?$")]
    private static partial Regex AnyHeaderLine();

    public static CodexLiveInfo Read(string? text)
    {
        var info = new CodexLiveInfo { FileExists = text is not null };
        if (text is null) return info;
        var root = ParseModel(text);
        info.ModelProvider = Str(root, "model_provider");
        info.Model = Str(root, "model");
        if (Table(root, "model_providers") is not { } providers
            || Table(providers, CodexConnection.FixedProviderId) is not { } table)
            return info;
        info.TableExists = true;
        info.Name = Str(table, "name");
        info.BaseUrl = Str(table, "base_url");
        info.WireApi = Str(table, "wire_api");
        info.RequiresOpenaiAuth = Bool(table, "requires_openai_auth");
        info.SupportsWebSockets = NullableBool(table, "supports_websockets");
        info.SupportsStandaloneWebSearch = NullableBool(table, "supports_standalone_web_search");
        if (Table(table, "http_headers") is { } headers)
        {
            info.UserAgent = Str(headers, "User-Agent");
            info.Originator = Str(headers, "Originator");
        }
        if (Table(table, "auth") is { } auth)
        {
            info.HasAuthCommand = true;
            info.AuthCommand = Str(auth, "command");
            if (auth.TryGetValue("args", out var args) && args is TomlArray arr)
                foreach (var a in arr) info.AuthArgs.Add(a?.ToString() ?? "");
            if (auth.TryGetValue("refresh_interval_ms", out var ri) && ri is long l) info.RefreshIntervalMs = l;
        }
        info.HasEnvKey = table.ContainsKey("env_key");
        info.HasBearerToken = table.ContainsKey("experimental_bearer_token");
        var managed = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "wire_api", "requires_openai_auth", "supports_websockets",
            "supports_standalone_web_search", "http_headers", "auth", "base_url",
            "env_key", "experimental_bearer_token",
        };
        foreach (var key in table.Keys)
            if (!managed.Contains(key))
                info.ForeignKeys.Add(key);
        return info;
    }

    /// <summary>按连接投影生成新的 config.toml 全文（不落盘）。</summary>
    public static string Project(string liveText, CodexConnection conn, string credentialExePath)
    {
        var newline = liveText.Contains("\r\n") ? "\r\n" : "\n";
        var lines = liveText.Replace("\r\n", "\n").Replace("\r", "\n")
            .Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var firstHeader = lines.FindIndex(l => AnyHeaderLine().IsMatch(l));

        var (tableStart, tableEnd) = FindManagedTableSpan(lines);
        var modelProviderIdx = FindTopLevelKey(lines, firstHeader, "model_provider");
        var modelIdx = FindTopLevelKey(lines, firstHeader, "model");

        if (modelProviderIdx >= 0)
            lines[modelProviderIdx] = $"model_provider = \"{CodexConnection.FixedProviderId}\"";
        else if (firstHeader >= 0)
            lines.Insert(firstHeader, $"model_provider = \"{CodexConnection.FixedProviderId}\"");
        else
            lines.Add($"model_provider = \"{CodexConnection.FixedProviderId}\"");
        firstHeader = lines.FindIndex(l => AnyHeaderLine().IsMatch(l));

        if (!string.IsNullOrWhiteSpace(conn.DefaultModel))
        {
            var line = $"model = \"{Esc(conn.DefaultModel.Trim())}\"";
            var at = FindTopLevelKey(lines, firstHeader, "model");
            if (at >= 0) lines[at] = line;
            else
            {
                var providerAt = FindTopLevelKey(lines, firstHeader, "model_provider");
                lines.Insert(providerAt + 1, line);
            }
        }

        var tableLines = RenderTable(conn, credentialExePath);
        if (tableStart >= 0)
        {
            lines.RemoveRange(tableStart, tableEnd - tableStart);
            lines.InsertRange(tableStart, tableLines);
        }
        else
        {
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(tableLines);
        }
        return string.Join(newline, lines) + newline;
    }

    /// <summary>投影结果的语法与不变量校验；通过返回 null，失败返回错误说明。</summary>
    public static string? Validate(string text, CodexConnection conn, string credentialExePath)
    {
        TomlTable root;
        try
        {
            root = ParseModel(text);
        }
        catch (Exception ex)
        {
            return "TOML 语法无效：" + ex.Message;
        }
        if (!string.Equals(Str(root, "model_provider"), CodexConnection.FixedProviderId, StringComparison.Ordinal))
            return "顶层 model_provider 必须是 " + CodexConnection.FixedProviderId;
        if (Table(root, "model_providers") is not { } providers
            || Table(providers, CodexConnection.FixedProviderId) is not { } table)
            return "缺少 [model_providers.OpenAI] 表";
        if (conn.IsOfficial)
        {
            if (table.ContainsKey("base_url")) return "官方订阅不得写 base_url";
            if (table.ContainsKey("auth")) return "官方订阅不得写 auth 表";
            if (!Bool(table, "requires_openai_auth")) return "官方订阅必须 requires_openai_auth = true";
        }
        else
        {
            if (Bool(table, "requires_openai_auth")) return "中转连接不得写 requires_openai_auth";
            if (table.ContainsKey("env_key") || table.ContainsKey("experimental_bearer_token"))
                return "中转连接不得残留 env_key / experimental_bearer_token";
            var baseUrl = Str(table, "base_url");
            if (string.IsNullOrWhiteSpace(baseUrl)) return "中转连接必须写 base_url";
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return "base_url 必须是绝对 http/https 地址";
            if (Table(table, "auth") is not { } auth)
                return "中转连接必须写 auth 表";
            var command = Str(auth, "command");
            if (string.IsNullOrWhiteSpace(command)) return "auth.command 不能为空";
            if (!string.Equals(credentialExePath, command, StringComparison.OrdinalIgnoreCase))
                return "auth.command 必须指向当前 AgentHub 可执行文件";
            if (auth.TryGetValue("args", out var args) && args is TomlArray arr
                && arr.Count > 0 && arr[^1]?.ToString() == conn.Id) { }
            else return "auth.args 末位必须是本连接 Id";
        }
        return null;
    }

    /// <summary>受管表在原文中的行区间（含 [model_providers.OpenAI.*] 子表）。未找到时 start&lt;0。</summary>
    public static (int Start, int End) FindManagedTableSpan(List<string> lines)
    {
        var start = lines.FindIndex(l => TableHeaderLine().IsMatch(l));
        if (start < 0) return (-1, -1);
        var end = lines.Count;
        for (var k = start + 1; k < lines.Count; k++)
        {
            if (!AnyHeaderLine().IsMatch(lines[k])) continue;
            if (TableSubHeaderLine().IsMatch(lines[k])) continue;
            end = k;
            break;
        }
        return (start, end);
    }

    /// <summary>受管表与 live 的逐字段差异（应用预览）。value 不含任何凭据。</summary>
    public static List<CodexDiffRow> Diff(string? liveText, CodexConnection conn, string credentialExePath)
    {
        var live = Read(liveText);
        var rows = new List<CodexDiffRow>();
        void Row(string field, string? a, string? b, bool same)
        {
            var change = same ? "keep" : a is null ? "set" : b is null ? "clear" : "change";
            rows.Add(new CodexDiffRow { Field = field, Live = a, Candidate = b, Change = change });
        }
        Row("model_provider", live.ModelProvider, CodexConnection.FixedProviderId,
            string.Equals(live.ModelProvider, CodexConnection.FixedProviderId, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(conn.DefaultModel))
        {
            // 连接未指定默认模型：不改顶层 model，只做展示
            if (live.Model is not null)
                Row("model", live.Model, live.Model, true);
        }
        else
        {
            Row("model", live.Model, conn.DefaultModel.Trim(),
                string.Equals(live.Model, conn.DefaultModel.Trim(), StringComparison.Ordinal));
        }
        if (conn.IsOfficial)
        {
            Row("base_url", live.BaseUrl, null, live.BaseUrl is null);
            Row("requires_openai_auth", live.RequiresOpenaiAuth ? "true" : null, "true", live.RequiresOpenaiAuth);
            Row("supports_websockets",
                live.SupportsWebSockets is null ? null : (live.SupportsWebSockets == true ? "true" : "false"),
                "true", live.SupportsWebSockets == true);
            Row("supports_standalone_web_search",
                live.SupportsStandaloneWebSearch is null ? null : (live.SupportsStandaloneWebSearch == true ? "true" : "false"),
                "true", live.SupportsStandaloneWebSearch == true);
            Row("http_headers", live.UserAgent is null && live.Originator is null ? null : "User-Agent/Originator", null,
                live.UserAgent is null && live.Originator is null);
            Row("auth", live.HasAuthCommand ? live.AuthCommand : null, null, !live.HasAuthCommand);
        }
        else
        {
            Row("base_url", live.BaseUrl, conn.BaseUrl, string.Equals(live.BaseUrl, conn.BaseUrl, StringComparison.Ordinal));
            Row("requires_openai_auth", live.RequiresOpenaiAuth ? "true" : null, null, !live.RequiresOpenaiAuth);
            Row("supports_websockets",
                live.SupportsWebSockets is null ? null : (live.SupportsWebSockets == true ? "true" : "false"),
                conn.SupportsWebSockets ? "true" : "false",
                live.SupportsWebSockets == conn.SupportsWebSockets);
            var liveHeaders = live.UserAgent is null && live.Originator is null
                ? null : $"User-Agent: {live.UserAgent}\nOriginator: {live.Originator}";
            var candHeaders = string.IsNullOrWhiteSpace(conn.UserAgent) && string.IsNullOrWhiteSpace(conn.Originator)
                ? null : $"User-Agent: {conn.UserAgent}\nOriginator: {conn.Originator}";
            Row("http_headers", liveHeaders, candHeaders,
                string.Equals(live.UserAgent, conn.UserAgent, StringComparison.Ordinal)
                && string.Equals(live.Originator, conn.Originator, StringComparison.Ordinal));
            var candAuth = $"{credentialExePath} codex-credential {conn.Id}";
            Row("auth", live.HasAuthCommand ? live.AuthCommand : null, candAuth,
                live.HasAuthCommand && string.Equals(live.AuthCommand, credentialExePath, StringComparison.OrdinalIgnoreCase)
                && live.AuthArgs is ["codex-credential", var last] && last == conn.Id);
        }
        foreach (var key in live.ForeignKeys)
            rows.Add(new CodexDiffRow { Field = key, Live = "（受管表内保留原值）", Candidate = "（保留）", Change = "keep" });
        return rows;
    }

    private static List<string> RenderTable(CodexConnection conn, string credentialExePath)
    {
        var lines = new List<string>();
        if (conn.IsOfficial)
        {
            lines.Add(TableHeader);
            lines.Add($"name = \"{CodexConnection.FixedProviderId}\"");
            lines.Add("wire_api = \"responses\"");
            lines.Add("requires_openai_auth = true");
            lines.Add("supports_websockets = true");
            lines.Add("supports_standalone_web_search = true");
            return lines;
        }
        lines.Add(TableHeader);
        lines.Add($"name = \"{CodexConnection.FixedProviderId}\"");
        lines.Add($"base_url = \"{Esc(conn.BaseUrl.Trim())}\"");
        lines.Add("wire_api = \"responses\"");
        lines.Add($"supports_websockets = {(conn.SupportsWebSockets ? "true" : "false")}");
        if (!string.IsNullOrWhiteSpace(conn.UserAgent) || !string.IsNullOrWhiteSpace(conn.Originator))
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(conn.UserAgent))
                parts.Add($"User-Agent = \"{Esc(conn.UserAgent.Trim())}\"");
            if (!string.IsNullOrWhiteSpace(conn.Originator))
                parts.Add($"Originator = \"{Esc(conn.Originator.Trim())}\"");
            lines.Add($"http_headers = {{ {string.Join(", ", parts)} }}");
        }
        lines.Add("");
        lines.Add(AuthHeader);
        lines.Add($"command = \"{Esc(credentialExePath)}\"");
        lines.Add($"args = [\"codex-credential\", \"{Esc(conn.Id)}\"]");
        lines.Add("refresh_interval_ms = 0");
        return lines;
    }

    /// <summary>顶层（首个表头之前）key=value 行的索引；找不到返回 -1。</summary>
    private static int FindTopLevelKey(List<string> lines, int firstHeader, string key)
    {
        var limit = firstHeader < 0 ? lines.Count : firstHeader;
        for (var i = 0; i < limit; i++)
        {
            var m = KeyValueLine().Match(lines[i]);
            if (m.Success && m.Groups[1].Value == key) return i;
        }
        return -1;
    }

    private static TomlTable ParseModel(string text)
    {
        var doc = Toml.Parse(text);
        if (doc.Diagnostics.Count > 0)
            throw new InvalidOperationException("config.toml 解析失败：" + doc.Diagnostics[0].Message);
        return doc.ToModel();
    }

    private static TomlTable? Table(TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is TomlTable t ? t : null;

    private static string? Str(TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is string s ? s : null;
    private static bool Bool(TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is bool b && b;
    private static bool? NullableBool(TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is bool b ? b : null;

    /// <summary>TOML basic string 转义（反斜杠、引号与控制字符）。</summary>
    public static string Esc(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (char.IsControl(ch)) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}

public sealed class CodexDiffRow
{
    public string Field { get; set; } = "";
    public string? Live { get; set; }
    public string? Candidate { get; set; }
    public string Change { get; set; } = "keep";
}
