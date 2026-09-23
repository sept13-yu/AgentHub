using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// TokenTracker 被动用量。只产出 token / 模型 / 时间 / 项目，不保留提示词或正文。
/// 新增一家：在这里加一个 Read，再在 <see cref="UsageSourceRegistry"/> 登记一行。
/// </summary>
internal static partial class PassiveUsage
{
    private const string ObserverSegment = "--claude-mem-observer-sessions";

    public static List<UsageRecord> ReadMiniMax(IEnumerable<string> homes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<UsageRecord>();
        foreach (var file in SessionFiles(homes, Path.Combine("v2", "sessions"), 5,
                     static (path, depth) => depth > 1 && Path.GetFileName(path) == "messages.jsonl"))
        {
            var sessionId = Path.GetFileName(Path.GetDirectoryName(file)) ?? "minimax";
            foreach (var line in UsageParsers.ReadLinesShared(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var msg = UsageParsers.GetObj(root, "message");
                    if (msg is null || !IsRole(msg, "assistant")) continue;
                    var id = UsageParsers.GetStr(root, "message_id");
                    if (string.IsNullOrEmpty(id) || seen.Contains(id)) continue;
                    var usage = UsageParsers.GetObj(msg, "usage");
                    if (usage is null) continue;
                    var input = UsageIo.JsonLong(usage.Value, "input");
                    var output = UsageIo.JsonLong(usage.Value, "output");
                    var cacheRead = UsageIo.JsonLong(usage.Value, "cacheRead");
                    var cacheWrite = UsageIo.JsonLong(usage.Value, "cacheWrite");
                    var reasoning = UsageIo.JsonLong(usage.Value, "reasoningTokens");
                    // 全零和缺时间也占住 message_id，避免同一条历史再被算一次。
                    seen.Add(id);
                    if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning == 0)
                        continue;
                    var ts = UsageParsers.ParseMs(UsageIo.JsonLong(msg.Value, "timestamp"));
                    if (ts is null) continue;
                    list.Add(Row("minimax", sessionId, id, ts.Value, input, output, cacheRead, cacheWrite, reasoning,
                        UsageParsers.GetStr(msg, "model") ?? "minimax-unknown", null));
                }
            }
        }
        return list;
    }

    public static List<UsageRecord> ReadReasonix(IEnumerable<string> homes)
    {
        var list = new List<UsageRecord>();
        foreach (var file in SessionFiles(homes, "", 8,
                     static (path, _) => path.EndsWith(".jsonl.telemetry.json", StringComparison.Ordinal)))
        {
            JsonDocument telemetry;
            try { telemetry = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is IOException or JsonException) { continue; }
            using (telemetry)
            {
                var usage = UsageParsers.GetObj(telemetry.RootElement, "usage");
                if (usage is null) continue;
                var totals = ReasonixTotals(usage.Value);
                if (totals.Input == 0 && totals.Output == 0 && totals.CacheRead == 0
                    && totals.CacheWrite == 0 && totals.Reasoning == 0)
                    continue;

                var metaPath = file[..^".telemetry.json".Length] + ".meta";
                string? model = null, sessionId = null;
                DateTime? ts = null;
                if (File.Exists(metaPath))
                {
                    try
                    {
                        using var metaDoc = JsonDocument.Parse(File.ReadAllText(metaPath));
                        var meta = metaDoc.RootElement;
                        model = UsageParsers.GetStr(meta, "model");
                        sessionId = UsageParsers.GetStr(meta, "id");
                        ts = UsageParsers.ParseIso(UsageParsers.GetStr(meta, "updated_at"))
                             ?? UsageParsers.ParseIso(UsageParsers.GetStr(meta, "created_at"));
                    }
                    catch (Exception ex) when (ex is IOException or JsonException) { }
                }
                ts ??= File.GetLastWriteTimeUtc(file);
                sessionId ??= ReasonixSessionId(file);
                list.Add(Row("reasonix", sessionId, "cumulative", ts.Value,
                    totals.Input, totals.Output, totals.CacheRead, totals.CacheWrite, totals.Reasoning,
                    ReasonixModel(model), ReasonixProject(file)));
            }
        }
        return list;
    }

    public static List<UsageRecord> ReadClaudeCode(IEnumerable<string> homes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<string>();
        foreach (var home in homes)
        {
            files.AddRange(UsageIo.EnumerateFiles(Path.Combine(home, "projects"), 8,
                static (path, _) => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                                    && !path.Contains(ObserverSegment, StringComparison.Ordinal)));
        }
        files.Sort(StringComparer.Ordinal);
        var list = new List<UsageRecord>();
        foreach (var file in files)
        {
            try { list.AddRange(ReadClaudeFile(file, seen)); }
            catch (IOException) { }
        }
        return list;
    }

    public static List<UsageRecord> ReadDevin(IEnumerable<string> dbPaths)
    {
        var best = new Dictionary<string, DevinCandidate>(StringComparer.Ordinal);
        foreach (var dbPath in dbPaths.Where(File.Exists))
        {
            if (!UsageIo.TrySnapshot(dbPath, "agenthub-devin-", out var db, out var tmp)) continue;
            try
            {
                using var conn = UsageIo.OpenReadOnly(db);
                if (!UsageIo.TableExists(conn, "message_nodes")) continue;
                var hasSessions = UsageIo.TableExists(conn, "sessions");
                using var cmd = conn.CreateCommand();
                cmd.CommandText = hasSessions ? DevinSqlWithSessions : DevinSqlNodesOnly;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = ReadDevinRow(reader, hasSessions);
                    if (row is null) continue;
                    if (!best.TryGetValue(row.RequestId, out var prev) || DevinPrefer(row, prev))
                        best[row.RequestId] = row;
                }
            }
            finally { UsageIo.DeleteSnapshot(tmp); }
        }

        return best.Values
            .OrderBy(r => r.TsUtc)
            .ThenBy(r => r.RequestId, StringComparer.Ordinal)
            .Select(r => Row("devin", string.IsNullOrEmpty(r.SessionId) ? "devin" : r.SessionId, r.RequestId,
                r.TsUtc, r.Input, r.Output, r.CacheRead, r.CacheWrite, 0, r.Model, r.Project))
            .ToList();
    }

    public static List<UsageRecord> ReadOpenCode(IEnumerable<string> dataDirs)
    {
        var byKey = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        var fingerprintOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in dataDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in UsageIo.EnumerateFiles(Path.Combine(dir, "storage", "message"), 12,
                         static (path, _) =>
                         {
                             var name = Path.GetFileName(path);
                             return name.StartsWith("msg_", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal);
                         }))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    ConsiderOpenCode(doc.RootElement, null, null, null, byKey, fingerprintOwner, requireRole: true);
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }

            var dbPath = Path.Combine(dir, "opencode.db");
            if (!File.Exists(dbPath)) continue;
            if (!UsageIo.TrySnapshot(dbPath, "agenthub-opencode-", out var db, out var tmp)) continue;
            try { ReadOpenCodeDb(db, byKey, fingerprintOwner); }
            finally { UsageIo.DeleteSnapshot(tmp); }
        }
        return byKey.Values.ToList();
    }

    public static List<UsageRecord> ReadAntigravity(IEnumerable<string> geminiHomes)
    {
        var list = new List<UsageRecord>();
        foreach (var home in geminiHomes)
        {
            foreach (var variant in new[] { "antigravity", "antigravity-ide", "antigravity-cli" })
            {
                var brain = Path.Combine(home, variant, "brain");
                foreach (var file in UsageIo.EnumerateFiles(brain, 4,
                             static (path, _) => Path.GetFileName(path) == "transcript.jsonl"))
                {
                    try { list.AddRange(ReadAntigravityFile(file)); }
                    catch (IOException) { }
                }
            }
        }
        return list;
    }

    internal static AntigravityGenInfo? ExtractAntigravityGenInfo(byte[] payload)
    {
        if (payload is null || payload.Length == 0) return null;
        var root = ReadProtoFields(payload);
        var innerBytes = FirstBytes(root, 1);
        if (innerBytes is null) return null;
        var inner = ReadProtoFields(innerBytes);
        if (inner is null) return null;

        var model = FirstBytes(inner, 19) is { } rawModel ? Encoding.UTF8.GetString(rawModel).Trim() : null;
        long context = 0;
        if (FirstBytes(inner, 9) is { } f9 && FirstBytes(ReadProtoFields(f9), 10) is { } f10)
        {
            var tok = FirstVarint(ReadProtoFields(f10), 1);
            if (tok is not null) context = tok.Value;
        }

        int? step = null;
        foreach (var field in inner)
        {
            if (field.Number != 20 || field.Bytes is null) continue;
            var kv = ReadProtoFields(field.Bytes);
            var key = FirstBytes(kv, 1);
            var val = FirstBytes(kv, 2);
            if (key is null || val is null) continue;
            if (Encoding.UTF8.GetString(key) != "last_step_index") continue;
            var stepText = Encoding.UTF8.GetString(val).Trim();
            if (stepText.Length > 0 && stepText.All(char.IsAsciiDigit) && int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                step = parsed;
        }

        var info = new AntigravityGenInfo
        {
            Model = string.IsNullOrEmpty(model) ? null : model,
            ContextTokens = context,
            LastStepIndex = step,
        };
        var f4 = FirstBytes(inner, 4);
        if (f4 is null) return info;
        var usage = ReadProtoFields(f4);
        if (usage is null) return info;
        var sys = FirstVarint(usage, 1);
        var prompt = FirstVarint(usage, 2);
        var output = FirstVarint(usage, 3);
        var cached = FirstVarint(usage, 5);
        var textOutput = FirstVarint(usage, 9);
        var reasoning = FirstVarint(usage, 10);
        if (sys is null && prompt is null && output is null && cached is null && textOutput is null && reasoning is null)
            return info;
        return info with
        {
            HasUsage = true,
            UncachedInput = (sys ?? 0) + (prompt ?? 0),
            CachedInput = cached ?? 0,
            OutputTokens = output ?? 0,
            TextOutput = textOutput ?? 0,
            ReasoningOutput = reasoning ?? 0,
        };
    }

    private static IEnumerable<string> SessionFiles(
        IEnumerable<string> homes, string relative, int maxDepth, Func<string, int, bool> include)
    {
        var files = new List<string>();
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home)) continue;
            var root = string.IsNullOrEmpty(relative) ? home : Path.Combine(home, relative);
            files.AddRange(UsageIo.EnumerateFiles(root, maxDepth, include));
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static List<UsageRecord> ReadClaudeFile(string file, HashSet<string> seen)
    {
        string? cwd = null;
        var batch = new List<UsageRecord>();
        var fallbacks = new Dictionary<string, int>(StringComparer.Ordinal);
        var sessionId = ClaudeSessionId(file);
        var sub = file.Contains($"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                  || file.Contains($"{Path.AltDirectorySeparatorChar}subagents{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                cwd ??= UsageParsers.GetStr(root, "cwd");
                var msg = UsageParsers.GetObj(root, "message");
                var usage = msg is not null ? UsageParsers.GetObj(msg, "usage") : null;
                usage ??= UsageParsers.GetObj(root, "usage");
                if (usage is null) continue;

                var input = UsageIo.JsonLong(usage.Value, "input_tokens");
                var output = UsageIo.JsonLong(usage.Value, "output_tokens");
                var cacheRead = UsageIo.JsonLong(usage.Value, "cache_read_input_tokens");
                var cacheWrite = UsageIo.JsonLong(usage.Value, "cache_creation_input_tokens");
                var reasoning = ClaudeReasoning(usage.Value, output);
                output = Math.Max(0, output - reasoning);
                if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning == 0)
                    continue;
                var ts = UsageParsers.ParseIso(UsageParsers.GetStr(root, "timestamp"));
                if (ts is null) continue;
                var model = UsageParsers.GetStr(msg, "model") ?? UsageParsers.GetStr(root, "model") ?? "unknown";
                var key = ClaudeKey(root, msg, ts.Value, model, input, output, cacheRead, cacheWrite, reasoning, fallbacks);
                if (key.StartsWith("id:", StringComparison.Ordinal) && !seen.Add(key)) continue;
                batch.Add(new UsageRecord
                {
                    Tool = "claude-code",
                    SessionId = sessionId,
                    RequestKey = key.StartsWith("id:", StringComparison.Ordinal) ? key[3..] : key,
                    TsUtc = ts.Value,
                    InputTokens = input,
                    OutputTokens = output,
                    CachedInputTokens = cacheRead,
                    CacheWriteTokens = cacheWrite,
                    ReasoningTokens = reasoning,
                    IsSubagent = sub,
                    Model = model,
                });
            }
        }
        if (cwd is null) return batch;
        return batch.Select(r => r with { Project = cwd }).ToList();
    }

    private static long ClaudeReasoning(JsonElement usage, long output)
    {
        var details = UsageParsers.GetObj(usage, "output_tokens_details");
        long reasoning = 0;
        if (details is not null)
        {
            if (UsageIo.HasJsonField(details.Value, "thinking_tokens"))
                reasoning = UsageIo.JsonLong(details.Value, "thinking_tokens");
            else
                reasoning = UsageIo.JsonLong(details.Value, "reasoning_tokens");
        }
        if (reasoning < 0) reasoning = 0;
        return Math.Min(output, reasoning);
    }

    private static string ClaudeKey(
        JsonElement root, JsonElement? msg, DateTime ts, string model,
        long input, long output, long cacheRead, long cacheWrite, long reasoning,
        Dictionary<string, int> fallbacks)
    {
        var messageId = UsageParsers.GetStr(msg, "id");
        if (!string.IsNullOrEmpty(messageId))
        {
            var requestId = UsageParsers.GetStr(root, "requestId");
            return "id:" + (string.IsNullOrEmpty(requestId) ? messageId : messageId + ":" + requestId);
        }
        var sig = string.Create(CultureInfo.InvariantCulture,
            $"{ts:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}|{model}|{input}|{output}|{cacheRead}|{cacheWrite}|{reasoning}");
        var n = fallbacks.TryGetValue(sig, out var seen) ? seen + 1 : 1;
        fallbacks[sig] = n;
        return n == 1 ? sig : sig + "#" + n.ToString(CultureInfo.InvariantCulture);
    }

    private static string ClaudeSessionId(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var dir = Path.GetDirectoryName(file);
        if (dir is not null && string.Equals(Path.GetFileName(dir), "subagents", StringComparison.OrdinalIgnoreCase))
        {
            var session = Path.GetFileName(Path.GetDirectoryName(dir));
            if (!string.IsNullOrEmpty(session)) return session;
        }
        return string.IsNullOrEmpty(name) ? "claude-code" : name;
    }

    private const string DevinMetric = """
        CASE WHEN json_valid(n.chat_message) THEN json_extract(n.chat_message, '{0}') ELSE NULL END AS {1}
        """;

    private static readonly string DevinSqlNodesOnly = DevinSelect(false);
    private static readonly string DevinSqlWithSessions = DevinSelect(true);

    private static string DevinSelect(bool sessions)
    {
        string Col(string path, string alias) => string.Format(CultureInfo.InvariantCulture, DevinMetric, path, alias);
        var join = sessions ? "LEFT JOIN sessions s ON s.id = n.session_id" : "";
        var created = sessions ? "s.created_at" : "NULL";
        var work = sessions ? "s.working_directory" : "NULL";
        return $"""
            SELECT
              n.row_id AS row_id,
              n.session_id AS session_id,
              {created} AS session_created_at,
              {work} AS working_directory,
              {Col("$.metadata.request_id", "request_id")},
              {Col("$.metadata.generation_model", "generation_model")},
              {Col("$.metadata.started_generation_at", "started_generation_at")},
              {Col("$.metadata.created_at", "message_created_at")},
              {Col("$.metadata.metrics.input_tokens", "input_tokens")},
              {Col("$.metadata.metrics.output_tokens", "output_tokens")},
              {Col("$.metadata.metrics.cache_read_tokens", "cache_read_tokens")},
              {Col("$.metadata.metrics.cache_creation_tokens", "cache_creation_tokens")}
            FROM message_nodes n
            {join}
            WHERE json_valid(n.chat_message)
              AND json_extract(n.chat_message, '$.role') = 'assistant'
              AND json_extract(n.chat_message, '$.metadata.request_id') IS NOT NULL
            ORDER BY n.row_id
            """;
    }

    private static DevinCandidate? ReadDevinRow(SqliteDataReader reader, bool hasSessions)
    {
        var requestId = ReadText(reader, "request_id");
        if (string.IsNullOrEmpty(requestId)) return null;
        if (!TryCount(reader, "input_tokens", required: true, out var input)
            || !TryCount(reader, "output_tokens", required: true, out var output)
            || !TryCount(reader, "cache_read_tokens", required: false, out var cacheRead)
            || !TryCount(reader, "cache_creation_tokens", required: false, out var cacheWrite))
            return null;
        var ts = UsageParsers.ParseIso(ReadText(reader, "started_generation_at"))
                 ?? UsageParsers.ParseIso(ReadText(reader, "message_created_at"));
        if (ts is null) return null;
        long? sessionCreated = null;
        if (hasSessions && !reader.IsDBNull(reader.GetOrdinal("session_created_at")))
        {
            var seconds = Convert.ToInt64(reader.GetValue(reader.GetOrdinal("session_created_at")), CultureInfo.InvariantCulture);
            if (seconds > 0) sessionCreated = seconds;
        }
        var rowId = reader.IsDBNull(reader.GetOrdinal("row_id"))
            ? 0
            : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("row_id")), CultureInfo.InvariantCulture);
        return new DevinCandidate(
            requestId,
            ReadText(reader, "session_id") ?? "",
            rowId,
            sessionCreated,
            ReadText(reader, "working_directory"),
            ReadText(reader, "generation_model") ?? "unknown",
            ts.Value,
            input, output, cacheRead, cacheWrite);
    }

    private static bool DevinPrefer(DevinCandidate next, DevinCandidate prev)
    {
        if (next.TsUtc != prev.TsUtc) return next.TsUtc < prev.TsUtc;
        var nextCreated = next.SessionCreatedSeconds ?? long.MaxValue;
        var prevCreated = prev.SessionCreatedSeconds ?? long.MaxValue;
        if (nextCreated != prevCreated) return nextCreated < prevCreated;
        var session = string.Compare(next.SessionId, prev.SessionId, StringComparison.Ordinal);
        if (session != 0) return session < 0;
        return next.RowId > prev.RowId;
    }

    private static void ReadOpenCodeDb(
        string db, Dictionary<string, UsageRecord> byKey, Dictionary<string, string> fingerprintOwner)
    {
        using var conn = UsageIo.OpenReadOnly(db);
        if (UsageIo.TableExists(conn, "message"))
            ReadOpenCodeTable(conn, v2: false, null, byKey, fingerprintOwner);
        if (!UsageIo.TableExists(conn, "session_message")) return;
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM session_message LIMIT 1";
            if (probe.ExecuteScalar() is null) return;
        }
        var sessionTable = UsageIo.TableExists(conn, "session_v2") ? "session_v2"
            : UsageIo.TableExists(conn, "session") ? "session" : null;
        ReadOpenCodeTable(conn, v2: true, sessionTable, byKey, fingerprintOwner);
    }

    private static void ReadOpenCodeTable(
        SqliteConnection conn, bool v2, string? sessionTable,
        Dictionary<string, UsageRecord> byKey, Dictionary<string, string> fingerprintOwner)
    {
        using var cmd = conn.CreateCommand();
        if (!v2)
        {
            cmd.CommandText = """
                SELECT id, session_id, data, NULL AS directory
                FROM message
                WHERE json_extract(data, '$.role') = 'assistant'
                ORDER BY time_created ASC
                """;
        }
        else if (sessionTable is null)
        {
            cmd.CommandText = """
                SELECT id, session_id, data, NULL AS directory
                FROM session_message
                WHERE type = 'assistant'
                ORDER BY time_created ASC
                """;
        }
        else
        {
            cmd.CommandText = $"""
                SELECT sm.id AS id, sm.session_id AS session_id, sm.data AS data, s.directory AS directory
                FROM session_message sm
                LEFT JOIN {sessionTable} s ON s.id = sm.session_id
                WHERE sm.type = 'assistant'
                ORDER BY sm.time_created ASC
                """;
        }
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var raw = ReadText(reader, "data");
            if (string.IsNullOrEmpty(raw)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch (JsonException) { continue; }
            using (doc)
            {
                ConsiderOpenCode(doc.RootElement, ReadText(reader, "id"), ReadText(reader, "session_id"),
                    ReadText(reader, "directory"), byKey, fingerprintOwner, requireRole: false);
            }
        }
    }

    private static void ConsiderOpenCode(
        JsonElement msg, string? id, string? sessionId, string? directory,
        Dictionary<string, UsageRecord> byKey, Dictionary<string, string> fingerprintOwner, bool requireRole)
    {
        if (requireRole && !IsRole(msg, "assistant")) return;
        if (UsageParsers.GetStr(msg, "role") is { } role && !role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            return;
        if (!TryOpenCodeTokens(msg, out var input, out var output, out var reasoning, out var cacheRead, out var cacheWrite))
            return;
        var (created, completed, ts) = OpenCodeTime(msg);
        if (ts is null) return;
        var (model, provider) = OpenCodeModel(msg);
        sessionId = FirstText(sessionId, UsageParsers.GetStr(msg, "sessionID"), UsageParsers.GetStr(msg, "sessionId"), UsageParsers.GetStr(msg, "session_id"));
        id = FirstText(id, UsageParsers.GetStr(msg, "id"), UsageParsers.GetStr(msg, "messageID"), UsageParsers.GetStr(msg, "messageId"));
        var key = !string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(id) ? sessionId + "|" + id : id ?? sessionId;
        if (string.IsNullOrEmpty(key)) return;
        var fingerprint = $"opencode\0{created}\0{completed}\0{input}\0{output}\0{cacheRead}\0{cacheWrite}\0{reasoning}\0{model}\0{provider}";
        if (!string.IsNullOrEmpty(sessionId)
            && fingerprintOwner.TryGetValue(fingerprint, out var owner)
            && !string.Equals(owner, sessionId, StringComparison.Ordinal))
            return;
        if (!string.IsNullOrEmpty(sessionId)) fingerprintOwner[fingerprint] = sessionId;
        var project = directory;
        if (string.IsNullOrWhiteSpace(project) && UsageParsers.GetObj(msg, "path") is { } path)
            project = UsageParsers.GetStr(path, "cwd");
        byKey[key] = Row("opencode", string.IsNullOrEmpty(sessionId) ? key : sessionId, key, ts.Value,
            input, output, cacheRead, cacheWrite, reasoning, model, string.IsNullOrWhiteSpace(project) ? null : project.Trim());
    }

    private static bool TryOpenCodeTokens(
        JsonElement msg, out long input, out long output, out long reasoning, out long cacheRead, out long cacheWrite)
    {
        input = output = reasoning = cacheRead = cacheWrite = 0;
        var tokens = UsageParsers.GetObj(msg, "tokens");
        if (tokens is null) return false;
        input = UsageIo.JsonLong(tokens.Value, "input");
        output = UsageIo.JsonLong(tokens.Value, "output");
        reasoning = UsageIo.JsonLong(tokens.Value, "reasoning");
        if (UsageParsers.GetObj(tokens, "cache") is { } cache)
        {
            cacheRead = UsageIo.JsonLong(cache, "read");
            cacheWrite = UsageIo.JsonLong(cache, "write");
        }
        return input + output + reasoning + cacheRead + cacheWrite > 0;
    }

    private static (long Created, long Completed, DateTime? Ts) OpenCodeTime(JsonElement msg)
    {
        var time = UsageParsers.GetObj(msg, "time");
        if (time is null) return (0, 0, null);
        var created = EpochMs(time.Value, "created");
        var completed = EpochMs(time.Value, "completed");
        var ts = UsageParsers.ParseMs(completed) ?? UsageParsers.ParseMs(created);
        return (created, completed, ts);
    }

    private static long EpochMs(JsonElement el, string name)
    {
        if (!UsageIo.TryJsonLong(el, name, out var n) || n <= 0) return 0;
        return n < 10_000_000_000L ? n * 1000 : n;
    }

    private static (string Model, string Provider) OpenCodeModel(JsonElement msg)
    {
        // v1 是 modelID / providerID；有的 fork 把模型写成字符串 model；v2 嵌成 model.{id,providerID}。
        string? modelString = null;
        JsonElement nested = default;
        var hasModel = msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("model", out nested);
        if (hasModel && nested.ValueKind == JsonValueKind.String)
            modelString = nested.GetString();
        var flat = FirstText(UsageParsers.GetStr(msg, "modelID"), modelString, UsageParsers.GetStr(msg, "modelId"));
        if (flat is not null)
        {
            var provider = FirstText(
                UsageParsers.GetStr(msg, "providerID"),
                UsageParsers.GetStr(msg, "provider"),
                UsageParsers.GetStr(msg, "providerId")) ?? "";
            return (flat, provider);
        }
        if (hasModel && nested.ValueKind == JsonValueKind.Object)
            return (FirstText(UsageParsers.GetStr(nested, "id")) ?? "unknown", UsageParsers.GetStr(nested, "providerID") ?? "");
        return ("unknown", "");
    }

    private static List<UsageRecord> ReadAntigravityFile(string file)
    {
        var sessionId = AntigravitySessionId(file) ?? "antigravity";
        var steps = ReadAntigravitySteps(AntigravityDbPath(file));
        var model = ReadAntigravityDefaultModel(file) ?? "antigravity-unknown";
        long context = 0;
        long previous = 0;
        string? lastPlannerModel = null;
        var list = new List<UsageRecord>();
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var ev = doc.RootElement;
                var type = UsageParsers.GetStr(ev, "type");
                if (type is "USER_INPUT" or "USER_SETTINGS_CHANGE")
                {
                    var picked = AntigravityModelSelection(UsageParsers.GetStr(ev, "content"));
                    if (picked is not null) model = picked;
                }
                var eventTokens = AntigravityContextTokens(ev, type);
                AntigravityGenInfo? turn = null;
                if (type == "PLANNER_RESPONSE" && UsageIo.TryJsonLong(ev, "step_index", out var step))
                    steps.TryGetValue(step, out turn);
                if (!string.IsNullOrWhiteSpace(turn?.Model))
                {
                    var norm = NormalizeAntigravityModel(turn.Model);
                    if (norm is not null) model = norm;
                }
                var dbContext = turn is { ContextTokens: > 0 } ? turn.ContextTokens : 0;
                var ts = UsageParsers.ParseIso(UsageParsers.GetStr(ev, "created_at"));
                if (ts is null || type != "PLANNER_RESPONSE")
                {
                    context += eventTokens;
                    continue;
                }

                long input, output, cached = 0, reasoning;
                if (turn is { HasUsage: true })
                {
                    input = turn.UncachedInput;
                    cached = turn.CachedInput;
                    reasoning = turn.ReasoningOutput;
                    output = turn.TextOutput > 0 ? turn.TextOutput : Math.Max(0, turn.OutputTokens - reasoning);
                    context = dbContext > 0 ? dbContext : input + cached;
                }
                else
                {
                    if (dbContext > 0) context = dbContext;
                    if (lastPlannerModel is not null && model != lastPlannerModel) previous = 0;
                    input = Math.Max(0, context - previous);
                    output = ValueTokens(ev, "content") + ValueTokens(ev, "tool_calls");
                    reasoning = ValueTokens(ev, "thinking");
                }
                var total = input + output + cached + reasoning;
                if (total > 0)
                {
                    var key = UsageIo.TryJsonLong(ev, "step_index", out var stepKey)
                        ? stepKey.ToString(CultureInfo.InvariantCulture)
                        : ts.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                    list.Add(Row("antigravity", sessionId, key, ts.Value, input, output, cached, 0, reasoning, model, null));
                    previous = context;
                    lastPlannerModel = model;
                }
                context += eventTokens;
            }
        }
        return list;
    }

    private static Dictionary<long, AntigravityGenInfo> ReadAntigravitySteps(string? dbPath)
    {
        var map = new Dictionary<long, AntigravityGenInfo>();
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return map;
        if (!UsageIo.TrySnapshot(dbPath, "agenthub-antigravity-", out var db, out var tmp)) return map;
        try
        {
            using var conn = UsageIo.OpenReadOnly(db);
            if (!UsageIo.TableExists(conn, "gen_metadata")) return map;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT idx, data FROM gen_metadata ORDER BY idx";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1)) continue;
                var bytes = (byte[])reader.GetValue(1);
                var info = ExtractAntigravityGenInfo(bytes);
                if (info?.LastStepIndex is int step && (info.HasUsage || info.ContextTokens > 0))
                    map[step + 1L] = info;
            }
        }
        catch (SqliteException) { return map; }
        finally { UsageIo.DeleteSnapshot(tmp); }
        return map;
    }

    private static string? AntigravityDbPath(string transcript)
    {
        var logs = Path.GetDirectoryName(transcript);
        var sys = logs is null ? null : Path.GetDirectoryName(logs);
        var conv = sys is null ? null : Path.GetDirectoryName(sys);
        var brain = conv is null ? null : Path.GetDirectoryName(conv);
        var variant = brain is null ? null : Path.GetDirectoryName(brain);
        if (variant is null || conv is null || brain is null || sys is null || logs is null) return null;
        if (!string.Equals(Path.GetFileName(logs), "logs", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(Path.GetFileName(sys), ".system_generated", StringComparison.Ordinal)) return null;
        if (!string.Equals(Path.GetFileName(brain), "brain", StringComparison.OrdinalIgnoreCase)) return null;
        var id = Path.GetFileName(conv);
        return string.IsNullOrEmpty(id) ? null : Path.Combine(variant, "conversations", id + ".db");
    }

    private static string? AntigravitySessionId(string transcript)
    {
        var logs = Path.GetDirectoryName(transcript);
        var sys = logs is null ? null : Path.GetDirectoryName(logs);
        var conv = sys is null ? null : Path.GetDirectoryName(sys);
        return conv is null ? null : Path.GetFileName(conv);
    }

    private static string? ReadAntigravityDefaultModel(string transcript)
    {
        var dir = transcript;
        for (var i = 0; i < 5; i++)
        {
            var next = Path.GetDirectoryName(dir);
            if (next is null) return null;
            dir = next;
        }
        var settings = Path.Combine(dir, "settings.json");
        if (!File.Exists(settings)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settings));
            return NormalizeAntigravityModel(UsageParsers.GetStr(doc.RootElement, "model"));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static string? AntigravityModelSelection(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var match = AntigravityModelChange.Match(content);
        return match.Success ? NormalizeAntigravityModel(match.Groups[1].Value) : null;
    }

    internal static string? NormalizeAntigravityModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        var slug = AntigravityParen.Replace(modelName, " ");
        slug = AntigravitySpeed.Replace(slug, " ").ToLowerInvariant();
        slug = AntigravityNonSlug.Replace(slug, "-").Trim('-');
        slug = AntigravityDash.Replace(slug, "-");
        if (slug.Length == 0) return null;
        foreach (var marker in new[] { "gemini", "claude", "gpt" })
        {
            var idx = slug.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                slug = slug[idx..];
                break;
            }
        }
        return slug.StartsWith("gemini-", StringComparison.Ordinal)
               || slug.StartsWith("claude-", StringComparison.Ordinal)
               || slug.StartsWith("gpt-", StringComparison.Ordinal)
            ? slug
            : "antigravity-" + slug;
    }

    private static long AntigravityContextTokens(JsonElement ev, string? type)
    {
        var tokens = ValueTokens(ev, "content");
        if (type == "PLANNER_RESPONSE") tokens += ValueTokens(ev, "tool_calls");
        return tokens;
    }

    private static long ValueTokens(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return 0;
        if (value.ValueKind == JsonValueKind.String) return EstimateTokens(value.GetString());
        return EstimateTokens(value.GetRawText());
    }

    private static long EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cjk = 0;
        var other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjk(rune.Value)) cjk++;
            else other++;
        }
        return cjk + (other + 3) / 4;
    }

    private static bool IsCjk(int code) =>
        code is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0x3040 and <= 0x30FF;

    private static readonly Regex AntigravityParen = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex AntigravitySpeed = new(@"\b(thinking|xhigh|high|medium|low|fast)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AntigravityNonSlug = new(@"[^a-z0-9.]+", RegexOptions.Compiled);
    private static readonly Regex AntigravityDash = new(@"-{2,}", RegexOptions.Compiled);
    private static readonly Regex AntigravityModelChange = new(
        @"changed setting `Model Selection` from .*? to ([^`\n]+?)(?:\s*\([^)]*\))?\.(?:\s+|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly struct ProtoField
    {
        public int Number { get; init; }
        public long? Varint { get; init; }
        public byte[]? Bytes { get; init; }
    }

    private static List<ProtoField>? ReadProtoFields(byte[]? buf) =>
        buf is null ? null : ReadProtoFields(buf.AsSpan());

    private static List<ProtoField>? ReadProtoFields(ReadOnlySpan<byte> buf)
    {
        var fields = new List<ProtoField>();
        var offset = 0;
        while (offset < buf.Length)
        {
            if (!TryVarint(buf, ref offset, out var tag, out var tagSafe) || !tagSafe || tag < 0) return null;
            var fieldNum = (int)(tag / 8);
            var wire = (int)(tag & 7);
            if (fieldNum <= 0) return null;
            if (wire == 0)
            {
                if (!TryVarint(buf, ref offset, out var val, out var safe)) return null;
                fields.Add(new ProtoField { Number = fieldNum, Varint = safe ? val : null });
            }
            else if (wire == 2)
            {
                if (!TryVarint(buf, ref offset, out var len, out var lenSafe) || !lenSafe || len < 0 || len > buf.Length - offset)
                    return null;
                fields.Add(new ProtoField { Number = fieldNum, Bytes = buf.Slice(offset, (int)len).ToArray() });
                offset += (int)len;
            }
            else if (wire == 1)
            {
                if (buf.Length - offset < 8) return null;
                offset += 8;
            }
            else if (wire == 5)
            {
                if (buf.Length - offset < 4) return null;
                offset += 4;
            }
            else return null;
        }
        return fields;
    }

    private static bool TryVarint(ReadOnlySpan<byte> buf, ref int offset, out long value, out bool safe)
    {
        value = 0;
        safe = false;
        ulong acc = 0;
        for (var count = 0; count < 10; count++)
        {
            if (offset >= buf.Length) return false;
            var b = buf[offset++];
            if (count == 9 && b > 1) return false;
            acc |= (ulong)(b & 0x7f) << (count * 7);
            if ((b & 0x80) == 0)
            {
                if (acc <= long.MaxValue)
                {
                    value = (long)acc;
                    safe = true;
                }
                return true;
            }
        }
        return false;
    }

    private static byte[]? FirstBytes(List<ProtoField>? fields, int number)
    {
        if (fields is null) return null;
        foreach (var field in fields)
            if (field.Number == number) return field.Bytes;
        return null;
    }

    private static long? FirstVarint(List<ProtoField>? fields, int number)
    {
        if (fields is null) return null;
        foreach (var field in fields)
            if (field.Number == number) return field.Varint;
        return null;
    }

    private readonly record struct ReasonixParts(long Input, long Output, long CacheRead, long CacheWrite, long Reasoning);

    private static ReasonixParts ReasonixTotals(JsonElement usage)
    {
        var prompt = UsageIo.JsonLong(usage, "promptTokens");
        var reasoning = UsageIo.JsonLong(usage, "reasoningTokens");
        var completion = UsageIo.JsonLong(usage, "completionTokens");
        var cacheMiss = Math.Min(prompt, UsageIo.JsonLong(usage, "cacheMissTokens"));
        var cacheHit = Math.Min(prompt, UsageIo.JsonLong(usage, "cacheHitTokens"));
        var hasMiss = UsageIo.HasJsonField(usage, "cacheMissTokens");
        var uncached = hasMiss ? cacheMiss : Math.Max(0, prompt - cacheHit);
        var cacheWrite = Math.Min(uncached, UsageIo.JsonLong(usage, "cacheWriteTokens"));
        var cacheRead = hasMiss ? prompt - cacheMiss : cacheHit;
        return new ReasonixParts(uncached - cacheWrite, Math.Max(0, completion - reasoning), cacheRead, cacheWrite, reasoning);
    }

    private static string ReasonixModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "reasonix-unknown";
        var parts = model.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "reasonix-unknown" : parts[^1];
    }

    private static string ReasonixSessionId(string file)
    {
        var name = Path.GetFileName(file);
        const string suffix = ".jsonl.telemetry.json";
        if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        return string.IsNullOrEmpty(name) ? "reasonix" : name;
    }

    private static string? ReasonixProject(string file)
    {
        var sessions = Path.GetDirectoryName(file);
        var project = sessions is null ? null : Path.GetDirectoryName(sessions);
        var projects = project is null ? null : Path.GetDirectoryName(project);
        if (projects is null || !string.Equals(Path.GetFileName(projects), "projects", StringComparison.OrdinalIgnoreCase))
            return null;
        var name = Path.GetFileName(project);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static bool TryCount(SqliteDataReader reader, string column, bool required, out long value)
    {
        value = 0;
        var i = reader.GetOrdinal(column);
        if (reader.IsDBNull(i)) return !required;
        var raw = reader.GetValue(i);
        long? n = raw switch
        {
            long l => l,
            int v => v,
            double d when double.IsFinite(d) && d >= 0 && d == Math.Floor(d) => (long)d,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        if (n is null || n < 0) return false;
        value = n.Value;
        return true;
    }

    private static string? ReadText(SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        if (reader.IsDBNull(i)) return null;
        var text = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? FirstText(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    private static bool IsRole(JsonElement? el, string role) =>
        string.Equals(UsageParsers.GetStr(el, "role"), role, StringComparison.OrdinalIgnoreCase);

    private static UsageRecord Row(
        string tool, string sessionId, string requestKey, DateTime ts,
        long input, long output, long cacheRead, long cacheWrite, long reasoning, string model, string? project) =>
        new()
        {
            Tool = tool,
            SessionId = sessionId,
            RequestKey = requestKey,
            TsUtc = ts,
            InputTokens = input,
            OutputTokens = output,
            CachedInputTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            ReasoningTokens = reasoning,
            Model = string.IsNullOrWhiteSpace(model) ? "unknown" : model,
            Project = project,
        };

    private sealed record DevinCandidate(
        string RequestId, string SessionId, long RowId, long? SessionCreatedSeconds, string? Project,
        string Model, DateTime TsUtc, long Input, long Output, long CacheRead, long CacheWrite);
}

internal sealed record AntigravityGenInfo
{
    public string? Model { get; init; }
    public long ContextTokens { get; init; }
    public int? LastStepIndex { get; init; }
    public bool HasUsage { get; init; }
    public long UncachedInput { get; init; }
    public long CachedInput { get; init; }
    public long OutputTokens { get; init; }
    public long TextOutput { get; init; }
    public long ReasoningOutput { get; init; }
}
