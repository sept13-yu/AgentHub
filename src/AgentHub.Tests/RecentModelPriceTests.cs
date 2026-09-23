using System.Text.Json;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.TokenCore;
using Xunit;

namespace AgentHub.Tests;

public class RecentModelPriceTests
{
    [Fact]
    public void PricesJson_MatchesDefaultPrices()
    {
        var file = LoadPricesJson();
        Assert.Equal("2026-09-23", file.UpdatedAt);
        Assert.False(string.IsNullOrWhiteSpace(file.Note));

        var fromJson = file.Prices!
            .Where(p => !string.IsNullOrWhiteSpace(p.Model))
            .ToDictionary(p => p.Model!, StringComparer.OrdinalIgnoreCase);
        var fromCode = PriceSyncService.DefaultPrices
            .ToDictionary(p => p.Model!, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(fromJson.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
            fromCode.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        foreach (var (model, jsonRow) in fromJson)
        {
            Assert.True(fromCode.TryGetValue(model, out var codeRow), model);
            Assert.Equal(jsonRow.InputPer1m, codeRow.InputPer1m);
            Assert.Equal(jsonRow.OutputPer1m, codeRow.OutputPer1m);
            Assert.Equal(jsonRow.CacheReadPer1m, codeRow.CacheReadPer1m);
            Assert.Equal(jsonRow.CacheWritePer1m, codeRow.CacheWritePer1m);
            Assert.Equal(jsonRow.Currency, codeRow.Currency, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MimoV26_OfficialCnyRowsUnchanged()
    {
        AssertRow("mimo-v2.6-flash", 1.0, 2.0, 0.02, 0.0, "CNY");
        AssertRow("mimo-v2.6-pro", 3.0, 6.0, 0.025, 0.0, "CNY");
        AssertRow("mimo-v2.6-pro-ultraspeed", 30.0, 60.0, 0.25, 0.0, "CNY");
    }

    [Fact]
    public void Grok47_Matches46ShortContextAndFastDouble()
    {
        AssertRow("cursor-grok-4.7-high", 2.0, 6.0, 0.5, null, "USD");
        AssertRow("cursor-grok-4.7-xhigh", 2.0, 6.0, 0.5, null, "USD");
        AssertRow("cursor-grok-4.7-high-fast", 4.0, 12.0, 1.0, null, "USD");
        AssertRow("cursor-grok-4.7-xhigh-fast", 4.0, 12.0, 1.0, null, "USD");
    }

    [Fact]
    public void ClaudeOpus55_OfficialUsdListPrice()
    {
        AssertRow("claude-opus-5-5-thinking-high", 4.0, 20.0, 0.2, 5.0, "USD");
        AssertRow("claude-opus-5-thinking-high", 5.0, 25.0, 0.5, 6.25, "USD");
    }

    [Fact]
    public void Gpt6Family_OfficialShortContextAndFastDouble()
    {
        AssertRow("gpt-6-astra", 10.0, 50.0, 1.0, 12.5, "USD");
        AssertRow("gpt-6-sol", 2.0, 10.0, 0.2, 2.5, "USD");
        AssertRow("gpt-6-sol-fast", 4.0, 20.0, 0.4, 5.0, "USD");
        AssertRow("gpt-6-luna", 0.1, 0.5, 0.01, 0.125, "USD");
        AssertRow("gpt-6-luna-fast", 0.2, 1.0, 0.02, 0.25, "USD");
    }

    [Theory]
    [InlineData("grok-4.7", "cursor-grok-4.7-high")]
    [InlineData("cursor-grok-4.7-medium", "cursor-grok-4.7-high")]
    [InlineData("cursor-grok-4.7-max", "cursor-grok-4.7-xhigh")]
    [InlineData("cursor-grok-4.7-low-fast", "cursor-grok-4.7-high-fast")]
    [InlineData("claude-opus-5-5", "claude-opus-5-5-thinking-high")]
    [InlineData("claude-opus-5-5-high", "claude-opus-5-5-thinking-high")]
    [InlineData("claude-opus-5-thinking-medium", "claude-opus-5-thinking-high")]
    [InlineData("gpt-6-sol-high", "gpt-6-sol")]
    [InlineData("gpt-6-sol-high-fast", "gpt-6-sol-fast")]
    [InlineData("gpt-6-luna-max", "gpt-6-luna")]
    [InlineData("openai/gpt-6-luna", "gpt-6-luna")]
    public void UsageLogAliases_MapToCanonical(string raw, string canonical)
    {
        Assert.True(PriceAliases.TryMap(raw, out var mapped));
        Assert.Equal(canonical, mapped);
    }

    [Theory]
    [InlineData("cursor-grok-4.7-high")]
    [InlineData("cursor-grok-4.7-medium")]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-opus-5-5-thinking-high")]
    [InlineData("gpt-6-sol")]
    [InlineData("gpt-6-sol-fast")]
    [InlineData("gpt-6-luna")]
    [InlineData("gpt-6-luna-fast")]
    [InlineData("gpt-6-astra")]
    [InlineData("mimo-v2.6-flash")]
    public void CanonicalAndAliases_HavePrice(string model)
    {
        Assert.True(UsageCost.HasPrice(model, PriceSyncService.DefaultPrices, "USD"));
    }

    [Theory]
    [InlineData("cursor-grok-4.7-high", "xai/grok-4.7")]
    [InlineData("cursor-grok-4.7-xhigh-fast", "xai/grok-4.7")]
    [InlineData("claude-opus-5-5-thinking-high", "claude-opus-5-5")]
    [InlineData("gpt-6-sol", "gpt-6-sol")]
    [InlineData("gpt-6-sol-fast", "gpt-6-sol")]
    [InlineData("gpt-6-luna", "gpt-6-luna")]
    [InlineData("gpt-6-luna-fast", "gpt-6-luna")]
    [InlineData("gpt-6-astra", "gpt-6-astra")]
    public void LiteLlmMap_UsesVerifiedKeys(string model, string liteKey)
    {
        Assert.True(LiteLlmPriceMap.TryMap(model, out var mapped));
        Assert.Equal(liteKey, mapped);
    }

    [Theory]
    [InlineData("cursor-grok-4.7-high")]
    [InlineData("claude-opus-5-5-thinking-high")]
    [InlineData("gpt-6-sol")]
    [InlineData("gpt-6-luna")]
    public void CanonicalIds_AreNotSelfAliased(string id)
    {
        Assert.False(PriceAliases.TryMap(id, out _));
        Assert.Contains(PriceSyncService.DefaultPrices,
            p => string.Equals(p.Model, id, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertRow(
        string model, double input, double output, double? cacheRead, double? cacheWrite, string currency)
    {
        var row = PriceSyncService.DefaultPrices.FirstOrDefault(p =>
            string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(row);
        Assert.Equal(input, row.InputPer1m);
        Assert.Equal(output, row.OutputPer1m);
        Assert.Equal(cacheRead, row.CacheReadPer1m);
        Assert.Equal(cacheWrite, row.CacheWritePer1m);
        Assert.Equal(currency, row.Currency, StringComparer.OrdinalIgnoreCase);
    }

    private static PriceFile LoadPricesJson()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "prices.json");
        Assert.True(File.Exists(path), path);
        var file = JsonSerializer.Deserialize<PriceFile>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(file);
        Assert.NotNull(file.Prices);
        return file;
    }

    private sealed class PriceFile
    {
        public string? UpdatedAt { get; set; }
        public string? Note { get; set; }
        public List<PriceRow>? Prices { get; set; }
    }
}
