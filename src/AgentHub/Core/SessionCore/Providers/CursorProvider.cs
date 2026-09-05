using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>Cursor 会话（方案 §4.2 + docs/探测/cursor.md）：
/// 主库 state.vscdb（~1GB+，宿主常驻）：composerHeaders（列表）+ cursorDiskKV（正文）。
/// 只读一律 Mode=ReadOnly + PRAGMA query_only=ON（实测 0.6ms，宿主在跑不报 locked；
/// 禁止 copy / immutable=1 / wal_checkpoint）。
/// 写操作（改名/删除/VACUUM）：必须 Cursor 完全退出（含托盘），否则拒绝并说明。
/// agentKv:blob 是内容寻址共享存储，删除严禁按会话前缀触碰；磁盘回收靠 VACUUM。</summary>
public sealed class CursorProvider(TitleOverrideStore titles) : IConversationProvider
{
    public string AgentId => "cursor";

    private static string GlobalStorage => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage");
    private static string MainDb => Path.Combine(GlobalStorage, "state.vscdb");
    private static string SearchDb => Path.Combine(GlobalStorage, "conversation-search.db");

    public string? MissingReason => !File.Exists(MainDb) ? $"未找到 Cursor 主库：{MainDb}" : null;

    // ---------------- 只读连接 ----------------

    private static SqliteConnection OpenRead(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA query_only=ON";   // 双保险：模式只读 + 查询只读
        cmd.ExecuteNonQuery();
        return conn;
    }

    // ---------------- 列表 / 详情 ----------------

