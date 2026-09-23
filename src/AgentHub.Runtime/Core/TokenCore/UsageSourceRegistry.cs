using AgentHub.Core.Platform;
using AgentHub.Core.SessionCore.Providers;

namespace AgentHub.Core.TokenCore;

/// <summary>一次入库单位。一个坏文件只跳过自己。</summary>
internal sealed record UsageUnit(string Label, Func<IEnumerable<UsageRecord>> Read);

/// <summary>本地用量源。ScanAllLocal 只循环这张表；下一批工具加一行即可。</summary>
internal sealed class UsageSource
{
    public required string Id { get; init; }
    public required Func<IEnumerable<string>> ProbeRoots { get; init; }
    public required Func<IEnumerable<UsageUnit>> Units { get; init; }
}

internal static class UsageSourceRegistry
{
    public static IReadOnlyList<UsageSource> Local { get; } =
    [
        Files("codex", () => [Path.Combine(UsagePaths.Home, ".codex")], CodexUnits),
        Files("workbuddy", () => [Path.Combine(UsagePaths.Home, ".workbuddy")], WorkBuddyUnits),
        Files("dsh", () => [Path.Combine(UsagePaths.Home, ".dsh")], DshUnits),
        Db("zcode", () => [Path.Combine(UsagePaths.Home, ".zcode")], () => ZcodeLocal.DbExists, () => ZcodeLocal.DbPath, ZcodeLocal.ReadUsage),
        Db("mimocode", () => [Path.Combine(UsagePaths.Home, ".config", "mimocode")], () => MimocodeLocal.DbExists, () => MimocodeLocal.DbPath, MimocodeLocal.ReadUsage),
        Db("grok", () => [GrokLocal.Home], () => GrokLocal.SessionsExist, () => GrokLocal.Home, GrokLocal.ReadUsage),
        Db("qoder", () => [Path.Combine(PlatformPaths.RoamingAppData, "Qoder")],
            () => QoderLocal.DbExists(QoderQuota.International),
            () => QoderLocal.DbPath(QoderQuota.International),
            QoderLocal.ReadInternational),
        Db("qoder-cn", () => [QoderLocal.ChinaHome], () => QoderLocal.ChinaUsageExists, () => QoderLocal.ChinaHome, QoderLocal.ReadChina),
        Dirs("minimax", UsagePaths.MiniMaxHomes, PassiveUsage.ReadMiniMax),
        Dirs("antigravity", UsagePaths.GeminiHomes, PassiveUsage.ReadAntigravity),
        Dirs("reasonix", UsagePaths.ReasonixHomes, PassiveUsage.ReadReasonix),
        Dirs("opencode", UsagePaths.OpenCodeDataDirs, PassiveUsage.ReadOpenCode),
        Dirs("claude-code", UsagePaths.ClaudeHomes, PassiveUsage.ReadClaudeCode),
        new UsageSource
        {
            Id = "devin",
            ProbeRoots = UsagePaths.DevinProbeRoots,
            Units = () =>
            {
                var dbs = UsagePaths.DevinDbCandidates().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return dbs.Count == 0 ? [] : [new UsageUnit(dbs[0], () => PassiveUsage.ReadDevin(dbs))];
            },
        },
        Dirs("gemini-cli", UsagePaths.GeminiHomes, PassiveUsage.ReadGeminiCli),
        Dirs("kiro", UsagePaths.KiroBases, PassiveUsage.ReadKiro),
        new UsageSource
        {
            Id = "copilot",
            ProbeRoots = UsagePaths.CopilotProbeRoots,
            Units = () =>
            {
                var otel = UsagePaths.CopilotOtelFiles().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var stores = UsagePaths.CopilotSessionStoreDbs().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var apps = UsagePaths.CopilotAppDbs().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (otel.Count == 0 && stores.Count == 0 && apps.Count == 0) return [];
                var label = stores.FirstOrDefault() ?? apps.FirstOrDefault() ?? otel[0];
                return [new UsageUnit(label, () => PassiveUsage.ReadCopilot(otel, stores, apps))];
            },
        },
        Dirs("kimi-code", UsagePaths.KimiCodeHomes, PassiveUsage.ReadKimiCode),
        new UsageSource
        {
            Id = "codebuddy",
            ProbeRoots = UsagePaths.CodeBuddyHomes,
            Units = () =>
            {
                var homes = UsagePaths.CodeBuddyHomes().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var logs = UsagePaths.CodeBuddyLogRoots().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (homes.Count == 0 && logs.Count == 0) return [];
                return [new UsageUnit(homes.FirstOrDefault() ?? logs[0], () => PassiveUsage.ReadCodeBuddy(homes, logs))];
            },
        },
        Dirs("hermes", UsagePaths.HermesHomes, PassiveUsage.ReadHermes),
        Dirs("openclaw", UsagePaths.OpenClawHomes, PassiveUsage.ReadOpenClaw),
        Dirs("every-code", UsagePaths.EveryCodeHomes, PassiveUsage.ReadEveryCode),
        Dirs("astudio", UsagePaths.AcodeHomes, PassiveUsage.ReadAStudio),
        Dirs("oh-my-pi", UsagePaths.OmpAgentDirs, PassiveUsage.ReadOhMyPi),
        UnlessSameDir("omo", UsagePaths.OmoAgentDirs, UsagePaths.OmpAgentDirs, PassiveUsage.ReadOmo),
        UnlessSameDir("pi", UsagePaths.PiAgentDirs, UsagePaths.OmpAgentDirs, PassiveUsage.ReadPi),
        // Dots 走 pi 的 provider。ReadPi 把 slug dots 写成 Tool=dots；这里不再扫第二遍。
        new UsageSource
        {
            Id = "dots",
            ProbeRoots = static () => [],
            Units = static () => [],
        },
        Dirs("prime-agent", UsagePaths.PrimeAgentDirs, PassiveUsage.ReadPrimeAgent),
        Dirs("craft-agents", UsagePaths.CraftConfigDirs, PassiveUsage.ReadCraft),
        new UsageSource
        {
            Id = "kilo-cli",
            ProbeRoots = UsagePaths.KiloCliHomes,
            Units = () =>
            {
                var dbs = UsagePaths.KiloCliDbs().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return dbs.Count == 0 ? [] : [new UsageUnit(dbs[0], () => PassiveUsage.ReadKiloCli(dbs))];
            },
        },
        Dirs("kilo-code", UsagePaths.EditorDataRoots, PassiveUsage.ReadKiloCode),
        Dirs("roo-code", UsagePaths.EditorDataRoots, PassiveUsage.ReadRooCode),
        new UsageSource
        {
            Id = "zed-agent",
            ProbeRoots = UsagePaths.ZedProbeDirs,
            Units = () =>
            {
                var dbs = UsagePaths.ZedDbCandidates().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return dbs.Count == 0 ? [] : [new UsageUnit(dbs[0], () => PassiveUsage.ReadZed(dbs))];
            },
        },
        new UsageSource
        {
            Id = "goose",
            ProbeRoots = UsagePaths.GooseProbeDirs,
            Units = () =>
            {
                var db = UsagePaths.GooseDbCandidates().FirstOrDefault(File.Exists);
                return db is null ? [] : [new UsageUnit(db, () => PassiveUsage.ReadGoose([db]))];
            },
        },
        Dirs("droid", UsagePaths.DroidSessionsDirs, PassiveUsage.ReadDroid),
        new UsageSource
        {
            Id = "anythingllm",
            ProbeRoots = UsagePaths.AnythingLlmProbeDirs,
            Units = () =>
            {
                var dbs = UsagePaths.AnythingLlmDbs().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return dbs.Count == 0 ? [] : [new UsageUnit(dbs[0], () => PassiveUsage.ReadAnythingLlm(dbs))];
            },
        },
        new UsageSource
        {
            Id = "claude-science",
            ProbeRoots = UsagePaths.ClaudeScienceProbeDirs,
            Units = () =>
            {
                var dbs = UsagePaths.ClaudeScienceDbCandidates().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return dbs.Count == 0 ? [] : [new UsageUnit(dbs[0], () => PassiveUsage.ReadClaudeScience(dbs))];
            },
        },
        Dirs("lm-studio", UsagePaths.LmStudioHomes, PassiveUsage.ReadLmStudio),
        new UsageSource
        {
            Id = "unsloth-studio",
            ProbeRoots = UsagePaths.UnslothProbeDirs,
            Units = () =>
            {
                var dbs = UsagePaths.UnslothStudioDbs().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return dbs.Count == 0 ? [] : [new UsageUnit(dbs[0], () => PassiveUsage.ReadUnsloth(dbs))];
            },
        },
    ];

