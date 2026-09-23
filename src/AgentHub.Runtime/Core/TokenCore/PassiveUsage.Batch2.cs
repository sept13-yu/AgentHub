using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

internal static partial class PassiveUsage
{
    public static List<UsageRecord> ReadGeminiCli(IEnumerable<string> geminiHomes)
    {
        var list = new List<UsageRecord>();
        foreach (var file in SessionFiles(geminiHomes, "tmp", 4,
                     static (path, _) =>
                     {
                         var name = Path.GetFileName(path);
                         var parent = Path.GetFileName(Path.GetDirectoryName(path));
                         return name.StartsWith("session-", StringComparison.Ordinal)
                                && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(parent, "chats", StringComparison.OrdinalIgnoreCase);
                     }))
        {
            try { list.AddRange(ReadGeminiSession(file)); }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return list;
    }

    public static List<UsageRecord> ReadKiro(IEnumerable<string> bases)
    {
        var list = new List<UsageRecord>();
        foreach (var root in bases)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            var timeline = KiroTimeline(root);
            var db = Path.Combine(root, "dev_data", "devdata.sqlite");
            var jsonl = Path.Combine(root, "dev_data", "tokens_generated.jsonl");
            if (File.Exists(db)) list.AddRange(ReadKiroDb(root, db, timeline));
            else if (File.Exists(jsonl)) list.AddRange(ReadKiroJsonl(root, jsonl, timeline));
        }
        return list;
    }

