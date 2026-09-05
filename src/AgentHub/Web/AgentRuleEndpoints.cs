using System.Text.Json;
using AgentHub.Core.DocCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentHub.Web;

public static class AgentRuleEndpoints
{
    public static void MapAgentRuleEndpoints(this WebApplication app, AgentRuleBootstrapService service,
        Func<HttpContext, bool> writeAuth)
    {
        app.MapGet("/api/agent-rules/status", () => Results.Json(ToPayload(service.Inspect())));
        app.MapGet("/api/agent-rules/preview", () => Results.Json(service.Preview()));
        app.MapGet("/api/agent-rules/hub", () =>
        {
            var hub = service.ReadHub();
            return Results.Json(new { path = hub.Path, hub.Exists, hub.Enabled, hub.Content });
        });

        app.MapPut("/api/agent-rules/hub", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var content = doc.RootElement.TryGetProperty("content", out var el)
                    && el.ValueKind == JsonValueKind.String
                    ? el.GetString() ?? ""
                    : throw new ArgumentException("缺少 content");
                var hub = service.WriteHub(content);
                return Results.Json(new { path = hub.Path, hub.Exists, hub.Enabled, hub.Content });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/agent-rules/enable", (HttpContext ctx) =>
            WriteApply(ctx, writeAuth, service.Enable));
        app.MapPost("/api/agent-rules/disable", (HttpContext ctx) =>
            WriteApply(ctx, writeAuth, service.Disable));
        app.MapPost("/api/agent-rules/update", (HttpContext ctx) =>
            WriteApply(ctx, writeAuth, service.Update));

        app.MapPost("/api/agent-rules/open-hub", (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                service.OpenHub();
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPut("/api/agent-rules/library", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("缺少 path");
                var move = root.TryGetProperty("move", out var moveEl)
                    && moveEl.ValueKind is JsonValueKind.True;
                var result = service.SetLibrary(pathEl.GetString() ?? "", move);
                return Results.Json(new { path = result.Path, result.Moved, notes = result.Notes });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });
    }

    private static IResult WriteApply(HttpContext ctx, Func<HttpContext, bool> writeAuth,
        Func<AgentRulesApplyResult> apply)
    {
        if (!writeAuth(ctx))
            return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
        try
        {
            var result = apply();
            return Results.Json(result, statusCode: result.Ok ? 200 : 409);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }
    }

    private static object ToPayload(AgentRulesStatus status) => new
    {
        status.LibraryRoot,
        status.LibraryRootExists,
        status.SharedRulesPath,
        sharedRulesStatus = Name(status.SharedRulesStatus),
        agents = status.Agents.Select(x => new
        {
            x.AgentId,
            x.DisplayName,
            x.Detected,
            status = Name(x.Status),
            x.RulePath,
            x.Message,
            x.CanWrite,
        }),
        status.HasChanges,
        status.HasConflicts,
        status.Enabled,
    };

    private static string Name(AgentRuleStatus status) => status.ToString() switch
    {
        "NotDetected" => "notDetected",
        "NeedsFirstLaunch" => "needsFirstLaunch",
        "NeedsSync" => "needsSync",
        _ => char.ToLowerInvariant(status.ToString()[0]) + status.ToString()[1..],
    };
}
