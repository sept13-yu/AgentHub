namespace AgentHub.Core.TokenCore;

/// <summary>解析结果：modelId 稳定身份，display 仅展示，Known=false 时保留可追溯原名。</summary>
public sealed record ResolvedModel(string ModelId, string Display, bool Known);

/// <summary>目录价一行。返回后调用方不得修改。</summary>
public sealed record CatalogPrice(
    double InputPer1m, double OutputPer1m, double? CacheReadPer1m, double? CacheWritePer1m, bool IsCny);

public sealed record CatalogAlias(string Name, IReadOnlyList<string>? Agents);

public sealed record CatalogModel(
    string ModelId,
    string Display,
    IReadOnlyList<CatalogAlias> Aliases,
    string? LiteLlmKey,
    IReadOnlyList<string> LegacyPriceKeys,
    CatalogPrice? Price);

public sealed record CatalogFile(
    int SchemaVersion,
    string UpdatedAt,
    string Note,
    IReadOnlyList<CatalogModel> Models);

/// <summary>字段是否出现（区分缺失与显式 null）。</summary>
public sealed record AliasPatchItem(string Name, IReadOnlyList<string>? Agents);

public sealed record ModelPatch(
    string ModelId,
    bool HasDisplay, string? Display,
    bool HasAliases, IReadOnlyList<AliasPatchItem>? Aliases,
    bool HasPrice, CatalogPrice? Price,
    bool HasLiteLlmKey, string? LiteLlmKey);

public sealed record CatalogPatchFile(int SchemaVersion, IReadOnlyList<ModelPatch> Models);

/// <summary>LiteLLM 价的只读视图：查询开始时捕获，供整行回退使用，避免中途刷新读到两套价。</summary>
public sealed record LiteLlmPrices(IReadOnlyDictionary<string, LiteLlmRate> Rates)
{
    public bool TryGet(string key, out LiteLlmRate rate) => Rates.TryGetValue(key, out rate!);
}

/// <summary>LiteLLM 单条价（USD/1M）。</summary>
public sealed record LiteLlmRate(double InputPer1m, double OutputPer1m, double? CacheReadPer1m, double? CacheWritePer1m);