    public static List<UsageRecord> ReadCopilot(IEnumerable<string> otelFiles, IEnumerable<string> storeDbs, IEnumerable<string> appDbs)
    {
        var store = new List<CopilotEvent>();
        foreach (var db in storeDbs) ReadCopilotStore(db, store);
        var matcher = new CopilotMatcher(store);
        var list = store.Select(e => e.Row).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in otelFiles.OrderBy(p => p, StringComparer.Ordinal))
        {
            try { list.AddRange(ReadCopilotOtel(file, matcher, seen)); }
            catch (IOException) { }
        }
        foreach (var db in appDbs) ReadCopilotApp(db, list);
        return list;
    }

    public static List<UsageRecord> ReadKimiCode(IEnumerable<string> homes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<UsageRecord>();
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home)) continue;
            var fallback = KimiFallback(home);
            var session = Path.GetFileName(home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            foreach (var file in UsageIo.EnumerateFiles(Path.Combine(home, "sessions"), 6,
                         static (path, _) => Path.GetFileName(path) == "wire.jsonl"))
            {
                try { ReadKimiFile(file, fallback, session ?? "kimi-code", seen, list); }
                catch (IOException) { }
            }
        }
        return list;
    }

    public static List<UsageRecord> ReadCodeBuddy(IEnumerable<string> homes) =>
        ReadCodeBuddy(homes, []);

    public static List<UsageRecord> ReadCodeBuddy(IEnumerable<string> homes, IEnumerable<string> logRoots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new Dictionary<string, (int Jsonl, int Log)>(StringComparer.Ordinal);
        var list = new List<UsageRecord>();
        var jsonl = new List<string>();
        foreach (var home in homes)
        {
            jsonl.AddRange(UsageIo.EnumerateFiles(Path.Combine(home, "projects"), 8,
                static (path, _) => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)));
        }
        jsonl.Sort(StringComparer.Ordinal);
        foreach (var file in jsonl)
        {
            var fallback = CodeBuddyFallback(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..")));
            try { ReadCodeBuddyJsonl(file, fallback, seen, fingerprints, list); }
            catch (IOException) { }
        }
        var logs = new List<string>();
        foreach (var root in logRoots)
        {
            logs.AddRange(UsageIo.EnumerateFiles(root, 8, (path, _) => IncludeCodeBuddyLog(root, path)));
        }
        logs.Sort(StringComparer.Ordinal);
        foreach (var file in logs)
        {
            try { ReadCodeBuddyLog(file, seen, fingerprints, list); }
            catch (IOException) { }
        }
        return list;
    }

    public static List<UsageRecord> ReadHermes(IEnumerable<string> homes)
    {
        var list = new List<UsageRecord>();
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home)) continue;
            var main = Path.Combine(home, "state.db");
            if (File.Exists(main)) ReadHermesDb(main, "default", list);
            foreach (var db in UsageIo.EnumerateFiles(Path.Combine(home, "profiles"), 2,
                         static (path, _) => Path.GetFileName(path) == "state.db"))
            {
                var profile = Path.GetFileName(Path.GetDirectoryName(db));
                if (!string.IsNullOrEmpty(profile)) ReadHermesDb(db, profile, list);
            }
        }
        return list;
    }

    private static List<UsageRecord> ReadGeminiSession(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return [];
        var sessionId = Path.GetFileNameWithoutExtension(file);
        if (string.IsNullOrEmpty(sessionId)) sessionId = "gemini-cli";
        string? model = null;
        GeminiTok? prev = null;
        var list = new List<UsageRecord>();
        var index = 0;
        foreach (var msg in messages.EnumerateArray())
        {
            var i = index++;
            var nextModel = UsageParsers.GetStr(msg, "model");
            if (nextModel is not null) model = nextModel;
            var current = GeminiTokens(msg);
            var ts = UsageParsers.ParseIso(UsageParsers.GetStr(msg, "timestamp"));
            if (current is null || ts is null)
            {
                prev = current ?? prev;
                continue;
            }
            var delta = GeminiDelta(current.Value, prev);
            prev = current;
            if (delta is null) continue;
            var d = delta.Value;
            if (d.Input == 0 && d.Output == 0 && d.Cached == 0 && d.Reasoning == 0) continue;
            list.Add(Row("gemini-cli", sessionId, i.ToString(CultureInfo.InvariantCulture), ts.Value,
                d.Input, d.Output, d.Cached, 0, d.Reasoning, model ?? "gemini-unknown", null));
        }
        return list;
    }

    private readonly record struct GeminiTok(long Input, long Cached, long Output, long Reasoning, long Total);

    private static GeminiTok? GeminiTokens(JsonElement msg)
    {
        var tokens = UsageParsers.GetObj(msg, "tokens");
        if (tokens is null) return null;
        var input = UsageIo.JsonLong(tokens.Value, "input");
        var cached = UsageIo.JsonLong(tokens.Value, "cached");
        var output = UsageIo.JsonLong(tokens.Value, "output");
        var tool = UsageIo.JsonLong(tokens.Value, "tool");
        var thoughts = UsageIo.JsonLong(tokens.Value, "thoughts");
        var reported = UsageIo.JsonLong(tokens.Value, "total");
        var computed = input + cached + output + tool + thoughts;
        return new GeminiTok(input, cached, output + tool, thoughts, Math.Max(reported, computed));
    }

    private static GeminiTok? GeminiDelta(GeminiTok current, GeminiTok? previous)
    {
        if (previous is null) return current;
        if (current.Input == previous.Value.Input && current.Cached == previous.Value.Cached
            && current.Output == previous.Value.Output && current.Reasoning == previous.Value.Reasoning
            && current.Total == previous.Value.Total)
            return null;
        if (current.Total < previous.Value.Total) return current;
        var delta = new GeminiTok(
            Math.Max(0, current.Input - previous.Value.Input),
            Math.Max(0, current.Cached - previous.Value.Cached),
            Math.Max(0, current.Output - previous.Value.Output),
            Math.Max(0, current.Reasoning - previous.Value.Reasoning),
            Math.Max(0, current.Total - previous.Value.Total));
        return delta.Input == 0 && delta.Cached == 0 && delta.Output == 0 && delta.Reasoning == 0 && delta.Total == 0
            ? null
            : delta;
    }

    private readonly record struct KiroSpan(long StartMs, long EndMs, string Model);

    private static List<KiroSpan> KiroTimeline(string root)
    {
        var timeline = new List<KiroSpan>();
        foreach (var file in UsageIo.EnumerateFiles(root, 2, static (path, depth) => depth == 2 && path.EndsWith(".chat", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var meta = UsageParsers.GetObj(doc.RootElement, "metadata");
                var model = UsageParsers.GetStr(meta, "modelId");
                if (model is null || meta is null || !UsageIo.TryJsonLong(meta.Value, "startTime", out var start) || start <= 0)
                    continue;
                var end = UsageIo.TryJsonLong(meta.Value, "endTime", out var stop) && stop > 0 ? stop : start;
                timeline.Add(new KiroSpan(start, end, model));
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        timeline.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        return timeline;
    }

    private static string KiroModelAt(List<KiroSpan> timeline, DateTime ts)
    {
        if (timeline.Count == 0) return "kiro-agent";
        var ms = new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        string? best = null;
        var bestDist = long.MaxValue;
        foreach (var span in timeline)
        {
            if (ms >= span.StartMs && ms <= span.EndMs) return KiroModelName(span.Model);
            var dist = Math.Min(Math.Abs(ms - span.StartMs), Math.Abs(ms - span.EndMs));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = span.Model;
            }
        }
        return bestDist < 10 * 60 * 1000 ? KiroModelName(best) : "kiro-agent";
    }

    private static string KiroModelName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "kiro-agent";
        var name = raw.Trim();
        if (name == name.ToLowerInvariant() && name.Contains('-')) return name;
        name = Regex.Replace(name, @"_\d{8}_V\d+_\d+$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"_V\d+$", "", RegexOptions.IgnoreCase);
        name = name.ToLowerInvariant().Replace('_', '-');
        return string.IsNullOrEmpty(name) ? "kiro-agent" : name;
    }

    private static List<UsageRecord> ReadKiroDb(string root, string dbPath, List<KiroSpan> timeline)
    {
        var list = new List<UsageRecord>();
        WithSqlite(dbPath, "agenthub-kiro-", conn =>
        {
            if (!UsageIo.TableExists(conn, "tokens_generated")) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, tokens_prompt, tokens_generated, timestamp
                FROM tokens_generated
                ORDER BY id
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!TryCount(reader, "tokens_prompt", required: false, out var input)
                    || !TryCount(reader, "tokens_generated", required: false, out var output))
                    continue;
                if (input == 0 && output == 0) continue;
                var ts = KiroTimestamp(ReadText(reader, "timestamp"));
                if (ts is null) continue;
                var id = reader.IsDBNull(reader.GetOrdinal("id"))
                    ? "0"
                    : Convert.ToString(reader.GetValue(reader.GetOrdinal("id")), CultureInfo.InvariantCulture) ?? "0";
                list.Add(Row("kiro", root, id, ts.Value, input, output, 0, 0, 0, KiroModelAt(timeline, ts.Value), null));
            }
        });
        return list;
    }

    private static List<UsageRecord> ReadKiroJsonl(string root, string file, List<KiroSpan> timeline)
    {
        var list = new List<UsageRecord>();
        DateTime ts;
        try { ts = File.GetLastWriteTimeUtc(file); }
        catch (IOException) { return list; }
        var model = KiroModelAt(timeline, ts);
        var lineNo = 0;
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            lineNo++;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var input = UsageIo.JsonLong(doc.RootElement, "promptTokens");
                var output = UsageIo.JsonLong(doc.RootElement, "generatedTokens");
                if (input == 0 && output == 0) continue;
                list.Add(Row("kiro", root, lineNo.ToString(CultureInfo.InvariantCulture), ts, input, output, 0, 0, 0, model, null));
            }
        }
        return list;
    }

    private static DateTime? KiroTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var iso = raw.Trim().Replace(' ', 'T');
        if (!iso.EndsWith("Z", StringComparison.Ordinal) && iso.IndexOf('+') < 0 && iso.IndexOf('-', 11) < 0)
            iso += "Z";
        return UsageParsers.ParseIso(iso);
    }

    private sealed record CopilotEvent(string SessionId, string Model, long Input, long Output, long CacheRead, long CacheWrite, long Reasoning, long TsMs, UsageRecord Row);

    private sealed class CopilotMatcher
    {
        private readonly Dictionary<string, List<(long Ts, int Index)>> _byKey = new(StringComparer.Ordinal);
        private readonly bool[] _used;

        public CopilotMatcher(List<CopilotEvent> events)
        {
            _used = new bool[events.Count];
            for (var i = 0; i < events.Count; i++)
            {
                var key = CopilotMatchKey(events[i]);
                if (!_byKey.TryGetValue(key, out var list))
                {
                    list = [];
                    _byKey[key] = list;
                }
                list.Add((events[i].TsMs, i));
            }
        }

        public bool Consume(string sessionId, string model, long input, long output, long cacheRead, long cacheWrite, long reasoning, long tsMs)
        {
            var key = string.Join('|', sessionId, model, input, output, cacheRead, cacheWrite, reasoning);
            if (!_byKey.TryGetValue(key, out var list)) return false;
            var best = -1;
            var bestDist = long.MaxValue;
            for (var i = 0; i < list.Count; i++)
            {
                if (_used[list[i].Index]) continue;
                var dist = Math.Abs(list[i].Ts - tsMs);
                if (dist <= 2000 && dist < bestDist)
                {
                    best = i;
                    bestDist = dist;
                }
            }
            if (best < 0) return false;
            _used[list[best].Index] = true;
            return true;
        }
    }

    private static string CopilotMatchKey(CopilotEvent e) =>
        string.Join('|', e.SessionId, e.Model, e.Input, e.Output, e.CacheRead, e.CacheWrite, e.Reasoning);

    private static void ReadCopilotStore(string dbPath, List<CopilotEvent> into)
    {
        WithSqlite(dbPath, "agenthub-copilot-store-", conn =>
        {
            if (!UsageIo.TableExists(conn, "assistant_usage_events")) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_id, model, input_tokens, output_tokens, cache_read_tokens,
                       cache_write_tokens, reasoning_tokens, token_details_json, created_at
                FROM assistant_usage_events
                ORDER BY id
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sessionId = ReadText(reader, "session_id") ?? "";
                var created = ReadText(reader, "created_at");
                var ts = CopilotTime(created, reader, "created_at");
                if (ts is null || string.IsNullOrEmpty(sessionId)) continue;
                var rawIn = CountOrZero(reader, "input_tokens");
                var rawOut = CountOrZero(reader, "output_tokens");
                var reasoning = CountOrZero(reader, "reasoning_tokens");
                var (input, cacheRead, cacheWrite, output) = CopilotStoreTokens(
                    rawIn, rawOut, CountOrZero(reader, "cache_read_tokens"), CountOrZero(reader, "cache_write_tokens"),
                    ReadText(reader, "token_details_json"));
                var reasoningClamped = Math.Min(reasoning, output);
                output = Math.Max(0, output - reasoningClamped);
                if (input + output + cacheRead + cacheWrite + reasoningClamped == 0) continue;
                var model = CopilotModel(ReadText(reader, "model"));
                var id = Convert.ToString(reader.GetValue(reader.GetOrdinal("id")), CultureInfo.InvariantCulture) ?? "0";
                var row = Row("copilot", sessionId, "store:" + id, ts.Value, input, output, cacheRead, cacheWrite, reasoningClamped, model, null);
                var ms = new DateTimeOffset(DateTime.SpecifyKind(ts.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                into.Add(new CopilotEvent(sessionId, model, input, output, cacheRead, cacheWrite, reasoningClamped, ms, row));
            }
        });
    }

    private static (long Input, long CacheRead, long CacheWrite, long Output) CopilotStoreTokens(
        long rawIn, long rawOut, long cacheReadCol, long cacheWriteCol, string? detailsJson)
    {
        if (!string.IsNullOrWhiteSpace(detailsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(detailsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    long input = 0, cacheRead = 0, cacheWrite = 0, output = 0, recognized = 0;
                    foreach (var entry in doc.RootElement.EnumerateArray())
                    {
                        var count = UsageIo.JsonLong(entry, "tokenCount");
                        switch (UsageParsers.GetStr(entry, "tokenType"))
                        {
                            case "input": input += count; recognized++; break;
                            case "cache_read": cacheRead += count; recognized++; break;
                            case "cache_write": cacheWrite += count; recognized++; break;
                            case "output": output += count; recognized++; break;
                        }
                    }
                    if (recognized > 0 && input + cacheRead + cacheWrite == rawIn && output == rawOut)
                        return (input, cacheRead, cacheWrite, output);
                }
            }
            catch (JsonException) { }
        }
        var read = Math.Min(cacheReadCol, rawIn);
        var write = Math.Min(cacheWriteCol, Math.Max(0, rawIn - read));
        return (Math.Max(0, rawIn - read - write), read, write, rawOut);
    }

    private static List<UsageRecord> ReadCopilotOtel(string file, CopilotMatcher matcher, HashSet<string> seen)
    {
        var list = new List<UsageRecord>();
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            if (string.IsNullOrWhiteSpace(line) || line.Contains("\"scopeMetrics\"", StringComparison.Ordinal)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var record = doc.RootElement;
                if (record.TryGetProperty("scopeMetrics", out _)) continue;
                var attrs = UsageParsers.GetObj(record, "attributes");
                var op = UsageParsers.GetStr(attrs, "gen_ai.operation.name");
                var type = UsageParsers.GetStr(record, "type");
                var name = UsageParsers.GetStr(record, "name");
                var chat = op == "chat" || (type == "span" && name is not null && name.StartsWith("chat ", StringComparison.Ordinal));
                if (!chat || attrs is null) continue;
                var cli = type == "span" && (op == "chat" || (name is not null && name.StartsWith("chat ", StringComparison.Ordinal)));
                var responseId = UsageParsers.GetStr(attrs, "gen_ai.response.id");
                string? key = null;
                if (!cli)
                    key = string.IsNullOrEmpty(responseId) ? null : "resp:" + responseId;
                else
                {
                    var trace = UsageParsers.GetStr(record, "traceId");
                    var span = UsageParsers.GetStr(record, "spanId");
                    key = !string.IsNullOrEmpty(trace) && !string.IsNullOrEmpty(span)
                        ? trace + ":" + span
                        : string.IsNullOrEmpty(responseId) ? null : "resp:" + responseId;
                }
                if (key is not null && !seen.Add(key)) continue;
                if (!TryCopilotOtel(attrs.Value, record, out var input, out var output, out var cacheRead, out var cacheWrite, out var reasoning, out var ts))
                    continue;
                var model = CopilotModel(UsageParsers.GetStr(attrs, "gen_ai.response.model") ?? UsageParsers.GetStr(attrs, "gen_ai.request.model"));
                var sessionId = UsageParsers.GetStr(attrs, "gen_ai.conversation.id") ?? "copilot";
                if (cli && matcher.Consume(sessionId, model, input, output, cacheRead, cacheWrite, reasoning, new DateTimeOffset(ts).ToUnixTimeMilliseconds()))
                    continue;
                list.Add(Row("copilot", sessionId, key ?? ts.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    ts, input, output, cacheRead, cacheWrite, reasoning, model, null));
            }
        }
        return list;
    }

    private static bool TryCopilotOtel(
        JsonElement attrs, JsonElement record,
        out long input, out long output, out long cacheRead, out long cacheWrite, out long reasoning, out DateTime ts)
    {
        input = output = cacheRead = cacheWrite = reasoning = 0;
        ts = default;
        var inputRaw = UsageIo.JsonLong(attrs, "gen_ai.usage.input_tokens");
        var outputRaw = UsageIo.JsonLong(attrs, "gen_ai.usage.output_tokens");
        cacheRead = FirstAttr(attrs, "gen_ai.usage.cache_read.input_tokens", "gen_ai.usage.cache_read_input_tokens", "gen_ai.usage.cached_input_tokens");
        cacheWrite = FirstAttr(attrs, "gen_ai.usage.cache_write.input_tokens", "gen_ai.usage.cache_creation.input_tokens", "gen_ai.usage.cache_write_input_tokens", "gen_ai.usage.cache_creation_input_tokens");
        reasoning = FirstAttr(attrs, "gen_ai.usage.reasoning.output_tokens", "gen_ai.usage.reasoning_tokens", "gen_ai.usage.reasoning_output_tokens");
        var cli = UsageParsers.GetStr(record, "type") == "span";
        var read = Math.Min(cacheRead, inputRaw);
        var write = Math.Min(cacheWrite, Math.Max(0, inputRaw - read));
        input = Math.Max(0, inputRaw - read - (cli ? write : 0));
        cacheRead = read;
        var writeAccounted = cli ? write : cacheWrite;
        cacheWrite = writeAccounted;
        reasoning = Math.Min(reasoning, outputRaw);
        output = Math.Max(0, outputRaw - reasoning);
        if (input + output + cacheRead + cacheWrite + reasoning == 0) return false;
        var ms = OtelMs(record, "endTime") ?? OtelMs(record, "startTime") ?? OtelMs(record, "hrTime") ?? OtelMs(record, "hrTimeObserved");
        if (ms is null or <= 0) return false;
        ts = DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).UtcDateTime;
        return true;
    }

    private static long FirstAttr(JsonElement attrs, params string[] names)
    {
        foreach (var name in names)
            if (UsageIo.HasJsonField(attrs, name)) return UsageIo.JsonLong(attrs, name);
        return 0;
    }

    private static long? OtelMs(JsonElement record, string name)
    {
        if (!record.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2)
            return null;
        if (!value[0].TryGetInt64(out var seconds)) return null;
        if (!value[1].TryGetInt64(out var nanos)) return null;
        var ms = seconds * 1000 + (long)Math.Round(nanos / 1_000_000.0);
        return ms > 0 ? ms : null;
    }

    private static void ReadCopilotApp(string dbPath, List<UsageRecord> into)
    {
        WithSqlite(dbPath, "agenthub-copilot-app-", conn =>
        {
            if (!UsageIo.TableExists(conn, "sessions")) return;
            var columns = SqliteColumns(conn, "sessions");
            if (!columns.Contains("id")) return;
            string Opt(string col) => columns.Contains(col) ? col : "NULL AS " + col;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, {Opt("session_type")}, {Opt("model")}, {Opt("provider_id")},
                       {Opt("created_at")}, {Opt("updated_at")},
                       {Opt("total_input_tokens")}, {Opt("total_output_tokens")},
                       {Opt("total_cached_tokens")}, {Opt("total_reasoning_tokens")}
                FROM sessions
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = ReadText(reader, "id");
                if (string.IsNullOrEmpty(id)) continue;
                if (!CopilotCountApp(ReadText(reader, "session_type"), ReadText(reader, "provider_id"))) continue;
                var rawIn = CountOrZero(reader, "total_input_tokens");
                var rawOut = CountOrZero(reader, "total_output_tokens");
                var cached = CountOrZero(reader, "total_cached_tokens");
                var reasoning = CountOrZero(reader, "total_reasoning_tokens");
                var cacheRead = Math.Min(cached, rawIn);
                var input = Math.Max(0, rawIn - cacheRead);
                var reasoningClamped = Math.Min(reasoning, rawOut);
                var output = Math.Max(0, rawOut - reasoningClamped);
                if (input + output + cacheRead + reasoningClamped == 0) continue;
                var ts = CopilotTime(ReadText(reader, "updated_at"), reader, "updated_at")
                         ?? CopilotTime(ReadText(reader, "created_at"), reader, "created_at");
                if (ts is null) continue;
                into.Add(Row("copilot", id, "app:" + id, ts.Value, input, output, cacheRead, 0, reasoningClamped,
                    CopilotModel(ReadText(reader, "model")), null));
            }
        });
    }

    private static bool CopilotCountApp(string? sessionType, string? provider)
    {
        foreach (var value in new[] { sessionType, provider })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var v = value.Trim().ToLowerInvariant();
            if (v is "cli" or "copilot-cli" || v.Contains("vscode", StringComparison.Ordinal) || v.Contains("extension", StringComparison.Ordinal) || v.Contains("otel", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string CopilotModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "github-copilot";
        var name = model.Trim();
        var match = CopilotClaudeVersion.Match(name);
        if (match.Success)
            name = match.Groups[1].Value + "-" + match.Groups[2].Value + name[match.Length..];
        return name;
    }

    private static readonly Regex CopilotClaudeVersion = new(@"^(claude-(?:sonnet|opus|haiku)-\d+)\.(\d+)", RegexOptions.Compiled);

    private static DateTime? CopilotTime(string? text, SqliteDataReader reader, string column)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return UsageParsers.ParseMs(n);
            return UsageParsers.ParseIso(text);
        }
        var i = reader.GetOrdinal(column);
        if (reader.IsDBNull(i)) return null;
        var raw = reader.GetValue(i);
        if (raw is long l) return UsageParsers.ParseMs(l);
        if (raw is int v) return UsageParsers.ParseMs(v);
        return null;
    }

    private static void ReadKimiFile(string file, string fallback, string homeName, HashSet<string> seen, List<UsageRecord> list)
    {
        var sessionId = KimiSessionId(file) ?? homeName;
        var model = fallback;
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (UsageParsers.GetStr(root, "type") == "config.update")
                {
                    var alias = UsageParsers.GetStr(root, "modelAlias");
                    if (!string.IsNullOrEmpty(alias))
                    {
                        var slash = alias.LastIndexOf('/');
                        model = slash >= 0 && slash < alias.Length - 1 ? alias[(slash + 1)..] : alias;
                    }
                    continue;
                }
                var msg = UsageParsers.GetObj(root, "message");
                if (msg is not null && UsageParsers.GetStr(msg, "type") == "StatusUpdate")
                {
                    ReadKimiLegacy(root, msg.Value, sessionId, model, seen, list);
                    continue;
                }
                var evt = UsageParsers.GetStr(root, "type") == "context.append_loop_event"
                    ? UsageParsers.GetObj(root, "event") ?? root
                    : root;
                if (UsageParsers.GetStr(evt, "type") != "step.end") continue;
                var usage = UsageParsers.GetObj(evt, "usage");
                var id = UsageParsers.GetStr(evt, "uuid");
                if (usage is null || string.IsNullOrEmpty(id) || seen.Contains(id)) continue;
                seen.Add(id);
                if (!KimiCodeTokens(usage.Value, out var input, out var output, out var cacheRead, out var cacheWrite))
                    continue;
                var ms = UsageIo.TryJsonLong(root, "time", out var top) ? top
                    : UsageIo.TryJsonLong(evt, "time", out var inner) ? inner : 0;
                var ts = UsageParsers.ParseMs(ms);
                if (ts is null) continue;
                list.Add(Row("kimi-code", sessionId, id, ts.Value, input, output, cacheRead, cacheWrite, 0, model, null));
            }
        }
    }

    private static void ReadKimiLegacy(JsonElement root, JsonElement msg, string sessionId, string model, HashSet<string> seen, List<UsageRecord> list)
    {
        var payload = UsageParsers.GetObj(msg, "payload");
        var usage = payload is null ? null : UsageParsers.GetObj(payload, "token_usage");
        var id = UsageParsers.GetStr(payload, "message_id");
        if (usage is null || string.IsNullOrEmpty(id) || seen.Contains(id)) return;
        seen.Add(id);
        var input = UsageIo.JsonLong(usage.Value, "input_other");
        var output = UsageIo.JsonLong(usage.Value, "output");
        var cacheRead = UsageIo.JsonLong(usage.Value, "input_cache_read");
        var cacheWrite = UsageIo.JsonLong(usage.Value, "input_cache_creation");
        if (input + output + cacheRead + cacheWrite == 0) return;
        long epoch = 0;
        if (UsageIo.TryJsonLong(root, "timestamp", out var top)) epoch = top;
        else if (payload is not null && UsageIo.TryJsonLong(payload.Value, "timestamp", out var inner)) epoch = inner;
        var ts = epoch > 0 ? UsageParsers.ParseMs(epoch < 10_000_000_000L ? epoch * 1000 : epoch) : null;
        if (ts is null) return;
        list.Add(Row("kimi-code", sessionId, id, ts.Value, input, output, cacheRead, cacheWrite, 0, model, null));
    }

    private static bool KimiCodeTokens(JsonElement usage, out long input, out long output, out long cacheRead, out long cacheWrite)
    {
        input = output = cacheRead = cacheWrite = 0;
        if (UsageIo.HasJsonField(usage, "inputOther"))
        {
            input = UsageIo.JsonLong(usage, "inputOther");
            cacheRead = UsageIo.JsonLong(usage, "inputCacheRead");
            cacheWrite = UsageIo.JsonLong(usage, "inputCacheCreation");
            output = UsageIo.JsonLong(usage, "output");
        }
        else
        {
            cacheWrite = UsageIo.JsonLong(usage, "cache_creation_input_tokens");
            if (UsageIo.HasJsonField(usage, "cache_read_input_tokens"))
            {
                cacheRead = UsageIo.JsonLong(usage, "cache_read_input_tokens");
                input = UsageIo.JsonLong(usage, "input_tokens");
            }
            else
            {
                var details = UsageParsers.GetObj(usage, "input_tokens_details");
                cacheRead = details is null ? 0 : UsageIo.JsonLong(details.Value, "cached_tokens");
                input = Math.Max(0, UsageIo.JsonLong(usage, "input_tokens") - cacheRead);
            }
            output = UsageIo.JsonLong(usage, "output_tokens");
        }
        return input + output + cacheRead + cacheWrite > 0;
    }

    private static string? KimiSessionId(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (dir is null) return null;
        var name = Path.GetFileName(dir);
        if (string.Equals(name, "agents", StringComparison.OrdinalIgnoreCase))
            return FileNameOrNull(Path.GetDirectoryName(dir));
        var parent = Path.GetFileName(Path.GetDirectoryName(dir));
        if (string.Equals(parent, "agents", StringComparison.OrdinalIgnoreCase))
            return FileNameOrNull(Path.GetDirectoryName(Path.GetDirectoryName(dir)));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string? FileNameOrNull(string? path)
    {
        var name = path is null ? null : Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string KimiFallback(string home)
    {
        var cfg = Path.Combine(home, "config.toml");
        if (!File.Exists(cfg)) return "kimi-for-coding";
        try
        {
            var match = KimiDefaultModel.Match(File.ReadAllText(cfg));
            if (!match.Success) return "kimi-for-coding";
            var value = match.Groups[1].Value.Trim();
            var slash = value.LastIndexOf('/');
            return slash >= 0 && slash < value.Length - 1 ? value[(slash + 1)..] : string.IsNullOrEmpty(value) ? "kimi-for-coding" : value;
        }
        catch (IOException) { return "kimi-for-coding"; }
    }

    private static readonly Regex KimiDefaultModel = new(@"^\s*default_model\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.Compiled);

    private static void ReadCodeBuddyJsonl(
        string file, string fallback, HashSet<string> seen, Dictionary<string, (int Jsonl, int Log)> fingerprints, List<UsageRecord> list)
    {
        var sessionFromName = Path.GetFileNameWithoutExtension(file);
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("rawUsage", StringComparison.Ordinal)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var entry = doc.RootElement;
                var provider = UsageParsers.GetObj(entry, "providerData");
                var usage = provider is null ? null : UsageParsers.GetObj(provider, "rawUsage");
                if (usage is null) continue;
                var sessionId = UsageParsers.GetStr(entry, "sessionId") ?? sessionFromName;
                if (!UsageIo.TryJsonLong(entry, "timestamp", out var tsMs) || tsMs <= 0) continue;
                var messageId = UsageParsers.GetStr(provider, "messageId")
                                 ?? UsageParsers.GetStr(entry, "uuid")
                                 ?? UsageParsers.GetStr(entry, "id")
                                 ?? sessionId + ":" + tsMs.ToString(CultureInfo.InvariantCulture);
                if (!seen.Add(messageId)) continue;
                var prompt = UsageIo.JsonLong(usage.Value, "prompt_tokens");
                var completion = UsageIo.JsonLong(usage.Value, "completion_tokens");
                var details = UsageParsers.GetObj(usage, "prompt_tokens_details");
                var completionDetails = UsageParsers.GetObj(usage, "completion_tokens_details");
                var cached = Math.Max(
                    details is null ? 0 : UsageIo.JsonLong(details.Value, "cached_tokens"),
                    UsageIo.JsonLong(usage.Value, "prompt_cache_hit_tokens"));
                var cacheRead = Math.Max(cached, UsageIo.JsonLong(usage.Value, "cache_read_input_tokens"));
                var cacheWrite = Math.Max(
                    UsageIo.JsonLong(usage.Value, "cache_creation_input_tokens"),
                    UsageIo.JsonLong(usage.Value, "prompt_cache_write_tokens"));
                var reasoning = Math.Min(completion, completionDetails is null ? 0 : UsageIo.JsonLong(completionDetails.Value, "reasoning_tokens"));
                var output = Math.Max(0, completion - reasoning);
                var input = Math.Max(0, prompt - cacheRead - cacheWrite);
                if (input + output + cacheRead + cacheWrite + reasoning == 0) continue;
                var ts = UsageParsers.ParseMs(tsMs);
                if (ts is null) continue;
                var model = UsageParsers.GetStr(provider, "model") ?? UsageParsers.GetStr(entry, "model") ?? fallback;
                if (CodeBuddyMirrored(fingerprints, CodeBuddyFingerprint(model, tsMs, input, cacheRead, cacheWrite, output, reasoning), fromJsonl: true))
                    continue;
                list.Add(Row("codebuddy", sessionId, messageId, ts.Value, input, output, cacheRead, cacheWrite, reasoning, model, null));
            }
        }
    }

    private static void ReadCodeBuddyLog(
        string file, HashSet<string> seen, Dictionary<string, (int Jsonl, int Log)> fingerprints, List<UsageRecord> list)
    {
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("[CraftInvokableAgent]", StringComparison.Ordinal) && line.Contains("Model prepared:", StringComparison.Ordinal))
            {
                var agent = BracketAfter(line, "[CraftInvokableAgent]");
                var marker = "Model prepared:";
                var at = line.IndexOf(marker, StringComparison.Ordinal);
                if (agent is null || at < 0) continue;
                var prepared = line[(at + marker.Length)..].Trim();
                var open = prepared.LastIndexOf('(');
                if (open >= 0)
                {
                    var close = prepared.IndexOf(')', open + 1);
                    if (close > open) prepared = prepared[(open + 1)..close].Trim();
                }
                if (prepared.Length > 0) models[agent] = prepared;
                continue;
            }
            if (!line.Contains("[AgentReporter]", StringComparison.Ordinal) || !line.Contains("Agent execution successful with usage:", StringComparison.Ordinal))
                continue;
            var agentId = BracketAfter(line, "[AgentReporter]");
            if (agentId is null) continue;
            var usageMarker = "Agent execution successful with usage:";
            var cut = line.IndexOf(usageMarker, StringComparison.Ordinal);
            if (cut < 0) continue;
            var raw = line[(cut + usageMarker.Length)..].Trim();
            var end = raw.LastIndexOf('}');
            if (end < 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw[..(end + 1)]); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var usage = doc.RootElement;
                var input = FirstLong(usage, "cachedMissTokens", "cacheMissTokens", "input_tokens", "inputTokens", "prompt_tokens");
                var output = FirstLong(usage, "output_tokens", "outputTokens", "completion_tokens");
                var cacheRead = FirstPositiveOrPresent(usage, "cache_read_input_tokens", "cacheReadInputTokens", "cacheTokens", "prompt_cache_hit_tokens", "cached_tokens");
                var cacheWrite = FirstPositiveOrPresent(usage, "cache_creation_input_tokens", "cacheCreationInputTokens", "cachedWriteTokens", "prompt_cache_write_tokens");
                var reasoning = FirstLong(usage, "completion_thinking_tokens", "completionThinkingTokens", "reasoningTokens");
                var fromTotal = !UsageIo.HasJsonField(usage, "cachedMissTokens")
                                && !UsageIo.HasJsonField(usage, "cacheMissTokens")
                                && !UsageIo.HasJsonField(usage, "prompt_cache_miss_tokens");
                if (fromTotal && cacheRead > 0) input = Math.Max(0, input - cacheRead);
                if (input + output + cacheRead + cacheWrite == 0) continue;
                var tsMs = LogTimestampMs(line, File.GetLastWriteTimeUtc(file));
                if (tsMs <= 0) continue;
                var model = models.TryGetValue(agentId, out var prepared) ? prepared : "codebuddy-unknown";
                var second = tsMs / 1000;
                var id = $"codebuddy:extension-log:{agentId}:{second}:{input}:{output}:{cacheRead}:{cacheWrite}:{reasoning}";
                if (!seen.Add(id)) continue;
                if (CodeBuddyMirrored(fingerprints, CodeBuddyFingerprint(model, tsMs, input, cacheRead, cacheWrite, output, reasoning), fromJsonl: false))
                    continue;
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime;
                list.Add(Row("codebuddy", agentId, id, ts, input, output, cacheRead, cacheWrite, reasoning, model, null));
            }
        }
    }

    private static bool CodeBuddyMirrored(Dictionary<string, (int Jsonl, int Log)> state, string fingerprint, bool fromJsonl)
    {
        state.TryGetValue(fingerprint, out var counts);
        var mirrored = fromJsonl ? counts.Log > counts.Jsonl : counts.Jsonl > counts.Log;
        if (fromJsonl) counts.Jsonl++;
        else counts.Log++;
        state[fingerprint] = counts;
        return mirrored;
    }

    private static string CodeBuddyFingerprint(string model, long tsMs, long input, long cacheRead, long cacheWrite, long output, long reasoning) =>
        string.Join(':', tsMs / 1000, model, input, cacheRead, cacheWrite, output, reasoning);

    private static string CodeBuddyFallback(string home)
    {
        var settings = Path.Combine(home, "settings.json");
        if (!File.Exists(settings)) return "codebuddy-unknown";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settings));
            return UsageParsers.GetStr(doc.RootElement, "model") ?? "codebuddy-unknown";
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return "codebuddy-unknown"; }
    }

    private static string? BracketAfter(string line, string marker)
    {
        var at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = line[(at + marker.Length)..];
        var open = rest.IndexOf('[');
        if (open < 0) return null;
        var close = rest.IndexOf(']', open + 1);
        if (close < 0) return null;
        var value = rest[(open + 1)..close].Trim();
        return value.Length == 0 ? null : value;
    }

    private static long LogTimestampMs(string line, DateTime fallback)
    {
        string raw;
        if (line.StartsWith('['))
        {
            var end = line.IndexOf(']');
            raw = end > 1 ? line[1..end].Trim() : "";
        }
        else
        {
            var at = line.IndexOf(" [", StringComparison.Ordinal);
            raw = (at > 0 ? line[..at] : line.Length > 23 ? line[..23] : line).Trim();
        }
        var ts = UsageParsers.ParseIso(raw) ?? UsageParsers.ParseIso(raw.Replace('/', '-'));
        return ts is null
            ? new DateTimeOffset(DateTime.SpecifyKind(fallback, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
            : new DateTimeOffset(ts.Value).ToUnixTimeMilliseconds();
    }

    private static bool IncludeCodeBuddyLog(string root, string path)
    {
        if (!path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return false;
        var norm = root.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var sep = Path.DirectorySeparatorChar;
        var ideLogs = norm.Contains($"{sep}CodeBuddy CN{sep}logs", StringComparison.OrdinalIgnoreCase)
                      || norm.Contains($"{sep}Code{sep}logs", StringComparison.OrdinalIgnoreCase);
        return !ideLogs || path.Contains("tencent-cloud.coding-copilot", StringComparison.OrdinalIgnoreCase);
    }

    private static long FirstLong(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (UsageIo.HasJsonField(el, name)) return UsageIo.JsonLong(el, name);
        return 0;
    }

    private static long FirstPositiveOrPresent(JsonElement el, params string[] names)
    {
        long? first = null;
        foreach (var name in names)
        {
            if (!UsageIo.HasJsonField(el, name)) continue;
            var n = UsageIo.JsonLong(el, name);
            first ??= n;
            if (n > 0) return n;
        }
        return first ?? 0;
    }

    private static void ReadHermesDb(string dbPath, string profile, List<UsageRecord> list)
    {
        WithSqlite(dbPath, "agenthub-hermes-", conn =>
        {
            if (!UsageIo.TableExists(conn, "sessions")) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, model, started_at, ended_at, input_tokens, output_tokens,
                       cache_read_tokens, cache_write_tokens, reasoning_tokens
                FROM sessions
                WHERE input_tokens > 0 OR output_tokens > 0 OR cache_read_tokens > 0
                   OR cache_write_tokens > 0 OR reasoning_tokens > 0
                ORDER BY started_at
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = ReadText(reader, "id");
                if (string.IsNullOrEmpty(id)) continue;
                if (!TryCount(reader, "input_tokens", false, out var input)
                    || !TryCount(reader, "output_tokens", false, out var output)
                    || !TryCount(reader, "cache_read_tokens", false, out var cacheRead)
                    || !TryCount(reader, "cache_write_tokens", false, out var cacheWrite)
                    || !TryCount(reader, "reasoning_tokens", false, out var reasoning))
                    continue;
                if (input + output + cacheRead + cacheWrite + reasoning == 0) continue;
                var ended = EpochSeconds(reader, "ended_at");
                var started = EpochSeconds(reader, "started_at");
                var ts = UsageParsers.ParseMs((ended ?? started ?? 0) * 1000);
                if (ts is null) continue;
                var sessionId = profile + "/" + id;
                list.Add(Row("hermes", sessionId, "snapshot", ts.Value, input, output, cacheRead, cacheWrite, reasoning,
                    ReadText(reader, "model") ?? "hermes-agent", null));
            }
        });
    }

    private static long? EpochSeconds(SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        if (reader.IsDBNull(i)) return null;
        var raw = reader.GetValue(i);
        long n = raw switch
        {
            long l => l,
            int v => v,
            double d when double.IsFinite(d) => (long)d,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
        return n > 0 ? n : null;
    }

    private static long CountOrZero(SqliteDataReader reader, string column) =>
        TryCount(reader, column, required: false, out var value) ? value : 0;

    private static HashSet<string> SqliteColumns(SqliteConnection conn, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        if (!table.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_')) return names;
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (!string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                names.Add(name);
        }
        return names;
    }

    private static void WithSqlite(string path, string prefix, Action<SqliteConnection> read)
    {
        if (!File.Exists(path) || !UsageIo.TrySnapshot(path, prefix, out var db, out var tmp)) return;
        try
        {
            using var conn = UsageIo.OpenReadOnly(db);
            read(conn);
        }
        catch (SqliteException) { }
        finally { UsageIo.DeleteSnapshot(tmp); }
    }
}
