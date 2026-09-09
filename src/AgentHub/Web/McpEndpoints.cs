using System.Text.Json;
using AgentHub.Core.McpCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentHub.Web;

/// <summary>/api/mcp/* — 母本 + 多 Agent MCP 同步（设计 §4）。</summary>
public static class McpEndpoints
{
    public static void MapMcpEndpoints(this WebApplication app, McpSyncService mcp, Func<HttpContext, bool> writeAuth)
    {
        IResult Forbidden() => Results.Json(
            new { error = "forbidden：写接口仅限 AgentHub 本体" }, statusCode: StatusCodes.Status403Forbidden);

        app.MapGet("/api/mcp", () =>
        {
            try { return Results.Json(mcp.ListMasked()); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        app.MapGet("/api/mcp/raw", (string name) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.Json(new { error = "需要 name" }, statusCode: 400);
            var spec = mcp.GetRaw(name.Trim());
            if (spec is null)
                return Results.Json(new { error = "找不到该配置" }, statusCode: 404);
            // 仅供编辑表单；列表接口已掩码。不写日志。
            return Results.Json(new
            {
                id = spec.Id,
                transport = spec.Transport.ToString().ToLowerInvariant(),
                command = spec.Command,
                args = spec.Args,
                env = spec.Env,
                url = spec.Url,
                headers = spec.Headers,
                enabled = spec.Enabled,
                startupTimeoutSec = spec.StartupTimeoutSec,
                timeoutMs = spec.TimeoutMs,
                alias = spec.Alias,
                note = spec.Note,
                explicitType = spec.ExplicitType,
                hasSecretRisk = McpSecrets.HasSecretRisk(spec),
            });
        });

        app.MapPost("/api/mcp/import", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<ImportBody>();
                if (string.IsNullOrWhiteSpace(body?.source))
                    return Results.Json(new { error = "body 需为 { source }" }, statusCode: 400);
                mcp.ImportFrom(body.source.Trim());
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/mcp/meta", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<MetaBody>();
                if (string.IsNullOrWhiteSpace(body?.name))
                    return Results.Json(new { error = "body 需为 { name, alias?, note? }" }, statusCode: 400);
                mcp.SetMeta(body.name.Trim(), body.alias, body.note);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/mcp/upsert", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = doc.RootElement;
                var id = Str(root, "name") ?? Str(root, "id");
                if (string.IsNullOrWhiteSpace(id))
                    return Results.Json(new { error = "需要 name/id" }, statusCode: 400);
                var transport = Str(root, "transport");
                var spec = new McpServerSpec
                {
                    Id = id.Trim(),
                    Transport = string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase)
                        ? McpTransport.Http
                        : McpTransport.Stdio,
                    Command = Str(root, "command"),
                    Url = Str(root, "url"),
                    Enabled = !root.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False,
                    Alias = Str(root, "alias"),
                    Note = Str(root, "note"),
                    ExplicitType = Str(root, "explicitType") ?? Str(root, "type"),
                };
                if (root.TryGetProperty("startupTimeoutSec", out var st) && st.TryGetInt32(out var sec))
                    spec.StartupTimeoutSec = sec;
                if (root.TryGetProperty("timeoutMs", out var tm) && tm.TryGetInt32(out var ms))
                    spec.TimeoutMs = ms;
                if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                    foreach (var a in args.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.String)
                            spec.Args.Add(a.GetString() ?? "");
                if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                    foreach (var p in env.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String)
                            spec.Env[p.Name] = p.Value.GetString() ?? "";
                if (root.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
                    foreach (var p in headers.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String)
                            spec.Headers[p.Name] = p.Value.GetString() ?? "";
                // 若 URL 非空则视为 HTTP
                if (!string.IsNullOrWhiteSpace(spec.Url))
                    spec.Transport = McpTransport.Http;
                mcp.UpsertMother(spec);
                return Results.Json(new { ok = true, hasSecretRisk = McpSecrets.HasSecretRisk(spec) });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        // 直接写入指定 Agent（不依赖母本门闸；母本可静默缓存）
        app.MapPost("/api/mcp/upsert-agents", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = doc.RootElement;
                var id = Str(root, "name") ?? Str(root, "id");
                if (string.IsNullOrWhiteSpace(id))
                    return Results.Json(new { error = "需要 name/id" }, statusCode: 400);
                var targets = new List<string>();
                if (root.TryGetProperty("targets", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in tEl.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()))
                            targets.Add(a.GetString()!.Trim());
                }
                if (targets.Count == 0)
                    return Results.Json(new { error = "需要 targets" }, statusCode: 400);
                var transport = Str(root, "transport");
                var spec = new McpServerSpec
                {
                    Id = id.Trim(),
                    Transport = string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase)
                        ? McpTransport.Http
                        : McpTransport.Stdio,
                    Command = Str(root, "command"),
                    Url = Str(root, "url"),
                    Enabled = !root.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False,
                    Alias = Str(root, "alias"),
                    Note = Str(root, "note"),
                    ExplicitType = Str(root, "explicitType") ?? Str(root, "type"),
                };
                if (root.TryGetProperty("startupTimeoutSec", out var st) && st.TryGetInt32(out var sec))
                    spec.StartupTimeoutSec = sec;
                if (root.TryGetProperty("timeoutMs", out var tm) && tm.TryGetInt32(out var ms))
                    spec.TimeoutMs = ms;
                if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                    foreach (var a in args.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.String)
                            spec.Args.Add(a.GetString() ?? "");
                if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                    foreach (var p in env.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String)
                            spec.Env[p.Name] = p.Value.GetString() ?? "";
                if (root.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
                    foreach (var p in headers.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String)
                            spec.Headers[p.Name] = p.Value.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(spec.Url))
                    spec.Transport = McpTransport.Http;
                var ensureMother = !root.TryGetProperty("ensureMother", out var em) || em.ValueKind != JsonValueKind.False;
                var result = mcp.UpsertToAgents(spec, targets, ensureMother);
                return Results.Json(new
                {
                    ok = result.Ok,
                    hasSecretRisk = McpSecrets.HasSecretRisk(spec),
                    items = result.Items,
                    error = result.Error,
                }, statusCode: result.Ok ? 200 : 207);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/mcp/enable", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<EnableBody>();
                if (string.IsNullOrWhiteSpace(body?.name))
                    return Results.Json(new { error = "body 需为 { name, enabled }" }, statusCode: 400);
                mcp.EnableMother(body.name.Trim(), body.enabled);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/mcp/sync", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<SyncBody>();
                var result = mcp.Sync(body?.names, body?.targets);
                return Results.Json(result, statusCode: result.Ok ? 200 : 207);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });


        app.MapPost("/api/mcp/push", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<PushBody>();
                var names = new List<string>();
                if (body?.names is { Length: > 0 })
                    names.AddRange(body.names);
                if (!string.IsNullOrWhiteSpace(body?.name))
                    names.Add(body.name.Trim());
                if (names.Count == 0)
                    return Results.Json(new { error = "body 需要 { name } 或 { names }" }, statusCode: 400);
                var result = mcp.PushNames(names, body?.targets, body?.source, ensureMother: true);
                return Results.Json(result, statusCode: result.Ok ? 200 : 207);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/mcp/agent-enable", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<AgentEnableBody>();
                if (string.IsNullOrWhiteSpace(body?.agent) || string.IsNullOrWhiteSpace(body.name))
                    return Results.Json(new { error = "body 需为 { agent, name, enabled }" }, statusCode: 400);
                mcp.AgentEnable(body.agent.Trim(), body.name.Trim(), body.enabled);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapGet("/api/mcp/open", (HttpContext ctx, string agent) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                if (string.IsNullOrWhiteSpace(agent))
                    return Results.Json(new { error = "需要 agent" }, statusCode: 400);
                var path = mcp.OpenConfig(agent.Trim());
                return Results.Json(new { ok = true, path });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        // 也提供 POST open，与其它写接口一致（前端可统一 post）
        app.MapPost("/api/mcp/open", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<OpenBody>();
                if (string.IsNullOrWhiteSpace(body?.agent))
                    return Results.Json(new { error = "body 需为 { agent }" }, statusCode: 400);
                var path = mcp.OpenConfig(body.agent.Trim());
                return Results.Json(new { ok = true, path });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapGet("/api/mcp/mother", (HttpContext ctx) =>
        {
            try
            {
                var q = ctx.Request.Query["reveal"].ToString();
                var wantReveal = q is "1" or "true" or "True" or "yes";
                if (wantReveal && !writeAuth(ctx)) return Forbidden();
                return Results.Json(mcp.GetMother(wantReveal));
            }
            catch (Exception ex)
            {
                var msg = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                return Results.Json(new { error = msg }, statusCode: 500);
            }
        });

        app.MapPost("/api/mcp/mother", async (HttpContext ctx) =>
        {
            try
            {
                MotherBody? body = null;
                if (ctx.Request.ContentLength is > 0 || ctx.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    try { body = await ctx.Request.ReadFromJsonAsync<MotherBody>(); }
                    catch (JsonException) { body = null; }
                }
                var wantReveal = body?.reveal == true;
                if (wantReveal && !writeAuth(ctx)) return Forbidden();
                return Results.Json(mcp.GetMother(wantReveal));
            }
            catch (Exception ex)
            {
                var msg = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                return Results.Json(new { error = msg }, statusCode: 500);
            }
        });

        app.MapPost("/api/mcp/remove", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<RemoveBody>();
                if (string.IsNullOrWhiteSpace(body?.name))
                    return Results.Json(new { error = "body needs { name }" }, statusCode: 400);
                var fromMother = body.fromMother ?? true;
                var result = mcp.Remove(body.name.Trim(), fromMother, body.targets);
                return Results.Json(result, statusCode: result.Ok ? 200 : 207);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/mcp/open-mother", (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                var path = mcp.OpenMotherFile();
                return Results.Json(new { ok = true, path });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private sealed record ImportBody(string source);
    private sealed record MetaBody(string name, string? alias, string? note);
    private sealed record EnableBody(string name, bool enabled);
    private sealed record SyncBody(string[]? names, string[]? targets);
    private sealed record PushBody(string? name, string[]? names, string[]? targets, string? source);
    private sealed record AgentEnableBody(string agent, string name, bool enabled);
    private sealed record OpenBody(string agent);
    private sealed record MotherBody(bool? reveal);
    private sealed record RemoveBody(string name, bool? fromMother, string[]? targets);
}
