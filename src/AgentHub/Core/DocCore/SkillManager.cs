using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.DocCore;

public sealed class SkillManager
{
    private static readonly Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex Frontmatter = new(@"^---\s*\r?\n([\s\S]*?)\r?\n---", RegexOptions.Compiled);
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules",
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SkillsCliUpdater _cli;
    private readonly Action<string>? _log;
    private readonly string _userProfile;
    private readonly string _localDataRoot;

    public SkillManager(SkillsCliUpdater? cli = null, Action<string>? log = null,
        string? userProfile = null, string? localDataRoot = null)
    {
        _cli = cli ?? new SkillsCliUpdater();
        _log = log;
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _localDataRoot = localDataRoot ?? AgentHubConfig.LocalDataDir;
    }

    public string ActiveRoot => Path.Combine(_userProfile, ".agents", "skills");
    public string StoreRoot => Path.Combine(_localDataRoot, "SkillStore");
    public string StatePath => Path.Combine(_localDataRoot, "skills-state.json");
    public string StagingRoot => Path.Combine(_localDataRoot, "SkillStaging");
    public string BackupRoot => Path.Combine(_localDataRoot, "SkillBackups");
    public string LegacyStoreRoot => Path.Combine(_userProfile, ".agents", "All-Skills");
    private string ActiveStagingRoot => Path.Combine(_userProfile, ".agents", ".agenthub-staging");

    public SkillsCliStatus CliStatus => _cli.Detect();

