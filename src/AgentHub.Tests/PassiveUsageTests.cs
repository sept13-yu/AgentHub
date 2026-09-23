using AgentHub.Core.TokenCore;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentHub.Tests;

/// <summary>
/// 被动用量共用一张表：每个 source id 一份夹具，断言 token / 去重，不落提示词。
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
        ["gemini-cli"],
        ["kiro"],
        ["copilot"],
        ["kimi-code"],
        ["codebuddy"],
        ["hermes"],
        ["openclaw"],
        ["every-code"],
        ["astudio"],
        ["oh-my-pi"],
        ["omo"],
        ["pi"],
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
            case "gemini-cli": AssertGeminiCli(rows); break;
            case "kiro": AssertKiro(rows); break;
            case "copilot": AssertCopilot(rows); break;
            case "kimi-code": AssertKimiCode(rows); break;
            case "codebuddy": AssertCodeBuddy(rows); break;
            case "hermes": AssertHermes(rows); break;
            case "openclaw": AssertOpenClaw(rows); break;
            case "every-code": AssertCodexFamily(rows); break;
            case "astudio": AssertCodexFamily(rows); break;
            case "oh-my-pi": AssertOhMyPi(rows); break;
            case "omo": AssertOmo(rows); break;
            case "pi": AssertPi(rows); break;
        }

        if (id == "codebuddy")
        {
            var homeOnly = PassiveUsage.ReadCodeBuddy([Path.Combine(root.Path, "home")]);
            Assert.DoesNotContain(homeOnly, row => row.RequestKey.Contains("extension-log", StringComparison.Ordinal));
            Assert.Contains(rows, row => row.RequestKey.Contains("extension-log", StringComparison.Ordinal));
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

    [Fact]
    public void Batch2_paths_match_token_tracker_layouts()
    {
        Assert.Equal(
            Path.Combine("roam", "Kiro", "User", "globalStorage", "kiro.kiroagent"),
            UsagePaths.KiroBase("roam"));
        Assert.Equal(Path.Combine("home", ".hermes", "state.db"), UsagePaths.HermesStateDb("home"));
        Assert.Equal(Path.Combine("home", ".copilot"), UsagePaths.CopilotHome("home"));
        Assert.Equal(Path.Combine("copilot", "session-store.db"), UsagePaths.CopilotSessionStore("copilot"));
        Assert.Equal(Path.Combine("copilot", "data.db"), UsagePaths.CopilotAppDb("copilot"));
    }

    [Fact]
    public void Batch3_paths_match_token_tracker_layouts()
    {
        Assert.Equal(Path.Combine("home", ".openclaw"), UsagePaths.OpenClawHome("home"));
        Assert.Equal(Path.Combine("home", ".code"), UsagePaths.EveryCodeHome("home"));
        Assert.Equal(Path.Combine("home", ".acode"), UsagePaths.AcodeHome("home"));
        Assert.Equal(Path.Combine("home", ".omp", "agent"), UsagePaths.OmpAgentDir("home"));
        Assert.Equal(Path.Combine("home", ".omo", "agent"), UsagePaths.OmoAgentDir("home"));
        Assert.Equal(Path.Combine("home", ".pi", "agent"), UsagePaths.PiAgentDir("home"));
    }

    [Fact]
    public void Pi_reader_attributes_dots_and_registry_does_not_rescan()
    {
        using var root = new TempDir();
        var agent = Path.Combine(root.Path, "agent");
        var cwd = Path.Combine(agent, "sessions", "--cwd--");
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, "t_s.jsonl"), string.Join('\n',
            Line(new { type = "session", cwd = "/work/dots" }),
            Line(new
            {
                type = "message",
                id = "d1",
                timestamp = "2026-01-01T00:00:00.000Z",
                message = new
                {
                    role = "assistant",
                    provider = "Dots",
                    model = "dots-model",
                    content = "SECRET",
                    usage = new { input = 4, output = 2, cacheRead = 1, cacheWrite = 0, reasoningTokens = 1 },
                },
            })) + "\n");

        var rows = PassiveUsage.ReadPi([agent]);
        var row = Assert.Single(rows);
        Assert.Equal("dots", row.Tool);
        Assert.Equal(4, row.InputTokens);
        Assert.Equal(2, row.OutputTokens);
        Assert.Equal(1, row.CachedInputTokens);
        Assert.Equal(1, row.ReasoningTokens);
        Assert.Equal("dots-model", row.Model);
        Assert.Equal("/work/dots", row.Project);
        Assert.DoesNotContain("SECRET", Dump(row), StringComparison.Ordinal);

        var dots = UsageSourceRegistry.Find("dots");
        Assert.NotNull(dots);
        Assert.Empty(dots.Units());
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
        "gemini-cli" => PassiveUsage.ReadGeminiCli([SeedGeminiCli(root)]),
        "kiro" => PassiveUsage.ReadKiro([Path.Combine(SeedKiro(root), "db"), Path.Combine(root, "jsonl")]),
        "copilot" => ReadCopilot(SeedCopilot(root)),
        "kimi-code" => PassiveUsage.ReadKimiCode([
            Path.Combine(SeedKimiCode(root), "kimi-code"),
            Path.Combine(root, "kimi"),
        ]),
        "codebuddy" => PassiveUsage.ReadCodeBuddy(
            [Path.Combine(SeedCodeBuddy(root), "home")],
            [Path.Combine(root, "logs")]),
        "hermes" => PassiveUsage.ReadHermes([SeedHermes(root)]),
        "openclaw" => PassiveUsage.ReadOpenClaw([SeedOpenClaw(root)]),
        "every-code" => PassiveUsage.ReadEveryCode([SeedCodexHome(root)]),
        "astudio" => PassiveUsage.ReadAStudio([SeedCodexHome(root)]),
        "oh-my-pi" => PassiveUsage.ReadOhMyPi([SeedOhMyPi(root)]),
        "omo" => PassiveUsage.ReadOmo([SeedOmo(root)]),
        "pi" => PassiveUsage.ReadPi([SeedPi(root)]),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    private static List<UsageRecord> ReadCopilot(string root) =>
        PassiveUsage.ReadCopilot(
            Directory.EnumerateFiles(Path.Combine(root, "otel"), "*.jsonl"),
            [Path.Combine(root, "session-store.db")],
            [Path.Combine(root, "data.db")]);

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

    private static void AssertGeminiCli(List<UsageRecord> rows)
    {
        Assert.Equal(3, rows.Count);
        var first = rows.Single(r => r.RequestKey == "0");
        Assert.Equal("session-abc", first.SessionId);
        Assert.Equal(10, first.InputTokens);
        Assert.Equal(2, first.OutputTokens);
        Assert.Equal(2, first.CachedInputTokens);
        Assert.Equal(0, first.ReasoningTokens);
        Assert.Equal("gemini-2.5-pro", first.Model);
        Assert.Equal(new DateTime(2026, 4, 5, 14, 0, 0, DateTimeKind.Utc), first.TsUtc);

        var grown = rows.Single(r => r.RequestKey == "2");
        Assert.Equal(8, grown.InputTokens);
        Assert.Equal(2, grown.OutputTokens);
        Assert.Equal(0, grown.CachedInputTokens);
        Assert.Equal(1, grown.ReasoningTokens);

        var reset = rows.Single(r => r.RequestKey == "3");
        Assert.Equal(3, reset.InputTokens);
        Assert.Equal(1, reset.OutputTokens);
        Assert.Equal(0, reset.CachedInputTokens);

        Assert.Equal(21, rows.Sum(r => r.InputTokens));
        Assert.Equal(5, rows.Sum(r => r.OutputTokens));
        Assert.Equal(2, rows.Sum(r => r.CachedInputTokens));
        Assert.Equal(1, rows.Sum(r => r.ReasoningTokens));
        Assert.DoesNotContain(rows, r => r.InputTokens == 999);
    }

    private static void AssertKiro(List<UsageRecord> rows)
    {
        Assert.Equal(2, rows.Count);
        var db = rows.Single(r => r.Model == "claude-sonnet-4");
        Assert.Equal("1", db.RequestKey);
        Assert.Equal(40, db.InputTokens);
        Assert.Equal(5, db.OutputTokens);
        Assert.Equal(new DateTime(2026, 1, 9, 15, 25, 30, DateTimeKind.Utc), db.TsUtc);

        var jsonl = rows.Single(r => r.Model == "kiro-agent");
        Assert.Equal("1", jsonl.RequestKey);
        Assert.Equal(7, jsonl.InputTokens);
        Assert.Equal(2, jsonl.OutputTokens);
        Assert.Equal(new DateTime(2026, 1, 9, 16, 0, 0, DateTimeKind.Utc), jsonl.TsUtc);
        Assert.DoesNotContain(rows, r => r.InputTokens == 999999);
    }

    private static void AssertCopilot(List<UsageRecord> rows)
    {
        Assert.Equal(4, rows.Count);
        var store = rows.Single(r => r.RequestKey == "store:1");
        Assert.Equal("s1", store.SessionId);
        Assert.Equal(100, store.InputTokens);
        Assert.Equal(10, store.OutputTokens);
        Assert.Equal("gpt-4o", store.Model);

        Assert.DoesNotContain(rows, r => r.RequestKey == "trace-dup:span-dup");
        var kept = rows.Single(r => r.RequestKey == "trace-keep:span-keep");
        Assert.Equal("s2", kept.SessionId);
        Assert.Equal(30, kept.InputTokens);
        Assert.Equal(4, kept.OutputTokens);

        var extension = rows.Single(r => r.RequestKey == "resp:resp-ext");
        Assert.Equal("s1", extension.SessionId);
        Assert.Equal(100, extension.InputTokens);
        Assert.Equal(10, extension.OutputTokens);

        var app = rows.Single(r => r.RequestKey == "app:desk");
        Assert.Equal(30, app.InputTokens);
        Assert.Equal(20, app.CachedInputTokens);
        Assert.Equal(5, app.OutputTokens);
        Assert.Equal(3, app.ReasoningTokens);
        Assert.Equal("claude-sonnet-4-5", app.Model);
        Assert.DoesNotContain(rows, r => r.InputTokens == 999999);
        Assert.Equal(260, rows.Sum(r => r.InputTokens));
    }

    private static void AssertKimiCode(List<UsageRecord> rows)
    {
        Assert.Equal(3, rows.Count);
        var official = rows.Single(r => r.RequestKey == "u1");
        Assert.Equal("sess-official", official.SessionId);
        Assert.Equal(11, official.InputTokens);
        Assert.Equal(6, official.OutputTokens);
        Assert.Equal(4, official.CachedInputTokens);
        Assert.Equal(2, official.CacheWriteTokens);
        Assert.Equal("kimi-k2.6", official.Model);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_767_225_600_000).UtcDateTime, official.TsUtc);

        var anthropic = rows.Single(r => r.RequestKey == "u2");
        Assert.Equal("sess-official", anthropic.SessionId);
        Assert.Equal(14, anthropic.InputTokens);
        Assert.Equal(6, anthropic.CachedInputTokens);
        Assert.Equal(5, anthropic.OutputTokens);
        Assert.Equal("kimi-k2.6", anthropic.Model);

        var legacy = rows.Single(r => r.RequestKey == "m1");
        Assert.Equal("sess-legacy", legacy.SessionId);
        Assert.Equal(9, legacy.InputTokens);
        Assert.Equal(3, legacy.OutputTokens);
        Assert.Equal(1, legacy.CachedInputTokens);
        Assert.Equal(2, legacy.CacheWriteTokens);
        Assert.Equal("kimi-for-coding", legacy.Model);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_767_225_700).UtcDateTime, legacy.TsUtc);
        Assert.DoesNotContain(rows, r => r.InputTokens is 99 or 50 or 80);
    }

    private static void AssertCodeBuddy(List<UsageRecord> rows)
    {
        Assert.Equal(3, rows.Count);
        var main = rows.Single(r => r.RequestKey == "mid-1");
        Assert.Equal("sess-cb", main.SessionId);
        Assert.Equal(60, main.InputTokens);
        Assert.Equal(15, main.OutputTokens);
        Assert.Equal(40, main.CachedInputTokens);
        Assert.Equal(5, main.ReasoningTokens);
        Assert.Equal("glm-5", main.Model);

        var fallback = rows.Single(r => r.RequestKey == "mid-2");
        Assert.Equal("sess-fallback", fallback.SessionId);
        Assert.Equal(8, fallback.InputTokens);
        Assert.Equal(2, fallback.OutputTokens);
        Assert.Equal("settings-model", fallback.Model);

        var log = rows.Single(r => r.SessionId == "agent-2");
        Assert.Contains("extension-log", log.RequestKey, StringComparison.Ordinal);
        Assert.Equal(4, log.InputTokens);
        Assert.Equal(1, log.OutputTokens);
        Assert.Equal("claude-x", log.Model);
        Assert.DoesNotContain(rows, r => r.InputTokens == 999);
    }

    private static void AssertHermes(List<UsageRecord> rows)
    {
        Assert.Equal(2, rows.Count);
        var main = rows.Single(r => r.SessionId == "default/s1");
        Assert.Equal("snapshot", main.RequestKey);
        Assert.Equal(100, main.InputTokens);
        Assert.Equal(20, main.OutputTokens);
        Assert.Equal(5, main.CachedInputTokens);
        Assert.Equal(3, main.CacheWriteTokens);
        Assert.Equal(4, main.ReasoningTokens);
        Assert.Equal("hermes-model", main.Model);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_767_225_600).UtcDateTime, main.TsUtc);

        var profile = rows.Single(r => r.SessionId == "p1/s2");
        Assert.Equal("snapshot", profile.RequestKey);
        Assert.Equal(9, profile.InputTokens);
        Assert.Equal(2, profile.OutputTokens);
        Assert.Equal("hermes-agent", profile.Model);
    }

    private static string SeedGeminiCli(string root)
    {
        var chats = Path.Combine(root, "tmp", "proj", "chats");
        var other = Path.Combine(root, "tmp", "proj", "other");
        Directory.CreateDirectory(chats);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(chats, "session-abc.json"), Line(new
        {
            messages = new object[]
            {
                new
                {
                    timestamp = "2026-04-05T14:00:00.000Z",
                    model = "gemini-2.5-pro",
                    content = "SECRET",
                    tokens = new { input = 10, cached = 2, output = 1, tool = 1, thoughts = 0, total = 14 },
                },
                new
                {
                    timestamp = "2026-04-05T14:00:01.000Z",
                    content = "SECRET",
                    tokens = new { input = 10, cached = 2, output = 1, tool = 1, thoughts = 0, total = 14 },
                },
                new
                {
                    timestamp = "2026-04-05T14:00:02.000Z",
                    content = "SECRET",
                    tokens = new { input = 18, cached = 2, output = 4, tool = 0, thoughts = 1, total = 25 },
                },
                new
                {
                    timestamp = "2026-04-05T14:00:03.000Z",
                    content = "SECRET",
                    tokens = new { input = 3, cached = 0, output = 1, tool = 0, thoughts = 0, total = 4 },
                },
            },
        }));
        File.WriteAllText(Path.Combine(chats, "notes.json"), Line(new
        {
            messages = new object[]
            {
                new { timestamp = "2026-04-05T14:00:00.000Z", content = "SECRET", tokens = new { input = 999, output = 1, total = 1000 } },
            },
        }));
        File.WriteAllText(Path.Combine(other, "session-nope.json"), Line(new
        {
            messages = new object[]
            {
                new { timestamp = "2026-04-05T14:00:00.000Z", content = "SECRET", tokens = new { input = 999, output = 1, total = 1000 } },
            },
        }));
        return root;
    }

    private static string SeedKiro(string root)
    {
        var dbRoot = Path.Combine(root, "db");
        var chatDir = Path.Combine(dbRoot, "ws");
        var dev = Path.Combine(dbRoot, "dev_data");
        Directory.CreateDirectory(chatDir);
        Directory.CreateDirectory(dev);
        var at = new DateTimeOffset(2026, 1, 9, 15, 25, 30, TimeSpan.Zero).ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(chatDir, "a.chat"), Line(new
        {
            content = "SECRET",
            metadata = new
            {
                modelId = "CLAUDE_SONNET_4_20250514_V1_0",
                startTime = at - 1000,
                endTime = at + 1000,
            },
        }));
        var db = Path.Combine(dev, "devdata.sqlite");
        using (var conn = OpenDb(db))
        {
            Exec(conn, """
                CREATE TABLE tokens_generated (
                  id INTEGER PRIMARY KEY,
                  model TEXT,
                  tokens_prompt INTEGER,
                  tokens_generated INTEGER,
                  timestamp TEXT
                );
                """);
            Exec(conn, "INSERT INTO tokens_generated (id, model, tokens_prompt, tokens_generated, timestamp) VALUES (1, 'IGNORED_MODEL', 40, 5, '2026-01-09 15:25:30')");
            Exec(conn, "INSERT INTO tokens_generated (id, model, tokens_prompt, tokens_generated, timestamp) VALUES (2, 'IGNORED_MODEL', 0, 0, '2026-01-09 15:26:30')");
        }
        File.WriteAllText(Path.Combine(dev, "tokens_generated.jsonl"),
            Line(new { promptTokens = 999999, generatedTokens = 1, content = "SECRET" }) + "\n");

        var jsonlRoot = Path.Combine(root, "jsonl");
        var jsonlDev = Path.Combine(jsonlRoot, "dev_data");
        Directory.CreateDirectory(jsonlDev);
        var jsonl = Path.Combine(jsonlDev, "tokens_generated.jsonl");
        File.WriteAllText(jsonl, Line(new { promptTokens = 7, generatedTokens = 2, content = "SECRET" }) + "\n");
        File.SetLastWriteTimeUtc(jsonl, new DateTime(2026, 1, 9, 16, 0, 0, DateTimeKind.Utc));
        return root;
    }

    private static string SeedCopilot(string root)
    {
        var otel = Path.Combine(root, "otel");
        Directory.CreateDirectory(otel);
        File.WriteAllText(Path.Combine(otel, "spans.jsonl"), string.Join('\n',
            Line(new
            {
                scopeMetrics = Array.Empty<object>(),
                attributes = new Dictionary<string, object> { ["gen_ai.operation.name"] = "chat", ["gen_ai.usage.input_tokens"] = 999999 },
            }),
            Line(new
            {
                type = "span",
                name = "chat gpt-4o",
                traceId = "trace-dup",
                spanId = "span-dup",
                endTime = new long[] { 1_767_225_600, 0 },
                attributes = new Dictionary<string, object>
                {
                    ["gen_ai.operation.name"] = "chat",
                    ["gen_ai.conversation.id"] = "s1",
                    ["gen_ai.response.model"] = "gpt-4o",
                    ["gen_ai.usage.input_tokens"] = 100,
                    ["gen_ai.usage.output_tokens"] = 10,
                },
            }),
            Line(new
            {
                type = "span",
                name = "chat gpt-4o",
                traceId = "trace-keep",
                spanId = "span-keep",
                endTime = new long[] { 1_767_225_700, 0 },
                attributes = new Dictionary<string, object>
                {
                    ["gen_ai.operation.name"] = "chat",
                    ["gen_ai.conversation.id"] = "s2",
                    ["gen_ai.response.model"] = "gpt-4o",
                    ["gen_ai.usage.input_tokens"] = 30,
                    ["gen_ai.usage.output_tokens"] = 4,
                },
            }),
            Line(new
            {
                type = "log",
                hrTime = new long[] { 1_767_225_600, 0 },
                attributes = new Dictionary<string, object>
                {
                    ["gen_ai.operation.name"] = "chat",
                    ["gen_ai.conversation.id"] = "s1",
                    ["gen_ai.response.model"] = "gpt-4o",
                    ["gen_ai.response.id"] = "resp-ext",
                    ["gen_ai.usage.input_tokens"] = 100,
                    ["gen_ai.usage.output_tokens"] = 10,
                },
            })) + "\n");

        using (var store = OpenDb(Path.Combine(root, "session-store.db")))
        {
            Exec(store, """
                CREATE TABLE assistant_usage_events (
                  id INTEGER PRIMARY KEY,
                  session_id TEXT,
                  model TEXT,
                  input_tokens INTEGER,
                  output_tokens INTEGER,
                  cache_read_tokens INTEGER,
                  cache_write_tokens INTEGER,
                  reasoning_tokens INTEGER,
                  token_details_json TEXT,
                  created_at TEXT,
                  prompt TEXT
                );
                """);
            Exec(store, """
                INSERT INTO assistant_usage_events
                  (id, session_id, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, created_at, prompt)
                VALUES (1, 's1', 'gpt-4o', 100, 10, 0, 0, 0, '2026-01-01T00:00:00.000Z', 'SECRET')
                """);
        }

        using (var app = OpenDb(Path.Combine(root, "data.db")))
        {
            Exec(app, """
                CREATE TABLE sessions (
                  id TEXT PRIMARY KEY,
                  session_type TEXT,
                  model TEXT,
                  provider_id TEXT,
                  created_at TEXT,
                  updated_at TEXT,
                  total_input_tokens INTEGER,
                  total_output_tokens INTEGER,
                  total_cached_tokens INTEGER,
                  total_reasoning_tokens INTEGER,
                  title TEXT
                );
                """);
            Exec(app, """
                INSERT INTO sessions
                  (id, session_type, model, created_at, updated_at, total_input_tokens, total_output_tokens, total_cached_tokens, total_reasoning_tokens, title)
                VALUES ('cli-sess', 'cli', 'gpt-4o', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', 999999, 9, 0, 0, 'SECRET')
                """);
            Exec(app, """
                INSERT INTO sessions
                  (id, session_type, model, provider_id, created_at, updated_at, total_input_tokens, total_output_tokens, total_cached_tokens, total_reasoning_tokens, title)
                VALUES ('desk', 'desktop', 'claude-sonnet-4.5', 'app', '2026-01-01T00:00:00.000Z', '2026-01-01T01:00:00.000Z', 50, 8, 20, 3, 'SECRET')
                """);
        }
        return root;
    }

    private static string SeedKimiCode(string root)
    {
        var official = Path.Combine(root, "kimi-code", "sessions", "ws", "sess-official", "agents", "main");
        var legacy = Path.Combine(root, "kimi", "sessions", "ws", "sess-legacy");
        Directory.CreateDirectory(official);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(official, "wire.jsonl"), string.Join('\n',
            Line(new { type = "config.update", modelAlias = "kimi-code/kimi-k2.6" }),
            Line(new
            {
                type = "context.append_loop_event",
                time = 1_767_225_600_000,
                content = "SECRET",
                @event = new
                {
                    type = "step.end",
                    uuid = "u1",
                    usage = new { inputOther = 11, inputCacheRead = 4, inputCacheCreation = 2, output = 6 },
                },
            }),
            Line(new
            {
                type = "context.append_loop_event",
                time = 1_767_225_601_000,
                @event = new
                {
                    type = "step.end",
                    uuid = "u1",
                    usage = new { inputOther = 99, output = 99 },
                },
            }),
            Line(new
            {
                type = "usage.record",
                time = 1_767_225_602_000,
                uuid = "u-usage",
                usage = new { inputOther = 50, output = 50 },
            }),
            Line(new
            {
                type = "step.end",
                time = 1_767_225_603_000,
                uuid = "u2",
                content = "SECRET",
                usage = new
                {
                    input_tokens = 20,
                    output_tokens = 5,
                    input_tokens_details = new { cached_tokens = 6 },
                },
            })) + "\n");
        File.WriteAllText(Path.Combine(legacy, "wire.jsonl"), string.Join('\n',
            Line(new
            {
                timestamp = 1_767_225_700,
                message = new
                {
                    type = "StatusUpdate",
                    content = "SECRET",
                    payload = new
                    {
                        message_id = "m1",
                        token_usage = new { input_other = 9, output = 3, input_cache_read = 1, input_cache_creation = 2 },
                    },
                },
            }),
            Line(new
            {
                timestamp = 1_767_225_701,
                message = new
                {
                    type = "StatusUpdate",
                    payload = new
                    {
                        message_id = "m1",
                        token_usage = new { input_other = 80, output = 1 },
                    },
                },
            })) + "\n");
        return root;
    }

    private static string SeedCodeBuddy(string root)
    {
        var home = Path.Combine(root, "home");
        var project = Path.Combine(home, "projects", "cwd");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(home, "settings.json"), Line(new { model = "settings-model" }));
        var mirroredMs = 1_767_225_600_123L;
        File.WriteAllText(Path.Combine(project, "sess.jsonl"), string.Join('\n',
            Line(new
            {
                timestamp = mirroredMs,
                sessionId = "sess-cb",
                content = "SECRET",
                providerData = new
                {
                    messageId = "mid-1",
                    model = "glm-5",
                    rawUsage = new
                    {
                        prompt_tokens = 100,
                        completion_tokens = 20,
                        prompt_tokens_details = new { cached_tokens = 40 },
                        completion_tokens_details = new { reasoning_tokens = 5 },
                    },
                },
            }),
            Line(new
            {
                timestamp = mirroredMs + 10,
                sessionId = "sess-cb",
                content = "SECRET",
                providerData = new
                {
                    messageId = "mid-1",
                    rawUsage = new { prompt_tokens = 999, completion_tokens = 9 },
                },
            }),
            Line(new
            {
                timestamp = 1_767_225_700_000L,
                sessionId = "sess-fallback",
                content = "SECRET",
                providerData = new
                {
                    messageId = "mid-2",
                    rawUsage = new { prompt_tokens = 8, completion_tokens = 2 },
                },
            })) + "\n");

        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(mirroredMs).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var unique = DateTimeOffset.FromUnixTimeMilliseconds(1_767_225_800_000).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        File.WriteAllText(Path.Combine(logs, "extension.log"), string.Join('\n',
            $"[{stamp}] [CraftInvokableAgent] [agent-1] Model prepared: route (glm-5)",
            $"[{stamp}] [AgentReporter] [agent-1] Agent execution successful with usage: {Line(new { cachedMissTokens = 60, output_tokens = 15, cache_read_input_tokens = 40, completion_thinking_tokens = 5, prompt = "SECRET" })}",
            $"[{unique}] [CraftInvokableAgent] [agent-2] Model prepared: route (claude-x)",
            $"[{unique}] [AgentReporter] [agent-2] Agent execution successful with usage: {Line(new { cachedMissTokens = 4, output_tokens = 1, prompt = "SECRET" })}") + "\n");
        return root;
    }

    private static string SeedHermes(string root)
    {
        Directory.CreateDirectory(root);
        WriteHermes(Path.Combine(root, "state.db"), "s1", "hermes-model", 100, 20, 5, 3, 4, 1_767_225_600);
        var profile = Path.Combine(root, "profiles", "p1");
        Directory.CreateDirectory(profile);
        WriteHermes(Path.Combine(profile, "state.db"), "s2", null, 9, 2, 0, 0, 0, 1_767_225_800);
        return root;
    }

    private static void WriteHermes(
        string db, string id, string? model, int input, int output, int cacheRead, int cacheWrite, int reasoning, long ended)
    {
        using var conn = OpenDb(db);
        Exec(conn, """
            CREATE TABLE sessions (
              id TEXT PRIMARY KEY,
              model TEXT,
              started_at INTEGER,
              ended_at INTEGER,
              input_tokens INTEGER,
              output_tokens INTEGER,
              cache_read_tokens INTEGER,
              cache_write_tokens INTEGER,
              reasoning_tokens INTEGER,
              title TEXT,
              body TEXT
            );
            """);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions
              (id, model, started_at, ended_at, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, title, body)
            VALUES ($id, $model, $started, $ended, $input, $output, $read, $write, $reason, 'SECRET', 'SECRET')
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started", ended - 10);
        cmd.Parameters.AddWithValue("$ended", ended);
        cmd.Parameters.AddWithValue("$input", input);
        cmd.Parameters.AddWithValue("$output", output);
        cmd.Parameters.AddWithValue("$read", cacheRead);
        cmd.Parameters.AddWithValue("$write", cacheWrite);
        cmd.Parameters.AddWithValue("$reason", reasoning);
        cmd.ExecuteNonQuery();
        using var zero = conn.CreateCommand();
        zero.CommandText = """
            INSERT INTO sessions
              (id, model, started_at, ended_at, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens, title, body)
            VALUES ('zero', 'skip', 1, 2, 0, 0, 0, 0, 0, 'SECRET', 'SECRET')
            """;
        zero.ExecuteNonQuery();
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

    private static void AssertOpenClaw(List<UsageRecord> rows)
    {
        Assert.Equal(4, rows.Count);
        var main = rows.Single(r => r.RequestKey == "m1");
        Assert.Equal("main/sess.jsonl", main.SessionId);
        Assert.Equal(60, main.InputTokens);
        Assert.Equal(8, main.OutputTokens);
        Assert.Equal(40, main.CachedInputTokens);
        Assert.Equal(5, main.CacheWriteTokens);
        Assert.Equal(0, main.ReasoningTokens);
        Assert.Equal("openclaw-model", main.Model);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), main.TsUtc);
        Assert.Equal(2, rows.Count(r => r.RequestKey.StartsWith("meta:", StringComparison.Ordinal)));

        var archived = rows.Single(r => r.RequestKey == "arch1");
        Assert.Equal("main/old.jsonl.reset.1", archived.SessionId);
        Assert.Equal(3, archived.InputTokens);
        Assert.Equal(1, archived.OutputTokens);
        Assert.DoesNotContain(rows, r => r.OutputTokens == 9 || r.InputTokens == 999 || r.RequestKey == "notes");
    }

    private static void AssertCodexFamily(List<UsageRecord> rows)
    {
        Assert.Equal(2, rows.Count);
        var live = rows.Single(r => r.SessionId == "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        Assert.Equal(80, live.InputTokens);
        Assert.Equal(7, live.OutputTokens);
        Assert.Equal(20, live.CachedInputTokens);
        Assert.Equal(0, live.CacheWriteTokens);
        Assert.Equal(3, live.ReasoningTokens);
        Assert.Equal("/work/app", live.Project);
        Assert.Equal("gpt-5", live.Model);
        Assert.False(live.IsSubagent);

        var archived = rows.Single(r => r.SessionId == "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Assert.Equal(10, archived.InputTokens);
        Assert.Equal(2, archived.OutputTokens);
        Assert.DoesNotContain(rows, r => r.InputTokens == 999);
    }

    private static void AssertOhMyPi(List<UsageRecord> rows)
    {
        Assert.Equal(2, rows.Count);
        var main = rows.Single(r => r.RequestKey == "a1");
        Assert.Equal("t_s", main.SessionId);
        Assert.Equal(10, main.InputTokens);
        Assert.Equal(4, main.OutputTokens);
        Assert.Equal(2, main.CachedInputTokens);
        Assert.Equal(1, main.CacheWriteTokens);
        Assert.Equal(3, main.ReasoningTokens);
        Assert.Equal("claude", main.Model);
        Assert.Equal("/work/omp", main.Project);
        Assert.False(main.IsSubagent);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), main.TsUtc);

        var sub = rows.Single(r => r.RequestKey == "sub1");
        Assert.True(sub.IsSubagent);
        Assert.Equal("t_s", sub.SessionId);
        Assert.Equal(1, sub.InputTokens);
        Assert.Equal("/work/omp", sub.Project);
        Assert.DoesNotContain(rows, r => r.InputTokens == 999 || r.RequestKey == "a0");
    }

    private static void AssertOmo(List<UsageRecord> rows)
    {
        Assert.Equal(2, rows.Count);
        var main = rows.Single(r => r.RequestKey == "o1");
        Assert.Equal("t_s", main.SessionId);
        Assert.Equal(8, main.InputTokens);
        Assert.Equal(7, main.OutputTokens);
        Assert.Equal(1, main.CachedInputTokens);
        Assert.Equal(5, main.ReasoningTokens);
        Assert.Equal("grok-4.6", main.Model);
        Assert.Equal("/work/omo", main.Project);
        Assert.False(main.IsSubagent);

        var sub = rows.Single(r => r.RequestKey == "sub1");
        Assert.True(sub.IsSubagent);
        Assert.Equal(2, sub.OutputTokens);
        Assert.Equal(1, sub.ReasoningTokens);
        Assert.Equal("t_s", sub.SessionId);
    }

    private static void AssertPi(List<UsageRecord> rows)
    {
        Assert.Equal(3, rows.Count);
        var routed = rows.Single(r => r.RequestKey == "p1");
        Assert.Equal(10, routed.InputTokens);
        Assert.Equal(4, routed.OutputTokens);
        Assert.Equal(3, routed.ReasoningTokens);
        Assert.Equal("claude", routed.Model);
        Assert.Equal("/work/pi", routed.Project);
        Assert.False(routed.IsSubagent);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), routed.TsUtc);

        var plain = rows.Single(r => r.RequestKey == "p2");
        Assert.Equal(6, plain.InputTokens);
        Assert.Equal(1, plain.OutputTokens);
        Assert.Equal("pi-model", plain.Model);

        var sub = rows.Single(r => r.RequestKey == "s1");
        Assert.True(sub.IsSubagent);
        Assert.Equal("t_s", sub.SessionId);
        Assert.Equal(1, sub.InputTokens);
    }

    private static string SeedOpenClaw(string root)
    {
        var sessions = Path.Combine(root, "agents", "main", "sessions");
        var archive = Path.Combine(root, "agents", "main", "session-sqlite-import-archive");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(sessions, "sess.jsonl"), string.Join('\n',
            Line(new
            {
                type = "message",
                id = "m1",
                timestamp = "2026-01-01T00:00:00.000Z",
                message = new
                {
                    role = "assistant",
                    model = "openclaw-model",
                    content = "SECRET",
                    usage = new { input = 100, cacheRead = 40, cacheWrite = 5, output = 8, totalTokens = 113 },
                },
            }),
            Line(new
            {
                type = "message",
                id = "m1",
                timestamp = "2026-01-01T00:00:01.000Z",
                message = new
                {
                    content = "SECRET",
                    usage = new { input = 100, cacheRead = 40, cacheWrite = 5, output = 8, totalTokens = 113 },
                },
            }),
            Line(new
            {
                type = "message",
                id = "bad",
                timestamp = "2026-01-01T00:00:02.000Z",
                message = new
                {
                    content = "SECRET",
                    usage = new { input = 1.5, cacheRead = 0, cacheWrite = 0, output = 9, totalTokens = 10 },
                },
            }),
            Line(new
            {
                type = "message",
                timestamp = "2026-01-01T00:00:03.000Z",
                message = new
                {
                    content = "SECRET",
                    usage = new { input = 2, output = 2, cacheRead = 0, cacheWrite = 0, totalTokens = 4 },
                },
            }),
            Line(new
            {
                type = "message",
                timestamp = "2026-01-01T00:00:03.000Z",
                message = new
                {
                    content = "SECRET",
                    usage = new { input = 2, output = 2, cacheRead = 0, cacheWrite = 0, totalTokens = 4 },
                },
            })) + "\n");
        File.WriteAllText(Path.Combine(sessions, "notes.json"), Line(new
        {
            type = "message",
            id = "notes",
            timestamp = "2026-01-01T00:00:00.000Z",
            message = new { content = "SECRET", usage = new { input = 999, output = 999, totalTokens = 1998 } },
        }));
        File.WriteAllText(Path.Combine(archive, "old.jsonl.reset.1"), Line(new
        {
            type = "message",
            id = "arch1",
            timestamp = "2026-01-01T00:00:04.000Z",
            message = new
            {
                model = "openclaw-model",
                content = "SECRET",
                usage = new { input = 3, cacheRead = 0, cacheWrite = 0, output = 1, totalTokens = 4 },
            },
        }) + "\n");
        return root;
    }

    private static string SeedCodexHome(string root)
    {
        var day = Path.Combine(root, "sessions", "2026", "01", "01");
        var archived = Path.Combine(root, "archived_sessions");
        Directory.CreateDirectory(day);
        Directory.CreateDirectory(archived);
        const string live = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        const string old = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        File.WriteAllText(
            Path.Combine(day, "rollout-2026-01-01T00-00-00-" + live + ".jsonl"),
            string.Join('\n', CodexMeta(), CodexEvent("2026-01-01T00:00:01.000Z", 100, 20, 10, 3), CodexEvent("2026-01-01T00:00:02.000Z", 100, 20, 10, 3)) + "\n");
        File.WriteAllText(
            Path.Combine(archived, "rollout-2026-01-01T00-00-00-" + old + ".jsonl"),
            string.Join('\n', CodexMeta(), CodexEvent("2026-01-01T00:00:03.000Z", 10, 0, 2, 0)) + "\n");
        File.WriteAllText(Path.Combine(root, "sessions", "notes.jsonl"), CodexEvent("2026-01-01T00:00:04.000Z", 999, 0, 999, 0) + "\n");
        return root;
    }

    private static string CodexMeta() =>
        Line(new
        {
            timestamp = "2026-01-01T00:00:00.000Z",
            type = "session_meta",
            payload = new { cwd = "/work/app", model = "gpt-5", prompt = "SECRET" },
        });

    private static string CodexEvent(string ts, int input, int cached, int output, int reasoning) =>
        Line(new
        {
            timestamp = ts,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = CodexUsage(input, cached, output, reasoning),
                    total_token_usage = CodexUsage(input, cached, output, reasoning),
                },
            },
        });

    private static object CodexUsage(int input, int cached, int output, int reasoning) => new
    {
        input_tokens = input,
        cached_input_tokens = cached,
        output_tokens = output,
        reasoning_output_tokens = reasoning,
        total_tokens = input + output,
    };

    private static string SeedOhMyPi(string root)
    {
        var cwd = Path.Combine(root, "sessions", "--cwd--");
        var nested = Path.Combine(cwd, "t_s");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(cwd, "t_s.jsonl"), string.Join('\n',
            Line(new { type = "session", cwd = "/work/omp" }),
            PiMessage("a1", "claude", 10, 4, 2, 1, 3, includeProvider: true, provider: "anthropic", timestampMs: 1767225600000L),
            PiMessage("a1", "claude", 10, 4, 2, 1, 3, includeProvider: true, provider: "anthropic", timestampMs: 1767225600000L),
            PiMessage("a0", "claude", 0, 0, 0, 0, 0, includeProvider: false, provider: null, timestampMs: null),
            PiMessage("a0", "claude", 999, 1, 0, 0, 0, includeProvider: false, provider: null, timestampMs: null),
            Line(new
            {
                type = "message",
                id = "user1",
                timestamp = "2026-01-01T00:00:00.000Z",
                message = new
                {
                    role = "user",
                    content = "SECRET",
                    usage = new { input = 999, output = 1, cacheRead = 0, cacheWrite = 0, reasoningTokens = 0 },
                },
            })) + "\n");
        File.WriteAllText(Path.Combine(nested, "sub.jsonl"), string.Join('\n',
            Line(new { type = "session", cwd = "/work/omp" }),
            PiMessage("sub1", "claude", 1, 1, 0, 0, 0, includeProvider: false, provider: null, timestampMs: null)) + "\n");
        File.WriteAllText(Path.Combine(root, "sessions", "stray.jsonl"),
            PiMessage("stray", "claude", 999, 1, 0, 0, 0, includeProvider: false, provider: null, timestampMs: null) + "\n");
        return root;
    }

    private static string SeedOmo(string root)
    {
        var cwd = Path.Combine(root, "sessions", "--cwd--");
        var nested = Path.Combine(cwd, "t_s");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(cwd, "t_s.jsonl"), string.Join('\n',
            Line(new { type = "session", cwd = "/work/omo" }),
            Line(new
            {
                type = "message",
                id = "o1",
                timestamp = "2026-01-02T00:00:00.000Z",
                message = new
                {
                    role = "assistant",
                    model = "grok-4.6",
                    content = "SECRET",
                    timestamp = 1767225600000L,
                    usage = new { input = 8, output = 12, cacheRead = 1, cacheWrite = 0, reasoningTokens = 5, reasoning = 99 },
                },
            })) + "\n");
        File.WriteAllText(Path.Combine(nested, "sub.jsonl"), string.Join('\n',
            Line(new { type = "session", cwd = "/work/omo" }),
            Line(new
            {
                type = "message",
                id = "sub1",
                timestamp = "2026-01-01T00:00:05.000Z",
                message = new
                {
                    role = "assistant",
                    model = "grok-4.6",
                    content = "SECRET",
                    usage = new { input = 1, output = 3, cacheRead = 0, cacheWrite = 0, reasoning = 1 },
                },
            })) + "\n");
        return root;
    }

    private static string SeedPi(string root)
    {
        var cwd = Path.Combine(root, "sessions", "--cwd--");
        var nested = Path.Combine(cwd, "t_s");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(cwd, "t_s.jsonl"), string.Join('\n',
            Line(new { type = "session", cwd = "/work/pi" }),
            PiMessage("p1", "claude", 10, 4, 2, 1, 3, includeProvider: true, provider: "anthropic", timestampMs: 1767225600000L),
            Line(new
            {
                type = "message",
                id = "p2",
                timestamp = "2026-01-01T00:00:06.000Z",
                message = new
                {
                    role = "assistant",
                    model = "pi-model",
                    content = "SECRET",
                    usage = new { input = 6, output = 1, cacheRead = 0, cacheWrite = 0, reasoningTokens = 0 },
                },
            })) + "\n");
        File.WriteAllText(Path.Combine(nested, "sub.jsonl"), string.Join('\n',
            Line(new { type = "session", cwd = "/work/pi" }),
            PiMessage("s1", "claude", 1, 1, 0, 0, 0, includeProvider: true, provider: "openai", timestampMs: null)) + "\n");
        return root;
    }

    private static string PiMessage(
        string id, string model, int input, int output, int cacheRead, int cacheWrite, int reasoning,
        bool includeProvider, string? provider, long? timestampMs)
    {
        if (includeProvider)
        {
            return Line(new
            {
                type = "message",
                id,
                timestamp = "2026-01-02T00:00:00.000Z",
                message = new
                {
                    role = "assistant",
                    provider,
                    model,
                    content = "SECRET",
                    timestamp = timestampMs,
                    usage = new
                    {
                        input,
                        output,
                        cacheRead,
                        cacheWrite,
                        reasoningTokens = reasoning,
                    },
                },
            });
        }
        return Line(new
        {
            type = "message",
            id,
            timestamp = "2026-01-01T00:00:00.000Z",
            message = new
            {
                role = "assistant",
                model,
                content = "SECRET",
                usage = new
                {
                    input,
                    output,
                    cacheRead,
                    cacheWrite,
                    reasoningTokens = reasoning,
                },
            },
        });
    }

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
