using System.IO;
using System.Text.Json;
using AgentHub.Core.DocCore;
using AgentHub.Core.SessionCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentHub.Web;

/// <summary>/api/docs/*（资料中心，方案 §6）。</summary>
public static class DocEndpoints
{
    public static void MapDocEndpoints(this WebApplication app, DocService docs, SessionService sessions, Func<HttpContext, bool> writeAuth)
    {
        IResult Forbidden() => Results.Json(
            new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: StatusCodes.Status403Forbidden);

        // ---------------- 读 ----------------

        app.MapGet("/api/docs", (HttpContext ctx, string? kind, string? q) =>
        {
            kind ??= "all";
            var skillsAll = kind is "all" or "skills" ? docs.Skills.List(q) : [];
            var libraryAll = kind is "all" or "library" ? docs.ListLibrary(q) : [];
            const int skillCap = 100, libCap = 200;
            var skillsHint = skillsAll.Count > skillCap ? $"只显示前 {skillCap}" : null;
            var libraryHint = libraryAll.Count > libCap
                ? $"只显示前 {libCap}"
                : libraryAll.Count == 0 && Directory.Exists(docs.LibraryRoot)
                    ? $"资料目录 {docs.LibraryRoot} 下没有 Plans / SandBox 文稿"
                    : null;
            return Results.Json(new
            {
                skillsRoot = docs.SkillsRoot,
                skillsRootExists = Directory.Exists(docs.SkillsRoot),
                skillsStore = docs.SkillsStore,
                skillsStoreExists = Directory.Exists(docs.SkillsStore),
                skillsCli = docs.Skills.CliStatus,
                libraryRoot = docs.LibraryRoot,
                libraryRootExists = Directory.Exists(docs.LibraryRoot),
                skillsHint,
                libraryHint,
                skills = skillsAll.Take(skillCap).Select(s => new
                {
                    kind = "skill",
                    name = s.Name,
                    displayName = s.DisplayName,
                    description = s.Description,
                    path = s.PreviewPath,
                    relPath = s.Name,
                    state = char.ToLowerInvariant(s.State.ToString()[0]) + s.State.ToString()[1..],
                    modifiedUtc = s.ModifiedUtc,
                    enabled = s.State is ManagedSkillState.Enabled or ManagedSkillState.Modified or ManagedSkillState.LegacyLink,
                    conflict = s.State == ManagedSkillState.Conflict,
                    s.CanEnable,
                    s.CanDisable,
                    s.CanManage,
                    s.CanUpdate,
                }).ToList(),
                library = libraryAll.Take(libCap).Select(s => new
                {
                    kind = s.Kind,
                    name = s.Name,
                    path = s.Path,
                    relPath = s.RelPath,
                    sizeBytes = s.SizeBytes,
                    modifiedUtc = s.ModifiedUtc,
                    agentId = s.AgentId,
                    project = s.Project ?? "其他",
                }).ToList(),
            });
        });

        app.MapGet("/api/docs/preview", (string path) =>
        {
            var r = docs.Preview(path);
            return r is null
                ? Results.Json(new { error = "文件不可读（不存在或不在资料中心根目录内）" }, statusCode: 404)
                : Results.Json(new { path, content = r.Value.Content, sizeBytes = r.Value.SizeBytes, modifiedUtc = r.Value.ModifiedUtc });
        });

        // ---------------- 写（需壳内 token） ----------------

        app.MapPost("/api/docs/open", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<OpenBody>();
                if (body?.path is null) return Results.Json(new { error = "body 须为 {path}" }, statusCode: 400);
                docs.Open(body.path);
                return Results.Json(new { ok = true });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
            catch (FileNotFoundException ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 404);
            }
        });

        app.MapPost("/api/docs/skills/disable", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            var body = await ctx.Request.ReadFromJsonAsync<NameBody>();
            if (string.IsNullOrWhiteSpace(body?.name))
                return Results.Json(new { error = "body 须为 {name}" }, statusCode: 400);
            try
            {
                var result = docs.Skills.Disable(body.name.Trim());
                return Results.Json(result, statusCode: result.Ok ? 200 : 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/docs/skills/enable", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            var body = await ctx.Request.ReadFromJsonAsync<NameBody>();
            if (string.IsNullOrWhiteSpace(body?.name))
                return Results.Json(new { error = "body 须为 {name}" }, statusCode: 400);
            try
            {
                var result = docs.Skills.Enable(body.name.Trim());
                return Results.Json(result, statusCode: result.Ok ? 200 : 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/docs/skills/update", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            string[]? names = null;
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<UpdateBody>();
                names = body?.names;
            }
            catch (JsonException) { }
            try
            {
                var r = await docs.Skills.UpdateAsync(names, ctx.RequestAborted);
                return Results.Json(new
                {
                    ok = r.Errors.Count == 0,
                    updated = r.Updated,
                    skipped = r.Skipped,
                    errors = r.Errors,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/docs/skills/manage", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            var body = await ctx.Request.ReadFromJsonAsync<NameBody>();
            if (string.IsNullOrWhiteSpace(body?.name))
                return Results.Json(new { error = "body 须为 {name}" }, statusCode: 400);
            var result = docs.Skills.Manage(body.name.Trim());
            return Results.Json(result, statusCode: result.Ok ? 200 : 400);
        });

        app.MapPost("/api/docs/skills/resolve", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            var body = await ctx.Request.ReadFromJsonAsync<ResolveBody>();
            if (string.IsNullOrWhiteSpace(body?.name)
                || !Enum.TryParse<ModifiedResolution>(body.action, ignoreCase: true, out var action))
                return Results.Json(new { error = "body 须为 {name, action: keepLocalAsStore|restoreFromStore}" }, statusCode: 400);
            var result = docs.Skills.ResolveModified(body.name.Trim(), action);
            return Results.Json(result, statusCode: result.Ok ? 200 : 400);
        });

        app.MapGet("/api/docs/skills/legacy", () => Results.Json(docs.Skills.InspectLegacy()));
        app.MapPost("/api/docs/skills/legacy/migrate", (HttpContext ctx) =>
            writeAuth(ctx) ? Results.Json(docs.Skills.MigrateLegacy()) : Forbidden());
        app.MapPost("/api/docs/skills/legacy/clean", (HttpContext ctx) =>
            writeAuth(ctx) ? Results.Json(docs.Skills.CleanLegacyStore()) : Forbidden());

        app.MapPost("/api/docs/open-root", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            var body = await ctx.Request.ReadFromJsonAsync<RootBody>();
            var path = body?.kind switch
            {
                "active" => docs.SkillsRoot,
                "store" => docs.SkillsStore,
                "library" => docs.LibraryRoot,
                _ => null,
            };
            if (path is null) return Results.Json(new { error = "kind 非法" }, statusCode: 400);
            try
            {
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return Results.Json(new { ok = true });
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        // 会话源文件「用默认程序打开」（v8 预览栏按钮）
        app.MapPost("/api/sessions/open", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx)) return Forbidden();
            var body = await ctx.Request.ReadFromJsonAsync<OpenSessionBody>();
            if (body is null || string.IsNullOrEmpty(body.agent) || string.IsNullOrEmpty(body.id))
                return Results.Json(new { error = "body 须为 {agent, id}" }, statusCode: 400);
            try
            {
                if (body.agent.Equals("cursor", StringComparison.OrdinalIgnoreCase))
                    return Results.Json(new { error = "Cursor 不提供打开原文件" }, statusCode: 400);
                var detail = await sessions.LoadAsync(body.agent, body.id);
                var file = detail?.Summary.SourceFile;
                var exists = file is not null
                    && (System.IO.File.Exists(file) || System.IO.Directory.Exists(file));
                if (!exists)
                    return Results.Json(new { error = "源文件不存在" }, statusCode: 404);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file!) { UseShellExecute = true });
                return Results.Json(new { ok = true, path = file });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });
    }

    private sealed record OpenBody(string path);
    private sealed record OpenSessionBody(string agent, string id);
    private sealed record NameBody(string name);
    private sealed record UpdateBody(string[]? names);
    private sealed record ResolveBody(string name, string action);
    private sealed record RootBody(string kind);
}