    public IReadOnlyList<ManagedSkillItem> List(string? query = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSkillDirs(ActiveRoot, names);
        AddSkillDirs(StoreRoot, names);
        var state = LoadState();
        var items = new List<ManagedSkillItem>();
        foreach (var name in names)
        {
            try
            {
                var item = InspectOne(name, state);
                if (item is null) continue;
                if (!string.IsNullOrWhiteSpace(query)
                    && !item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && !item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                items.Add(item);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[skills] 扫描 {name} 失败：{ex.Message}");
            }
        }
        return items.OrderBy(i => StateOrder(i.State)).ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public SkillOperationResult Manage(string name) => Locked(name, () =>
    {
        GuardName(name);
        var active = Path.Combine(ActiveRoot, name);
        var store = Path.Combine(StoreRoot, name);
        EnsureRealSkill(active, "启用目录里没有可收进仓库的技能");
        if (Directory.Exists(store))
        {
            EnsureRealSkill(store, "持久仓中的同名目录不是有效真实 Skill");
            if (!string.Equals(HashDirectory(active), HashDirectory(store), StringComparison.Ordinal))
                return new(false, "持久仓已有不同内容，未覆盖任一侧");
        }
        else
        {
            CopyIntoMissing(active, store, StagingRoot);
        }

        var state = LoadState();
        state.Skills[name] = new SkillStateEntry
        {
            Enabled = true,
            LastDeployedHash = HashDirectory(active),
        };
        SaveState(state);
        return Success(name, "已收进仓库");
    });

    public SkillOperationResult Enable(string name) => Locked(name, () =>
    {
        GuardName(name);
        var store = Path.Combine(StoreRoot, name);
        var active = Path.Combine(ActiveRoot, name);
        EnsureRealSkill(store, "持久仓中没有这个 Skill");
        if (PathExists(active)) return new(false, "启用目录已有同名项，未覆盖");
        CopyIntoMissing(store, active, ActiveStagingRoot);
        var hash = HashDirectory(active);
        var state = LoadState();
        state.Skills[name] = new SkillStateEntry { Enabled = true, LastDeployedHash = hash };
        SaveState(state);
        return Success(name, "已启用真实副本");
    });

    public SkillOperationResult Disable(string name) => Locked(name, () =>
    {
        GuardName(name);
        var active = Path.Combine(ActiveRoot, name);
        var state = LoadState();
        if (!state.Skills.TryGetValue(name, out var entry) || !entry.Enabled)
            return new(false, "该 Skill 不是 AgentHub 部署的启用副本");
        EnsureRealSkill(active, "启用项不是可安全停用的真实 Skill");
        var current = HashDirectory(active);
        if (string.IsNullOrEmpty(entry.LastDeployedHash)
            || !string.Equals(current, entry.LastDeployedHash, StringComparison.Ordinal))
            return new(false, "启用副本有本地修改，请先选择保留本地或恢复仓库");
        Directory.Delete(active, recursive: true);
        entry.Enabled = false;
        SaveState(state);
        return Success(name, "已停用，持久仓仍保留");
    });

    public SkillOperationResult ResolveModified(string name, ModifiedResolution resolution) => Locked(name, () =>
    {
        GuardName(name);
        var active = Path.Combine(ActiveRoot, name);
        var store = Path.Combine(StoreRoot, name);
        EnsureRealSkill(active, "启用副本不存在或不是有效真实 Skill");
        EnsureRealSkill(store, "持久仓不存在或不是有效真实 Skill");
        Directory.CreateDirectory(BackupRoot);
        var sourceToBackup = resolution == ModifiedResolution.KeepLocalAsStore ? store : active;
        var backup = Path.Combine(BackupRoot, $"{name}-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        CopyIntoMissing(sourceToBackup, backup, StagingRoot);
        if (resolution == ModifiedResolution.KeepLocalAsStore)
            ReplaceDirectory(active, store, StagingRoot);
        else
            ReplaceDirectory(store, active, ActiveStagingRoot);

        var hash = HashDirectory(active);
        var state = LoadState();
        state.Skills[name] = new SkillStateEntry { Enabled = true, LastDeployedHash = hash };
        SaveState(state);
        return Success(name, resolution == ModifiedResolution.KeepLocalAsStore
            ? "已保留本地版本并备份旧仓库"
            : "已恢复仓库版本并备份本地版本");
    });

    public LegacySkillStatus InspectLegacy()
    {
        var errors = new List<string>();
        var links = LegacyLinks(errors).ToList();
        var stores = EnumerateRealSkills(LegacyStoreRoot).ToList();
        var canClean = stores.Count > 0 && links.Count == 0;
        foreach (var legacy in stores)
        {
            var current = Path.Combine(StoreRoot, Path.GetFileName(legacy));
            try
            {
                if (!Directory.Exists(current) || IsReparsePoint(current)
                    || HashDirectory(legacy) != HashDirectory(current))
                    canClean = false;
            }
            catch (Exception ex)
            {
                canClean = false;
                errors.Add($"{Path.GetFileName(legacy)}：{ex.Message}");
            }
        }
        return new(links.Count, stores.Count, canClean, errors);
    }

    public SkillBatchResult MigrateLegacy()
    {
        var migrated = 0;
        var skipped = 0;
        var errors = new List<string>();
        foreach (var legacy in EnumerateRealSkills(LegacyStoreRoot))
        {
            var name = Path.GetFileName(legacy);
            try
            {
                var store = Path.Combine(StoreRoot, name);
                if (!Directory.Exists(store)) CopyIntoMissing(legacy, store, StagingRoot);
                else if (IsReparsePoint(store) || HashDirectory(legacy) != HashDirectory(store))
                    throw new InvalidOperationException("新旧持久仓内容冲突");
                else skipped++;
            }
            catch (Exception ex) { errors.Add($"{name}：{ex.Message}"); }
        }

        foreach (var (name, link, target) in LegacyLinks(errors).ToList())
        {
            try
            {
                var store = Path.Combine(StoreRoot, name);
                if (!Directory.Exists(store)) throw new InvalidOperationException("新持久仓复制失败");
                ReplaceLinkWithDirectory(link, target);
                var hash = HashDirectory(link);
                var state = LoadState();
                state.Skills[name] = new SkillStateEntry { Enabled = true, LastDeployedHash = hash };
                SaveState(state);
                migrated++;
            }
            catch (Exception ex) { errors.Add($"{name}：{ex.Message}"); }
        }
        return new(migrated, skipped, errors);
    }

    public SkillBatchResult CleanLegacyStore()
    {
        var status = InspectLegacy();
        if (!status.CanClean) return new(0, 0, ["旧仓仍有引用、冲突或未完成迁移"]);
        var removed = 0;
        var skipped = 0;
        var errors = new List<string>();
        foreach (var legacy in EnumerateRealSkills(LegacyStoreRoot).ToList())
        {
            var name = Path.GetFileName(legacy);
            try
            {
                var current = Path.Combine(StoreRoot, name);
                if (HashDirectory(legacy) != HashDirectory(current)) { skipped++; continue; }
                Directory.Delete(legacy, true);
                removed++;
            }
            catch (Exception ex) { errors.Add($"{name}：{ex.Message}"); }
        }
        return new(removed, skipped, errors);
    }

    public async Task<SkillBatchResult> UpdateAsync(IReadOnlyList<string>? names, CancellationToken cancellationToken)
    {
        var available = List().Where(x => x.CanUpdate).ToList();
        if (names is { Count: > 0 })
        {
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            available = available.Where(x => wanted.Contains(x.Name)).ToList();
        }
        var updated = 0;
        var skipped = 0;
        var errors = new List<string>();
        foreach (var item in available)
        {
            var result = await UpdateOneAsync(item.Name, cancellationToken);
            if (result.Ok) updated++;
            else errors.Add($"{item.Name}：{result.Message}");
        }
        if (names is { Count: > 0 }) skipped = names.Count - available.Count;
        else skipped = List().Count(x => !x.CanUpdate);
        return new(updated, skipped, errors);
    }

    public void RecoverStaging()
    {
        RecoverInterruptedReplacements(ActiveRoot);
        RecoverInterruptedReplacements(StoreRoot);
        foreach (var root in new[] { StagingRoot, ActiveStagingRoot })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try { Directory.Delete(dir, true); }
                catch (Exception ex) { _log?.Invoke($"[skills] 清理暂存失败 {dir}：{ex.Message}"); }
            }
        }
    }

    private async Task<SkillOperationResult> UpdateOneAsync(string name, CancellationToken cancellationToken)
    {
        GuardName(name);
        var gate = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken)) return new(false, "该 Skill 正在执行其它操作");
        try
        {
            var item = List().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item?.CanUpdate != true) return new(false, "只有已启用且未修改的托管 Skill 可以更新");
            var active = Path.Combine(ActiveRoot, name);
            var store = Path.Combine(StoreRoot, name);
            Directory.CreateDirectory(BackupRoot);
            var backup = Path.Combine(BackupRoot, $"{name}-before-update-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            CopyIntoMissing(active, backup, StagingRoot);
            var cliResult = await _cli.UpdateAsync(name, cancellationToken);
            if (!cliResult.Ok)
            {
                if (IsReparsePoint(active)) ReplaceLinkWithDirectory(active, backup);
                else ReplaceDirectory(backup, active, ActiveStagingRoot);
                return new(false, cliResult.Output);
            }
            if (IsReparsePoint(active)) MaterializeLink(active);
            EnsureRealSkill(active, "npm 更新后没有得到有效真实 Skill");
            ReplaceDirectory(active, store, StagingRoot);
            var hash = HashDirectory(active);
            var state = LoadState();
            state.Skills[name] = new SkillStateEntry
            {
                Enabled = true,
                LastDeployedHash = hash,
                LastUpdatedUtc = DateTime.UtcNow,
            };
            SaveState(state);
            return Success(name, cliResult.Output);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
        finally { gate.Release(); }
    }

