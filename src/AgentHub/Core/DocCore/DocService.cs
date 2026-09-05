using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.DocCore;

/// <summary>资料条目（skills 卡片 / 文档表格行共用形状）。</summary>
public sealed record DocItem
{
    public required string Kind { get; init; }         // skill | library
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Path { get; init; }         // 绝对路径（预览/外开用）
    public required string RelPath { get; init; }      // 相对根目录的显示路径
    public long SizeBytes { get; init; }
    public DateTime ModifiedUtc { get; init; }
    /// <summary>方案文档的家；技能不填。</summary>
    public string? AgentId { get; init; }
    /// <summary>方案所属项目；技能不填。Sandbox 为「其他」。</summary>
    public string? Project { get; init; }
    /// <summary>技能：使用中为 true，已归档为 false。</summary>
    public bool? Enabled { get; init; }
    /// <summary>skills 与 All-Skills 两边都是真目录，或联接指向别处。</summary>
    public bool Conflict { get; init; }
}

/// <summary>DocCore（方案 §6）：skills + 方案文档两条根路径只从配置读；
/// 只读预览 + 用系统默认程序打开；不写源文件。</summary>
public sealed class DocService
{
    private readonly AgentHubConfig _config;
    public SkillManager Skills { get; }

    public DocService(AgentHubConfig config, SkillManager? skills = null)
    {
        _config = config;
        Skills = skills ?? new SkillManager();
    }

    public string SkillsRoot => Skills.ActiveRoot;
    public string SkillsStore => Skills.StoreRoot;
    public string LibraryRoot => ResolvedLibraryRoot();

    public string ResolvedLibraryRoot()
    {
        try { return DocsSettings.NormalizeLibraryRoot(_config.Docs.LibraryRoot); }
        catch (Exception) { return _config.Docs.LibraryRoot; }
    }

    // ------------------------------------------------------------------
    // skills：各目录的 SKILL.md（frontmatter 取 name/description）
    // ------------------------------------------------------------------

    private static readonly Regex Frontmatter = new(@"^---\s*\n([\s\S]*?)\n---", RegexOptions.Compiled);

    private static (string Name, string? Desc) ParseFrontmatter(string text, string fallbackName)
    {
        var m = Frontmatter.Match(text);
        if (!m.Success) return (fallbackName, FirstParagraph(text));
        var name = fallbackName;
        string? desc = null;
        foreach (var line in m.Groups[1].Value.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim().Trim('"', '\'');
            if (key == "name" && val.Length > 0) name = val;
            if (key is "description" or "summary" && val.Length > 0) desc ??= val;
        }
        return (name, desc ?? FirstParagraph(text[m.Length..]));
    }

    private static string? FirstParagraph(string rest)
    {
        var trimmed = rest.TrimStart('\r', '\n', ' ');
        var lines = trimmed.Split('\n').TakeWhile(l => l.Trim().Length > 0).ToList();
        var joined = string.Join(" ", lines.Select(l => l.TrimStart('#', ' '))).Trim();
        return joined.Length == 0 ? null : (joined.Length <= 200 ? joined : joined[..200] + "…");
    }

    // ------------------------------------------------------------------
    // library：Plans/{项目}/{Agent} 与 Sandbox/{Agent}
    // ------------------------------------------------------------------

    public IReadOnlyList<DocItem> ListLibrary(string? q)
    {
        var root = ResolvedLibraryRoot();
        var items = new List<DocItem>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return items;

        var exclude = new HashSet<string>(_config.Docs.Exclude, StringComparer.OrdinalIgnoreCase);
        CollectPlans(root, exclude, q, items);
        CollectSandbox(root, exclude, q, items);
        return items.OrderByDescending(i => i.ModifiedUtc).ToList();
    }

    private static void CollectPlans(string root, HashSet<string> exclude, string? q, List<DocItem> items)
    {
        var plans = FindChild(root, "Plans");
        if (plans is null) return;
        foreach (var projectDir in SafeDirs(plans, exclude))
        {
            var project = Path.GetFileName(projectDir);
            foreach (var agentDir in SafeDirs(projectDir, exclude))
            {
                var agentId = AgentFromFolder(Path.GetFileName(agentDir));
                foreach (var file in EnumerateFilesSafe(agentDir, "*.md", exclude))
                    TryAddLibrary(items, root, file, project, agentId, q);
            }
        }
    }

