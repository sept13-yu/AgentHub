using System.Globalization;
using System.Text.Json;
using AgentHub.Core.TokenCore;
using Xunit;

namespace AgentHub.Tests;

public class CodexQuotaParseTests
{
    /// <summary>ChatGPT Pro Lite 实样：唯一周窗在 primary_window，secondary_window 为 null。
    /// 旧解析把 null 槽按 fallback 标成 7d（used=0、无 reset），Flatten 后写覆盖成 100% + 空 period，
    /// 首页就画成「每周 100% / 不限期」。</summary>
    public const string ProLiteWeeklyOnlySecondaryNull = """
        {
          "plan_type": "prolite",
          "rate_limit": {
            "allowed": true,
            "limit_reached": false,
            "primary_window": {
              "used_percent": 52,
              "limit_window_seconds": 604800,
              "reset_after_seconds": 520217,
              "reset_at": 1778605571
            },
            "secondary_window": null
          }
        }
        """;

    public const string PlusBothWindows = """
        {
          "plan_type": "plus",
          "rate_limit": {
            "primary_window": {
              "used_percent": 22,
              "limit_window_seconds": 18000,
              "reset_at": 1778091218
            },
            "secondary_window": {
              "used_percent": 49,
              "limit_window_seconds": 604800,
              "reset_at": 1778605571
            }
          }
        }
        """;

    [Fact]
    public void ProLiteWeeklyOnly_DoesNotRenderAsFullUnlimited()
    {
        using var doc = JsonDocument.Parse(ProLiteWeeklyOnlySecondaryNull);
        var windows = CodexAccountQuota.ParseWindows(doc.RootElement);

        Assert.Equal("prolite", CodexAccountQuota.ReadPlan(doc.RootElement));
        var weekly = Assert.Single(windows);
        Assert.Equal("7d", weekly["id"]);
        Assert.Equal(48m, ToDec(weekly["remainPercent"]));
        Assert.Equal(52m, ToDec(weekly["usedPercent"]));
        Assert.Equal(604800L, ToLong(weekly["windowSeconds"]));
        Assert.Equal(UnixIso(1778605571), weekly["resetAt"]);
    }

    [Fact]
    public void ProLiteWeeklyOnly_HomepageTileKeepsRealRemainAndReset()
    {
        using var doc = JsonDocument.Parse(ProLiteWeeklyOnlySecondaryNull);
        var items = FlattenAccount("live", "happyalways001100 · 当前", doc.RootElement);

        var weekly = Assert.Single(items, i => (string?)i["id"] == "codex:live:7d");
        Assert.Equal("每周", weekly["name"]);
        Assert.Equal(48d, ToDouble(weekly["remainPercent"]), 3);
        Assert.Equal(UnixIso(1778605571), weekly["period"]);
        Assert.Equal("PROLITE", FormatPlanForAssert((string?)weekly["plan"]));
        Assert.DoesNotContain(items, i => (string?)i["id"] == "codex:live:5h");
        // 空 period 在前端 formatPeriod 会变成「不限期」；这里必须带着上游 reset_at
        Assert.False(string.IsNullOrEmpty(weekly["period"] as string));
        Assert.NotEqual("never", weekly["period"]);
    }

    [Fact]
    public void EmptySecondaryOrDisabledPrimary_DoesNotOverwriteWeekly()
    {
        const string json = """
            {
              "plan_type": "prolite",
              "rate_limit": {
                "primary_window": {
                  "used_percent": 52,
                  "limit_window_seconds": 604800,
                  "reset_at": 1778605571
                },
                "secondary_window": { "used_percent": 0, "limit_window_seconds": 0 }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var weekly = Assert.Single(CodexAccountQuota.ParseWindows(doc.RootElement));
        Assert.Equal("7d", weekly["id"]);
        Assert.Equal(48m, ToDec(weekly["remainPercent"]));
        Assert.Equal(UnixIso(1778605571), weekly["resetAt"]);
    }

    [Fact]
    public void PlusBothWindows_StillMapsSessionAndWeekly()
    {
        using var doc = JsonDocument.Parse(PlusBothWindows);
        var windows = CodexAccountQuota.ParseWindows(doc.RootElement);
        Assert.Equal(2, windows.Count);
        Assert.Equal("5h", windows[0]["id"]);
        Assert.Equal(78m, ToDec(windows[0]["remainPercent"]));
        Assert.Equal("7d", windows[1]["id"]);
        Assert.Equal(51m, ToDec(windows[1]["remainPercent"]));
        Assert.Equal(UnixIso(1778091218), windows[0]["resetAt"]);
        Assert.Equal(UnixIso(1778605571), windows[1]["resetAt"]);
    }

    [Fact]
    public void MissingUsedPercent_IsNotTreatedAsZeroUsed()
    {
        const string json = """
            {
              "rate_limit": {
                "primary_window": { "limit_window_seconds": 604800, "reset_at": 1778605571 },
                "secondary_window": null
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(CodexAccountQuota.ParseWindows(doc.RootElement));
    }

    [Fact]
    public void ResetAfterSeconds_FillsResetWhenResetAtAbsent()
    {
        const string json = """
            {
              "rate_limit": {
                "primary_window": {
                  "used_percent": 10,
                  "limit_window_seconds": 18000,
                  "reset_after_seconds": 3600
                }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var session = Assert.Single(CodexAccountQuota.ParseWindows(doc.RootElement));
        Assert.Equal("5h", session["id"]);
        Assert.False(string.IsNullOrEmpty(session["resetAt"] as string));
        var reset = DateTimeOffset.Parse((string)session["resetAt"]!, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Assert.InRange((reset - DateTimeOffset.UtcNow).TotalMinutes, 50, 70);
    }

    private static List<Dictionary<string, object?>> FlattenAccount(string key, string label, JsonElement root)
    {
        var sources = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["codex"] = new()
            {
                ["status"] = "ok",
                ["accounts"] = new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["key"] = key,
                        ["label"] = label,
                        ["plan"] = CodexAccountQuota.ReadPlan(root),
                        ["status"] = "ok",
                        ["windows"] = CodexAccountQuota.ParseWindows(root),
                    },
                },
            },
        };
        return QuotaPresenter.Flatten(sources, ["codex"]);
    }

    private static string UnixIso(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("o");

    private static decimal ToDec(object? raw) => raw switch
    {
        decimal m => m,
        double d => (decimal)d,
        int i => i,
        long l => l,
        _ => Convert.ToDecimal(raw, CultureInfo.InvariantCulture),
    };

    private static double ToDouble(object? raw) => Convert.ToDouble(raw, CultureInfo.InvariantCulture);

    private static long ToLong(object? raw) => Convert.ToInt64(raw, CultureInfo.InvariantCulture);

    /// <summary>与前端 formatPlan 一致：纯 ascii 套餐名大写，便于对照砖角标 PROLITE。</summary>
    private static string FormatPlanForAssert(string? raw)
    {
        var t = (raw ?? "").Trim();
        return t.Length == 0 ? "" : t.ToUpperInvariant();
    }
}
