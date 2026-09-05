using System.IO;
using System.Text.Json;
using AgentHub.Core.SessionCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentHub.Web;

/// <summary>/api/sessions：周档 + 锁 + 子会话合并。写接口须壳内 token。</summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app, SessionService sessions, Func<HttpContext, bool> writeAuth)
    {
        IResult Forbidden() => Results.Json(
            new { error = "forbidden：写操作仅限 AgentHub 壳内（浏览器访问为只读模式）" },
            statusCode: StatusCodes.Status403Forbidden);

        app.MapGet("/api/sessions", async (string? agent, string? q, string? range, string? project, int? offset, int? limit) =>
        {
            var page = await sessions.QueryPageAsync(agent, q, range ?? "week", offset ?? 0, limit ?? 40, project);
            var weekStart = SessionIndex.WeekStartLocal();
            return Results.Json(new
            {
                total = page.Total,
                offset = page.Offset,
                limit = page.Limit,
                lockedCount = page.LockedCount,
                indexedCount = page.IndexedCount,
                indexedAt = page.IndexedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                weekStart = weekStart.ToString("yyyy-MM-dd"),
                cursorAvailable = sessions.Cursor.MissingReason is null,
                cursorMissingReason = sessions.Cursor.MissingReason,
                cursorRunning = CursorProviderRunning(),
                sources = sessions.Sources().Select(s => new { id = s.Id, name = s.Name }).ToList(),
                items = page.Items.Select(s => new
                {
                    id = s.Id,
                    agent = s.AgentId,
                    title = s.Title,
                    project = s.Project,
                    messageCount = s.MessageCount,
                    sizeBytes = s.SizeBytes,
                    lastActivity = LocalIso(s.LastActivityUtc),
                    locked = sessions.Locks.IsLocked(s.AgentId, s.Id),
                    orphanSub = s.OrphanSub,
                }).ToList(),
            });
        });

        app.MapGet("/api/sessions/projects", async (string? agent) =>
        {
            if (string.IsNullOrEmpty(agent) || agent.Equals("all", StringComparison.OrdinalIgnoreCase))
                return Results.Json(Array.Empty<SessionProject>());
            var items = await sessions.ListProjectsAsync(agent);
            return Results.Json(items);
        });

        app.MapPost("/api/sessions/index/refresh", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            await sessions.EnsureIndexAsync(force: true);
            return Results.Json(new { ok = true });
        });

        app.MapGet("/api/sessions/detail", async (string agent, string id) =>
        {
            try
            {
                var detail = await sessions.LoadAsync(agent, id);
                if (detail is null)
                    return Results.Json(new { error = "会话不存在（可能已被删除）" }, statusCode: 404);
                var s = detail.Summary;
                var canOpen = sessions.CanOpen(agent)
                    && !string.IsNullOrEmpty(s.SourceFile)
                    && (File.Exists(s.SourceFile) || Directory.Exists(s.SourceFile));
                return Results.Json(new
                {
                    id = s.Id,
                    agent = s.AgentId,
                    title = s.Title,
                    project = s.Project,
                    messageCount = s.MessageCount,
                    sizeBytes = s.SizeBytes,
                    lastActivity = LocalIso(s.LastActivityUtc),
                    locked = sessions.Locks.IsLocked(s.AgentId, s.Id),
                    orphanSub = s.OrphanSub,
                    canRename = true,
                    canOpen,
                    note = detail.Note,
                    sourceFile = s.SourceFile,
                    messages = detail.Messages.Select(m => new
                    {
                        role = m.Role,
                        timestamp = m.TimestampUtc is { } t ? LocalIso(t) : null,
                        text = m.Text,
                    }).ToList(),
                });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/sessions/export", async (HttpContext ctx, string agent, string id) =>
        {
            var detail = await sessions.LoadAsync(agent, id);
            if (detail is null)
                return Results.Json(new { error = "会话不存在" }, statusCode: 404);
            var md = sessions.ExportMarkdown(detail);
            var safeChars = detail.Summary.Title.Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_').ToArray();
            var safeTitle = safeChars.Length > 0 ? new string(safeChars) : detail.Summary.Id;
            if (safeTitle.Length > 60) safeTitle = safeTitle[..60];
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{agent}-{safeTitle}.md\"; filename*=UTF-8''{Uri.EscapeDataString(detail.Summary.Title)[..Math.Min(120, Uri.EscapeDataString(detail.Summary.Title).Length)]}.md";
            return Results.Text(md, "text/markdown; charset=utf-8");
        });

        app.MapPost("/api/sessions/rename", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            RenameBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<RenameBody>(); }
            catch (JsonException) { body = null; }
            if (body is null || string.IsNullOrWhiteSpace(body.agent) || string.IsNullOrWhiteSpace(body.id)
                || string.IsNullOrWhiteSpace(body.title) || body.title.Length > 200)
                return Results.Json(new { error = "body 须为 {agent, id, title}，title ≤ 200 字" }, statusCode: 400);
            try
            {
                await sessions.RenameAsync(body.agent, body.id, body.title.Trim());
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/sessions/lock", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            LockBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<LockBody>(); }
            catch (JsonException) { body = null; }
            if (body is null || string.IsNullOrWhiteSpace(body.agent) || string.IsNullOrWhiteSpace(body.id))
                return Results.Json(new { error = "body 须为 {agent, id, locked}" }, statusCode: 400);
            sessions.SetLocked(body.agent, body.id, body.locked);
            return Results.Json(new { ok = true, locked = body.locked });
        });

        app.MapPost("/api/sessions/delete", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            DeleteBody? body;
            try { body = await ctx.Request.ReadFromJsonAsync<DeleteBody>(); }
            catch (JsonException) { body = null; }
            if (body?.items is null || body.items.Length == 0)
                return Results.Json(new { error = "body 须为 {items: [{agent, id}], vacuum: bool}" }, statusCode: 400);

            var (results, skipped) = await sessions.DeleteAsync(body.items.Select(x => (x.agent, x.id)).ToList());
            object? vacuum = null;
            if (body.vacuum == true && body.items.Any(x => x.agent.Equals("cursor", StringComparison.OrdinalIgnoreCase)))
            {
                if (CursorProviderRunning())
                    vacuum = new { ok = false, error = "请先退出 Cursor" };
                else
                    vacuum = sessions.Cursor.Vacuum();
            }
            return Results.Json(new { ok = results.All(r => r.Ok), results, skipped, vacuum });
        });

        app.MapPost("/api/sessions/cursor/shell-clean", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            bool vacuum = false;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("vacuum", out var v)
                    && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    vacuum = v.GetBoolean();
            }
            catch (JsonException) { }

            try
            {
                var shells = sessions.Cursor.FindShells();
                var results = sessions.Cursor.CleanShells();
                object? vac = null;
                if (vacuum)
                {
                    if (CursorProviderRunning())
                        vac = new { ok = false, error = "请先退出 Cursor" };
                    else
                        vac = sessions.Cursor.Vacuum();
                }
                return Results.Json(new { ok = results.All(r => r.Ok), found = shells.Count, results, vacuum = vac });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/sessions/cursor/vacuum", (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            var r = sessions.Cursor.Vacuum();
            return Results.Json(r, statusCode: r.Ok ? 200 : 400);
        });

        app.MapGet("/api/sessions/cursor/shells", (HttpContext ctx) =>
        {
            try
            {
                if (sessions.Cursor.MissingReason is { } miss)
                    return Results.Json(new { error = miss }, statusCode: 404);
                var shells = sessions.Cursor.FindShells();
                return Results.Json(new { count = shells.Count, items = shells });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/sessions/cursor/storage", () =>
        {
            try
            {
                if (sessions.Cursor.MissingReason is { } miss)
                    return Results.Json(new { error = miss }, statusCode: 404);
                return Results.Json(sessions.Cursor.GetStorageOverview());
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/sessions/cursor/orphans", () =>
        {
            try
            {
                if (sessions.Cursor.MissingReason is { } miss)
                    return Results.Json(new { error = miss }, statusCode: 404);
                return Results.Json(sessions.Cursor.FindOrphans());
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapPost("/api/sessions/cursor/orphan-clean", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            bool vacuum = false;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("vacuum", out var v)
                    && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    vacuum = v.GetBoolean();
            }
            catch (JsonException) { }

            try
            {
                var result = sessions.Cursor.CleanOrphans();
                object? vac = null;
                if (vacuum)
                {
                    if (CursorProviderRunning())
                        vac = new { ok = false, error = "请先退出 Cursor" };
                    else
                        vac = sessions.Cursor.Vacuum();
                }
                return Results.Json(new
                {
                    ok = result.Ok,
                    deletedRows = result.DeletedRows,
                    deletedBytes = result.DeletedBytes,
                    before = result.Before,
                    vacuum = vac,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });
    }

    private static bool CursorProviderRunning() =>
        AgentHub.Core.SessionCore.Providers.CursorProvider.CursorRunning();

    internal static string LocalIso(DateTime utc)
    {
        var dto = utc.Kind switch
        {
            DateTimeKind.Local => new DateTimeOffset(utc),
            DateTimeKind.Utc => new DateTimeOffset(utc, TimeSpan.Zero),
            _ => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero),
        };
        return dto.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz");
    }

    private sealed record RenameBody(string agent, string id, string title);
    private sealed record LockBody(string agent, string id, bool locked);
    private sealed record DeleteItemBody(string agent, string id);
    private sealed record DeleteBody(DeleteItemBody[] items, bool? vacuum);
}