    private static void CollectSandbox(string root, HashSet<string> exclude, string? q, List<DocItem> items)
    {
        var sandbox = FindChild(root, "Sandbox");
        if (sandbox is null) return;
        foreach (var agentDir in SafeDirs(sandbox, exclude))
        {
            var agentId = AgentFromFolder(Path.GetFileName(agentDir));
            foreach (var file in EnumerateFilesSafe(agentDir, "*.md", exclude))
                TryAddLibrary(items, root, file, "其他", agentId, q);
        }
    }

    private static void TryAddLibrary(List<DocItem> items, string root, string file, string project, string? agentId, string? q)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrEmpty(q) && !name.Contains(q, StringComparison.OrdinalIgnoreCase))
                return;
            var fi = new FileInfo(file);
            items.Add(new DocItem
            {
                Kind = "library",
                Name = name,
                Path = file,
                RelPath = Path.GetRelativePath(root, file),
                SizeBytes = fi.Length,
                ModifiedUtc = fi.LastWriteTimeUtc,
                AgentId = agentId,
                Project = project,
            });
        }
        catch (Exception) { }
    }

    private static string? FindChild(string root, string name)
    {
        try
        {
            return Directory.GetDirectories(root)
                .FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeDirs(string root, HashSet<string> exclude)
    {
        string[] dirs = [];
        try { dirs = Directory.GetDirectories(root); } catch (Exception) { yield break; }
        foreach (var d in dirs)
            if (!exclude.Contains(Path.GetFileName(d)))
                yield return d;
    }

    internal static string? AgentFromFolder(string? folder) => folder?.ToLowerInvariant() switch
    {
        "cursor" => "cursor",
        "codex" => "codex",
        "dsh" => "dsh",
        "trae" or "traework" => "trae",
        "workbuddy" => "workbuddy",
        "zcode" => "zcode",
        _ => null,
    };

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern, HashSet<string> exclude)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files = [], dirs = [];
            try
            {
                files = Directory.GetFiles(dir, pattern);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (!exclude.Contains(Path.GetFileName(d)))
                    stack.Push(d);
        }
    }

    // ------------------------------------------------------------------
    // 预览（路径必须落在两条根路径内，防任意文件读）
    // ------------------------------------------------------------------

    public (string Content, long SizeBytes, DateTime ModifiedUtc)? Preview(string path)
    {
        if (!IsAllowedPath(path)) return null;
        if (!File.Exists(path)) return null;
        var fi = new FileInfo(path);
        // 超大文件只取前 256KB（预览，不是编辑器）
        using var fs = File.OpenRead(path);
        var buf = new byte[Math.Min(fs.Length, 256 * 1024)];
        int n = fs.Read(buf, 0, buf.Length);
        return (Encoding.UTF8.GetString(buf, 0, n), fi.Length, fi.LastWriteTimeUtc);
    }

    public bool IsAllowedPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return false; }
        foreach (var root in new[] { SkillsRoot, SkillsStore, ResolvedLibraryRoot() })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string rootFull;
            try { rootFull = Path.GetFullPath(root); }
            catch (Exception) { continue; }
            var relative = Path.GetRelativePath(rootFull, full);
            if (relative == "." || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)))
                return !HasReparsePoint(rootFull, full);
        }
        return false;
    }

    private static bool HasReparsePoint(string root, string target)
    {
        try
        {
            var current = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
                if (string.Equals(Path.GetFullPath(current).TrimEnd('\\', '/'),
                    Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception) { return true; }
        return true;
    }

    /// <summary>用系统默认程序打开（方案 §6）。只允许两条根路径内的文件。</summary>
    public void Open(string path)
    {
        if (!IsAllowedPath(path)) throw new UnauthorizedAccessException("路径不在资料中心根目录内");
        if (!File.Exists(path)) throw new FileNotFoundException("文件不存在", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
