using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.TokenCore;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>Cursor Cloud Agents（官方 v0 API）：列表 / 会话 / 删除。
/// Basic 认证：API Key 为用户名、密码为空。无 Key 时不出现在会话源。不记日志中的 Key。</summary>
public sealed class CursorCloudProvider(TitleOverrideStore titles, AgentHubConfig config) : IConversationProvider
{
    public const string Id = "cursor-cloud";
    public string AgentId => Id;

    private const string BaseUrl = "https://api.cursor.com";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public string? MissingReason
    {
        get
        {
            var key = ApiKey();
            return string.IsNullOrEmpty(key) ? "未配置 Cursor Cloud API Key（设置 → 凭据）" : null;
        }
    }

    private string? ApiKey() => Dpapi.Unprotect(config.Credentials.CursorCloudApiKey);

    private HttpRequestMessage Req(HttpMethod method, string path)
    {
        var key = ApiKey() ?? throw new InvalidOperationException("未配置 Cursor Cloud API Key");
        var req = new HttpRequestMessage(method, path);
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(key + ":"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        return req;
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync()
    {
        if (MissingReason is not null) return [];
        var list = new List<ConversationSummary>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var path = "/v0/agents?limit=100" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            using var req = Req(HttpMethod.Get, path);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Cursor Cloud 列表失败 HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in agents.EnumerateArray())
                {
                    var id = CodexProvider.GetString(a, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var name = CodexProvider.GetString(a, "name");
                    var summary = CodexProvider.GetString(a, "summary");
                    var status = CodexProvider.GetString(a, "status");
                    var created = CodexProvider.GetString(a, "createdAt");
                    var repo = RepoOf(a);
                    var overrideTitle = titles.Get(AgentId, id);
                    var title = overrideTitle ?? name ?? summary ?? id;
                    if (overrideTitle is null && !string.IsNullOrEmpty(status))
                        title = $"{title} · {status}";
                    list.Add(new ConversationSummary
                    {
                        AgentId = AgentId,
                        Id = id,
                        Title = title,
                        TitleSource = overrideTitle is not null ? "override" : name is not null ? "source" : "derived",
                        Project = repo,
                        MessageCount = 0,
                        SizeBytes = 0,
                        LastActivityUtc = CodexProvider.ParseTs(created) ?? DateTime.UtcNow,
                        SourceFile = "cursor-cloud://" + id,
                    });
                }
            }
            cursor = CodexProvider.GetString(root, "nextCursor");
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return list;
    }

    public async Task<ConversationDetail?> LoadAsync(string id)
    {
        CodexProvider.GuardDbId(id);
        if (MissingReason is not null) return null;

        using var statusReq = Req(HttpMethod.Get, "/v0/agents/" + Uri.EscapeDataString(id));
        using var statusResp = await Http.SendAsync(statusReq).ConfigureAwait(false);
        if (statusResp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var statusBody = await statusResp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!statusResp.IsSuccessStatusCode)
            throw new HttpRequestException($"Cursor Cloud 详情失败 HTTP {(int)statusResp.StatusCode}");

        using var statusDoc = JsonDocument.Parse(statusBody);
        var agent = statusDoc.RootElement;
        var name = CodexProvider.GetString(agent, "name");
        var summary = CodexProvider.GetString(agent, "summary");
        var status = CodexProvider.GetString(agent, "status");
        var created = CodexProvider.GetString(agent, "createdAt");
        var repo = RepoOf(agent);

        using var convReq = Req(HttpMethod.Get, "/v0/agents/" + Uri.EscapeDataString(id) + "/conversation");
        using var convResp = await Http.SendAsync(convReq).ConfigureAwait(false);
        var convBody = await convResp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (convResp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!convResp.IsSuccessStatusCode)
            throw new HttpRequestException($"Cursor Cloud 会话失败 HTTP {(int)convResp.StatusCode}");

        var messages = new List<ConversationMessage>();
        using (var convDoc = JsonDocument.Parse(convBody))
        {
            if (convDoc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in msgs.EnumerateArray())
                {
                    var type = CodexProvider.GetString(m, "type") ?? "";
                    var text = CodexProvider.GetString(m, "text") ?? "";
                    if (text.Length == 0) continue;
                    string? role = type switch
                    {
                        "user_message" => "user",
                        "assistant_message" => "assistant",
                        _ => null,
                    };
                    if (role is null) continue;
                    if (text.Length > 4000) text = text[..4000] + "\n…（截断）";
                    messages.Add(new ConversationMessage { Role = role, Text = text });
                }
            }
        }

        var total = messages.Count;
        IReadOnlyList<ConversationMessage> capped = total > 200
            ? messages.Skip(total - 200).ToList()
            : messages;
        var overrideTitle = titles.Get(AgentId, id);
        var title = overrideTitle ?? name ?? summary ?? id;
        var noteParts = new List<string>();
        if (!string.IsNullOrEmpty(status)) noteParts.Add("状态：" + status);
        if (!string.IsNullOrEmpty(summary)) noteParts.Add(summary);
        if (total > 200) noteParts.Add($"共 {total} 条消息，预览仅显示最后 200 条。");
        if (total == 0) noteParts.Add("云端会话暂无消息。");

        return new ConversationDetail
        {
            Summary = new ConversationSummary
            {
                AgentId = AgentId,
                Id = id,
                Title = title,
                TitleSource = overrideTitle is not null ? "override" : name is not null ? "source" : "derived",
                Project = repo,
                MessageCount = total,
                SizeBytes = 0,
                LastActivityUtc = CodexProvider.ParseTs(created) ?? DateTime.UtcNow,
                SourceFile = "cursor-cloud://" + id,
            },
            Messages = capped,
            Note = noteParts.Count > 0 ? string.Join(" · ", noteParts) : null,
        };
    }

    public Task RenameAsync(string id, string title)
    {
        CodexProvider.GuardDbId(id);
        titles.Set(AgentId, id, title);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<DeleteItemResult>> DeleteAsync(IEnumerable<string> ids)
    {
        var results = new List<DeleteItemResult>();
        if (MissingReason is not null)
        {
            foreach (var id in ids)
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = MissingReason });
            return results;
        }

        foreach (var id in ids)
        {
            try
            {
                CodexProvider.GuardDbId(id);
                using var req = Req(HttpMethod.Delete, "/v0/agents/" + Uri.EscapeDataString(id));
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    titles.Remove(AgentId, id);
                    results.Add(new DeleteItemResult
                    {
                        AgentId = AgentId,
                        Id = id,
                        Ok = true,
                        Note = "已从 Cursor Cloud 删除",
                    });
                }
                else
                {
                    results.Add(new DeleteItemResult
                    {
                        AgentId = AgentId,
                        Id = id,
                        Ok = false,
                        Error = $"删除失败 HTTP {(int)resp.StatusCode}",
                    });
                }
            }
            catch (Exception ex)
            {
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = ex.Message });
            }
        }
        return results;
    }

    private static string? RepoOf(JsonElement agent)
    {
        if (agent.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
        {
            var repo = CodexProvider.GetString(src, "repository");
            if (!string.IsNullOrEmpty(repo)) return repo;
        }
        return null;
    }
}
