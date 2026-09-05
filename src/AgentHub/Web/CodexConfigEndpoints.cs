using System.Text.Json;
using AgentHub.Core.CodexConfigCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentHub.Web;

/// <summary>/api/codex-config/*（方案 §10）：连接管理 + live 状态 + 应用切换。
/// 写接口全部走壳内写授权；Key 只回 keySet，任何响应与日志不得出现明文 Key。</summary>
public static class CodexConfigEndpoints
{
    public static void MapCodexConfigEndpoints(this WebApplication app,
        CodexConfigService service, Func<HttpContext, bool> writeAuth)
    {
        app.MapGet("/api/codex-config/status", () =>
        {
            try
            {
                return Results.Json(service.GetStatus());
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/codex-config/connections", () =>
            Results.Json(new { connections = service.ListConnections() }));

        app.MapPost("/api/codex-config/connections", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            var (body, error) = await ParseBody(ctx);
            if (error is not null) return Results.Json(new { error }, statusCode: 400);
            try
            {
                var id = service.SaveRelay(null, body.Name, body.BaseUrl, body.DefaultModel,
                    body.SupportsWebSockets, body.UserAgent, body.Originator, body.ApiKey, body.UsageBaseUrl);
                return Results.Json(new { ok = true, id });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPut("/api/codex-config/connections/{id}", async (string id, HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            var (body, error) = await ParseBody(ctx);
            if (error is not null) return Results.Json(new { error }, statusCode: 400);
            try
            {
                var saved = service.SaveRelay(id, body.Name, body.BaseUrl, body.DefaultModel,
                    body.SupportsWebSockets, body.UserAgent, body.Originator, body.ApiKey, body.UsageBaseUrl);
                return Results.Json(new { ok = true, id = saved });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapDelete("/api/codex-config/connections/{id}", (string id, HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                service.Delete(id);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/codex-config/connections/{id}/apply", async (string id, HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                var result = await service.ApplyAsync(id);
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                return Results.Json(new CodexApplyResult { Ok = false, Error = ex.Message }, statusCode: 500);
            }
        });

        app.MapPost("/api/codex-config/import-current", (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                var conn = service.ImportCurrent();
                return Results.Json(new { ok = true, id = conn.Id });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapGet("/api/codex-config/diff/{id}", (string id) =>
        {
            try
            {
                return Results.Json(service.Diff(id));
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });
    }

    private static async Task<(ConnectionBody Body, string? Error)> ParseBody(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (default, "请求体必须是 JSON 对象");
            string? Str(string name) =>
                root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString() : null;
            bool SupportsWebSockets() =>
                root.TryGetProperty("supportsWebSockets", out var el) && el.ValueKind == JsonValueKind.True;
            return (new ConnectionBody(
                Str("name"), Str("baseUrl"), Str("defaultModel"),
                SupportsWebSockets(), Str("userAgent"), Str("originator"),
                Str("apiKey"), Str("usageBaseUrl")), null);
        }
        catch (JsonException)
        {
            return (default, "请求体不是有效 JSON");
        }
    }

    private readonly record struct ConnectionBody(
        string? Name, string? BaseUrl, string? DefaultModel, bool SupportsWebSockets,
        string? UserAgent, string? Originator, string? ApiKey, string? UsageBaseUrl);
}
