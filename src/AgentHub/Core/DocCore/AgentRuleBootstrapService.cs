using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.DocCore;

public sealed class AgentRuleBootstrapService
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private readonly AgentHubConfig _config;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly string _home;
    private readonly string _localData;
    private readonly Action<string>? _log;

    public AgentRuleBootstrapService(AgentHubConfig config, string? userProfile = null,
        string? localDataRoot = null, Action<string>? log = null)
    {
        _config = config;
        _home = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _localData = localDataRoot ?? AgentHubConfig.LocalDataDir;
        _log = log;
    }

    public string SharedRulesPath => Path.Combine(_home, ".agents", "AGENTS.md");
    public string BackupRoot => Path.Combine(_localData, "AgentRuleBackups");
    public string StatePath => Path.Combine(_localData, "agent-rules-state.json");
    public string TransactionPath => Path.Combine(_localData, "agent-rules-transaction.json");
    public bool Enabled => _config.Docs.UnifiedRules;

    public AgentRulesStatus Inspect()
    {
        var libraryRoot = NormalizeLibraryRoot();
        var shared = File.Exists(SharedRulesPath) ? AgentRuleStatus.Current : AgentRuleStatus.Missing;
        var agents = Descriptors().Select(d => InspectAgent(d, libraryRoot)).ToList();
        var hasChanges = agents.Any(x => x.CanWrite && x.Status is AgentRuleStatus.Missing or AgentRuleStatus.NeedsSync);
        var hasConflicts = agents.Any(x => x.Status == AgentRuleStatus.Conflict);
        return new(libraryRoot, Directory.Exists(libraryRoot), SharedRulesPath, shared, agents,
            hasChanges, hasConflicts, Enabled);
    }

    public AgentRulesHub ReadHub()
    {
        var exists = File.Exists(SharedRulesPath);
        return new(SharedRulesPath, exists, Enabled, exists ? ReadText(SharedRulesPath) : "");
    }

    public AgentRulesPreview Preview()
    {
        var libraryRoot = NormalizeLibraryRoot();
        var plan = BuildAgentWritePlan(libraryRoot, createSharedIfMissing: Enabled || !File.Exists(SharedRulesPath));
        return new([], plan.Writes.Where(x => !x.Delete).Select(x => x.Path).ToList(), plan.Skips,
            Path.Combine(BackupRoot, "<transactionId>"));
    }

    public AgentRulesApplyResult Enable()
    {
        if (!_applyGate.Wait(0))
            return new(false, [new("all", false, "正在执行")], BackupRoot);
        try
        {
            var libraryRoot = NormalizeLibraryRoot();
            var plan = BuildAgentWritePlan(libraryRoot, createSharedIfMissing: true);
            var result = CommitPlan(plan);
            if (result.Ok)
            {
                _config.Docs.UnifiedRules = true;
                _config.Save();
            }
            return result;
        }
        finally { _applyGate.Release(); }
    }

    public AgentRulesApplyResult Update()
    {
        if (!Enabled)
            return new(false, [new("all", false, "请先打开统一管理")], BackupRoot);
        if (!_applyGate.Wait(0))
            return new(false, [new("all", false, "正在执行")], BackupRoot);
        try
        {
            return CommitPlan(BuildAgentWritePlan(NormalizeLibraryRoot(), createSharedIfMissing: false));
        }
        finally { _applyGate.Release(); }
    }

    public AgentRulesApplyResult Disable()
    {
        if (!_applyGate.Wait(0))
            return new(false, [new("all", false, "正在执行")], BackupRoot);
        try
        {
            var plan = BuildDisablePlan();
            var result = CommitPlan(plan);
            if (result.Ok)
            {
                _config.Docs.UnifiedRules = false;
                _config.Save();
            }
            return result;
        }
        finally { _applyGate.Release(); }
    }

    public AgentRulesHub WriteHub(string content)
    {
        if (!Enabled) throw new InvalidOperationException("关掉统一管理时不能改共用规则");
        if (!_applyGate.Wait(0)) throw new InvalidOperationException("正在执行");
        try
        {
            BackupHub();
            Directory.CreateDirectory(Path.GetDirectoryName(SharedRulesPath)!);
            AtomicWrite(TextWrite("shared", SharedRulesPath, content));
            return ReadHub();
        }
        finally { _applyGate.Release(); }
    }

    public void OpenHub()
    {
        if (!File.Exists(SharedRulesPath))
            throw new FileNotFoundException("还没有共用规则文件", SharedRulesPath);
        Process.Start(new ProcessStartInfo(SharedRulesPath) { UseShellExecute = true });
    }

    public AgentRulesLibraryResult SetLibrary(string path, bool move)
    {
        if (!_applyGate.Wait(0))
            throw new InvalidOperationException("正在执行");
        try
        {
            var next = DocsSettings.NormalizeLibraryRoot(path);
            var prev = NormalizeLibraryRoot();
            var notes = new List<string>();
            var moved = false;
            if (!PathsEqual(prev, next) && move)
            {
                moved = MoveLibraryFolder(prev, next, "Plans", notes)
                    | MoveLibraryFolder(prev, next, "SandBox", notes);
            }
            Directory.CreateDirectory(next);
            Directory.CreateDirectory(Path.Combine(next, "Plans"));
            Directory.CreateDirectory(Path.Combine(next, "SandBox"));
            if (!PathsEqual(prev, next))
                UpdateHubLibraryLine(next);
            _config.Docs.LibraryRoot = next;
            _config.Save();
            return new(next, moved, notes);
        }
        finally { _applyGate.Release(); }
    }

    public void RecoverPendingTransaction()
    {
        if (!File.Exists(TransactionPath)) return;
        try
        {
            var transaction = JsonSerializer.Deserialize<AgentRuleTransaction>(
                File.ReadAllText(TransactionPath), JsonOptions);
            if (transaction is null) throw new InvalidDataException("事务文件为空");
            if (Rollback(transaction)) _log?.Invoke("[agent-rules] 已恢复上次未完成事务");
            else _log?.Invoke("[agent-rules] 未完成事务存在外部修改，已保留事务文件供人工确认");
        }
        catch (Exception ex) { _log?.Invoke("[agent-rules] 恢复事务失败：" + ex.Message); }
    }

    private AgentRulesApplyResult CommitPlan(WritePlan plan)
    {
        var transactionId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8];
        var backupPath = Path.Combine(BackupRoot, transactionId);
        var transaction = PrepareTransaction(transactionId, backupPath, plan.Writes);
        var results = new List<AgentRuleApplyItem>();
        try
        {
            foreach (var write in plan.Writes)
            {
                if (write.Delete) DeleteTarget(write);
                else AtomicWrite(write);
                results.Add(new(write.AgentId, true, write.Delete ? "已去掉指向" : "已对齐", write.Path));
            }
            SaveManagedState(plan.Writes);
            DeleteTransaction();
        }
        catch (Exception ex)
        {
            var recovered = Rollback(transaction);
            results.Add(new("all", false, (recovered ? "写入失败，已回滚：" : "写入失败，存在需人工确认的冲突：") + ex.Message));
            return new(false, results, backupPath);
        }
        results.AddRange(plan.Skips.Select(x => new AgentRuleApplyItem("skip", false, x)));
        return new(true, results, backupPath);
    }

    private WritePlan BuildAgentWritePlan(string libraryRoot, bool createSharedIfMissing)
    {
        var writes = new List<PlannedWrite>();
        var skips = new List<string>();
        if (createSharedIfMissing && !File.Exists(SharedRulesPath))
            writes.Add(TextWrite("shared", SharedRulesPath, AgentRuleTemplates.RenderShared(libraryRoot)));

        foreach (var descriptor in Descriptors())
        {
            var inspected = InspectAgent(descriptor, libraryRoot);
            if (!inspected.Detected)
            {
                skips.Add($"{inspected.DisplayName}：{inspected.Message}");
                continue;
            }
            if (inspected.Status == AgentRuleStatus.Current) continue;
            if (!inspected.CanWrite)
            {
                skips.Add($"{inspected.DisplayName}：{inspected.Message}");
                continue;
            }
            try
            {
                var write = PlanAgentWrite(descriptor, inspected, libraryRoot);
                if (write is not null) writes.Add(write);
                writes.AddRange(PlanStaleDeletes(descriptor, write?.Path));
            }
            catch (Exception ex) { skips.Add($"{inspected.DisplayName}：{ex.Message}"); }
        }
        return new(writes, skips);
    }

    private WritePlan BuildDisablePlan()
    {
        var writes = new List<PlannedWrite>();
        var skips = new List<string>();
        var libraryRoot = NormalizeLibraryRoot();
        foreach (var descriptor in Descriptors())
        {
            var inspected = InspectAgent(descriptor, libraryRoot);
            if (!inspected.Detected)
            {
                skips.Add($"{inspected.DisplayName}：{inspected.Message}");
                continue;
            }
            if (inspected.Status is AgentRuleStatus.Busy or AgentRuleStatus.NeedsFirstLaunch
                or AgentRuleStatus.Conflict)
            {
                skips.Add($"{inspected.DisplayName}：{inspected.Message}");
                continue;
            }
            try
            {
                if (descriptor.Kind == RuleKind.WorkBuddy)
                {
                    var path = Path.Combine(descriptor.Root, "app", "app-config.json");
                    if (File.Exists(path))
                        writes.Add(WorkBuddyWrite(path, ""));
                    continue;
                }
                foreach (var file in PointerFiles(descriptor).Distinct(StringComparer.OrdinalIgnoreCase))
                    writes.Add(DeleteWrite(descriptor.Id, file));
            }
            catch (Exception ex) { skips.Add($"{inspected.DisplayName}：{ex.Message}"); }
        }
        return new(writes, skips);
    }

    private PlannedWrite? PlanAgentWrite(Descriptor descriptor, AgentRuleItem item, string libraryRoot)
    {
        if (descriptor.Kind == RuleKind.WorkBuddy)
        {
            var path = Path.Combine(descriptor.Root, "app", "app-config.json");
            return WorkBuddyWrite(path, AgentRuleTemplates.RenderReference("workbuddy", libraryRoot));
        }
        var pathWrite = CanonicalPath(descriptor);
        var body = descriptor.Kind == RuleKind.Cursor
            ? AgentRuleTemplates.RenderCursor(libraryRoot)
            : AgentRuleTemplates.RenderReference(descriptor.Id, libraryRoot);
        return TextWrite(descriptor.Id, pathWrite, body);
    }

    private IEnumerable<PlannedWrite> PlanStaleDeletes(Descriptor descriptor, string? keepPath)
    {
        if (descriptor.Kind is not (RuleKind.Cursor or RuleKind.Trae)) yield break;
        foreach (var file in PointerFiles(descriptor))
        {
            if (keepPath is not null && PathsEqual(file, keepPath)) continue;
            yield return DeleteWrite(descriptor.Id, file);
        }
    }

    private IEnumerable<string> PointerFiles(Descriptor descriptor)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = LoadManagedState();
        if (state.Agents.TryGetValue(descriptor.Id, out var managed) && File.Exists(managed.Path))
        {
            seen.Add(managed.Path);
            yield return managed.Path;
        }
        if (descriptor.Kind == RuleKind.Markdown)
        {
            var fixedPath = Path.Combine(descriptor.Root, "AGENTS.md");
            if (seen.Add(fixedPath) && File.Exists(fixedPath)
                && AgentRuleTemplates.LooksLikePointer(ReadText(fixedPath)))
                yield return fixedPath;
            yield break;
        }
        var dir = SearchDir(descriptor);
        if (dir is null || !Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, SearchPattern(descriptor), SearchOption.TopDirectoryOnly))
        {
            if (!seen.Add(file)) continue;
            if (AgentRuleTemplates.LooksLikePointer(ReadText(file)))
                yield return file;
        }
    }

    private AgentRuleItem InspectAgent(Descriptor descriptor, string libraryRoot)
    {
        if (!Directory.Exists(descriptor.Root))
            return Item(descriptor, false, AgentRuleStatus.NotDetected, null, "未发现", false);
        return descriptor.Kind switch
        {
            RuleKind.WorkBuddy => InspectWorkBuddy(descriptor, libraryRoot),
            _ => InspectPointerFile(descriptor, libraryRoot),
        };
    }

    private AgentRuleItem InspectPointerFile(Descriptor descriptor, string libraryRoot)
    {
        var canonical = CanonicalPath(descriptor);
        List<string> pointers;
        try { pointers = PointerFiles(descriptor).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex)
        {
            return Item(descriptor, true, AgentRuleStatus.Conflict, canonical, "规则无法读取：" + ex.Message, false);
        }
        var state = LoadManagedState();
        var statePath = state.Agents.TryGetValue(descriptor.Id, out var managed) && File.Exists(managed.Path)
            ? managed.Path : null;
        if (statePath is null && pointers.Count > 1)
            return Item(descriptor, true, AgentRuleStatus.Conflict, canonical, "冲突", false);
        var path = statePath ?? (pointers.Count == 1 ? pointers[0] : canonical);
        if (!File.Exists(path))
            return Item(descriptor, true, AgentRuleStatus.Missing, path, "待更新", true);
        var expected = descriptor.Kind == RuleKind.Cursor
            ? AgentRuleTemplates.RenderCursor(libraryRoot)
            : AgentRuleTemplates.RenderReference(descriptor.Id, libraryRoot);
        return AgentRuleTemplates.SameText(ReadText(path), expected)
            ? Item(descriptor, true, AgentRuleStatus.Current, path, "已对齐", false)
            : Item(descriptor, true, AgentRuleStatus.NeedsSync, path, "待更新", true);
    }

    private AgentRuleItem InspectWorkBuddy(Descriptor descriptor, string libraryRoot)
    {
        var path = Path.Combine(descriptor.Root, "app", "app-config.json");
        if (!File.Exists(path))
            return Item(descriptor, true, AgentRuleStatus.NeedsFirstLaunch, path, "需先启动", false);
        if (IsWorkBuddyRunning())
            return Item(descriptor, true, AgentRuleStatus.Busy, path, "使用中", false);
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var prompt = root?["personalization"]?["customPrompt"]?.GetValue<string>() ?? "";
            var expected = AgentRuleTemplates.RenderReference("workbuddy", libraryRoot);
            if (prompt.Length == 0)
                return Item(descriptor, true, AgentRuleStatus.Missing, path, "待更新", true);
            return AgentRuleTemplates.SameText(prompt, expected)
                ? Item(descriptor, true, AgentRuleStatus.Current, path, "已对齐", false)
                : Item(descriptor, true, AgentRuleStatus.NeedsSync, path, "待更新", true);
        }
        catch (Exception)
        {
            return Item(descriptor, true, AgentRuleStatus.Conflict, path, "解析失败", false);
        }
    }

    private PlannedWrite WorkBuddyWrite(string path, string prompt)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("配置不是 JSON 对象");
        var personalization = root["personalization"] as JsonObject ?? new JsonObject();
        root["personalization"] = personalization;
        personalization["customPrompt"] = prompt;
        return new( "workbuddy", path, Utf8.GetBytes(root.ToJsonString(JsonOptions) + Environment.NewLine), HashOrNull(path));
    }

    private void BackupHub()
    {
        if (!File.Exists(SharedRulesPath)) return;
        var dir = Path.Combine(BackupRoot, "hub-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"));
        Directory.CreateDirectory(dir);
        File.Copy(SharedRulesPath, Path.Combine(dir, "AGENTS.md"), overwrite: true);
    }

    private void UpdateHubLibraryLine(string libraryRoot)
    {
        if (!File.Exists(SharedRulesPath)) return;
        var original = ReadText(SharedRulesPath);
        var next = AgentRuleTemplates.ReplaceLibraryLine(original, libraryRoot);
        if (next is null || AgentRuleTemplates.SameText(original, next)) return;
        BackupHub();
        AtomicWrite(TextWrite("shared", SharedRulesPath, next));
    }

    private static bool MoveLibraryFolder(string fromRoot, string toRoot, string name, List<string> notes)
    {
        var src = ResolveExistingFolder(fromRoot, name);
        if (src is null) return false;
        var dest = Path.Combine(toRoot, name);
        if (PathsEqual(src, dest)) return false;
        Directory.CreateDirectory(toRoot);
        if (!Directory.Exists(dest))
        {
            Directory.Move(src, dest);
            notes.Add($"已搬走 {name}");
            return true;
        }

        var skipped = 0;
        var moved = MergeMove(src, dest, ref skipped);
        TryDeleteEmpty(src);
        if (skipped > 0) notes.Add($"{name} 有 {skipped} 个同名文件已跳过");
        if (moved) notes.Add($"已搬走 {name}");
        else if (skipped == 0) notes.Add($"{name} 目标已有，跳过");
        return moved;
    }

    private static bool MergeMove(string src, string dest, ref int skipped)
    {
        Directory.CreateDirectory(dest);
        var moved = false;
        foreach (var file in Directory.GetFiles(src))
        {
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            if (File.Exists(destFile))
            {
                skipped++;
                continue;
            }
            File.Move(file, destFile);
            moved = true;
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            if (MergeMove(dir, Path.Combine(dest, Path.GetFileName(dir)), ref skipped))
                moved = true;
            TryDeleteEmpty(dir);
        }
        return moved;
    }

    private static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception) { }
    }

    private static string? ResolveExistingFolder(string root, string name)
    {
        var exact = Path.Combine(root, name);
        if (Directory.Exists(exact)) return exact;
        if (!name.Equals("SandBox", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var dir in Directory.Exists(root) ? Directory.EnumerateDirectories(root) : [])
        {
            if (string.Equals(Path.GetFileName(dir), "Sandbox", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(dir), "SandBox", StringComparison.OrdinalIgnoreCase))
                return dir;
        }
        return null;
    }

    private string CanonicalPath(Descriptor descriptor) => descriptor.Kind switch
    {
        RuleKind.Cursor => Path.Combine(descriptor.Root, "rules", "AGENTS.mdc"),
        RuleKind.Trae => Path.Combine(descriptor.Root, "user_rules", "AGENTS.md"),
        RuleKind.WorkBuddy => Path.Combine(descriptor.Root, "app", "app-config.json"),
        _ => Path.Combine(descriptor.Root, "AGENTS.md"),
    };

    private static string? SearchDir(Descriptor descriptor) => descriptor.Kind switch
    {
        RuleKind.Cursor => Path.Combine(descriptor.Root, "rules"),
        RuleKind.Trae => Path.Combine(descriptor.Root, "user_rules"),
        RuleKind.Markdown => descriptor.Root,
        _ => null,
    };

    private static string SearchPattern(Descriptor descriptor) => descriptor.Kind switch
    {
        RuleKind.Cursor => "*.mdc",
        _ => "*.md",
    };

    private string NormalizeLibraryRoot() => DocsSettings.NormalizeLibraryRoot(_config.Docs.LibraryRoot);

    private IEnumerable<Descriptor> Descriptors()
    {
        yield return new("codex", "Codex", Path.Combine(_home, ".codex"), RuleKind.Markdown);
        yield return new("cursor", "Cursor", Path.Combine(_home, ".cursor"), RuleKind.Cursor);
        yield return new("dsh", "DSH", Path.Combine(_home, ".dsh"), RuleKind.Markdown);
        yield return new("trae", "Trae", Path.Combine(_home, ".trae-cn"), RuleKind.Trae);
        yield return new("workbuddy", "WorkBuddy", Path.Combine(_home, ".workbuddy"), RuleKind.WorkBuddy);
        yield return new("zcode", "ZCode", Path.Combine(_home, ".zcode"), RuleKind.Markdown);
    }

    private static AgentRuleItem Item(Descriptor d, bool detected, AgentRuleStatus status,
        string? path, string message, bool canWrite) => new(d.Id, d.DisplayName, detected, status, path, message, canWrite);

    private static bool IsWorkBuddyRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                    if (process.ProcessName.Contains("workbuddy", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch (Exception) { return false; }
    }

    private static string ReadText(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
    private static string? HashOrNull(string path) => File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;
    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static PlannedWrite TextWrite(string agentId, string path, string content) =>
        new(agentId, path, Utf8.GetBytes(content), HashOrNull(path));

    private static PlannedWrite DeleteWrite(string agentId, string path) =>
        new(agentId, path, [], HashOrNull(path), Delete: true);

    private AgentRuleTransaction PrepareTransaction(string transactionId, string backupRoot,
        IReadOnlyList<PlannedWrite> writes)
    {
        if (File.Exists(TransactionPath))
            throw new InvalidOperationException("检测到未恢复的规则事务，请重启 AgentHub 后重试");
        var transaction = new AgentRuleTransaction
        {
            TransactionId = transactionId,
            BackupRoot = backupRoot,
        };
        if (writes.Count == 0) return transaction;
        Directory.CreateDirectory(backupRoot);
        for (var i = 0; i < writes.Count; i++)
        {
            var write = writes[i];
            ValidateTarget(write);
            var currentHash = HashOrNull(write.Path);
            if (!string.Equals(currentHash, write.BeforeHash, StringComparison.Ordinal))
                throw new IOException($"文件在扫描后发生变化：{write.Path}");
            string? backup = null;
            if (write.BeforeHash is not null)
            {
                backup = Path.Combine(backupRoot, $"{i:D2}-{write.AgentId}-{Path.GetFileName(write.Path)}");
                File.Copy(write.Path, backup);
                if (!string.Equals(HashOrNull(backup), write.BeforeHash, StringComparison.Ordinal))
                    throw new IOException($"备份校验失败：{write.Path}");
            }
            transaction.Writes.Add(new()
            {
                AgentId = write.AgentId,
                Path = write.Path,
                BeforeHash = write.BeforeHash,
                WrittenHash = write.Delete ? "" : Hash(write.Content),
                BackupPath = backup,
                Delete = write.Delete,
            });
        }
        SaveJsonAtomic(TransactionPath, transaction);
        return transaction;
    }

    private void ValidateTarget(PlannedWrite write)
    {
        var allowedRoot = write.AgentId == "shared"
            ? Path.Combine(_home, ".agents")
            : Descriptors().First(x => x.Id == write.AgentId).Root;
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(write.Path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"写入目标超出白名单目录：{target}");
    }

    private static void AtomicWrite(PlannedWrite write)
    {
        var currentHash = HashOrNull(write.Path);
        if (!string.Equals(currentHash, write.BeforeHash, StringComparison.Ordinal))
            throw new IOException($"文件在扫描后发生变化：{write.Path}");
        Directory.CreateDirectory(Path.GetDirectoryName(write.Path)!);
        var temp = write.Path + ".agenthub-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, write.Content);
            if (Hash(File.ReadAllBytes(temp)) != Hash(write.Content)) throw new IOException("临时文件校验失败");
            if (!string.Equals(HashOrNull(write.Path), write.BeforeHash, StringComparison.Ordinal))
                throw new IOException($"文件在写入前发生变化：{write.Path}");
            File.Move(temp, write.Path, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { } }
    }

    private static void DeleteTarget(PlannedWrite write)
    {
        if (!File.Exists(write.Path)) return;
        var currentHash = HashOrNull(write.Path);
        if (!string.Equals(currentHash, write.BeforeHash, StringComparison.Ordinal))
            throw new IOException($"文件在扫描后发生变化：{write.Path}");
        File.Delete(write.Path);
    }

    private bool Rollback(AgentRuleTransaction transaction)
    {
        var allRecovered = true;
        foreach (var write in transaction.Writes.AsEnumerable().Reverse())
        {
            try
            {
                var current = HashOrNull(write.Path);
                if (write.Delete)
                {
                    if (write.BackupPath is null) { if (File.Exists(write.Path)) File.Delete(write.Path); continue; }
                    if (!File.Exists(write.BackupPath)
                        || !string.Equals(HashOrNull(write.BackupPath), write.BeforeHash, StringComparison.Ordinal))
                    {
                        allRecovered = false;
                        continue;
                    }
                    if (current is null || string.Equals(current, write.WrittenHash, StringComparison.Ordinal))
                        File.Copy(write.BackupPath, write.Path, overwrite: true);
                    else if (!string.Equals(current, write.BeforeHash, StringComparison.Ordinal))
                        allRecovered = false;
                    continue;
                }
                if (string.Equals(current, write.BeforeHash, StringComparison.Ordinal)) continue;
                if (!string.Equals(current, write.WrittenHash, StringComparison.Ordinal))
                {
                    allRecovered = false;
                    continue;
                }
                if (write.BackupPath is null) File.Delete(write.Path);
                else
                {
                    if (!File.Exists(write.BackupPath)
                        || !string.Equals(HashOrNull(write.BackupPath), write.BeforeHash, StringComparison.Ordinal))
                    {
                        allRecovered = false;
                        continue;
                    }
                    File.Copy(write.BackupPath, write.Path, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                allRecovered = false;
                _log?.Invoke($"[agent-rules] 回滚失败 {write.Path}：{ex.Message}");
            }
        }
        if (allRecovered)
        {
            try { DeleteTransaction(); }
            catch (Exception ex)
            {
                allRecovered = false;
                _log?.Invoke("[agent-rules] 删除已恢复事务文件失败：" + ex.Message);
            }
        }
        return allRecovered;
    }

    private void SaveManagedState(IEnumerable<PlannedWrite> writes)
    {
        var state = LoadManagedState();
        foreach (var write in writes.Where(x => x.AgentId != "shared"))
        {
            if (write.Delete)
            {
                if (state.Agents.TryGetValue(write.AgentId, out var cur)
                    && PathsEqual(cur.Path, write.Path))
                    state.Agents.Remove(write.AgentId);
                continue;
            }
            state.Agents[write.AgentId] = new()
            {
                Path = write.Path,
                WrittenHash = Hash(write.Content),
                LastSyncedUtc = DateTime.UtcNow,
            };
        }
        SaveJsonAtomic(StatePath, state);
    }

    private AgentRuleStateFile LoadManagedState()
    {
        try
        {
            if (!File.Exists(StatePath)) return new();
            var state = JsonSerializer.Deserialize<AgentRuleStateFile>(File.ReadAllText(StatePath), JsonOptions) ?? new();
            state.Agents = new Dictionary<string, AgentRuleStateEntry>(state.Agents, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex)
        {
            _log?.Invoke("[agent-rules] 状态文件读取失败：" + ex.Message);
            return new();
        }
    }

    private static void SaveJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions), Utf8);
            File.Move(temp, path, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { } }
    }

    private void DeleteTransaction()
    {
        if (File.Exists(TransactionPath)) File.Delete(TransactionPath);
    }

    private sealed record Descriptor(string Id, string DisplayName, string Root, RuleKind Kind);
    private enum RuleKind { Markdown, Cursor, Trae, WorkBuddy }
    private sealed record PlannedWrite(string AgentId, string Path, byte[] Content, string? BeforeHash, bool Delete = false);
    private sealed record WritePlan(List<PlannedWrite> Writes, List<string> Skips);
}
