using System.Text.Json;
using System.IO;
using AgentHub.Core.Platform;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.TokenCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentHub.Web;

/// <summary>/api/usage + /api/quotas（仪表盘）+ /api/settings（设置页）。</summary>
public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this WebApplication app,
        TokenService tokens, QuotaService quotas, AgentHubConfig config,
        Func<HttpContext, bool> writeAuth, Action? onSettingsSaved = null,
        Func<Task<ScanAllResult>>? usageScan = null,
        Func<string, string?>? pickFolder = null,
        IAutostartService? autostart = null,
        IAppUpdateService? appUpdate = null)
    {
        autostart ??= UnsupportedAutostartService.Instance;
        appUpdate ??= new ManualAppUpdateService(RuntimeVersion.Current);

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

        app.MapGet("/api/settings", () =>
        {
            var update = appUpdate.Snapshot();
            // 明文凭据不回传：设置页只吃 *Set 标志，密钥仅经 PUT 写入
            return Results.Json(new
            {
                app = new
                {
                    config.App.Autostart,
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
                    agentOrder = config.Dashboard.ResolvedAgentOrder(),
                    quotaOrder = config.Dashboard.ResolvedQuotaOrder(),
                    costCurrency = DashboardSettings.NormalizeCurrency(config.Dashboard.CostCurrency),
                    prices = PriceSyncService.Resolve(config.Dashboard.PriceOverrides),
                    priceSync = PriceSyncService.Status(),
                },
                credentials = new
                {
                    deepseekKeySet = !string.IsNullOrEmpty(Secrets.Unprotect(config.Credentials.DeepSeekKey)),
                    deepseekKey = "",
                    relayKeySet = !string.IsNullOrEmpty(Secrets.Unprotect(config.Credentials.RelayKey)),
                    relayKey = "",
                    relayPanelBaseUrl = config.Credentials.RelayPanelBaseUrl,
                    relayPanelAuthTokenSet = !string.IsNullOrEmpty(Secrets.Unprotect(config.Credentials.RelayPanelAuthToken)),
                    relayPanelRefreshTokenSet = !string.IsNullOrEmpty(Secrets.Unprotect(config.Credentials.RelayPanelRefreshToken)),
                    workbuddySessionSet = !string.IsNullOrEmpty(Secrets.Unprotect(config.Credentials.WorkBuddySession)),
                    workbuddySession = "",
                    traeSessionSet = !string.IsNullOrEmpty(Secrets.Unprotect(config.Credentials.TraeSession)),
                    traeSession = "",
                    cursorCloudApiKeySet = !string.IsNullOrEmpty(Secrets.Unprotect(config.Credentials.CursorCloudApiKey)),
                    cursorCloudApiKey = "",
                },
                autostartActual = autostart.IsSupported && autostart.IsEnabled(),
                autostartSupported = autostart.IsSupported,
                updateSupported = appUpdate.IsSupported,
                secretsSupported = Secrets.IsSupported,
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

        app.MapGet("/api/update", async () => Results.Json(await appUpdate.CheckAsync()));

        app.MapGet("/api/update/progress", () => Results.Json(appUpdate.Progress));

        app.MapPost("/api/settings/apply-update", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            return Results.Json(await appUpdate.ApplyAsync());
        });

        app.MapPost("/api/settings/cancel-update", (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            appUpdate.Cancel();
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/settings/launch-update", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            return Results.Json(await appUpdate.LaunchAsync());
        });

        app.MapPost("/api/settings/open-release", async (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            var url = ProjectLinks.LatestReleaseUrl;
            try
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (body.RootElement.TryGetProperty("url", out var p) && p.ValueKind == JsonValueKind.String)
                    url = p.GetString() ?? url;
            }
            catch (JsonException) { }
            if (!ProjectLinks.IsReleasePage(url))
                return Results.Json(new { error = "链接无效" }, statusCode: 400);
            ShellLauncher.Open(url);
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/settings/open-config", (HttpContext ctx) =>
        {
            if (!writeAuth(ctx))
                return Results.Json(new { error = "forbidden：写操作仅限 AgentHub 壳内" }, statusCode: 403);
            try
            {
                if (!File.Exists(AgentHubConfig.ConfigPath)) config.Save();
                ShellLauncher.Open(AgentHubConfig.ConfigPath);
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
                var notes = new List<string>();
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = doc.RootElement;
                if (root.TryGetProperty("app", out var appEl))
                {
                    ApplyStr(appEl, "theme", v =>
                    {
                        config.App.Theme = string.Equals(v, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
                    });
                    if (appEl.TryGetProperty("autostart", out var au)
                        && (au.ValueKind is JsonValueKind.True or JsonValueKind.False))
                    {
                        if (!autostart.IsSupported)
                            notes.Add("当前宿主不支持开机自启");
                        else if (au.GetBoolean()) autostart.Enable();
                        else autostart.Disable();
                    }
                    if (appEl.TryGetProperty("petEnabled", out _)
                        || appEl.TryGetProperty("petSize", out _)
                        || appEl.TryGetProperty("petMode", out _))
                    {
                        notes.Add("桌面宠物功能已移除");
                    }
                }
                if (root.TryGetProperty("dashboard", out var dash) && dash.ValueKind == JsonValueKind.Object)
                {
                    ApplyBool(dash, "costEstimate", v => config.Dashboard.CostEstimate = v);
                    ApplyStr(dash, "tokenUnit", v =>
                        config.Dashboard.TokenUnit = DashboardSettings.NormalizeTokenUnit(v));
                    ApplyInt(dash, "scanIntervalMinutes", v =>
                        config.Dashboard.ScanIntervalMinutes = Math.Clamp(v, 0, 1440));
                    var wroteAgent = false;
                    if (dash.TryGetProperty("agentOrder", out var agentEl) && agentEl.ValueKind == JsonValueKind.Array)
                    {
                        config.Dashboard.AgentOrder = DashboardSettings.NormalizeAgentOrder(
                            agentEl.EnumerateArray()
                                .Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString()!));
                        wroteAgent = true;
                    }
                    if (dash.TryGetProperty("quotaOrder", out var quotaEl) && quotaEl.ValueKind == JsonValueKind.Array)
                    {
                        config.Dashboard.QuotaOrder = DashboardSettings.NormalizeQuotaOrder(
                            quotaEl.EnumerateArray()
                                .Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString()!));
                    }
                    else if (wroteAgent)
                    {
                        config.Dashboard.QuotaOrder = config.Dashboard.QuotaOrder.Count > 0
                            ? DashboardSettings.MergeQuotaOrder(
                                config.Dashboard.QuotaOrder, config.Dashboard.AgentOrder)
                            : config.Dashboard.DeriveQuotaOrder();
                    }
                }
                if (root.TryGetProperty("credentials", out var cred))
                {
                    ApplyStr(cred, "relayPanelBaseUrl", v => config.Credentials.RelayPanelBaseUrl = v.Trim().TrimEnd('/'));
                    if (cred.TryGetProperty("deepseekKey", out var dk) && dk.ValueKind == JsonValueKind.String)
                        config.Credentials.DeepSeekKey = Secrets.Protect(dk.GetString()!);
                    if (cred.TryGetProperty("relayKey", out var rk) && rk.ValueKind == JsonValueKind.String)
                        config.Credentials.RelayKey = Secrets.Protect(rk.GetString()!);
                    if (cred.TryGetProperty("workbuddySession", out var wbs) && wbs.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(wbs.GetString()))
                        config.Credentials.WorkBuddySession = Secrets.Protect(wbs.GetString()!.Trim());
                    if (cred.TryGetProperty("traeSession", out var trs) && trs.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(trs.GetString()))
                        config.Credentials.TraeSession = Secrets.Protect(trs.GetString()!.Trim());
                    if (cred.TryGetProperty("cursorCloudApiKey", out var cck))
                    {
                        if (cck.ValueKind == JsonValueKind.Null
                            || (cck.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(cck.GetString())))
                            config.Credentials.CursorCloudApiKey = "";
                        else if (cck.ValueKind == JsonValueKind.String)
                            config.Credentials.CursorCloudApiKey = Secrets.Protect(cck.GetString()!.Trim());
                    }
                }

                config.Save();
                quotas.InvalidateCache();
                onSettingsSaved?.Invoke();
                return Results.Json(new { ok = true, notes });
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "body 不是合法 JSON" }, statusCode: 400);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
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