    private void MaterializeLink(string link)
    {
        var target = ResolveLink(link) ?? throw new InvalidOperationException("npm 生成了无法解析的联接");
        ReplaceLinkWithDirectory(link, target);
    }

    private void ReplaceLinkWithDirectory(string link, string source)
    {
        if (!IsReparsePoint(link)) throw new InvalidOperationException("替换目标不是联接");
        var staging = NewStaging(ActiveStagingRoot);
        var oldLink = link + ".agenthub-oldlink-" + Guid.NewGuid().ToString("N");
        var switched = false;
        try
        {
            CopyVerified(source, staging);
            Directory.Move(link, oldLink);
            try
            {
                Directory.Move(staging, link);
                switched = true;
            }
            catch
            {
                if (!PathExists(link) && PathExists(oldLink)) Directory.Move(oldLink, link);
                throw;
            }
            Directory.Delete(oldLink);
        }
        finally
        {
            TryDeleteRealDirectory(staging);
            if (!switched && !PathExists(link) && PathExists(oldLink))
            {
                try { Directory.Move(oldLink, link); } catch (Exception) { }
            }
        }
    }

    private SkillOperationResult Locked(string name, Func<SkillOperationResult> action)
    {
        GuardName(name);
        var gate = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) return new(false, "该 Skill 正在执行其它操作");
        try { return action(); }
        catch (Exception ex) { return new(false, ex.Message); }
        finally { gate.Release(); }
    }

    private SkillOperationResult Success(string name, string message) =>
        new(true, message, List().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    private ManagedSkillItem? InspectOne(string name, SkillStateFile state)
    {
        var active = Path.Combine(ActiveRoot, name);
        var store = Path.Combine(StoreRoot, name);
        var activeExists = PathExists(active);
        var storeExists = PathExists(store);
        var activeLink = activeExists && IsReparsePoint(active);
        var storeLink = storeExists && IsReparsePoint(store);
        ManagedSkillState status;

        if (storeLink) status = ManagedSkillState.Conflict;
        else if (activeLink)
        {
            var expected = Path.Combine(LegacyStoreRoot, name);
            status = SamePath(ResolveLink(active), expected)
                ? ManagedSkillState.LegacyLink
                : ManagedSkillState.Conflict;
        }
        else if (storeExists && !activeExists) status = ManagedSkillState.Disabled;
        else if (!storeExists && activeExists) status = ManagedSkillState.External;
        else if (storeExists && activeExists)
        {
            if (!File.Exists(Path.Combine(store, "SKILL.md")) || !File.Exists(Path.Combine(active, "SKILL.md")))
                status = ManagedSkillState.Conflict;
            else if (!state.Skills.TryGetValue(name, out var entry) || string.IsNullOrEmpty(entry.LastDeployedHash))
                status = ManagedSkillState.Conflict;
            else
                status = HashDirectory(active) == entry.LastDeployedHash
                    ? ManagedSkillState.Enabled
                    : ManagedSkillState.Modified;
        }
        else return null;

        var preview = storeExists && !storeLink && File.Exists(Path.Combine(store, "SKILL.md"))
            ? Path.Combine(store, "SKILL.md")
            : Path.Combine(active, "SKILL.md");
        var (display, description) = ReadMetadata(preview, name);
        var modified = File.Exists(preview) ? File.GetLastWriteTimeUtc(preview) : DateTime.MinValue;
        var cliAvailable = CliStatus.Available;
        var canManage = status == ManagedSkillState.External;
        if (status == ManagedSkillState.Conflict && activeExists && storeExists && !activeLink && !storeLink)
        {
            try
            {
                canManage = File.Exists(Path.Combine(active, "SKILL.md"))
                    && File.Exists(Path.Combine(store, "SKILL.md"))
                    && HashDirectory(active) == HashDirectory(store);
            }
            catch (Exception) { canManage = false; }
        }
        return new(name, display, description, status, preview,
            activeExists ? active : null, storeExists ? store : null, modified,
            status == ManagedSkillState.Disabled,
            status == ManagedSkillState.Enabled,
            canManage,
            status == ManagedSkillState.Enabled && cliAvailable);
    }

    private static int StateOrder(ManagedSkillState state) => state switch
    {
        ManagedSkillState.Enabled => 0,
        ManagedSkillState.Modified => 1,
        ManagedSkillState.LegacyLink => 2,
        ManagedSkillState.External => 3,
        ManagedSkillState.Conflict => 4,
        _ => 5,
    };

    private static void AddSkillDirs(string root, HashSet<string> names)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (!Path.GetFileName(dir).Contains(".agenthub-old", StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(Path.Combine(dir, "SKILL.md")) || IsReparsePoint(dir)))
                    names.Add(Path.GetFileName(dir));
        }
        catch (Exception) { }
    }

    private static IEnumerable<string> EnumerateRealSkills(string root)
    {
        if (!Directory.Exists(root)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch (Exception) { yield break; }
        foreach (var dir in dirs)
            if (!IsReparsePoint(dir) && File.Exists(Path.Combine(dir, "SKILL.md")))
                yield return dir;
    }

    private IEnumerable<(string Name, string Link, string Target)> LegacyLinks(List<string> errors)
    {
        if (!Directory.Exists(ActiveRoot)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(ActiveRoot); }
        catch (Exception ex) { errors.Add(ex.Message); yield break; }
        foreach (var link in dirs)
        {
            if (!IsReparsePoint(link)) continue;
            var name = Path.GetFileName(link);
            var target = ResolveLink(link);
            var expected = Path.Combine(LegacyStoreRoot, name);
            if (target is not null && SamePath(target, expected) && File.Exists(Path.Combine(target, "SKILL.md")))
                yield return (name, link, target);
        }
    }

    private static void CopyIntoMissing(string source, string destination, string stagingRoot)
    {
        if (PathExists(destination)) throw new IOException("目标已存在");
        var staging = NewStaging(stagingRoot);
        try
        {
            CopyVerified(source, staging);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
        }
        finally { TryDeleteRealDirectory(staging); }
    }

    private static void ReplaceDirectory(string source, string destination, string stagingRoot)
    {
        var staging = NewStaging(stagingRoot);
        string? old = null;
        try
        {
            CopyVerified(source, staging);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (PathExists(destination))
            {
                if (IsReparsePoint(destination)) throw new InvalidOperationException("拒绝替换重解析点");
                old = destination + ".agenthub-old-" + Guid.NewGuid().ToString("N");
                Directory.Move(destination, old);
            }
            try { Directory.Move(staging, destination); }
            catch
            {
                if (old is not null && !Directory.Exists(destination)) Directory.Move(old, destination);
                throw;
            }
            if (old is not null) TryDeleteRealDirectory(old);
        }
        finally { TryDeleteRealDirectory(staging); }
    }

    private static string NewStaging(string root)
    {
        Directory.CreateDirectory(root);
        return Path.Combine(root, Guid.NewGuid().ToString("N"));
    }

    private void RecoverInterruptedReplacements(string root)
    {
        if (!Directory.Exists(root)) return;
        string[] leftovers;
        try { leftovers = Directory.GetDirectories(root).Where(x => x.Contains(".agenthub-old", StringComparison.OrdinalIgnoreCase)).ToArray(); }
        catch (Exception ex) { _log?.Invoke($"[skills] 扫描替换残留失败 {root}：{ex.Message}"); return; }
        foreach (var old in leftovers)
        {
            try
            {
                var marker = old.IndexOf(".agenthub-oldlink-", StringComparison.OrdinalIgnoreCase);
                var isOldLink = marker >= 0;
                if (!isOldLink) marker = old.IndexOf(".agenthub-old-", StringComparison.OrdinalIgnoreCase);
                if (marker < 0) continue;
                var destination = old[..marker];
                if (!PathExists(destination))
                {
                    Directory.Move(old, destination);
                    _log?.Invoke($"[skills] 已恢复中断前目录：{destination}");
                }
                else if (isOldLink && IsReparsePoint(old))
                {
                    Directory.Delete(old);
                }
                else
                {
                    _log?.Invoke($"[skills] 保留待确认的替换前副本：{old}");
                }
            }
            catch (Exception ex) { _log?.Invoke($"[skills] 恢复替换残留失败 {old}：{ex.Message}"); }
        }
    }

    private static string CopyVerified(string source, string destination)
    {
        EnsureRealSkill(source, "复制源不是有效真实 Skill");
        CopyTree(source, destination);
        if (!File.Exists(Path.Combine(destination, "SKILL.md")))
            throw new InvalidDataException("复制结果缺少 SKILL.md");
        var sourceHash = HashDirectory(source);
        var destinationHash = HashDirectory(destination);
        if (sourceHash != destinationHash) throw new IOException("复制校验失败");
        return destinationHash;
    }

    private static void CopyTree(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("复制源包含重解析点");
        Directory.CreateDirectory(destination);
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Skill 内含重解析文件：{file.Name}");
            file.CopyTo(Path.Combine(destination, file.Name), overwrite: false);
        }
        foreach (var dir in sourceInfo.EnumerateDirectories())
        {
            if (IgnoredDirs.Contains(dir.Name)) continue;
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Skill 内含重解析目录：{dir.Name}");
            CopyTree(dir.FullName, Path.Combine(destination, dir.Name));
        }
    }

    internal static string HashDirectory(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateHashFiles(root).OrderBy(x => x.Relative, StringComparer.Ordinal))
        {
            var pathBytes = Encoding.UTF8.GetBytes(file.Relative.Replace('\\', '/'));
            hash.AppendData(BitConverter.GetBytes(pathBytes.Length));
            hash.AppendData(pathBytes);
            var info = new FileInfo(file.Full);
            hash.AppendData(BitConverter.GetBytes(info.Length));
            using var stream = File.OpenRead(file.Full);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<(string Full, string Relative)> EnumerateHashFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            var info = new DirectoryInfo(current);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Skill 内含重解析目录");
            foreach (var file in info.EnumerateFiles())
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Skill 内含重解析文件");
                yield return (file.FullName, Path.GetRelativePath(root, file.FullName));
            }
            foreach (var dir in info.EnumerateDirectories())
                if (!IgnoredDirs.Contains(dir.Name)) stack.Push(dir.FullName);
        }
    }

    private static (string Name, string? Description) ReadMetadata(string path, string fallback)
    {
        if (!File.Exists(path)) return (fallback, null);
        var text = File.ReadAllText(path, Encoding.UTF8);
        var match = Frontmatter.Match(text);
        var name = fallback;
        string? description = null;
        if (match.Success)
        {
            foreach (var line in match.Groups[1].Value.Split('\n'))
            {
                var index = line.IndexOf(':');
                if (index <= 0) continue;
                var key = line[..index].Trim();
                var value = line[(index + 1)..].Trim().Trim('"', '\'');
                if (key == "name" && value.Length > 0) name = value;
                if (key is "description" or "summary" && value.Length > 0) description ??= value;
            }
        }
        return (name, description);
    }

    private SkillStateFile LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return new();
            var state = JsonSerializer.Deserialize<SkillStateFile>(File.ReadAllText(StatePath), JsonOptions) ?? new();
            state.Skills = new Dictionary<string, SkillStateEntry>(state.Skills, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception) { return new(); }
    }

    private void SaveState(SkillStateFile state)
    {
        Directory.CreateDirectory(_localDataRoot);
        var temp = StatePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, StatePath, overwrite: true);
    }

    private static void EnsureRealSkill(string path, string message)
    {
        if (!Directory.Exists(path) || IsReparsePoint(path) || !File.Exists(Path.Combine(path, "SKILL.md")))
            throw new InvalidOperationException(message);
    }

    private static void GuardName(string name)
    {
        if (name is "." or ".." || !SafeName.IsMatch(name)) throw new ArgumentException("非法 Skill 名");
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path) || IsReparsePoint(path);

    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception) { return false; }
    }

    private static string? ResolveLink(string path)
    {
        try { return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName; }
        catch (Exception) { return null; }
    }

    private static bool SamePath(string? left, string right) => left is not null
        && string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.GetFullPath(path).TrimEnd('\\', '/');
    }

    private static void TryDeleteRealDirectory(string path)
    {
        try { if (Directory.Exists(path) && !IsReparsePoint(path)) Directory.Delete(path, true); }
        catch (Exception) { }
    }
}
