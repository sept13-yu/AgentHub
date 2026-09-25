using System.IO;
using System.Net.Http;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>模型目录同步：启动/刷新加载目录（含身份、别名、价格），异步拉 GitHub → Gitee。
/// 顺序：远程有效完整目录 → 本地 model-catalog.cache.json → 内嵌种子；用户 model-catalog.json 作稀疏补丁。
/// 旧 prices.json 只保留为旧客户端兼容投影，不参与新查询。
/// 唯一调度者：远程目录、LiteLLM 缓存均由此服务刷新，并原子发布新快照。</summary>
public static class PriceSyncService
{
    private static readonly string[] CatalogRemoteUrls =
    [
        "https://raw.githubusercontent.com/sept13-yu/AgentHub/main/model-catalog.json",
        "https://gitee.com/sept13-yu/AgentHub/raw/main/model-catalog.json",
    ];

    /// <summary>测试/探针注入点：替换远程目录地址；生产不设置。</summary>
    internal static string[]? CatalogRemoteUrlsOverrideForTest;

    /// <summary>测试/探针注入点：强制重新读取基线/补丁，避免静态状态串场。</summary>
    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _catalog = null;
            _remoteCatalog = null;
            _effectiveBaseline = null;
            _catalogBase = null;
            _catalogFromCache = default;
            _attemptedExists = false;
            _attemptedWriteTimeUtc = default;
            _attemptedLength = -1;
            _catalogError = null;
            _localOverrideActive = false;
            _remoteLabel = null;
            _source = "builtin";
        }
    }

    /// <summary>跑一次生产刷新路径（含远程下载与 LiteLLM 刷新）。测试用于验证编排，不夹带夹具替代。</summary>
    internal static Task RunRefreshForTest() => RefreshAsync();

    /// <summary>等待最近一次后台刷新结束。测试用于让 RefreshInBackground 的副作用归属当前用例，避免串场。</summary>
    internal static void WaitForBackgroundRefreshForTest()
    {
        Task pending;
        lock (Gate) pending = _backgroundRefresh;
        try { pending.Wait(TimeSpan.FromSeconds(20)); }
        catch (AggregateException) { /* 后台失败已记入状态 */ }
    }

    // 计算属性：AgentHubConfig.Dir 可被 AGENTHUB_CONFIG_DIR 覆盖，静态字段会冻结首次求值
    private static string CatalogCachePath => Path.Combine(AgentHubConfig.Dir, "model-catalog.cache.json");

    private static string UserCatalogPath => Path.Combine(AgentHubConfig.Dir, "model-catalog.json");

    private static readonly HttpClient Http = CreateHttp();
    private static readonly object Gate = new();

    /// <summary>内嵌种子投影的列表价，供设置页与测试读取；目录是唯一人工维护真源。</summary>
    public static readonly IReadOnlyList<PriceRow> DefaultPrices = ProjectSeedPrices();

    public static Action? OnBaselineChanged;

    private static ModelCatalogSnapshot? _catalog;
    private static CatalogFile? _remoteCatalog;
    private static Dictionary<string, CatalogPrice> _lastOverrides = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _attemptedWriteTimeUtc;
    private static long _attemptedLength = -1;
    private static bool _attemptedExists;
    private static string? _catalogError;
    private static bool _localOverrideActive;

    /// <summary>一次用量查询捕获目录快照；用户补丁变更时重读，不等待远程。
    /// 读、解析、合并、索引构建全部成功才替换有效快照；无效用户文件保留上次有效结果并记录错误。
    /// legacyOverrides 为 null 表示沿用上次覆价（供刷新路径调用，避免清空用户价）。</summary>
    public static ModelCatalogSnapshot Capture(
        IEnumerable<PriceRow>? legacyOverrides,
        string defaultCurrency,
        bool forceLocalReload = false)
    {
        lock (Gate)
        {
            if (legacyOverrides is not null)
                _lastOverrides = BuildRawOverrides(legacyOverrides, defaultCurrency);
            var rawOverrides = _lastOverrides;
            var litePrices = LiteLlmPriceEnricher.CapturePrices();

            var exists = false;
            DateTime writeTime = default;
            long length = -1;
            try
            {
                exists = File.Exists(UserCatalogPath);
                if (exists)
                {
                    var info = new FileInfo(UserCatalogPath);
                    writeTime = info.LastWriteTimeUtc;
                    length = info.Length;
                }
            }
            catch (IOException)
            {
                exists = false;
            }

            // 基线或补丁来源变化才重建索引；否则只换 LiteLLM 视图，保持同一目录版本
            var baselineChanged = _catalog is null
                || !ReferenceEquals(_catalogBase, _remoteCatalog)
                || _catalogFromCache != ReadCacheStamp();
            var fileChanged = exists != _attemptedExists
                || (exists && (writeTime != _attemptedWriteTimeUtc || length != _attemptedLength));

            if (!forceLocalReload && !baselineChanged && !fileChanged)
            {
                // 只换 LiteLLM 视图：从**未补价**的有效目录用新比率重算 cache，避免沿用旧补价结果（R9）。
                var baseFile = _effectiveBaseline ?? _catalog!.File;
                var reEnriched = EnrichCatalogCache(baseFile, litePrices);
                _catalog = new ModelCatalogSnapshot(reEnriched, rawOverrides, litePrices);
                ModelCatalog.Publish(_catalog);
                return _catalog;
            }

            _attemptedExists = exists;
            _attemptedWriteTimeUtc = writeTime;
            _attemptedLength = length;

            if (!TryResolveBaseline(out var baseline, out var origin))
            {
                _catalogError = "目录基线不可用";
                if (_catalog is not null) return _catalog;
                baseline = new CatalogFile(1, "", "", []);
            }
            // 来源是「候选」：只有合并、补价、快照都成功后才随快照一起提交（R17）。
            // 走保留旧快照分支时不动 _source，也不先改再靠错误分支补救。
            var candidateSource = SourceForOrigin(origin);

            if (!exists)
            {
                // 明确删除 = 撤销本地补丁
                _localOverrideActive = false;
                _catalogError = null;
            }
            else if (TryApplyUserPatch(baseline, out var patched, out var patchErr))
            {
                baseline = patched;
                _localOverrideActive = true;
                _catalogError = null;
            }
            else if (_catalog is null)
            {
                // 首次启动即无效：忽略补丁，用基线并报告错误
                _localOverrideActive = false;
                _catalogError = patchErr;
            }
            else
            {
                // 运行中补丁变坏：保留上次有效快照、名称、价格与来源
                _catalogError = patchErr;
                return _catalog;
            }

            // 保存未补价的形态；补价字段在每次刷新时按当前 LiteLLM 比率重算（R9）
            _effectiveBaseline = baseline;
            baseline = EnrichCatalogCache(baseline, litePrices);
            _catalog = new ModelCatalogSnapshot(baseline, rawOverrides, litePrices);
            _catalogBase = _remoteCatalog;
            _catalogFromCache = ReadCacheStamp();
            _source = candidateSource;
            ModelCatalog.Publish(_catalog);
            return _catalog;
        }
    }

    /// <summary>合并用户补丁后、尚未补 LiteLLM cache 的有效目录；刷新时据此按新比率重算。</summary>
    private static CatalogFile? _effectiveBaseline;

    private static CatalogFile? _catalogBase;
    private static DateTime _catalogFromCache = default;

    /// <summary>候选基线对应的来源标签。远程候选发布时才用它作为生效来源。</summary>
    private static string SourceForOrigin(BaselineOrigin origin) => origin switch
    {
        BaselineOrigin.Cache => "cache",
        BaselineOrigin.Remote => _remoteLabel is "github" or "gitee" ? _remoteLabel : "cache",
        _ => "builtin",
    };

    /// <summary>最近一次成功下载的目录站点；仅表示下载来源，不代表已生效。</summary>
    private static string? _remoteLabel;

    private static DateTime ReadCacheStamp()
    {
        try
        {
            return File.Exists(CatalogCachePath) ? File.GetLastWriteTimeUtc(CatalogCachePath) : default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    /// <summary>旧覆价：非法或空币种按设置币种解释（与 DashboardSettings 口径一致）；不做目录币种继承。</summary>
    internal static Dictionary<string, CatalogPrice> BuildRawOverridesForTest(
        IEnumerable<PriceRow>? legacyOverrides, string defaultCurrency)
        => BuildRawOverrides(legacyOverrides, defaultCurrency);

    private static Dictionary<string, CatalogPrice> BuildRawOverrides(
        IEnumerable<PriceRow>? legacyOverrides, string defaultCurrency)
    {
        var result = new Dictionary<string, CatalogPrice>(StringComparer.OrdinalIgnoreCase);
        if (legacyOverrides is null) return result;
        var fallbackCny = !string.Equals(
            DashboardSettings.NormalizeCurrency(defaultCurrency), "USD", StringComparison.OrdinalIgnoreCase);
        foreach (var row in legacyOverrides)
        {
            var key = (row.Model ?? "").Trim();
            if (key.Length == 0) continue;
            if (row.InputPer1m is not { } inn || row.OutputPer1m is not { } outt) continue;
            if (!double.IsFinite(inn) || !double.IsFinite(outt) || inn < 0 || outt < 0) continue;
            var isCny = row.Currency?.Trim().ToUpperInvariant() switch
            {
                "CNY" => true,
                "USD" => false,
                _ => fallbackCny,
            };
            // 旧 BuildTable 的容错：非有限 cache 单价按 null（回退输入价），否则 0×Infinity 会污染整笔费用为 NaN
            var cacheRead = SanitizeCache(row.CacheReadPer1m);
            var cacheWrite = SanitizeCache(row.CacheWritePer1m);
            result[key] = new CatalogPrice(inn, outt, cacheRead, cacheWrite, isCny);
        }
        return result;
    }

    /// <summary>cache 单价：有限且非负才保留；0 保持免费语义，其余（非有限/负）按未提供。</summary>
    private static double? SanitizeCache(double? value)
    {
        if (value is not { } v) return null;
        if (!double.IsFinite(v) || v < 0) return null;
        return v;
    }

    /// <summary>基线择一：远程有效目录 → 本地缓存 → 内嵌种子；缓存损坏/读失败都回退，不抛给调用方。
    /// 同时报告实际采用的数据来源，供设置页如实展示（R17）。</summary>
    private static bool TryResolveBaseline(out CatalogFile baseline, out BaselineOrigin origin)
    {
        origin = BaselineOrigin.Seed;
        if (_remoteCatalog is { } remote)
        {
            baseline = remote;
            origin = BaselineOrigin.Remote;
            return true;
        }
        try
        {
            if (File.Exists(CatalogCachePath)
                && ModelCatalogLoader.TryParseFull(File.ReadAllText(CatalogCachePath), out var cached, out _))
            {
                baseline = cached;
                origin = BaselineOrigin.Cache;
                return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (ModelCatalogLoader.TryParseFull(ModelCatalog.EmbeddedSeed(), out baseline, out _))
        {
            origin = BaselineOrigin.Seed;
            return true;
        }
        baseline = new CatalogFile(1, "", "", []);
        return false;
    }

    /// <summary>当前生效基线来自哪里。</summary>
    private enum BaselineOrigin
    {
        Seed,
        Cache,
        Remote,
    }

    /// <summary>解析并合并用户补丁；任一失败返回 false 且不产生半成品。</summary>
    private static bool TryApplyUserPatch(CatalogFile baseline, out CatalogFile merged, out string error)
    {
        merged = baseline;
        error = "";
        string text;
        try
        {
            text = File.ReadAllText(UserCatalogPath);
        }
        catch (Exception ex)
        {
            error = "读取失败：" + ex.GetType().Name;
            return false;
        }
        if (!ModelCatalogLoader.TryParsePatch(text, out var patch, out var parseErr))
        {
            error = parseErr;
            return false;
        }
        if (!ModelCatalogLoader.TryMergePatch(baseline, patch, out merged, out var mergeErr))
        {
            error = mergeErr;
            return false;
        }
        return true;
    }

    /// <summary>LiteLLM 只补目录中为 null 的缓存字段；显式 0 与其他明确值都不覆盖。</summary>
    private static CatalogFile EnrichCatalogCache(CatalogFile baseline, LiteLlmPrices litePrices)
    {
        var adjusted = new List<CatalogModel>(baseline.Models.Count);
        foreach (var model in baseline.Models)
        {
            if (model.Price is not { } price
                || (price.CacheReadPer1m is not null && price.CacheWritePer1m is not null)
                || price.InputPer1m <= 0)
            {
                adjusted.Add(model);
                continue;
            }
            var key = model.LiteLlmKey ?? model.ModelId;
            if (!litePrices.TryGet(key, out var lite) || lite.InputPer1m is not > 0)
            {
                adjusted.Add(model);
                continue;
            }
            var cr = price.CacheReadPer1m;
            var cw = price.CacheWritePer1m;
            if (cr is null && lite.CacheReadPer1m is { } lcr && double.IsFinite(lcr))
                cr = price.InputPer1m * lcr / lite.InputPer1m;
            if (cw is null && lite.CacheWritePer1m is { } lcw && double.IsFinite(lcw))
                cw = price.InputPer1m * lcw / lite.InputPer1m;
            adjusted.Add(cr == price.CacheReadPer1m && cw == price.CacheWritePer1m
                ? model
                : model with { Price = price with { CacheReadPer1m = cr, CacheWritePer1m = cw } });
        }
        return baseline with { Models = adjusted };
    }

    /// <summary>当前快照的价格投影：Model 为稳定 modelId，Display 为展示名（旧消费者继续读 Model）。</summary>
    public static IReadOnlyList<PriceRow> CatalogPriceRows()
    {
        var snapshot = ModelCatalog.Snapshot;
        var rows = new List<PriceRow>();
        foreach (var model in snapshot.Models)
        {
            if (model.Price is not { } price) continue;
            rows.Add(new PriceRow
            {
                Model = model.ModelId,
                Display = model.Display,
                InputPer1m = price.InputPer1m,
                OutputPer1m = price.OutputPer1m,
                CacheReadPer1m = price.CacheReadPer1m,
                CacheWritePer1m = price.CacheWritePer1m,
                Currency = price.IsCny ? "CNY" : "USD",
            });
        }
        return rows;
    }

    private static string _source = "builtin";
    private static bool? _lastFetchOk;
    private static DateTimeOffset? _lastFetchAt;
    private static string? _lastFetchError;

    public static object Status()
    {
        lock (Gate)
        {
            return new
            {
                source = _source,
                lastFetchOk = _lastFetchOk,
                lastFetchAt = _lastFetchAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                lastFetchError = _lastFetchError,
                hasDiskCache = File.Exists(CatalogCachePath),
                cachePath = CatalogCachePath,
                userCatalogPath = UserCatalogPath,
                userCatalogExists = File.Exists(UserCatalogPath),
                localOverrideActive = _localOverrideActive,
                catalogError = _catalogError,
                usingLastGoodCatalog = _catalogError is not null && _catalog is not null,
                catalogUpdatedAt = _catalog?.File.UpdatedAt ?? "",
                modelCount = _catalog?.Models.Count ?? 0,
                liteLlm = LiteLlmPriceEnricher.Status(),
            };
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { UseProxy = true })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AgentHub");
        return http;
    }

    /// <summary>启动预热：载入 LiteLLM 缓存后构建首份目录快照。缓存/种子损坏均不影响启动。</summary>
    public static void TryLoadCache()
    {
        LiteLlmPriceEnricher.TryLoadCache();
        try
        {
            Capture(legacyOverrides: null, defaultCurrency: "CNY", forceLocalReload: true);
        }
        catch (Exception)
        {
            // 目录不可用不阻断启动；用量页按种子降级展示
        }
    }

    public static void RefreshInBackground()
    {
        Task task;
        try
        {
            task = Task.Run(async () =>
            {
                try { await RefreshAsync().ConfigureAwait(false); }
                catch (Exception ex) { MarkFetch(false, ex.GetType().Name); }
            });
        }
        catch (Exception)
        {
            return;
        }
        lock (Gate) _backgroundRefresh = task;
    }

    private static Task _backgroundRefresh = Task.CompletedTask;

    /// <summary>旧 callsite 兼容：返回当前快照的价格投影。</summary>
    public static IReadOnlyList<PriceRow> Resolve(IEnumerable<PriceRow>? overrides) => CatalogPriceRows();

    private static async Task RefreshAsync()
    {
        var errors = new List<string>();
        var catalogOk = false;
        var urls = CatalogRemoteUrlsOverrideForTest ?? CatalogRemoteUrls;
        foreach (var url in urls)
        {
            var label = url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase) ? "gitee" : "github";
            try
            {
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add(label + ": HTTP " + (int)resp.StatusCode);
                    continue;
                }
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!ModelCatalogLoader.TryParseFull(json, out var catalog, out var parseErr))
                {
                    errors.Add(label + ": " + parseErr);
                    continue;
                }
                try { WriteCatalogCache(json); }
                catch (IOException) { /* 缓存落盘失败不影响本次使用 */ }
                lock (Gate)
                {
                    _remoteCatalog = catalog;
                    _catalogFromCache = default;
                    // 只记「下载站点」，不宣告候选已生效；来源由 Capture 随有效快照提交（R17）
                    _remoteLabel = label;
                }
                catalogOk = true;
                MarkFetch(true, null);
                break;
            }
            catch (OperationCanceledException)
            {
                errors.Add(label + ": timeout");
            }
            catch (HttpRequestException ex)
            {
                errors.Add(label + ": " + (ex.StatusCode is { } code ? "HTTP " + (int)code : ex.GetType().Name));
            }
            catch (Exception ex)
            {
                errors.Add(label + ": " + ex.GetType().Name);
            }
        }

        // 两个来源都失败：保留上次有效基线
        if (!catalogOk) MarkFetch(false, string.Join("; ", errors));
        // 无论目录成功与否都尝试 LiteLLM 刷新，不能用目录成功当跳过条件（R2）
        await RefreshLiteLlmAsync().ConfigureAwait(false);
    }

    private static async Task RefreshLiteLlmAsync()
    {
        try { await LiteLlmPriceEnricher.RefreshAsync().ConfigureAwait(false); }
        catch (Exception) { /* LiteLLM 失败不阻断 */ }
        RebuildCatalogAfterRefresh();
    }

    /// <summary>远程 / LiteLLM 变化后重建快照；沿用用户覆价，不新建第二套数据。失败保留上次有效快照。</summary>
    private static void RebuildCatalogAfterRefresh()
    {
        var previous = ModelCatalog.Snapshot;
        ModelCatalogSnapshot next;
        try
        {
            next = Capture(legacyOverrides: null, defaultCurrency: "CNY");
        }
        catch (Exception)
        {
            return;
        }
        if (ReferenceEquals(next, previous)) return;
        try { OnBaselineChanged?.Invoke(); }
        catch (Exception) { /* 上层回调失败不影响缓存 */ }
    }

    private static void WriteCatalogCache(string json)
    {
        Directory.CreateDirectory(AgentHubConfig.Dir);
        var temp = CatalogCachePath + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(CatalogCachePath)) File.Replace(temp, CatalogCachePath, destinationBackupFileName: null);
        else File.Move(temp, CatalogCachePath);
    }

    /// <summary>只记录下载成功/失败、时间与错误；下载成功不代表候选已生效（来源随有效快照提交）。</summary>
    private static void MarkFetch(bool ok, string? error)
    {
        lock (Gate)
        {
            _lastFetchOk = ok;
            _lastFetchAt = DateTimeOffset.Now;
            _lastFetchError = error;
        }
    }

    /// <summary>从内嵌目录种子投影旧格式列表价，保持单一人工作真源。</summary>
    private static IReadOnlyList<PriceRow> ProjectSeedPrices()
    {
        if (!ModelCatalogLoader.TryParseFull(ModelCatalog.EmbeddedSeed(), out var file, out _))
            return [];
        var rows = new List<PriceRow>();
        foreach (var model in file.Models)
        {
            if (model.Price is not { } price) continue;
            rows.Add(new PriceRow
            {
                Model = model.ModelId,
                Display = model.Display,
                InputPer1m = price.InputPer1m,
                OutputPer1m = price.OutputPer1m,
                CacheReadPer1m = price.CacheReadPer1m,
                CacheWritePer1m = price.CacheWritePer1m,
                Currency = price.IsCny ? "CNY" : "USD",
            });
        }
        return rows;
    }
}
