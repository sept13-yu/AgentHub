using AgentHub.Core.ProxyCore;
using AgentHub.Core.TokenCore;
using Xunit;

namespace AgentHub.Tests;

public class MimoV26PriceTests
{
    [Theory]
    [InlineData("mimo-x-flash-preview", "mimo-v2.6-flash")]
    [InlineData("MIMO-X-FLASH-PREVIEW", "mimo-v2.6-flash")]
    [InlineData("X-Flash Preview", "mimo-v2.6-flash")]
    [InlineData("X Flash Preview", "mimo-v2.6-flash")]
    [InlineData("mimo x flash preview", "mimo-v2.6-flash")]
    [InlineData("MiMo V2.6 Flash", "mimo-v2.6-flash")]
    [InlineData("mimo-x-pro-preview", "mimo-v2.6-pro")]
    [InlineData("X-Pro Preview", "mimo-v2.6-pro")]
    [InlineData("X Pro Preview", "mimo-v2.6-pro")]
    [InlineData("mimo x pro preview", "mimo-v2.6-pro")]
    [InlineData("MiMo V2.6 Pro", "mimo-v2.6-pro")]
    [InlineData("mimo-x-pro-ultraspeed-preview", "mimo-v2.6-pro-ultraspeed")]
    [InlineData("MiMo V2.6 Pro Ultraspeed", "mimo-v2.6-pro-ultraspeed")]
    [InlineData("MiMo-V2.6-Pro-UltraSpeed", "mimo-v2.6-pro-ultraspeed")]
    public void PreviewAndDisplayNames_AliasToOfficialIds(string raw, string canonical)
    {
        Assert.True(PriceAliases.TryMap(raw, out var mapped));
        Assert.Equal(canonical, mapped);
    }

    [Theory]
    [InlineData("mimo-v2.6-flash")]
    [InlineData("mimo-v2.6-pro")]
    [InlineData("mimo-v2.6-pro-ultraspeed")]
    public void OfficialIds_AreCanonicalPriceKeys_NotAliases(string id)
    {
        Assert.False(PriceAliases.TryMap(id, out _));
        Assert.Contains(PriceSyncService.DefaultPrices,
            p => string.Equals(p.Model, id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OfficialRows_MatchPublishedCnyListPrices()
    {
        Assert.True(TryRow("mimo-v2.6-flash", out var flash));
        Assert.Equal(1.0, flash.InputPer1m);
        Assert.Equal(2.0, flash.OutputPer1m);
        Assert.Equal(0.02, flash.CacheReadPer1m);
        Assert.Equal("CNY", flash.Currency);

        Assert.True(TryRow("mimo-v2.6-pro", out var pro));
        Assert.Equal(3.0, pro.InputPer1m);
        Assert.Equal(6.0, pro.OutputPer1m);
        Assert.Equal(0.025, pro.CacheReadPer1m);
        Assert.Equal("CNY", pro.Currency);

        Assert.True(TryRow("mimo-v2.6-pro-ultraspeed", out var ultra));
        Assert.Equal(30.0, ultra.InputPer1m);
        Assert.Equal(60.0, ultra.OutputPer1m);
        Assert.Equal(0.25, ultra.CacheReadPer1m);
        Assert.Equal("CNY", ultra.Currency);
    }

    [Theory]
    [InlineData("mimo-x-flash-preview")]
    [InlineData("mimo-x-pro-preview")]
    [InlineData("mimo-v2.6-flash")]
    [InlineData("mimo-v2.6-pro")]
    [InlineData("mimo-v2.6-pro-ultraspeed")]
    [InlineData("X-Flash Preview")]
    [InlineData("X-Pro Preview")]
    public void PreviewAndOfficialIds_HavePrice(string model)
    {
        Assert.True(UsageCost.HasPrice(model, PriceSyncService.DefaultPrices, "CNY"));
    }

    [Theory]
    [InlineData("mimo-x-flash-preview", "MiMo V2.6 Flash")]
    [InlineData("mimo-x-pro-preview", "MiMo V2.6 Pro")]
    [InlineData("mimo-v2.6-flash", "MiMo V2.6 Flash")]
    [InlineData("mimo-v2.6-pro", "MiMo V2.6 Pro")]
    [InlineData("mimo-v2.6-pro-ultraspeed", "MiMo V2.6 Pro Ultraspeed")]
    [InlineData("X-Flash Preview", "MiMo V2.6 Flash")]
    [InlineData("X-Pro Preview", "MiMo V2.6 Pro")]
    [InlineData("xiaomi/mimo-v2.6-flash", "MiMo V2.6 Flash")]
    public void TokenUi_ShowsOfficialDesktopPickerNames(string raw, string label)
    {
        Assert.Equal(label, QoderLocal.ResolveChinaModelDisplay(raw));
    }

    [Fact]
    public void UnrelatedQoderRoute_StillMapsToExistingLabel()
    {
        Assert.Equal("Qwen3.8-Flash", QoderLocal.ResolveChinaModelDisplay("qfmodel"));
    }

    private static bool TryRow(string model, out PriceRow row)
    {
        row = PriceSyncService.DefaultPrices.FirstOrDefault(p =>
            string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase)) ?? new PriceRow();
        return !string.IsNullOrEmpty(row.Model);
    }
}
