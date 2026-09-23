using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Core.SessionCore.Providers;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// OpenClaw / Every Code / AStudio / oh-my-pi / OmO / pi。
/// Every Code 与 AStudio 复用 Codex rollout；pi 家族共用一个 JSONL 读法。
/// Dots 没有独立目录，provider slug 为 dots 时 Tool 写成 dots。
/// </summary>
internal static partial class PassiveUsage
{
    public static List<UsageRecord> ReadOpenClaw(IEnumerable<string> homes)
    {
        var list = new List<UsageRecord>();
        foreach (var file in OpenClawFiles(homes))
        {
            try { ReadOpenClawFile(file, OpenClawSessionId(file), list); }
            catch (IOException) { }
        }
        return list;
    }

    public static List<UsageRecord> ReadEveryCode(IEnumerable<string> homes) =>
        ReadCodexRollouts("every-code", homes);

    public static List<UsageRecord> ReadAStudio(IEnumerable<string> homes) =>
        ReadCodexRollouts("astudio", homes);

    public static List<UsageRecord> ReadOhMyPi(IEnumerable<string> agentDirs) =>
        ReadPiFamily(agentDirs, PiKind.OhMyPi);

    public static List<UsageRecord> ReadOmo(IEnumerable<string> agentDirs) =>
        ReadPiFamily(agentDirs, PiKind.Omo);

    public static List<UsageRecord> ReadPi(IEnumerable<string> agentDirs) =>
        ReadPiFamily(agentDirs, PiKind.Pi);

    public static List<UsageRecord> ReadPrimeAgent(IEnumerable<string> agentDirs) =>
        ReadPiFamily(agentDirs, PiKind.Prime);