    public static UsageSource? Find(string id) =>
        Local.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    private static UsageSource Files(string id, Func<IEnumerable<string>> roots, Func<IEnumerable<UsageUnit>> units) =>
        new() { Id = id, ProbeRoots = roots, Units = units };

    private static UsageSource Db(
        string id, Func<IEnumerable<string>> roots, Func<bool> exists, Func<string> label, Func<IEnumerable<UsageRecord>> read) =>
        new()
        {
            Id = id,
            ProbeRoots = roots,
            Units = () => exists() ? [new UsageUnit(label(), read)] : [],
        };

    /// <summary>目录和 occupiedBy 解析到同一路径时不建扫描单位，避免同一份 JSONL 记到两家。</summary>
    private static UsageSource UnlessSameDir(
        string id,
        Func<IEnumerable<string>> roots,
        Func<IEnumerable<string>> occupiedBy,
        Func<IReadOnlyList<string>, IEnumerable<UsageRecord>> read) =>
        new()
        {
            Id = id,
            ProbeRoots = roots,
            Units = () =>
            {
                var occupied = new HashSet<string>(occupiedBy().Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
                var found = roots()
                    .Where(Directory.Exists)
                    .Select(Path.GetFullPath)
                    .Where(dir => !occupied.Contains(dir))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return found.Count == 0 ? [] : [new UsageUnit(found[0], () => read(found))];
            },
        };

    private static UsageSource Dirs(
        string id, Func<IEnumerable<string>> roots, Func<IReadOnlyList<string>, IEnumerable<UsageRecord>> read) =>
        new()
        {
            Id = id,
            ProbeRoots = roots,
            Units = () =>
            {
                var found = roots().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return found.Count == 0 ? [] : [new UsageUnit(found[0], () => read(found))];
            },
        };

    private static IEnumerable<UsageUnit> CodexUnits()
    {
        var home = UsagePaths.Home;
        foreach (var root in new[] { Path.Combine(home, ".codex", "sessions"), Path.Combine(home, ".codex", "archived_sessions") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                var id = CodexProvider.SessionIdFromName(Path.GetFileName(file));
                if (id is null) continue;
                var path = file;
                var sessionId = id;
                yield return new UsageUnit(path, () => UsageParsers.ParseCodex(path, sessionId));
            }
        }
    }

    private static IEnumerable<UsageUnit> WorkBuddyUnits()
    {
        var root = Path.Combine(UsagePaths.Home, ".workbuddy", "projects");
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}tool-results{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            var id = Path.GetFileNameWithoutExtension(file);
            var sub = file.Contains($"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
            if (sub)
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file))!);
                if (Guid.TryParse(parent, out _)) id = parent;
            }
            else if (!Guid.TryParse(id, out _)) continue;
            var path = file;
            var sessionId = id;
            var isSub = sub;
            yield return new UsageUnit(path, () => UsageParsers.ParseWorkBuddy(path, sessionId, isSub));
        }
    }

    private static IEnumerable<UsageUnit> DshUnits()
    {
        var root = Path.Combine(UsagePaths.Home, ".dsh", "sessions");
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            foreach (var sessionDir in Directory.EnumerateDirectories(dir))
            {
                var file = DshProvider.PickSessionLog(sessionDir);
                if (file is null) continue;
                var path = file;
                var id = DshProvider.NormalizeSessionId(Path.GetFileName(sessionDir));
                yield return new UsageUnit(path, () =>
                {
                    var (plain, _, _, _) = DshProvider.DecompressAll(File.ReadAllBytes(path));
                    return plain.Length == 0 ? [] : UsageParsers.ParseDsh(plain, id, null);
                });
            }
        }
    }
}