    public Task<IReadOnlyList<ConversationSummary>> ListAsync() => Task.Run<IReadOnlyList<ConversationSummary>>(() =>
    {
        var list = new List<ConversationSummary>();
        if (!File.Exists(MainDb)) return list;

        using var conn = OpenRead(MainDb);
        // 列表只碰 composerHeaders（有 recency 索引）；不扫 cursorDiskKV value（秒级全表扫）
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT composerId, value, createdAt, lastUpdatedAt,
                       COALESCE(isSubagent, 0), COALESCE(isArchived, 0)
                FROM composerHeaders
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                var valueJson = r.IsDBNull(1) ? null : r.GetString(1);
                var createdAt = r.IsDBNull(2) ? 0L : r.GetInt64(2);
                var lastUpdated = r.IsDBNull(3) ? 0L : r.GetInt64(3);
                bool isSub = r.GetInt32(4) != 0;

                var (name, project, noTitle, parentId) = ParseHeaderValue(valueJson);
                var last = lastUpdated > 0 ? lastUpdated : createdAt;
                var lastUtc = last > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(last).UtcDateTime
                    : File.GetLastWriteTimeUtc(MainDb);

                list.Add(new ConversationSummary
                {
                    AgentId = AgentId,
                    Id = id,
                    Title = titles.Get(AgentId, id) ?? name ?? (noTitle ? "(无标题空壳)" : "(无标题)"),
                    TitleSource = titles.Get(AgentId, id) is not null ? "override" : name is not null ? "source" : "derived",
                    Project = project,
                    // 消息数不在列表算：2.6GB 级 cursorDiskKV 逐会话计数太贵；详情页现算
                    MessageCount = 0,
                    SizeBytes = 0,
                    LastActivityUtc = lastUtc,
                    IsSubagent = isSub,
                    ParentId = parentId,
                    SourceFile = MainDb,
                });
            }
        }
        return list;
    });

    public Task<ConversationDetail?> LoadAsync(string id)
    {
        CodexProvider.GuardDbId(id);
        return Task.Run<ConversationDetail?>(() =>
        {
            if (!File.Exists(MainDb)) return null;
            using var conn = OpenRead(MainDb);

            string? valueJson = null;
            long lastUpdated = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT value, lastUpdatedAt FROM composerHeaders WHERE composerId = $id";
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    valueJson = r.IsDBNull(0) ? null : r.GetString(0);
                    lastUpdated = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                }
            }
            if (valueJson is null) return null;

            var (name, project, _, parentId) = ParseHeaderValue(valueJson);
            var messages = new List<(int Type, string Created, string Text)>();
            using (var cmd = conn.CreateCommand())
            {
                // type 1 = 用户，2 = 助手。前缀范围走索引，避免整表 LIKE。
                var lo = "bubbleId:" + id + ":";
                cmd.CommandText = """
                    SELECT value FROM cursorDiskKV
                    WHERE key >= $lo AND key < $hi
                    """;
                cmd.Parameters.AddWithValue("$lo", lo);
                cmd.Parameters.AddWithValue("$hi", lo + char.MaxValue);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;
                    var (type, created, text) = ParseBubble(r.GetString(0));
                    if (text.Length > 0) messages.Add((type, created, text));
                }
            }
            messages.Sort((a, b) => string.CompareOrdinal(a.Created, b.Created));

            var capped = messages.Count > 200 ? messages.Skip(messages.Count - 200).ToList() : messages;
            var detailMessages = capped
                .Select(m => new ConversationMessage
                {
                    Role = m.Type == 1 ? "user" : "assistant",
                    TimestampUtc = CodexProvider.ParseTs(m.Created.Length > 0 ? m.Created : null),
                    Text = m.Text.Length > 4000 ? m.Text[..4000] + "\n…（截断）" : m.Text,
                })
                .ToList();

            string? note = null;
            if (messages.Count == 0) note = "该会话没有气泡数据（可能是空壳或已归档残留）。";
            else if (messages.Count > 200) note = $"共 {messages.Count} 条消息，预览仅显示最近 200 条。";

            return new ConversationDetail
            {
                Summary = new ConversationSummary
                {
                    AgentId = AgentId,
                    Id = id,
                    Title = titles.Get(AgentId, id) ?? name ?? "(无标题)",
                    TitleSource = titles.Get(AgentId, id) is not null ? "override" : name is not null ? "source" : "derived",
                    Project = project,
                    MessageCount = messages.Count,
                    SizeBytes = 0,
                    LastActivityUtc = lastUpdated > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(lastUpdated).UtcDateTime
                        : File.GetLastWriteTimeUtc(MainDb),
                    IsSubagent = false,
                    ParentId = parentId,
                    SourceFile = MainDb,
                },
                Messages = detailMessages,
                Note = note,
            };
        });
    }

    // ---------------- 写操作（必须 Cursor 退出） ----------------

    /// <summary>宿主是否在运行（含后台/托盘进程）。写库前必须为 false。</summary>
    public static bool CursorRunning() => Process.GetProcessesByName("Cursor").Length > 0;

    private static void EnsureWritable(string what)
    {
        if (CursorRunning())
            throw new InvalidOperationException($"{what}需要先完全退出 Cursor（包括托盘图标）后重试——宿主持有 WAL 写锁，写入会损坏数据库。");
    }

    public Task RenameAsync(string id, string title)
    {
        CodexProvider.GuardDbId(id);
        EnsureWritable("改标题");
        return Task.Run(() =>
        {
            using var conn = OpenWrite(MainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM composerHeaders WHERE composerId = $id";
            cmd.Parameters.AddWithValue("$id", id);
            var valueJson = cmd.ExecuteScalar() as string
                ?? throw new FileNotFoundException($"会话不存在：{id}");

            // C# 侧改 JSON（不依赖 SQLite JSON1 扩展是否启用），保留其余字段
            using var doc = JsonDocument.Parse(valueJson);
            var writer = new MemoryStream();
            using (var jw = new Utf8JsonWriter(writer))
            {
                jw.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name == "name") continue;
                    prop.WriteTo(jw);
                }
                jw.WriteString("name", title);
                jw.WriteEndObject();
            }
            var newValue = JsonSerializer.Serialize(
                JsonDocument.Parse(writer.ToArray()).RootElement);

            cmd.Parameters.Clear();
            cmd.CommandText = "UPDATE composerHeaders SET value = $v WHERE composerId = $id";
            cmd.Parameters.AddWithValue("$v", newValue);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();

            TryRenameSearchIndex(id, title);
        });
    }

    /// <summary>同步 conversation-search.db 的标题（表不存在/列不符时静默跳过，主库已改成功）。</summary>
    private static void TryRenameSearchIndex(string id, string title)
    {
        try
        {
            if (!File.Exists(SearchDb)) return;
            using var conn = OpenWrite(SearchDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE conversations SET title = $t, updated_at = $u WHERE id = $id";
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* 搜索库结构与预期不符：跳过（不影响主库改名结果） */ }
    }

    public Task<IReadOnlyList<DeleteItemResult>> DeleteAsync(IEnumerable<string> ids) => Task.Run<IReadOnlyList<DeleteItemResult>>(() =>
    {
        EnsureWritable("删除会话");
        var results = new List<DeleteItemResult>();
        // 整批共享一个连接：此前每会话各开连接 + LIKE 拼接表达式删除，
        // SQLite 对表达式模式不走索引（EXPLAIN=SCAN 全索引扫，实测 222ms/会话 vs 范围 4ms）
        using var conn = OpenWrite(MainDb);
        using var search = File.Exists(SearchDb) ? OpenWrite(SearchDb) : null;
        // agentKv:blob 是内容寻址共享存储，严禁按会话前缀删（findings §7）
        foreach (var id in ids)
        {
            try
            {
                CodexProvider.GuardDbId(id);
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM composerHeaders WHERE composerId = $id";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                DeleteComposerKv(conn, tx, id);
                DeleteInlineDiffsForComposer(conn, tx, id);
                tx.Commit();
                titles.Remove(AgentId, id);
                if (search is not null) DeleteFromSearch(search, id);
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = true, Note = "磁盘空间需 VACUUM 后才实际回收" });
            }
            catch (Exception ex)
            {
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = ex.Message });
            }
        }
        return results;
    });

    private static readonly string[] DelimitedPrefixes =
        ["bubbleId:", "checkpointId:", "ofsContent:", "codeBlockPartialInlineDiffFates:"];

    /// <summary>会话附属 KV。不含 composerHeaders、不含按 value 点的 inlineDiff。</summary>
    private static void DeleteComposerKv(SqliteConnection conn, SqliteTransaction tx, string id)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM cursorDiskKV WHERE key = $data OR key = $heights";
            cmd.Parameters.AddWithValue("$data", "composerData:" + id);
            cmd.Parameters.AddWithValue("$heights", "composerVirtualRowHeights:" + id);
            cmd.ExecuteNonQuery();
        }
        foreach (var prefix in DelimitedPrefixes)
            DeletePrefixRange(conn, tx, prefix + id + ":");
    }

    private static void DeletePrefixRange(SqliteConnection conn, SqliteTransaction tx, string lo)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM cursorDiskKV WHERE key >= $lo AND key < $hi";
        cmd.Parameters.AddWithValue("$lo", lo);
        cmd.Parameters.AddWithValue("$hi", lo + char.MaxValue);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteKey(SqliteConnection conn, SqliteTransaction tx, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM cursorDiskKV WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteFromSearch(SqliteConnection conn, string id)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM conversations WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { }
    }

    /// <summary>
    /// inlineDiff 键是 inlineDiff:{workspaceId}:{diffId}，会话只在 value.composerMetadata.composerId。
    /// 按 workspace 前缀会误伤同仓库其它会话；只删命中该 composerId 的行。
    /// </summary>
    private static void DeleteInlineDiffsForComposer(SqliteConnection conn, SqliteTransaction tx, string id)
    {
        var lo = "inlineDiff:";
        var keys = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT key, value FROM cursorDiskKV WHERE key >= $lo AND key < $hi";
            cmd.Parameters.AddWithValue("$lo", lo);
            cmd.Parameters.AddWithValue("$hi", lo + char.MaxValue);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(1)) continue;
                if (InlineDiffComposerId(r.GetString(1)) == id)
                    keys.Add(r.GetString(0));
            }
        }
        foreach (var key in keys)
            DeleteKey(conn, tx, key);
    }

    private static string? InlineDiffComposerId(string valueJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            return doc.RootElement.TryGetProperty("composerMetadata", out var meta)
                ? CodexProvider.GetString(meta, "composerId")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------- 空壳清理（Cursor 专属，方案 §0） ----------------

    /// <summary>空壳判定（findings §7 实测）：0 bubble 且无标题（isDraft/isEphemeral 的自然子集）。</summary>
    public sealed record ShellItem(string Id, long LastUpdated, string Reason);

    public sealed record StorageOverview(long MainDbBytes, long AgentKvBytes, long AgentKvCount);

    /// <summary>agentKv 内容库占用（只读）。用于引导用户跑官方 GC 命令回收孤儿 blob；
    /// 注意 agentKv 是内容寻址共享存储，AgentHub 自身不做清理（findings §7 红线）。</summary>
    public StorageOverview GetStorageOverview()
    {
        long mainBytes = File.Exists(MainDb) ? new FileInfo(MainDb).Length : 0;
        long kvBytes = 0;
        long kvCount = 0;
        if (File.Exists(MainDb))
        {
            using var conn = OpenRead(MainDb);
            using var cmd = conn.CreateCommand();
            var lo = "agentKv:";
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(LENGTH(value)), 0) FROM cursorDiskKV WHERE key >= $lo AND key < $hi";
            cmd.Parameters.AddWithValue("$lo", lo);
            cmd.Parameters.AddWithValue("$hi", lo + char.MaxValue);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                kvCount = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                kvBytes = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            }
        }
        return new StorageOverview(mainBytes, kvBytes, kvCount);
    }

    public List<ShellItem> FindShells()
    {
        using var conn = OpenRead(MainDb);
        var shells = new List<ShellItem>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT composerId, value, lastUpdatedAt FROM composerHeaders";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetString(0);
            var valueJson = r.IsDBNull(1) ? null : r.GetString(1);
            var (name, _, noTitle, _) = ParseHeaderValue(valueJson);
            if (name is not null) continue;
            if (CountBubbles(conn, id) != 0) continue;
            var last = r.IsDBNull(2) ? 0 : r.GetInt64(2);
            shells.Add(new ShellItem(id, last, noTitle ? "无标题 · 0 消息" : "0 消息"));
        }
        return shells;
    }

    /// <summary>删除空壳行（须 Cursor 退出）。返回逐条结果。</summary>
    public IReadOnlyList<DeleteItemResult> CleanShells()
    {
        EnsureWritable("空壳清理");
        var shells = FindShells();
        var ids = shells.Select(s => s.Id).ToList();
        return DeleteAsync(ids).GetAwaiter().GetResult();
    }

    /// <summary>已无 composerHeaders 的会话附属 KV（行高 / 部分 diff fate / inlineDiff）。
    /// 不包含 agentKv、composer.content（内容寻址共享）。</summary>
    public sealed record OrphanOverview(
        int FateRows, long FateBytes,
        int HeightRows, long HeightBytes,
        int InlineDiffRows, long InlineDiffBytes,
        int ComposerIds,
        int TotalRows,
        long TotalBytes);

    public sealed record OrphanCleanResult(bool Ok, string? Error, int DeletedRows, long DeletedBytes, OrphanOverview Before);

    private sealed class OrphanPlan
    {
        public required OrphanOverview Overview { get; init; }
        public required HashSet<string> Ids { get; init; }
        public required List<string> InlineDiffKeys { get; init; }
    }

    public OrphanOverview FindOrphans()
    {
        if (!File.Exists(MainDb))
            return new OrphanOverview(0, 0, 0, 0, 0, 0, 0, 0, 0);
        using var conn = OpenRead(MainDb);
        return PlanOrphans(conn).Overview;
    }

    /// <summary>回收已无 header 的会话附属行（须 Cursor 退出）。不碰 agentKv。</summary>
    public OrphanCleanResult CleanOrphans()
    {
        EnsureWritable("孤儿回收");
        using var conn = OpenWrite(MainDb);
        using var search = File.Exists(SearchDb) ? OpenWrite(SearchDb) : null;
        var plan = PlanOrphans(conn);
        foreach (var id in plan.Ids)
            CodexProvider.GuardDbId(id);
        using var tx = conn.BeginTransaction();
        foreach (var id in plan.Ids)
            DeleteComposerKv(conn, tx, id);
        foreach (var key in plan.InlineDiffKeys)
            DeleteKey(conn, tx, key);
        tx.Commit();
        foreach (var id in plan.Ids)
        {
            titles.Remove(AgentId, id);
            if (search is not null) DeleteFromSearch(search, id);
        }
        return new OrphanCleanResult(true, null, plan.Overview.TotalRows, plan.Overview.TotalBytes, plan.Overview);
    }

    private static HashSet<string> LoadHeaderIds(SqliteConnection conn)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT composerId FROM composerHeaders";
        using var r = cmd.ExecuteReader();
        while (r.Read()) live.Add(r.GetString(0));
        return live;
    }

    private static OrphanPlan PlanOrphans(SqliteConnection conn)
    {
        var live = LoadHeaderIds(conn);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int fateRows = 0; long fateBytes = 0;
        int heightRows = 0; long heightBytes = 0;
        int diffRows = 0; long diffBytes = 0;
        var inlineKeys = new List<string>();

        ScanPrefix(conn, "codeBlockPartialInlineDiffFates:", withValue: false, (key, bytes, _) =>
        {
            var id = IdAfterPrefix(key, "codeBlockPartialInlineDiffFates:", delimited: true);
            if (id is null || live.Contains(id)) return;
            ids.Add(id);
            fateRows++;
            fateBytes += bytes;
        });
        ScanPrefix(conn, "composerVirtualRowHeights:", withValue: false, (key, bytes, _) =>
        {
            var id = IdAfterPrefix(key, "composerVirtualRowHeights:", delimited: false);
            if (id is null || live.Contains(id)) return;
            ids.Add(id);
            heightRows++;
            heightBytes += bytes;
        });
        ScanPrefix(conn, "inlineDiff:", withValue: true, (key, bytes, json) =>
        {
            var id = json is null ? null : InlineDiffComposerId(json);
            if (id is null || live.Contains(id)) return;
            ids.Add(id);
            inlineKeys.Add(key);
            diffRows++;
            diffBytes += bytes;
        });

        var totalRows = fateRows + heightRows + diffRows;
        var totalBytes = fateBytes + heightBytes + diffBytes;
        return new OrphanPlan
        {
            Overview = new OrphanOverview(
                fateRows, fateBytes, heightRows, heightBytes,
                diffRows, diffBytes, ids.Count, totalRows, totalBytes),
            Ids = ids,
            InlineDiffKeys = inlineKeys,
        };
    }

    private static string? IdAfterPrefix(string key, string prefix, bool delimited)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = key[prefix.Length..];
        if (rest.Length == 0) return null;
        if (!delimited) return rest;
        var colon = rest.IndexOf(':');
        return colon < 0 ? rest : rest[..colon];
    }

    private static void ScanPrefix(
        SqliteConnection conn, string prefix, bool withValue,
        Action<string, long, string?> visit)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = withValue
            ? "SELECT key, value, LENGTH(value) FROM cursorDiskKV WHERE key >= $lo AND key < $hi"
            : "SELECT key, LENGTH(value) FROM cursorDiskKV WHERE key >= $lo AND key < $hi";
        cmd.Parameters.AddWithValue("$lo", prefix);
        cmd.Parameters.AddWithValue("$hi", prefix + char.MaxValue);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = r.GetString(0);
            if (withValue)
            {
                var json = r.IsDBNull(1) ? null : r.GetString(1);
                var bytes = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                visit(key, bytes, json);
            }
            else
            {
                var bytes = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                visit(key, bytes, null);
            }
        }
    }

    // ---------------- VACUUM（删除=磁盘也要回来，方案 §0） ----------------

    public sealed record VacuumResult(bool Ok, string? Error, long MainBefore, long MainAfter, long SearchBefore, long SearchAfter, double Seconds)
    {
        public long FreedBytes => (MainBefore - MainAfter) + (SearchBefore - SearchAfter);
    }

    /// <summary>VACUUM 两库（须 Cursor 退出；需要与库同等的临时磁盘空间，期间库锁定不可中断）。</summary>
    public VacuumResult Vacuum()
    {
        EnsureWritable("VACUUM");
        var sw = Stopwatch.StartNew();
        long mBefore = File.Exists(MainDb) ? new FileInfo(MainDb).Length : 0;
        long sBefore = File.Exists(SearchDb) ? new FileInfo(SearchDb).Length : 0;
        try
        {
            VacuumOne(MainDb);
            VacuumOne(SearchDb);
            sw.Stop();
            long mAfter = File.Exists(MainDb) ? new FileInfo(MainDb).Length : 0;
            long sAfter = File.Exists(SearchDb) ? new FileInfo(SearchDb).Length : 0;
            return new VacuumResult(true, null, mBefore, mAfter, sBefore, sAfter, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new VacuumResult(false, ex.Message, mBefore, 0, sBefore, 0, sw.Elapsed.TotalSeconds);
        }
    }

    private static void VacuumOne(string path)
    {
        if (!File.Exists(path)) return;
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;   // 1GB 级库可能要几分钟，不限时
        cmd.CommandText = "VACUUM";
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------

    private static SqliteConnection OpenWrite(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        return conn;
    }

    private static long CountBubbles(SqliteConnection conn, string id)
    {
        // 前缀范围走主键索引；LIKE 拼接表达式会让 SQLite 放弃索引优化（SCAN vs SEARCH）
        using var cmd = conn.CreateCommand();
        var lo = "bubbleId:" + id + ":";
        cmd.CommandText = "SELECT COUNT(*) FROM cursorDiskKV WHERE key >= $lo AND key < $hi";
        cmd.Parameters.AddWithValue("$lo", lo);
        cmd.Parameters.AddWithValue("$hi", lo + char.MaxValue);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>解析 composerHeaders.value：name / 项目路径（workspaceIdentifier）/ 无标题信号。</summary>
    private static (string? Name, string? Project, bool NoTitleSignal, string? ParentId) ParseHeaderValue(string? valueJson)
    {
        if (string.IsNullOrEmpty(valueJson)) return (null, null, true, null);
        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            var root = doc.RootElement;
            string? name = CodexProvider.GetString(root, "name");
            string? project = null;
            if (root.TryGetProperty("workspaceIdentifier", out var ws) && ws.ValueKind == JsonValueKind.Object)
            {
                if (ws.TryGetProperty("uri", out var uri))
                {
                    if (uri.ValueKind == JsonValueKind.Object)
                        project = CodexProvider.GetString(uri, "fsPath");
                    else if (uri.ValueKind == JsonValueKind.String)
                        project = UriToPath(uri.GetString());
                }
            }
            bool draft = root.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
            bool ephemeral = root.TryGetProperty("isEphemeral", out var e) && e.ValueKind == JsonValueKind.True;
            var parentId = CodexProvider.GetString(root, "parentComposerId")
                ?? CodexProvider.GetString(root, "parentId")
                ?? CodexProvider.GetString(root, "parent_composer_id")
                ?? CodexProvider.GetString(root, "parent_id");
            return (name, project, name is null && (draft || ephemeral), parentId);
        }
        catch (JsonException)
        {
            return (null, null, true, null);
        }
    }

    private static string? UriToPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile)
            return u.LocalPath;
        return uri;
    }

    /// <summary>解析 bubble value：type / createdAt / text。坏行返回 (0,"","")。</summary>
    private static (int Type, string Created, string Text) ParseBubble(string valueJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            var root = doc.RootElement;
            int type = root.TryGetProperty("type", out var t) && t.TryGetInt32(out var ti) ? ti : 0;
            string created = CodexProvider.GetString(root, "createdAt") ?? "";
            string text = CodexProvider.GetString(root, "text") ?? "";
            return (type, created, text);
        }
        catch (JsonException)
        {
            return (0, "", "");
        }
    }
}