    private static List<UsageRecord> ReadCodexRollouts(string tool, IEnumerable<string> homes)
    {
        var list = new List<UsageRecord>();
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home)) continue;
            foreach (var bucket in new[] { "sessions", "archived_sessions" })
            {
                var root = Path.Combine(home, bucket);
                foreach (var file in UsageIo.EnumerateFiles(root, 8, static (path, _) =>
                {
                    var name = Path.GetFileName(path);
                    return name.StartsWith("rollout-", StringComparison.Ordinal)
                           && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                }))
                {
                    var sessionId = CodexProvider.SessionIdFromName(Path.GetFileName(file));
                    if (sessionId is null) continue;
                    try
                    {
                        foreach (var row in UsageParsers.ParseCodex(file, sessionId))
                            list.Add(row with { Tool = tool });
                    }
                    catch (IOException) { }
                }
            }
        }
        return list;
    }

    private static readonly string[] OpenClawBuckets = ["sessions", "session-sqlite-import-archive"];

    private static List<string> OpenClawFiles(IEnumerable<string> homes)
    {
        var files = new List<string>();
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home)) continue;
            var agents = Path.Combine(home, "agents");
            if (!Directory.Exists(agents)) continue;
            foreach (var agent in Directory.EnumerateDirectories(agents))
            {
                foreach (var bucket in OpenClawBuckets)
                {
                    files.AddRange(UsageIo.EnumerateFiles(Path.Combine(agent, bucket), 1, static (path, _) =>
                    {
                        var name = Path.GetFileName(path);
                        return name.Contains(".jsonl", StringComparison.Ordinal)
                               && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                    }));
                }
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static string OpenClawSessionId(string file)
    {
        var bucket = Path.GetDirectoryName(file);
        var agent = bucket is null ? null : Path.GetFileName(Path.GetDirectoryName(bucket));
        var name = Path.GetFileName(file);
        return string.IsNullOrEmpty(agent) ? name : agent + "/" + name;
    }

    private static void ReadOpenClawFile(string file, string sessionId, List<UsageRecord> list)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var metaCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            if (line.Length == 0
                || !line.Contains("\"usage\"", StringComparison.Ordinal)
                || !line.Contains("totalTokens", StringComparison.Ordinal))
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!string.Equals(UsageParsers.GetStr(root, "type"), "message", StringComparison.Ordinal))
                    continue;
                var msg = UsageParsers.GetObj(root, "message");
                if (msg is null) continue;
                var usage = UsageParsers.GetObj(msg, "usage");
                if (usage is null || !TryOpenClawUsage(usage.Value, out var rawIn, out var cacheRead, out var cacheWrite, out var output, out var total))
                    continue;
                var ts = UsageParsers.ParseIso(UsageParsers.GetStr(root, "timestamp"));
                if (ts is null) continue;

                // input 含 cache 读，拆开后五列才不重叠。cache 写不在 input 里。reasoning 恒为 0。
                var input = Math.Max(0, rawIn - cacheRead);
                if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0)
                    continue;

                var model = UsageParsers.GetStr(msg, "model") ?? "unknown";
                var id = UsageParsers.GetStr(root, "id");
                string key;
                if (!string.IsNullOrEmpty(id))
                {
                    if (!unique.Add(id)) continue;
                    key = id;
                }
                else
                {
                    var hash = OpenClawMetaHash(root, msg.Value, model, rawIn, cacheRead, cacheWrite, output, total);
                    metaCounts.TryGetValue(hash, out var n);
                    n++;
                    metaCounts[hash] = n;
                    key = "meta:" + hash + ":" + n.ToString(CultureInfo.InvariantCulture);
                }

                list.Add(Row("openclaw", sessionId, key, ts.Value, input, output, cacheRead, cacheWrite, 0, model, null));
            }
        }
    }

    private static bool TryOpenClawUsage(
        JsonElement usage, out long input, out long cacheRead, out long cacheWrite, out long output, out long total)
    {
        input = cacheRead = cacheWrite = output = total = 0;
        return TryOpenClawInt(usage, "input", out input)
               && TryOpenClawInt(usage, "cacheRead", out cacheRead)
               && TryOpenClawInt(usage, "cacheWrite", out cacheWrite)
               && TryOpenClawInt(usage, "output", out output)
               && TryOpenClawInt(usage, "totalTokens", out total);
    }

    /// <summary>缺字段当 0。出现了但不是非负整数则整条用量作废（对齐 normalizeOpenclawUsage）。</summary>
    private static bool TryOpenClawInt(JsonElement usage, string name, out long value)
    {
        value = 0;
        if (usage.ValueKind != JsonValueKind.Object || !usage.TryGetProperty(name, out var v))
            return true;
        if (v.ValueKind != JsonValueKind.Number) return false;
        if (v.TryGetInt64(out var n))
        {
            if (n < 0) return false;
            value = n;
            return true;
        }
        if (v.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 && d <= long.MaxValue && d == Math.Floor(d))
        {
            value = (long)d;
            return true;
        }
        return false;
    }

    private static string OpenClawMetaHash(
        JsonElement root, JsonElement msg, string model, long input, long cacheRead, long cacheWrite, long output, long total)
    {
        var msgTs = UsageParsers.GetStr(msg, "timestamp");
        if (msg.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number)
            msgTs = ts.GetRawText();
        var payload = string.Join('\n',
            UsageParsers.GetStr(root, "timestamp"),
            msgTs,
            UsageParsers.GetStr(msg, "responseId"),
            model,
            UsageParsers.GetStr(msg, "provider"),
            UsageParsers.GetStr(msg, "api"),
            input.ToString(CultureInfo.InvariantCulture),
            cacheRead.ToString(CultureInfo.InvariantCulture),
            cacheWrite.ToString(CultureInfo.InvariantCulture),
            output.ToString(CultureInfo.InvariantCulture),
            total.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private enum PiKind { OhMyPi, Omo, Pi, Prime }

    private static List<UsageRecord> ReadPiFamily(IEnumerable<string> agentDirs, PiKind kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<UsageRecord>();
        var fallback = kind switch
        {
            PiKind.OhMyPi => "omp-unknown",
            PiKind.Omo => "omo-unknown",
            PiKind.Prime => "prime-agent-unknown",
            _ => "pi-unknown",
        };
        foreach (var file in PiSessionFiles(agentDirs, includeRoot: kind == PiKind.Prime))
        {
            string? project = null;
            var sub = IsPiSubagent(file);
            var sessionId = PiSessionId(file);
            foreach (var line in UsageParsers.ReadLinesShared(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var type = UsageParsers.GetStr(root, "type");
                    if (string.Equals(type, "session", StringComparison.Ordinal))
                    {
                        var cwd = UsageParsers.GetStr(root, "cwd");
                        if (!string.IsNullOrWhiteSpace(cwd)) project = cwd.Trim();
                        continue;
                    }
                    if (!string.Equals(type, "message", StringComparison.Ordinal)) continue;
                    var msg = UsageParsers.GetObj(root, "message");
                    if (msg is null || !IsRole(msg, "assistant")) continue;
                    var usage = UsageParsers.GetObj(msg, "usage");
                    if (usage is null) continue;
                    var id = UsageParsers.GetStr(root, "id");
                    if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;

                    var input = UsageIo.JsonLong(usage.Value, "input");
                    var output = UsageIo.JsonLong(usage.Value, "output");
                    var cacheRead = UsageIo.JsonLong(usage.Value, "cacheRead");
                    var cacheWrite = UsageIo.JsonLong(usage.Value, "cacheWrite");
                    var reasoning = kind == PiKind.Omo
                        ? FirstFinite(usage.Value, "reasoningTokens", "reasoning")
                        : UsageIo.JsonLong(usage.Value, "reasoningTokens");
                    if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning == 0)
                        continue;
                    var ts = PiTimestamp(msg.Value, root);
                    if (ts is null) continue;

                    // OmO 的 reasoning 含在 output 里，拆开后才不会按五列再加一次。
                    if (kind == PiKind.Omo) output = Math.Max(0, output - reasoning);
                    var tool = kind switch
                    {
                        PiKind.OhMyPi => "oh-my-pi",
                        PiKind.Omo => "omo",
                        PiKind.Prime => "prime-agent",
                        _ => PiTool(UsageParsers.GetStr(msg, "provider")),
                    };
                    var row = Row(tool, sessionId, id, ts.Value, input, output, cacheRead, cacheWrite, reasoning,
                        UsageParsers.GetStr(msg, "model") ?? fallback, project);
                    list.Add(sub ? row with { IsSubagent = true } : row);
                }
            }
        }
        return list;
    }

    /// <summary>只有 Dots 从 pi 拆出去。其它 provider 仍记在 pi，避免无限 pi-* id。</summary>
    private static string PiTool(string? provider) =>
        string.Equals(PiProviderSlug(provider), "dots", StringComparison.Ordinal) ? "dots" : "pi";

    private static string? PiProviderSlug(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        var sb = new StringBuilder();
        var dash = false;
        foreach (var ch in provider.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (sb.Length == 64) break;
                sb.Append(ch);
                dash = false;
            }
            else if (sb.Length > 0 && !dash && sb.Length < 64)
            {
                sb.Append('-');
                dash = true;
            }
        }
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static long FirstFinite(JsonElement usage, params string[] names)
    {
        foreach (var name in names)
        {
            if (!usage.TryGetProperty(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d))
                return UsageIo.JsonLong(usage, name);
            if (v.ValueKind == JsonValueKind.String)
                return UsageIo.JsonLong(usage, name);
        }
        return 0;
    }

    private static DateTime? PiTimestamp(JsonElement msg, JsonElement entry)
    {
        if (msg.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number)
        {
            long ms;
            if (ts.TryGetInt64(out var n)) ms = n;
            else if (ts.TryGetDouble(out var d) && double.IsFinite(d) && d > 0 && d <= long.MaxValue) ms = (long)d;
            else ms = 0;
            if (ms > 0)
            {
                try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime; }
                catch (ArgumentOutOfRangeException) { return null; }
            }
        }
        return UsageParsers.ParseIso(UsageParsers.GetStr(entry, "timestamp"));
    }

    private static List<string> PiSessionFiles(IEnumerable<string> agentDirs, bool includeRoot = false)
    {
        var files = new List<string>();
        foreach (var agent in agentDirs)
        {
            if (string.IsNullOrWhiteSpace(agent)) continue;
            var sessions = Path.Combine(agent, "sessions");
            if (!Directory.Exists(sessions)) continue;
            if (includeRoot)
            {
                files.AddRange(UsageIo.EnumerateFiles(sessions, 8, static (path, _) =>
                    path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)));
                continue;
            }
            IEnumerable<string> cwdDirs;
            try { cwdDirs = Directory.EnumerateDirectories(sessions).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var cwd in cwdDirs)
            {
                files.AddRange(UsageIo.EnumerateFiles(cwd, 8, static (path, _) =>
                    path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)));
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static bool IsPiSubagent(string file)
    {
        var segments = 1;
        var dir = Path.GetDirectoryName(file);
        while (dir is not null && !string.Equals(Path.GetFileName(dir), "sessions", StringComparison.OrdinalIgnoreCase))
        {
            segments++;
            dir = Path.GetDirectoryName(dir);
        }
        return segments > 2;
    }

    private static string PiSessionId(string file)
    {
        var parts = new List<string>();
        var current = file;
        while (!string.IsNullOrEmpty(current))
        {
            var name = Path.GetFileName(current);
            if (string.Equals(name, "sessions", StringComparison.OrdinalIgnoreCase)) break;
            parts.Add(name);
            current = Path.GetDirectoryName(current);
        }
        parts.Reverse();
        if (parts.Count >= 3) return parts[1];
        return Path.GetFileNameWithoutExtension(file);
    }
}
