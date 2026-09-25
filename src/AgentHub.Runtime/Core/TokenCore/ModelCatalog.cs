using System.Collections.Concurrent;
using System.Text;

namespace AgentHub.Core.TokenCore;

/// <summary>目录快照：一次查询内展示归一、noPrice、估价共用同一版本。
/// 构造后不可变；LiteLLM 整行回退数据也一并捕获，避免查询中途刷新读到两套价。</summary>
public sealed class ModelCatalogSnapshot
{
    private readonly Dictionary<string, CatalogModel> _byId;
    private readonly Dictionary<string, CatalogModel> _byName;
    private readonly Dictionary<(string Tool, string Name), CatalogModel> _byScoped;
    private readonly Dictionary<string, CatalogPrice> _rawPriceOverrides;
    private readonly LiteLlmPrices? _litePrices;
    private readonly ConcurrentDictionary<(string Tool, string Raw), ResolvedModel> _resolveCache = new();

    public ModelCatalogSnapshot(
        CatalogFile file,
        IReadOnlyDictionary<string, CatalogPrice>? rawPriceOverrides = null,
        LiteLlmPrices? litePrices = null)
    {
        File = file;
        _byId = new Dictionary<string, CatalogModel>(StringComparer.Ordinal);
        _byName = new Dictionary<string, CatalogModel>(StringComparer.OrdinalIgnoreCase);
        _byScoped = new Dictionary<(string Tool, string Name), CatalogModel>(ScopedComparer.Instance);
        foreach (var model in file.Models)
        {
            _byId[model.ModelId] = model;
            _byName[model.ModelId] = model;
            _byName[model.Display] = model;
            foreach (var alias in model.Aliases)
            {
                if (alias.Agents is { Count: > 0 } scoped)
                {
                    foreach (var tool in scoped)
                        _byScoped[(tool, alias.Name)] = model;
                }
                else
                {
                    _byName[alias.Name] = model;
                }
            }
        }
        _rawPriceOverrides = rawPriceOverrides is null
            ? new Dictionary<string, CatalogPrice>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, CatalogPrice>(rawPriceOverrides, StringComparer.OrdinalIgnoreCase);
        _litePrices = litePrices;
    }

    /// <summary>来源限定别名的键比较：大小写不敏感，与校验端同一规则。</summary>
    private sealed class ScopedComparer : IEqualityComparer<(string Tool, string Name)>
    {
        public static readonly ScopedComparer Instance = new();

        public bool Equals((string Tool, string Name) a, (string Tool, string Name) b)
            => StringComparer.OrdinalIgnoreCase.Equals(a.Tool, b.Tool)
            && StringComparer.OrdinalIgnoreCase.Equals(a.Name, b.Name);

