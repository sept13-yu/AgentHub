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

        app.MapGet("/api/agent-rules/hub-structured", () =>
        {
            var hub = service.ReadStructuredHub();
            return Results.Json(ToStructuredPayload(hub));
        });

        app.MapPut("/api/agent-rules/hub-structured", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = doc.RootElement;
                var shared = root.TryGetProperty("shared", out var sharedEl)
                    && sharedEl.ValueKind == JsonValueKind.String
                    ? sharedEl.GetString() ?? ""
                    : throw new ArgumentException("缺少 shared");
                var extras = ReadStringMap(root, "extras")
                    ?? throw new ArgumentException("缺少 extras");
                var orphans = ReadStringMap(root, "orphans");
                var hub = service.WriteStructuredHub(shared, extras, orphans);
                return Results.Json(ToStructuredPayload(hub));
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/agent-rules/hub-restore-empty", (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                var hub = service.RestoreEmptyTemplate();
                return Results.Json(ToStructuredPayload(hub));
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

        app.MapPost("/api/agent-rules/open-agent", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var agentId = doc.RootElement.TryGetProperty("agentId", out var el)
                    && el.ValueKind == JsonValueKind.String
                    ? el.GetString() ?? ""
                    : throw new ArgumentException("缺少 agentId");
                service.OpenAgent(agentId);
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
        source = new
        {
            status.Source.Exists,
            status.Source.Valid,
            status.Source.WillMigrate,
            status.Source.Warnings,
        },
    };

    private static object ToStructuredPayload(AgentRulesStructuredHub hub) => new
    {
        path = hub.Path,
        hub.Exists,
        hub.Enabled,
        hub.Valid,
        hub.Shared,
        extras = hub.Extras,
        orphans = hub.Orphans,
        agents = hub.Agents.Select(a => new { agentId = a.AgentId, displayName = a.DisplayName }),
    };

    private static Dictionary<string, string>? ReadStringMap(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(name + " 必须是对象");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in el.EnumerateObject())
        {
            map[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? ""
                : throw new ArgumentException($"{name}.{prop.Name} 必须是字符串");
        }
        return map;
    }

    private static string Name(AgentRuleStatus status) => status.ToString() switch
    {
        "NotDetected" => "notDetected",
        "NeedsFirstLaunch" => "needsFirstLaunch",
        "NeedsSync" => "needsSync",
        _ => char.ToLowerInvariant(status.ToString()[0]) + status.ToString()[1..],
    };
}