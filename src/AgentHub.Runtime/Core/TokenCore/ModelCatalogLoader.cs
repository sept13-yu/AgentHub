using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentHub.Core.TokenCore;

/// <summary>模型目录解析与校验。完整快照冲突则整份拒绝；用户补丁按 modelId 稀疏合并。</summary>
public static partial class ModelCatalogLoader
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex ModelIdRegex();

    public static bool TryParseFull(string json, out CatalogFile file, out string error)
    {
        file = null!;
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "root must be object";
                return false;
            }
            if (HasDupProps(doc.RootElement))
            {
                error = "duplicate property in object";
                return false;
            }
            if (!doc.RootElement.TryGetProperty("schemaVersion", out var sv) || !sv.TryGetInt32(out var ver) || ver != SchemaVersion)
            {
                error = $"schemaVersion must be {SchemaVersion}";
                return false;
            }
            var updatedAt = doc.RootElement.TryGetProperty("updatedAt", out var u) ? u.ToString() : "";
            var note = doc.RootElement.TryGetProperty("note", out var n) ? n.ToString() : "";
            if (!doc.RootElement.TryGetProperty("models", out var modelsEl) || modelsEl.ValueKind != JsonValueKind.Array)
            {
                error = "models must be array";
                return false;
            }
            var models = new List<CatalogModel>();
            var byId = new Dictionary<string, int>(StringComparer.Ordinal);
            var byDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // (scope, alias) -> modelId；scope 与 name 均大小写不敏感，与运行时索引同一规则
            var byAlias = new Dictionary<(string? Scope, string Name), string>(AliasScopeComparer.Instance);
            foreach (var el in modelsEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object || HasDupProps(el))
                {
                    error = "model entry invalid";
                    return false;
                }
                if (!TryReadModel(el, out var model, out error)) return false;
                if (byId.ContainsKey(model.ModelId))
                {
                    error = $"duplicate modelId {model.ModelId}";
                    return false;
                }
                if (byDisplay.TryGetValue(model.Display, out var prevId) && prevId != model.ModelId)
                {
                    error = $"display '{model.Display}' used by {prevId} and {model.ModelId}";
                    return false;
                }
                byId[model.ModelId] = models.Count;
                byDisplay[model.Display] = model.ModelId;
                // modelId / display 自识别
                if (!TryAddAlias(byAlias, null, model.ModelId, model.ModelId, out error)) return false;
                if (!TryAddAlias(byAlias, null, model.Display, model.ModelId, out error)) return false;
                foreach (var alias in model.Aliases)
                {
                    if (alias.Agents is { Count: > 0 } scoped)
                    {
                        foreach (var tool in scoped)
                        {
                            if (!TryAddAlias(byAlias, tool, alias.Name, model.ModelId, out error)) return false;
                        }
                    }
                    else
                    {
                        if (!TryAddAlias(byAlias, null, alias.Name, model.ModelId, out error)) return false;
                    }
                }
                models.Add(model);
            }
            file = new CatalogFile(ver, updatedAt, note, models);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryParsePatch(string json, out CatalogPatchFile patch, out string error)
    {
        patch = null!;
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "root must be object";
                return false;
            }
            if (HasDupProps(doc.RootElement))
            {
                error = "duplicate property in object";
                return false;
            }
            if (!doc.RootElement.TryGetProperty("schemaVersion", out var sv) || !sv.TryGetInt32(out var ver) || ver != SchemaVersion)
            {
                error = $"schemaVersion must be {SchemaVersion}";
                return false;
            }
            var list = new List<ModelPatch>();
            if (doc.RootElement.TryGetProperty("models", out var modelsEl))
            {
                if (modelsEl.ValueKind != JsonValueKind.Array)
                {
                    error = "patch models must be array";
                    return false;
                }
                foreach (var el in modelsEl.EnumerateArray())
                {
                    if (!TryReadPatch(el, out var p, out error)) return false;
                    list.Add(p);
                }
            }
            patch = new CatalogPatchFile(ver, list);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>用户补丁合并到完整基线。aliases 整组替换；price:null 清除；legacyPriceKeys 禁止补丁修改。</summary>
    public static bool TryMergePatch(CatalogFile baseline, CatalogPatchFile patch, out CatalogFile merged, out string error)
    {
        merged = baseline;
        error = "";
        var map = baseline.Models.ToDictionary(m => m.ModelId, StringComparer.Ordinal);
        foreach (var p in patch.Models)
        {
            if (p.ModelId.StartsWith("raw:", StringComparison.Ordinal))
            {
                error = $"modelId {p.ModelId} cannot be patched";
                return false;
            }
            if (!map.TryGetValue(p.ModelId, out var baseModel))
            {
                if (!p.HasDisplay || string.IsNullOrWhiteSpace(p.Display))
                {
                    error = $"new modelId {p.ModelId} requires display";
                    return false;
                }
                map[p.ModelId] = new CatalogModel(p.ModelId, p.Display!, [], null, [], null);
                baseModel = map[p.ModelId];
            }
            var display = p.HasDisplay && !string.IsNullOrWhiteSpace(p.Display) ? p.Display! : baseModel.Display;
            var aliases = p.HasAliases
                ? (IReadOnlyList<CatalogAlias>)(p.Aliases ?? []).Select(a => new CatalogAlias(a.Name, a.Agents)).ToList()
                : baseModel.Aliases;
            var price = p.HasPrice ? p.Price : baseModel.Price;
            var lite = p.HasLiteLlmKey ? p.LiteLlmKey : baseModel.LiteLlmKey;
            map[p.ModelId] = baseModel with { Display = display, Aliases = aliases, Price = price, LiteLlmKey = lite };
        }
        // 重新校验身份冲突
        var rebuilt = map.Values.OrderBy(m => m.ModelId, StringComparer.Ordinal).ToList();
        using var tmp = new MemoryStream();
        // 直接走 TryParseFull 保证约束一致
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            updatedAt = baseline.UpdatedAt,
            note = baseline.Note,
            models = rebuilt.Select(m => new
            {
                modelId = m.ModelId,
                display = m.Display,
                aliases = m.Aliases.Select(a => new
                {
                    name = a.Name,
                    agents = a.Agents,
                }),
                liteLlmKey = m.LiteLlmKey,
                legacyPriceKeys = m.LegacyPriceKeys,
                price = m.Price is null ? null : new
                {
                    inputPer1m = m.Price.InputPer1m,
                    outputPer1m = m.Price.OutputPer1m,
                    cacheReadPer1m = m.Price.CacheReadPer1m,
                    cacheWritePer1m = m.Price.CacheWritePer1m,
                    currency = m.Price.IsCny ? "CNY" : "USD",
                },
            }),
        });
        if (!TryParseFull(payload, out merged, out error)) return false;
        return true;
    }

    /// <summary>别名冲突判定键：来源与名称都按大小写不敏感比较，和运行时索引一致。</summary>
    private sealed class AliasScopeComparer : IEqualityComparer<(string? Scope, string Name)>
    {
        public static readonly AliasScopeComparer Instance = new();

        public bool Equals((string? Scope, string Name) a, (string? Scope, string Name) b)
            => string.Equals(a.Scope ?? "", b.Scope ?? "", StringComparison.OrdinalIgnoreCase)
            && StringComparer.OrdinalIgnoreCase.Equals(a.Name, b.Name);

        public int GetHashCode((string? Scope, string Name) key)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Scope ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }

    private static bool TryAddAlias(
        Dictionary<(string? Scope, string Name), string> index,
        string? tool, string name, string modelId, out string error)
    {
        error = "";
        var key = (tool, name.Trim().ToLowerInvariant());
        if (index.TryGetValue(key, out var existing) && existing != modelId)
        {
            error = $"alias '{name}' (scope={tool ?? "*"}) -> {existing} and {modelId}";
            return false;
        }
        index[key] = modelId;
        return true;
    }

    private static bool TryReadModel(JsonElement el, out CatalogModel model, out string error)
    {
        model = null!;
        error = "";
        if (!TryGetString(el, "modelId", required: true, out var id, out error)) return false;
        if (!ModelIdRegex().IsMatch(id!))
        {
            error = $"invalid modelId '{id}'";
            return false;
        }
        if (!TryGetString(el, "display", required: true, out var display, out error)) return false;
        var aliases = new List<CatalogAlias>();
        if (el.TryGetProperty("aliases", out var aEl))
        {
            if (aEl.ValueKind != JsonValueKind.Array) { error = "aliases must be array"; return false; }
            foreach (var a in aEl.EnumerateArray())
            {
                if (!TryReadAlias(a, out var alias, out error)) return false;
                aliases.Add(alias);
            }
        }
        if (!TryGetString(el, "liteLlmKey", required: false, out var lite, out error)) return false;
        var legacy = new List<string>();
        if (el.TryGetProperty("legacyPriceKeys", out var lgEl))
        {
            if (lgEl.ValueKind != JsonValueKind.Array) { error = "legacyPriceKeys must be array"; return false; }
            foreach (var x in lgEl.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.String) { error = "legacyPriceKeys entries must be string"; return false; }
                var key = x.GetString()?.Trim() ?? "";
                if (key.Length > 0) legacy.Add(key);
            }
        }
        CatalogPrice? price = null;
        if (el.TryGetProperty("price", out var pEl))
        {
            // 显式 null = 无目录价；其余非对象类型才报错
            if (pEl.ValueKind == JsonValueKind.Object)
            {
                if (!TryReadPrice(pEl, out price, out error)) return false;
            }
            else if (pEl.ValueKind != JsonValueKind.Null)
            {
                error = "price must be object or null";
                return false;
            }
        }
        model = new CatalogModel(id!, display!, aliases, lite, legacy, price);
        return true;
    }

    private static bool TryReadPrice(JsonElement pEl, out CatalogPrice? price, out string error)
    {
        price = null;
        error = "";
        if (HasDupProps(pEl)) { error = "price duplicate property"; return false; }
        if (!TryReadFinitePrice(pEl, "inputPer1m", required: true, out var input, out error)) return false;
        if (!TryReadFinitePrice(pEl, "outputPer1m", required: true, out var output, out error)) return false;
        if (!TryGetString(pEl, "currency", required: true, out var cur, out error)) return false;
        cur = cur!.ToUpperInvariant();
        if (cur is not ("CNY" or "USD"))
        {
            error = "price.currency must be CNY or USD";
            return false;
        }
        if (!TryReadFinitePrice(pEl, "cacheReadPer1m", required: false, out var cr, out error)) return false;
        if (!TryReadFinitePrice(pEl, "cacheWritePer1m", required: false, out var cw, out error)) return false;
        price = new CatalogPrice(input!.Value, output!.Value, cr, cw, cur == "CNY");
        return true;
    }

    private static bool TryReadPatch(JsonElement el, out ModelPatch patch, out string error)
    {
        patch = null!;
        error = "";
        if (el.ValueKind != JsonValueKind.Object) { error = "patch entry must be object"; return false; }
        if (HasDupProps(el)) { error = "patch entry duplicate property"; return false; }
        var id = el.TryGetProperty("modelId", out var idEl) ? idEl.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(id) || !ModelIdRegex().IsMatch(id) && !id.StartsWith("raw:", StringComparison.Ordinal))
        {
            error = $"invalid patch modelId '{id}'";
            return false;
        }
        var hasDisplay = el.TryGetProperty("display", out var dEl);
        if (hasDisplay && dEl.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            error = "patch display must be string";
            return false;
        }
        var display = hasDisplay ? dEl.GetString() : null;
        var hasAliases = el.TryGetProperty("aliases", out var aEl);
        List<AliasPatchItem>? aliases = null;
        if (hasAliases)
        {
            if (aEl.ValueKind == JsonValueKind.Null) aliases = [];
            else if (aEl.ValueKind == JsonValueKind.Array)
            {
                aliases = [];
                foreach (var a in aEl.EnumerateArray())
                {
                    // 与完整目录同一套 alias 规则：重复属性、类型、空作用域都拒绝
                    if (!TryReadAlias(a, out var item, out error)) return false;
                    aliases.Add(new AliasPatchItem(item.Name, item.Agents));
                }
            }
            else { error = "patch aliases must be array or null"; return false; }
        }
        var hasPrice = el.TryGetProperty("price", out var pEl);
        CatalogPrice? price = null;
        if (hasPrice && pEl.ValueKind == JsonValueKind.Object)
        {
            if (!TryReadPrice(pEl, out price, out error)) return false;
        }
        else if (hasPrice && pEl.ValueKind != JsonValueKind.Null)
        {
            error = "patch price must be object or null";
            return false;
        }
        // liteLlmKey 只接受字符串或显式 null；其他类型必须报错，不能当成清除映射
        var hasLite = el.TryGetProperty("liteLlmKey", out var liteEl);
        if (hasLite && liteEl.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            error = "patch liteLlmKey must be string or null";
            return false;
        }
        var lite = hasLite && liteEl.ValueKind == JsonValueKind.String ? liteEl.GetString()?.Trim() : null;
        if (el.TryGetProperty("legacyPriceKeys", out _))
        {
            error = "legacyPriceKeys is read-only in user patches";
            return false;
        }
        patch = new ModelPatch(id!, hasDisplay, display, hasAliases, aliases, hasPrice, price, hasLite, lite);
        return true;
    }

    /// <summary>alias 对象校验：完整目录与用户补丁共用，避免两套规则分叉。</summary>
    private static bool TryReadAlias(JsonElement a, out CatalogAlias alias, out string error)
    {
        alias = null!;
        error = "";
        if (a.ValueKind != JsonValueKind.Object || HasDupProps(a)) { error = "alias must be object"; return false; }
        if (!TryGetString(a, "name", required: true, out var name, out error)) return false;
        var agents = ReadAliasAgents(a, out error);
        if (error.Length > 0) return false;
        alias = new CatalogAlias(name!, agents);
        return true;
    }

    /// <summary>agents 字段：缺失/显式 null = 通用别名；数组时元素必须是字符串且非空。</summary>
    private static IReadOnlyList<string>? ReadAliasAgents(JsonElement a, out string error)
    {
        error = "";
        if (!a.TryGetProperty("agents", out var agEl)) return null;
        if (agEl.ValueKind == JsonValueKind.Null) return null;
        if (agEl.ValueKind != JsonValueKind.Array) { error = "alias agents must be array"; return null; }
        var agents = new List<string>();
        foreach (var x in agEl.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String) { error = "alias agents entries must be string"; return null; }
            var tool = x.GetString()?.Trim() ?? "";
            if (tool.Length > 0) agents.Add(tool);
        }
        if (agents.Count == 0) { error = "alias agents empty"; return null; }
        return agents;
    }

    private static bool HasDupProps(JsonElement obj)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
        {
            if (!seen.Add(p.Name)) return true;
        }
        return false;
    }

    /// <summary>读字符串字段：属性存在但类型不符必须报错，不静默转空；显式 null 视为未提供。</summary>
    private static bool TryGetString(JsonElement obj, string name, bool required, out string? value, out string error)
    {
        error = "";
        value = null;
        if (!obj.TryGetProperty(name, out var el))
        {
            if (!required) return true;
            error = $"{name} required";
            return false;
        }
        if (el.ValueKind == JsonValueKind.Null)
        {
            if (!required) return true;
            error = $"{name} required";
            return false;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            error = $"{name} must be string";
            return false;
        }
        var text = el.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            if (!required) return true;
            error = $"{name} must not be empty";
            return false;
        }
        value = text;
        return true;
    }

    /// <summary>读价格数值：必须 Number 且有限非负；缺失与显式 null 都表示未提供。</summary>
    private static bool TryReadFinitePrice(JsonElement obj, string name, bool required, out double? value, out string error)
    {
        error = "";
        value = null;
        if (!obj.TryGetProperty(name, out var el))
        {
            if (!required) return true;
            error = $"price.{name} required";
            return false;
        }
        if (el.ValueKind == JsonValueKind.Null)
        {
            if (!required) return true;
            error = $"price.{name} required";
            return false;
        }
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var parsed))
        {
            error = $"price.{name} must be number";
            return false;
        }
        if (!double.IsFinite(parsed))
        {
            error = $"price.{name} must be finite";
            return false;
        }
        if (parsed < 0)
        {
            error = $"price.{name} must be >= 0";
            return false;
        }
        value = parsed;
        return true;
    }
}
