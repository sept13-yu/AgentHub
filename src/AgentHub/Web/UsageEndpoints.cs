using System.Text.Json;
using System.IO;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.TokenCore;
using AgentHub.Shell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentHub.Web;

/// <summary>/api/usage + /api/quotas（仪表盘）+ /api/settings（设置页）。</summary>
public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this WebApplication app,
        TokenService tokens, QuotaService quotas, AgentHubConfig config,
        Func<HttpContext, bool> writeAuth, Action? onSettingsSaved = null,
        Func<bool>? petIsRunning = null, Func<Task<ScanAllResult>>? usageScan = null,
        Func<string, string?>? pickFolder = null)
    {
        // ---------------- 仪表盘 ----------------

        app.MapGet("/api/usage", (string? range) =>
        {
            try
            {
                return Results.Json(tokens.Usage(UsageRange.Normalize(range)));
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapPost("/api/usage/scan", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                quotas.InvalidateCache();
                if (usageScan is not null) await usageScan();
                else tokens.ScanAll();
                PriceSyncService.RefreshInBackground();
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/quotas", async (bool? force) =>
            Results.Json(await quotas.GetQuotasAsync(force ?? false)));

        app.MapGet("/api/quotas/expiry", async (string? id) =>
        {
            if (id is not ("trae" or "workbuddy"))
                return Results.Json(new { error = "id 须为 trae 或 workbuddy" }, statusCode: 400);
            return Results.Json(await quotas.GetCreditExpiryAsync(id));
        });

        // ---------------- 设置 ----------------

        app.MapGet("/api/settings", (HttpContext ctx) =>
        {
            var shell = writeAuth(ctx);
            var deepseekKey = Dpapi.Unprotect(config.Credentials.DeepSeekKey);
            var relayKey = Dpapi.Unprotect(config.Credentials.RelayKey);
            var relayAuth = Dpapi.Unprotect(config.Credentials.RelayPanelAuthToken);
            var relayRefresh = Dpapi.Unprotect(config.Credentials.RelayPanelRefreshToken);
            var workbuddySession = Dpapi.Unprotect(config.Credentials.WorkBuddySession);
            var traeSession = Dpapi.Unprotect(config.Credentials.TraeSession);
            var update = AppUpdate.Snapshot();
            return Results.Json(new
            {
                app = new
                {
                    config.App.Autostart, config.App.PetEnabled, config.App.PetMode, config.App.PetSize,
                    theme = string.Equals(config.App.Theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark",
                },
                docs = new
                {
                    libraryRoot = config.Docs.LibraryRoot,
                    libraryRootExists = Directory.Exists(config.Docs.LibraryRoot),
                },
                dashboard = new
                {
                    config.Dashboard.CostEstimate,
                    tokenUnit = DashboardSettings.NormalizeTokenUnit(config.Dashboard.TokenUnit),
                    config.Dashboard.ScanIntervalMinutes,
                    config.Dashboard.ShowQuotaDeepSeek,
                    config.Dashboard.ShowQuotaRelay,
                    config.Dashboard.ShowQuotaTrae,
                    config.Dashboard.ShowQuotaWorkBuddy,
                    config.Dashboard.ShowQuotaZcode,
                    config.Dashboard.ShowQuotaCursor,
                    config.Dashboard.ShowQuotaCodex,
                    config.Dashboard.ShowAgentDsh,
                    agentOrder = config.Dashboard.ResolvedAgentOrder(),
                    quotaOrder = config.Dashboard.DeriveQuotaOrder(),
                    costCurrency = DashboardSettings.NormalizeCurrency(config.Dashboard.CostCurrency),
                    prices = PriceSyncService.Resolve(config.Dashboard.PriceOverrides),
                    priceSync = PriceSyncService.Status(),
                },
                credentials = new
                {
                    deepseekKeySet = !string.IsNullOrEmpty(deepseekKey),
                    deepseekKey = shell ? deepseekKey : "",
                    relayKeySet = !string.IsNullOrEmpty(relayKey),
                    relayKey = shell ? relayKey : "",
                    relayPanelBaseUrl = config.Credentials.RelayPanelBaseUrl,
                    relayPanelAuthTokenSet = !string.IsNullOrEmpty(relayAuth),
                    relayPanelRefreshTokenSet = !string.IsNullOrEmpty(relayRefresh),
                    workbuddySessionSet = !string.IsNullOrEmpty(workbuddySession),
                    workbuddySession = shell ? workbuddySession : "",
                    traeSessionSet = !string.IsNullOrEmpty(traeSession),
                    traeSession = shell ? traeSession : "",
                },
                autostartActual = AutostartManager.IsEnabled(),
                petRunning = petIsRunning?.Invoke() ?? false,
                configPath = AgentHubConfig.ConfigPath,
                appVersion = update.current,
                updateInstalled = update.installed,
            });
        });

        app.MapPost("/api/settings/browse-folder", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            if (pickFolder is null)
                return Results.Json(new { error = "当前宿主不支持目录选择" }, statusCode: 400);
            string initial = config.Docs.LibraryRoot;
            try
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (body.RootElement.TryGetProperty("initialPath", out var p) && p.ValueKind == JsonValueKind.String)
                    initial = p.GetString() ?? initial;
            }
            catch (JsonException) { }
            var selected = pickFolder(initial);
            return selected is null
                ? Results.Json(new { cancelled = true })
                : Results.Json(new { path = DocsSettings.NormalizeLibraryRoot(selected) });
        });

        app.MapGet("/api/update", async () => Results.Json(await AppUpdate.CheckAsync()));

        app.MapPost("/api/settings/apply-update", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            return Results.Json(await AppUpdate.ApplyAsync());
        });

        app.MapPost("/api/settings/open-release", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            var url = GithubApiUpdateSource.RepoUrl + "/releases/latest";
            try
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (body.RootElement.TryGetProperty("url", out var p) && p.ValueKind == JsonValueKind.String)
                    url = p.GetString() ?? url;
            }
            catch (JsonException) { }
            if (!AppUpdate.IsReleasePage(url))
                return Results.Json(new { error = "链接无效" }, statusCode: 400);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/settings/open-config", (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                if (!File.Exists(AgentHubConfig.ConfigPath)) config.Save();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AgentHubConfig.ConfigPath)
                {
                    UseShellExecute = true,
                });
                return Results.Json(new { ok = true, path = AgentHubConfig.ConfigPath });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPut("/api/settings", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = doc.RootElement;
                if (root.TryGetProperty("app", out var appEl))
                {
                    ApplyBool(appEl, "petEnabled", v => config.App.PetEnabled = v);
                    ApplyStr(appEl, "petSize", v =>
                    {
                        if (v is "small" or "medium" or "large") config.App.PetSize = v;
                    });
                    ApplyStr(appEl, "theme", v =>
                    {
                        config.App.Theme = string.Equals(v, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
                    });
                    if (appEl.TryGetProperty("autostart", out var au)
                        && (au.ValueKind is JsonValueKind.True or JsonValueKind.False))
                    {
                        if (au.GetBoolean()) AutostartManager.Enable();
                        else AutostartManager.Disable();
                    }
                }
                if (root.TryGetProperty("dashboard", out var dash) && dash.ValueKind == JsonValueKind.Object)
                {
                    ApplyBool(dash, "costEstimate", v => config.Dashboard.CostEstimate = v);
                    ApplyStr(dash, "tokenUnit", v =>
                        config.Dashboard.TokenUnit = DashboardSettings.NormalizeTokenUnit(v));
                    ApplyInt(dash, "scanIntervalMinutes", v =>
                        config.Dashboard.ScanIntervalMinutes = Math.Clamp(v, 0, 1440));
                    ApplyBool(dash, "showQuotaDeepSeek", v => config.Dashboard.ShowQuotaDeepSeek = v);
                    ApplyBool(dash, "showQuotaCursor", v => config.Dashboard.ShowQuotaCursor = v);
                    ApplyBool(dash, "showQuotaRelay", v => config.Dashboard.ShowQuotaRelay = v);
                    ApplyBool(dash, "showQuotaWorkBuddy", v => config.Dashboard.ShowQuotaWorkBuddy = v);
                    ApplyBool(dash, "showQuotaTrae", v =>
                    {
                        config.Dashboard.ShowQuotaTrae = v;
                        config.Dashboard.TraeUsage = v;
                    });
                    ApplyBool(dash, "showQuotaZcode", v => config.Dashboard.ShowQuotaZcode = v);
                    ApplyBool(dash, "showQuotaCodex", v => config.Dashboard.ShowQuotaCodex = v);
                    ApplyBool(dash, "showAgentDsh", v => config.Dashboard.ShowAgentDsh = v);
                    if (dash.TryGetProperty("agentOrder", out var agentEl) && agentEl.ValueKind == JsonValueKind.Array)
                    {
                        config.Dashboard.AgentOrder = DashboardSettings.NormalizeAgentOrder(
                            agentEl.EnumerateArray()
                                .Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString()!));
                    }
                    config.Dashboard.QuotaOrder = config.Dashboard.DeriveQuotaOrder();
                }
                if (root.TryGetProperty("credentials", out var cred))
                {
                    ApplyStr(cred, "relayPanelBaseUrl", v => config.Credentials.RelayPanelBaseUrl = v.Trim().TrimEnd('/'));
                    if (cred.TryGetProperty("deepseekKey", out var dk) && dk.ValueKind == JsonValueKind.String)
                        config.Credentials.DeepSeekKey = Dpapi.Protect(dk.GetString()!);
                    if (cred.TryGetProperty("relayKey", out var rk) && rk.ValueKind == JsonValueKind.String)
                        config.Credentials.RelayKey = Dpapi.Protect(rk.GetString()!);
                    if (cred.TryGetProperty("workbuddySession", out var wbs) && wbs.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(wbs.GetString()))
                        config.Credentials.WorkBuddySession = Dpapi.Protect(wbs.GetString()!.Trim());
                    if (cred.TryGetProperty("traeSession", out var trs) && trs.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(trs.GetString()))
                        config.Credentials.TraeSession = Dpapi.Protect(trs.GetString()!.Trim());
                }

                config.Save();
                quotas.InvalidateCache();
                onSettingsSaved?.Invoke();
                return Results.Json(new { ok = true, notes = Array.Empty<string>() });
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "body 不是合法 JSON" }, statusCode: 400);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });
    }

    private static void ApplyInt(JsonElement el, string name, Action<int> apply)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            apply(n);
    }

    private static void ApplyStr(JsonElement el, string name, Action<string> apply)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            apply(v.GetString()!);
    }

    private static void ApplyBool(JsonElement el, string name, Action<bool> apply)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            apply(v.GetBoolean());
    }

}
