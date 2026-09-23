using AgentHub.Core.TokenCore;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentHub.Tests;

/// <summary>
/// 六家被动用量共用一张表：每个 source id 一份夹具，断言 token / 去重，不落提示词。
/// </summary>
public class PassiveUsageTests
{
    public static IEnumerable<object[]> SourceIds =>
    [
        ["minimax"],
        ["reasonix"],
        ["claude-code"],
        ["devin"],
        ["opencode"],
        ["antigravity"],
    ];

    [Theory]
    [MemberData(nameof(SourceIds))]
    public void Source_reads_tokens_and_drops_prompts(string id)
    {
        using var root = new TempDir();
        var rows = Read(id, root.Path);
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Equal(id, row.Tool);
            Assert.Null(row.ReportedCostUsd);
            Assert.DoesNotContain("SECRET", Dump(row), StringComparison.Ordinal);
        });
        switch (id)
        {
            case "minimax": AssertMiniMax(rows); break;
            case "reasonix": AssertReasonix(rows); break;
            case "claude-code": AssertClaude(rows); break;
            case "devin": AssertDevin(rows); break;
            case "opencode": AssertOpenCode(rows); break;
            case "antigravity": AssertAntigravity(rows); break;
        }
    }

    [Theory]
    [MemberData(nameof(SourceIds))]
    public void Registry_lists_source(string id) =>
        Assert.NotNull(UsageSourceRegistry.Find(id));

    [Fact]
    public void OpenCode_and_Devin_paths_follow_xdg_data_home()
    {
        var home = Path.Combine("Users", "me");
        Assert.Equal(
            Path.Combine(home, ".local", "share", "opencode"),
            UsagePaths.OpenCodeDataDir(home, null));
        Assert.Equal(
            Path.Combine(home, ".local", "share", "devin", "cli", "sessions.db"),
            UsagePaths.DevinDbPath(home, null));

        var xdg = Path.Combine("xdg", "data");
        Assert.Equal(Path.Combine(xdg, "opencode"), UsagePaths.OpenCodeDataDir(home, xdg));
        Assert.Equal(Path.Combine(xdg, "devin", "cli", "sessions.db"), UsagePaths.DevinDbPath(home, xdg));
    }

    [Theory]
    [InlineData("Gemini 3.8 Flash (Thinking)", "gemini-3.8-flash")]
    [InlineData("Claude Sonnet 4 (Thinking)", "claude-sonnet-4")]
    [InlineData("internal-router", "antigravity-internal-router")]
    public void Antigravity_model_slug_strips_speed_words(string raw, string slug) =>
        Assert.Equal(slug, PassiveUsage.NormalizeAntigravityModel(raw));

    [Fact]
    public void Antigravity_proto_reads_usage_and_ignores_unsafe_varints()
    {
        var info = PassiveUsage.ExtractAntigravityGenInfo(BuildProto(
            model: "gemini-3.8-flash",
            contextTokens: 25000,
            lastStepIndex: 0,
            systemTokens: 1016,
            promptTokens: 2000,
            cached: 5000,
            output: 200));
        Assert.NotNull(info);
        Assert.Equal("gemini-3.8-flash", info.Model);
        Assert.Equal(25000, info.ContextTokens);
        Assert.Equal(0, info.LastStepIndex);
        Assert.True(info.HasUsage);
        Assert.Equal(3016, info.UncachedInput);
        Assert.Equal(5000, info.CachedInput);
        Assert.Equal(200, info.OutputTokens);

        var absentText = PassiveUsage.ExtractAntigravityGenInfo(BuildProto(
            model: "gemini-3.8-flash",
            lastStepIndex: 0,
            promptTokens: 1000,
            cached: 2000,
            output: 300,
            reasoning: 100));
        Assert.NotNull(absentText);
        Assert.Equal(0, absentText.TextOutput);
        Assert.Equal(300, absentText.OutputTokens);
        Assert.Equal(100, absentText.ReasoningOutput);

        Assert.Null(PassiveUsage.ExtractAntigravityGenInfo([0x0a, 0x80, 0x80, 0x80]));

        var wide = BuildProto(
            model: "gemini-3.8-flash",
            lastStepIndex: 7,
            promptTokens: 4583,
            cached: 16319,
            contextBefore: Uint64MaxVarint());
        var kept = PassiveUsage.ExtractAntigravityGenInfo(wide);
        Assert.NotNull(kept);
        Assert.Equal(25000, kept.ContextTokens);
        Assert.Equal(4583, kept.UncachedInput);
        Assert.Equal(7, kept.LastStepIndex);
    }

    private static List<UsageRecord> Read(string id, string root) => id switch
    {
        "minimax" => PassiveUsage.ReadMiniMax([SeedMiniMax(root)]),
        "reasonix" => PassiveUsage.ReadReasonix([SeedReasonix(root)]),
        "claude-code" => PassiveUsage.ReadClaudeCode([SeedClaude(root)]),
        "devin" => PassiveUsage.ReadDevin([SeedDevin(root)]),
        "opencode" => PassiveUsage.ReadOpenCode([SeedOpenCode(root)]),
        "antigravity" => PassiveUsage.ReadAntigravity([SeedAntigravity(root)]),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    private static void AssertMiniMax(List<UsageRecord> rows)
    {
        Assert.Equal(2, rows.Count);
        var first = rows.Single(r => r.RequestKey == "m-1");
        Assert.Equal("14-05-00-000-session_ABC", first.SessionId);
        Assert.Equal(132719, first.InputTokens);
        Assert.Equal(16692, first.OutputTokens);
        Assert.Equal(439298, first.CachedInputTokens);
        Assert.Equal(0, first.CacheWriteTokens);
        Assert.Equal("MiniMax-M2.7", first.Model);
        Assert.Null(first.Project);
        Assert.Equal(new DateTime(2026, 9, 20, 14, 5, 0, DateTimeKind.Utc), first.TsUtc);

        var second = rows.Single(r => r.RequestKey == "m-2");
        Assert.Equal(100, second.InputTokens);
        Assert.Equal(20, second.OutputTokens);
        Assert.Equal(300, second.CachedInputTokens);
        Assert.Equal(7, second.CacheWriteTokens);
        Assert.Equal(3, second.ReasoningTokens);
        Assert.DoesNotContain(rows, r => r.Model == "historical-transcript" || r.InputTokens == 999999);
    }

    private static void AssertReasonix(List<UsageRecord> rows)
    {
        var row = rows.Single(r => r.SessionId == "session-1");
        Assert.Equal("cumulative", row.RequestKey);
        Assert.Equal("deepseek-v4-flash-0731", row.Model);
        Assert.Equal("project-a", row.Project);
        Assert.Equal(140, row.InputTokens);
        Assert.Equal(800, row.CachedInputTokens);
        Assert.Equal(60, row.CacheWriteTokens);
        Assert.Equal(180, row.OutputTokens);
        Assert.Equal(120, row.ReasoningTokens);
        Assert.Equal(1300, row.InputTokens + row.OutputTokens + row.CachedInputTokens + row.CacheWriteTokens + row.ReasoningTokens);

        var bounded = rows.Single(r => r.SessionId == "recovery");
        Assert.Equal(100, bounded.InputTokens);
        Assert.Equal(900, bounded.CachedInputTokens);
        Assert.Equal(30, bounded.OutputTokens);
        Assert.Equal(20, bounded.ReasoningTokens);
    }

    private static void AssertClaude(List<UsageRecord> rows)
    {
        var main = rows.Single(r => r.RequestKey == "msg-1:req-1");
        Assert.Equal("a-session", main.SessionId);
        Assert.Equal("/work/app", main.Project);
        Assert.Equal(100, main.InputTokens);
        Assert.Equal(25, main.OutputTokens);
        Assert.Equal(15, main.ReasoningTokens);
        Assert.Equal(10, main.CachedInputTokens);
        Assert.Equal(5, main.CacheWriteTokens);
        Assert.Equal("claude-sonnet-4", main.Model);
        Assert.False(main.IsSubagent);

        var sub = rows.Single(r => r.IsSubagent);
        Assert.Equal("parent-uuid", sub.SessionId);
        Assert.Equal(4, sub.InputTokens);

        Assert.DoesNotContain(rows, r => r.InputTokens == 999);
        Assert.Contains(rows, r => r.RequestKey.StartsWith("2026-04-05T14:01:00.000Z|", StringComparison.Ordinal)
                                    && r.OutputTokens == 1 && r.Project == "/work/app");
    }

    private static void AssertDevin(List<UsageRecord> rows)
    {
        Assert.Equal(2, rows.Count);
        var corrected = rows.Single(r => r.RequestKey == "R1");
        Assert.Equal("ses-a", corrected.SessionId);
        Assert.Equal("/src/app", corrected.Project);
        Assert.Equal(11, corrected.InputTokens);
        Assert.Equal(5, corrected.OutputTokens);
        Assert.Equal(2, corrected.CachedInputTokens);
        Assert.Equal(1, corrected.CacheWriteTokens);
        Assert.Equal("swe-2-high", corrected.Model);
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), corrected.TsUtc);

        var other = rows.Single(r => r.RequestKey == "R4");
        Assert.Equal(7, other.InputTokens);
        Assert.Equal(1, other.OutputTokens);
        Assert.DoesNotContain(rows, r => r.RequestKey is "R2" or "R3" or "R5" || r.InputTokens == 99);
    }

    private static void AssertOpenCode(List<UsageRecord> rows)
    {
        Assert.Equal(3, rows.Count);
        Assert.Equal(25, rows.Sum(r => r.InputTokens));
        var copied = rows.Single(r => r.InputTokens == 10);
        Assert.Equal(4, copied.OutputTokens);
        Assert.Equal(1, copied.ReasoningTokens);
        Assert.Equal(2, copied.CachedInputTokens);
        Assert.Equal(3, copied.CacheWriteTokens);
        Assert.Equal("claude-sonnet-5", copied.Model);

        var file = rows.Single(r => r.InputTokens == 8);
        Assert.Equal("ses_file", file.SessionId);

        var v2 = rows.Single(r => r.InputTokens == 7);
        Assert.Equal("/work/proj", v2.Project);
        Assert.Equal("gpt-5", v2.Model);
        Assert.Equal("ses_v2", v2.SessionId);
    }

    private static void AssertAntigravity(List<UsageRecord> rows)
    {
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("conv-123", r.SessionId);
            Assert.Equal("gemini-3.8-flash", r.Model);
        });
        var first = rows.Single(r => r.RequestKey == "1");
        Assert.Equal(18239, first.InputTokens);
        Assert.Equal(0, first.CachedInputTokens);
        Assert.Equal(1286, first.OutputTokens);
        Assert.Equal(80, first.ReasoningTokens);
        var second = rows.Single(r => r.RequestKey == "3");
        Assert.Equal(4583, second.InputTokens);
        Assert.Equal(16319, second.CachedInputTokens);
        Assert.Equal(82, second.OutputTokens);
        Assert.Equal(69, second.ReasoningTokens);
        Assert.Equal(18239 + 4583, rows.Sum(r => r.InputTokens));
        Assert.Equal(1286 + 82, rows.Sum(r => r.OutputTokens));
    }

    private static string SeedMiniMax(string root)
    {
        var home = Path.Combine(root, ".minimax");
        var session = Path.Combine(home, "v2", "sessions", "2026", "09", "20", "14-05-00-000-session_ABC");
        Directory.CreateDirectory(session);
        var t1 = DateTimeOffset.Parse("2026-09-20T14:05:00Z").ToUnixTimeMilliseconds();
        var t2 = DateTimeOffset.Parse("2026-09-20T14:29:59Z").ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(session, "messages.jsonl"), string.Join('\n',
            Line(new { message_id = "u-1", message = new { role = "user", content = "SECRET_PROMPT", timestamp = t1 } }),
            Line(new
            {
                message_id = "m-1",
                message = new
                {
                    role = "assistant",
                    model = "MiniMax-M2.7",
                    timestamp = t1,
                    content = "SECRET_REPLY",
                    usage = new { input = 132719, output = 16692, cacheRead = 439298, cacheWrite = 0, totalTokens = 588709, cost = new { total = 9 } },
                },
            }),
            Line(new
            {
                message_id = "m-2",
                message = new
                {
                    role = "assistant",
                    model = "MiniMax-M2.7",
                    timestamp = t2,
                    usage = new { input = 100, output = 20, cacheRead = 300, cacheWrite = 7, reasoningTokens = 3 },
                },
            }),
            Line(new
            {
                message_id = "m-1",
                message = new
                {
                    role = "assistant",
                    model = "MiniMax-M2.7",
                    timestamp = t1,
                    usage = new { input = 999999, output = 1, cacheRead = 0, cacheWrite = 0 },
                },
            }),
            Line(new
            {
                message_id = "legacy-1",
                message = new
                {
                    role = "assistant",
                    model = "historical-transcript",
                    timestamp = t1,
                    usage = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, reasoningTokens = 0 },
                },
            }),
            "not json") + "\n");
        File.WriteAllText(Path.Combine(session, "task-1.readable.v1.jsonl"), Line(new { message_id = "nope" }));
        var shallow = Path.Combine(home, "v2", "sessions");
        File.WriteAllText(Path.Combine(shallow, "notes.jsonl"), Line(new
        {
            message_id = "shallow",
            message = new { role = "assistant", timestamp = t1, usage = new { input = 50, output = 1 } },
        }));
        var later = Path.Combine(home, "v2", "sessions", "2026", "09", "21", "dup");
        Directory.CreateDirectory(later);
        File.WriteAllText(Path.Combine(later, "messages.jsonl"), Line(new
        {
            message_id = "m-1",
            message = new
            {
                role = "assistant",
                model = "MiniMax-M2.7",
                timestamp = t1,
                usage = new { input = 999999, output = 1 },
            },
        }));
        return home;
    }

    private static string SeedReasonix(string root)
    {
        var sessions = Path.Combine(root, "projects", "project-a", "sessions");
        Directory.CreateDirectory(sessions);
        WriteReasonix(sessions, "session-1", "基元律动-雷/deepseek-v4-flash-0731", new
        {
            promptTokens = 1000,
            cacheHitTokens = 800,
            cacheMissTokens = 200,
            cacheWriteTokens = 60,
            completionTokens = 300,
            reasoningTokens = 120,
            requestCount = 2,
        });
        File.WriteAllText(Path.Combine(sessions, "session-1.jsonl"), "{\"content\":\"SECRET_PROMPT\"}\n");
        WriteReasonix(sessions, "recovery", "vendor/recovery-model", new
        {
            promptTokens = 1000,
            cacheHitTokens = 5000,
            cacheMissTokens = 100,
            completionTokens = 50,
            reasoningTokens = 20,
            requestCount = 1,
            estimated = true,
        });
        return root;
    }

    private static void WriteReasonix(string sessions, string id, string model, object usage)
    {
        var stem = Path.Combine(sessions, id + ".jsonl");
        File.WriteAllText(stem + ".telemetry.json", Line(new { version = 2, usage }));
        File.WriteAllText(stem + ".meta", Line(new { id, model, updated_at = "2026-08-12T03:12:00Z", title = "SECRET_TITLE" }));
    }

    private static string SeedClaude(string root)
    {
        var project = Path.Combine(root, "projects", "proj");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "a-session.jsonl"), string.Join('\n',
            Line(new { type = "user", timestamp = "2026-04-05T13:59:00.000Z", message = new { content = "SECRET_PROMPT" } }),
            Line(new
            {
                timestamp = "2026-04-05T14:00:00.000Z",
                cwd = "/work/app",
                requestId = "req-1",
                message = new
                {
                    id = "msg-1",
                    model = "claude-sonnet-4",
                    content = "SECRET_REPLY",
                    usage = new
                    {
                        input_tokens = 100,
                        output_tokens = 40,
                        cache_read_input_tokens = 10,
                        cache_creation_input_tokens = 5,
                        output_tokens_details = new { thinking_tokens = 15 },
                    },
                },
            }),
            Line(new
            {
                timestamp = "2026-04-05T14:01:00.000Z",
                message = new { model = "claude-haiku", usage = new { input_tokens = 1, output_tokens = 1 } },
            })) + "\n");
        File.WriteAllText(Path.Combine(project, "b-session.jsonl"), Line(new
        {
            timestamp = "2026-04-05T14:00:00.000Z",
            cwd = "/work/app",
            requestId = "req-1",
            message = new
            {
                id = "msg-1",
                model = "claude-sonnet-4",
                usage = new { input_tokens = 999, output_tokens = 1 },
            },
        }));
        var sub = Path.Combine(project, "parent-uuid", "subagents");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "agent.jsonl"), Line(new
        {
            timestamp = "2026-04-05T15:00:00.000Z",
            cwd = "/work/app",
            message = new
            {
                id = "msg-sub",
                model = "claude-sonnet-4",
                usage = new { input_tokens = 4, output_tokens = 2 },
            },
        }));
        var observer = Path.Combine(project, "--claude-mem-observer-sessions");
        Directory.CreateDirectory(observer);
        File.WriteAllText(Path.Combine(observer, "skip.jsonl"), Line(new
        {
            timestamp = "2026-04-05T16:00:00.000Z",
            message = new
            {
                id = "msg-obs",
                model = "claude-sonnet-4",
                usage = new { input_tokens = 5000, output_tokens = 1 },
            },
        }));
        return root;
    }

    private static string SeedDevin(string root)
    {
        var db = Path.Combine(root, "sessions.db");
        using var conn = OpenDb(db);
        Exec(conn, """
            CREATE TABLE sessions (
              id TEXT PRIMARY KEY,
              working_directory TEXT,
              created_at INTEGER,
              title TEXT
            );
            CREATE TABLE message_nodes (
              row_id INTEGER PRIMARY KEY,
              session_id TEXT,
              chat_message TEXT,
              created_at INTEGER
            );
            """);
        Exec(conn, "INSERT INTO sessions (id, working_directory, created_at, title) VALUES ('ses-a', '/src/app', 100, 'SECRET_TITLE')");
        Exec(conn, "INSERT INTO sessions (id, working_directory, created_at, title) VALUES ('ses-b', '/other', 200, 'SECRET_TITLE')");
        InsertDevin(conn, 1, "ses-a", Assistant("R1", "swe-1", "2026-01-02T00:00:00Z", 10, 4, null, null));
        InsertDevin(conn, 2, "ses-b", Assistant("R1", "swe-1", "2026-01-02T00:00:00Z", 99, 9, null, null));
        InsertDevin(conn, 3, "ses-a", Assistant("R1", "swe-2-high", "2026-01-02T00:00:00Z", 11, 5, 2, 1));
        InsertDevin(conn, 4, "ses-a", """{"role":"user","content":"SECRET_PROMPT"}""");
        InsertDevin(conn, 5, "ses-a", """{"role":"assistant","content":"SECRET_REPLY","metadata":{"request_id":"R2"}}""");
        InsertDevin(conn, 6, "ses-a", Assistant("R3", "swe-1", "2026-01-02T01:00:00Z", 3, 1, -1, 0));
        InsertDevin(conn, 7, "ses-a", Assistant("R4", "swe-2-high", "2026-01-02T02:00:00Z", 7, 1, null, null));
        InsertDevin(conn, 8, "ses-a", """{"role":"assistant","content":"SECRET_REPLY","metadata":{"request_id":"R5","metrics":{"input_tokens":8,"output_tokens":1}}}""");
        return db;
    }

    private static string Assistant(string requestId, string model, string ts, int input, int output, int? cacheRead, int? cacheWrite) =>
        Line(new
        {
            role = "assistant",
            content = "SECRET_REPLY",
            metadata = new
            {
                request_id = requestId,
                generation_model = model,
                started_generation_at = ts,
                created_at = ts,
                metrics = new
                {
                    input_tokens = input,
                    output_tokens = output,
                    cache_read_tokens = cacheRead,
                    cache_creation_tokens = cacheWrite,
                },
            },
        });

    private static string SeedOpenCode(string root)
    {
        const long created = 1_780_000_000_000;
        var forkDir = Path.Combine(root, "storage", "message", "ses_fork");
        var fileDir = Path.Combine(root, "storage", "message", "ses_file");
        Directory.CreateDirectory(forkDir);
        Directory.CreateDirectory(fileDir);
        var copied = OpenCodeMessage("msg_f1", "ses_fork", created, 10, 4, 1, 2, 3, "claude-sonnet-5", "anthropic");
        File.WriteAllText(Path.Combine(forkDir, "msg_f1.json"), copied);
        File.WriteAllText(Path.Combine(fileDir, "msg_u1.json"),
            OpenCodeMessage("msg_u1", "ses_file", created + 5000, 8, 2, 0, 0, 0, "claude-sonnet-5", "anthropic"));

        var db = Path.Combine(root, "opencode.db");
        using var conn = OpenDb(db);
        Exec(conn, """
            CREATE TABLE message (
              id TEXT, session_id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT
            );
            CREATE TABLE session_message (
              id TEXT, session_id TEXT, type TEXT, time_created INTEGER, time_updated INTEGER, data TEXT
            );
            CREATE TABLE session_v2 (id TEXT, directory TEXT);
            """);
        Exec(conn, "INSERT INTO session_v2 (id, directory) VALUES ('ses_v2', '/work/proj')");
        InsertOpenCode(conn, "message", "msg_p1", "ses_parent", "assistant", created,
            OpenCodeMessage("msg_p1", "ses_parent", created, 10, 4, 1, 2, 3, "claude-sonnet-5", "anthropic"));
        InsertOpenCode(conn, "message", "msg_user", "ses_parent", "user", created,
            """{"id":"msg_user","sessionID":"ses_parent","role":"user","content":"SECRET_PROMPT","time":{"created":1},"tokens":{"input":50,"output":1}}""");
        InsertOpenCode(conn, "session_message", "msg_v2", "ses_v2", "assistant", created + 9000, Line(new
        {
            id = "msg_v2",
            sessionID = "ses_v2",
            content = "SECRET_REPLY",
            time = new { created = created + 9000, completed = created + 9100 },
            model = new { id = "gpt-5", providerID = "openai" },
            tokens = new { input = 7, output = 3, reasoning = 0, cache = new { read = 0, write = 0 } },
        }));
        return root;
    }

    private static string OpenCodeMessage(
        string id, string sessionId, long created, int input, int output, int reasoning, int cacheRead, int cacheWrite,
        string model, string provider) =>
        Line(new
        {
            id,
            sessionID = sessionId,
            role = "assistant",
            content = "SECRET_REPLY",
            model,
            modelID = model,
            providerID = provider,
            time = new { created, completed = created + 10 },
            tokens = new { input, output, reasoning, cache = new { read = cacheRead, write = cacheWrite } },
        });

    private static string SeedAntigravity(string root)
    {
        var logs = Path.Combine(root, "antigravity", "brain", "conv-123", ".system_generated", "logs");
        var conv = Path.Combine(root, "antigravity", "conversations");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(conv);
        File.WriteAllText(Path.Combine(root, "antigravity", "settings.json"),
            Line(new { model = "Claude Sonnet 4 (Thinking)" }));
        var decoy = Path.Combine(root, "gemini-cli", "brain", "other", ".system_generated", "logs");
        Directory.CreateDirectory(decoy);
        File.WriteAllText(Path.Combine(decoy, "transcript.jsonl"), Line(new
        {
            type = "PLANNER_RESPONSE",
            step_index = 1,
            created_at = "2026-04-05T14:01:00.000Z",
            content = "SECRET_DECOY",
        }));
        File.WriteAllText(Path.Combine(logs, "transcript.jsonl"), string.Join('\n',
            Line(new { type = "USER_INPUT", step_index = 0, created_at = "2026-04-05T14:00:00.000Z", content = "SECRET_PROMPT" }),
            Line(new { type = "PLANNER_RESPONSE", step_index = 1, created_at = "2026-04-05T14:01:00.000Z", content = "hi", thinking = "SECRET_THINK" }),
            Line(new { type = "USER_INPUT", step_index = 2, created_at = "2026-04-05T14:02:00.000Z", content = "SECRET_PROMPT" }),
            Line(new { type = "PLANNER_RESPONSE", step_index = 3, created_at = "2026-04-05T14:03:00.000Z", content = "done", thinking = "SECRET_THINK" })) + "\n");

        var db = Path.Combine(conv, "conv-123.db");
        using var conn = OpenDb(db);
        Exec(conn, "CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB)");
        InsertBlob(conn, 0, BuildProto(
            model: "gemini-3.8-flash", lastStepIndex: 0, promptTokens: 18239, cached: 0, output: 1366, text: 1286, reasoning: 80));
        InsertBlob(conn, 1, BuildProto(
            model: "gemini-3.8-flash", lastStepIndex: 2, promptTokens: 4583, cached: 16319, output: 151, text: 82, reasoning: 69));
        return root;
    }

    private static void InsertDevin(SqliteConnection conn, int rowId, string sessionId, string chat)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO message_nodes (row_id, session_id, chat_message, created_at) VALUES ($r, $s, $c, 1)";
        cmd.Parameters.AddWithValue("$r", rowId);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$c", chat);
        cmd.ExecuteNonQuery();
    }

    private static void InsertOpenCode(SqliteConnection conn, string table, string id, string sessionId, string roleOrType, long created, string data)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = table == "message"
            ? "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ($i, $s, $t, $t, $d)"
            : "INSERT INTO session_message (id, session_id, type, time_created, time_updated, data) VALUES ($i, $s, $role, $t, $t, $d)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$t", created);
        cmd.Parameters.AddWithValue("$d", data);
        if (table != "message") cmd.Parameters.AddWithValue("$role", roleOrType);
        cmd.ExecuteNonQuery();
    }

    private static void InsertBlob(SqliteConnection conn, int idx, byte[] data)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO gen_metadata (idx, data) VALUES ($i, $d)";
        cmd.Parameters.AddWithValue("$i", idx);
        cmd.Parameters.Add("$d", SqliteType.Blob).Value = data;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection OpenDb(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Line(object value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private static string Dump(UsageRecord row) =>
        string.Join('\n', row.SessionId, row.RequestKey, row.Model, row.Project, row.Tool);

    private static byte[] BuildProto(
        string? model = null,
        long? contextTokens = null,
        int? lastStepIndex = null,
        long? systemTokens = null,
        long? promptTokens = null,
        long? cached = null,
        long? output = null,
        long? text = null,
        long? reasoning = null,
        byte[]? contextBefore = null)
    {
        var parts = new List<byte[]>();
        if (model is not null) parts.Add(Ld(19, System.Text.Encoding.UTF8.GetBytes(model)));
        if (contextTokens is not null || contextBefore is not null)
        {
            var f10 = Ld(10, Vi(1, contextTokens ?? 25000));
            var body = contextBefore is null ? f10 : Concat(Tag(2, 0), contextBefore, f10);
            parts.Add(Ld(9, body));
        }
        if (systemTokens is not null || promptTokens is not null || cached is not null || output is not null || text is not null || reasoning is not null)
        {
            var usage = new List<byte[]>();
            if (systemTokens is not null) usage.Add(Vi(1, systemTokens.Value));
            if (promptTokens is not null) usage.Add(Vi(2, promptTokens.Value));
            if (output is not null) usage.Add(Vi(3, output.Value));
            if (cached is not null) usage.Add(Vi(5, cached.Value));
            if (text is not null) usage.Add(Vi(9, text.Value));
            if (reasoning is not null) usage.Add(Vi(10, reasoning.Value));
            parts.Add(Ld(4, Concat(usage.ToArray())));
        }
        if (lastStepIndex is not null)
        {
            parts.Add(Ld(20, Concat(
                Ld(1, "last_step_index"u8.ToArray()),
                Ld(2, System.Text.Encoding.ASCII.GetBytes(lastStepIndex.Value.ToString())))));
        }
        return Ld(1, Concat(parts.ToArray()));
    }

    private static byte[] Uint64MaxVarint() =>
        [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01];

    private static byte[] Vi(int field, long value) => Concat(Tag(field, 0), Varint((ulong)value));

    private static byte[] Ld(int field, byte[] payload) =>
        Concat(Tag(field, 2), Varint((ulong)payload.Length), payload);

    private static byte[] Tag(int field, int wire) => Varint((ulong)((field << 3) | wire));

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>(10);
        do
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0) b |= 0x80;
            bytes.Add(b);
        } while (value != 0);
        return bytes.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = 0;
        foreach (var part in parts) len += part.Length;
        var buf = new byte[len];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(buf, offset);
            offset += part.Length;
        }
        return buf;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = Directory.CreateTempSubdirectory("agenthub-usage-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