        public int GetHashCode((string Tool, string Name) key)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Tool),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }

    public CatalogFile File { get; }

    public IReadOnlyList<CatalogModel> Models => File.Models;

    public ResolvedModel Resolve(string tool, string? rawModel)
    {
        var raw = (rawModel ?? "").Trim();
        var key = (tool ?? "", raw);
        return _resolveCache.GetOrAdd(key, static (k, self) => self.ResolveCore(k.Tool, k.Raw), this);
    }

    public bool TryGetPrice(string tool, string? rawModel, out CatalogPrice price)
        => TryGetPrice(tool, rawModel, Resolve(tool, rawModel), out price);

    /// <summary>noPrice / HasPrice / Estimate 共用此路径，保证标志与金额一致。</summary>
    public bool TryGetPrice(string tool, string? rawModel, ResolvedModel resolved, out CatalogPrice price)
    {
        var raw = (rawModel ?? "").Trim();
        // 1) 用户旧覆价：原始名精确（最高优先级）
        if (raw.Length > 0 && _rawPriceOverrides.TryGetValue(raw, out price)) return true;
        // 2) 同身份的受控包装候选原名覆价：cn:grok-4.6-high → grok-4.6-high 的覆盖仍然生效。
        //    只用于**同一条 raw 的包装候选**，不推广到型号下其他 alias（high 的价不会给 xhigh）。
        if (raw.Length > 0 && resolved.Known)
        {
            foreach (var candidate in WrapperCandidates(raw))
            {
                if (candidate.Equals(raw, StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryLookup(tool, candidate, out var hit)) continue;
                if (!string.Equals(hit.ModelId, resolved.ModelId, StringComparison.Ordinal)) continue;
                if (_rawPriceOverrides.TryGetValue(candidate, out price)) return true;
            }
        }
        // 3) 稳定 modelId 覆价
        if (_rawPriceOverrides.TryGetValue(resolved.ModelId, out price)) return true;
        // 4) 已识别型号的目录价
        if (resolved.Known && _byId.TryGetValue(resolved.ModelId, out var model) && model.Price is { } catalogPrice)
        {
            price = catalogPrice;
            return true;
        }
        // 5) LiteLLM 整行精确回退（同身份候选；不跨档借价），数据来自本快照
        if (_litePrices is not null && TryLiteFallback(tool, raw, resolved, out price)) return true;
        price = default!;
        return false;
    }

    private bool TryLiteFallback(string tool, string raw, ResolvedModel resolved, out CatalogPrice price)
    {
        price = default!;
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in LiteFallbackCandidates(raw, resolved))
        {
            if (name.Length == 0 || !tried.Add(name)) continue;
            // 已识别原名 / 包装候选若最终解析为其他 modelId，不得借价（R14）。
            // 必须走完整 Resolve：候选自身可能还带合法包装，直接索引查不到不等于身份相容。
            // 原名本身是待查键，不做归属排除；其余候选必须与已解析型号同身份。
            if (!name.Equals(raw, StringComparison.OrdinalIgnoreCase) && resolved.Known)
            {
                var candidateResolved = ResolveCore(tool, name);
                if (candidateResolved.Known
                    && !string.Equals(candidateResolved.ModelId, resolved.ModelId, StringComparison.Ordinal))
                    continue;
            }
            if (!_litePrices!.TryGet(name, out var row)) continue;
            if (!double.IsFinite(row.InputPer1m) || !double.IsFinite(row.OutputPer1m)) continue;
            if (row.InputPer1m <= 0 || row.OutputPer1m <= 0) continue;
            price = new CatalogPrice(
                row.InputPer1m, row.OutputPer1m,
                SanitizeLiteCache(row.CacheReadPer1m), SanitizeLiteCache(row.CacheWritePer1m),
                IsCny: false);
            return true;
        }
        return false;
    }

    /// <summary>LiteLLM 可选 cache 单价同样按有限非负过滤，避免非有限值带来 NaN 费用。</summary>
    private static double? SanitizeLiteCache(double? value)
    {
        if (value is not { } v) return null;
        if (!double.IsFinite(v) || v < 0) return null;
        return v;
    }

    /// <summary>整行回退候选：原名、稳定 modelId、仅剥已知厂商包装前缀；不移除 -fast 等档位后缀。</summary>
    private static IEnumerable<string> LiteFallbackCandidates(string raw, ResolvedModel resolved)
    {
        if (raw.Length > 0) yield return raw;
        if (resolved.Known && resolved.ModelId.Length > 0) yield return resolved.ModelId;
        foreach (var candidate in PrefixCandidates(raw))
            yield return candidate;
    }

    private static IEnumerable<string> PrefixCandidates(string name)
    {
        var queue = new Queue<string>();
        queue.Enqueue(name);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in UnwrapPrefixOnce(current))
            {
                if (seen.Add(next))
                {
                    yield return next;
                    queue.Enqueue(next);
                }
            }
        }
    }

    /// <summary>允许的冒号前缀：只认已验证的区域/厂商标记，不用长度充当合法性检查。</summary>
    private static readonly string[] KnownColonPrefixes = ["cn", "us"];

    /// <summary>只剥已验证前缀（cn:/us:、已知厂商路径段、合法 qoder-custom UUID）；
    /// 保留 -fast/-high 等档位与 --max 之类后缀原样，删除 alias 后不能再靠隐式规则命中。</summary>
    private static IEnumerable<string> UnwrapPrefixOnce(string name)
    {
        var colon = name.IndexOf(':');
        if (colon > 0)
        {
            var head = name[..colon];
            var rest = name[(colon + 1)..].Trim();
            if (rest.Length > 0 && KnownColonPrefixes.Contains(head, StringComparer.OrdinalIgnoreCase))
                yield return rest;
        }
        var slash = name.IndexOf('/');
        if (slash > 0)
        {
            var head = name[..slash];
            var rest = name[(slash + 1)..].Trim();
            if (rest.Length == 0) yield break;
            if (KnownHeads.Contains(head, StringComparer.OrdinalIgnoreCase)
                || IsQoderCustomSegment(head))
                yield return rest;
        }
    }

    /// <summary>qoder-custom- 段必须带固定 UUID 结构（qoder-custom-&lt;uuid&gt;），否则保留原始身份。</summary>
    private static bool IsQoderCustomSegment(string head)
    {
        const string prefix = "qoder-custom-";
        if (!head.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = head[prefix.Length..];
        // 8-4-4-4-12 十六进制
        if (tail.Length != 36) return false;
        for (var i = 0; i < tail.Length; i++)
        {
            var c = tail[i];
            if (i is 8 or 13 or 18 or 23)
            {
                if (c != '-') return false;
                continue;
            }
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    private ResolvedModel ResolveCore(string tool, string raw)
    {
        if (raw.Length == 0)
            return new ResolvedModel(UnknownId(tool, "unknown"), $"未知模型（{ToolLabel(tool)}）", false);

        if (TryLookup(tool, raw, out var hit))
            return new ResolvedModel(hit.ModelId, hit.Display, true);

        foreach (var candidate in WrapperCandidates(raw))
        {
            if (candidate.Equals(raw, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryLookup(tool, candidate, out hit))
                return new ResolvedModel(hit.ModelId, hit.Display, true);
        }

        return new ResolvedModel(UnknownId(tool, raw), FriendlyUnknown(tool, raw), false);
    }

    private bool TryLookup(string tool, string name, out CatalogModel model)
    {
        if (_byScoped.TryGetValue((tool, name), out var scoped) && scoped is not null)
        {
            model = scoped;
            return true;
        }
        if (_byName.TryGetValue(name, out var named) && named is not null)
        {
            model = named;
            return true;
        }
        model = null!;
        return false;
    }

    private static readonly string[] KnownHeads =
    [
        "openai", "anthropic", "xiaomi", "qoder", "qodercn", "workbuddy", "traework",
        "xai", "zai", "dashscope", "azure_ai", "openrouter", "deepseek",
    ];

    /// <summary>受控包装候选：已知前缀/嵌套包装；不做任意路径取叶子，不砍任意 -- 后缀。</summary>
    internal static IEnumerable<string> WrapperCandidates(string raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { raw };
        var queue = new Queue<string>();
        queue.Enqueue(raw);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in UnwrapOnce(current))
            {
                if (seen.Add(next))
                {
                    yield return next;
                    queue.Enqueue(next);
                }
            }
        }
    }

    private static IEnumerable<string> UnwrapOnce(string name)
    {
        foreach (var candidate in UnwrapPrefixOnce(name)) yield return candidate;
    }

    /// <summary>未知身份 ID：逐段可逆转义（下划线、斜杠、冒号、中文互不同码），合法 modelId 不含冒号故不冲突。</summary>
    internal static string UnknownId(string tool, string raw)
        => $"raw:{EscapeSegment(tool)}:{EscapeSegment(raw)}";

    private static string EscapeSegment(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(value.ToLowerInvariant()))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
                sb.Append(c);
            else
                sb.Append('_').Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private static string FriendlyUnknown(string tool, string raw)
    {
        if (raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return $"自动选择（{ToolLabel(tool)}）";
        return $"未知模型（{ToolLabel(tool)}）· {raw}";
    }

    private static string ToolLabel(string tool) => tool switch
    {
        "cursor" => "Cursor",
        "cursor-cloud" => "Cursor",
        "codex" => "Codex",
        "mimocode" => "MiMo",
        "dsh" => "DSH",
        "zcode" => "ZCode",
        "workbuddy" => "WorkBuddy",
        "trae" => "Trae",
        "qoder" => "Qoder",
        "qoder-cn" => "Qoder CN",
        "grok" => "Grok",
        "relay" => "Sub2API",
        _ => tool,
    };
}

public static class ModelCatalog
{
    private static ModelCatalogSnapshot? _snapshot;
    private static readonly object Gate = new();

    public static ModelCatalogSnapshot Snapshot
    {
        get
        {
            lock (Gate)
            {
                return _snapshot ??= ModelCatalogLoader.TryParseFull(EmbeddedSeed(), out var file, out _)
                    ? new ModelCatalogSnapshot(file)
                    : new ModelCatalogSnapshot(new CatalogFile(1, "", "", []));
            }
        }
    }

    public static void Publish(ModelCatalogSnapshot snapshot)
    {
        lock (Gate) _snapshot = snapshot;
    }

    /// <summary>测试/探针：清空已发布快照，避免用例间静态状态串场；生产不调用。</summary>
    internal static void ResetForTest()
    {
        lock (Gate) _snapshot = null;
    }

    public static string EmbeddedSeed()
    {
        var asm = typeof(ModelCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream("AgentHub.ModelCatalog.json")
            ?? throw new InvalidOperationException("AgentHub.ModelCatalog.json not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static ResolvedModel Resolve(string tool, string? rawModel)
        => Snapshot.Resolve(tool, rawModel);
}
