namespace AgentHub.Core.McpCore.Adapters;

/// <summary>DSH home patch 中静态 MCP insert 的共享配置入口。</summary>
public sealed class DshMcpAdapter : IMcpAdapter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public string AgentId => "dsh";
    public string DisplayName => "DSH · 共享配置";
    public string ConfigPath { get; }
    public bool Detected => File.Exists(ConfigPath) ||
        Directory.Exists(Path.Combine(Home, "profiles")) ||
        File.Exists(Path.Combine(Home, "package.json")) ||
        File.Exists(Path.Combine(Home, "config.yml"));
    private string Home => Path.GetDirectoryName(ConfigPath)!;

    public DshMcpAdapter(string? configPath = null)
    {
        var home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        ConfigPath = Path.GetFullPath(configPath ?? Path.Combine(home, "cordis.patch.yml"));
    }

    public IReadOnlyList<McpServerSpec> List() => DshMcpPatchDocument.Load(ConfigPath).List();

    public void Upsert(McpServerSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Id) || spec.Id.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidOperationException("DSH MCP 名称无效");
        if (spec.Transport == McpTransport.Stdio && string.IsNullOrWhiteSpace(spec.Command))
            throw new InvalidOperationException("DSH stdio 传输需要 command");
        if (spec.Transport == McpTransport.Http &&
            (string.IsNullOrWhiteSpace(spec.Url) || !Uri.TryCreate(spec.Url, UriKind.Absolute, out var uri) ||
             uri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("DSH HTTP 传输需要有效 url");
        if (spec.TimeoutMs is <= 0) throw new InvalidOperationException("DSH timeout 必须为正整数");
        Mutate(spec.Id, true, doc => doc.Upsert(spec));
    }

    public void SetEnabled(string id, bool enabled) => Mutate(id, true, doc => doc.SetEnabled(id, enabled));
    public void Remove(string id) => Mutate(id, false, doc => doc.Remove(id));

    public List<(string Profile, string Path, McpServerSpec Spec)> FindServerElsewhere(string serverName)
    {
        var result = new List<(string, string, McpServerSpec)>();
        var profiles = Path.Combine(Home, "profiles");
        if (!Directory.Exists(profiles)) return result;
        foreach (var dir in Directory.EnumerateDirectories(profiles))
        {
            var path = Path.Combine(dir, "cordis.patch.yml");
            if (!File.Exists(path)) continue;
            foreach (var spec in DshMcpPatchDocument.Load(path).List())
                if (string.Equals(spec.Id, serverName, StringComparison.OrdinalIgnoreCase))
                    result.Add((Path.GetFileName(dir), path, spec));
        }
        return result;
    }

    private void Mutate(string id, bool expectTarget, Func<DshMcpPatchDocument, string> edit)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("DSH MCP 名称无效");
        lock (Locks.GetOrAdd(ConfigPath, _ => new object()))
        {
            var snapshot = DshMcpFileWriter.Read(ConfigPath);
            if (!snapshot.Exists && !expectTarget) return;
            var profiles = CaptureProfiles();
            var document = DshMcpPatchDocument.Parse(snapshot.Text, ConfigPath, allowEmpty: !snapshot.Exists);
            var before = document.List();
            if (FindServerElsewhere(id).Count > 0)
                throw new InvalidOperationException($"DSH MCP {SafeId(id)} 同时存在于 profile 层，请先完成迁移");
            var edited = edit(document);
            if (edited == snapshot.Text) return;
            var after = DshMcpPatchDocument.Parse(edited, ConfigPath).List();
            var beforeOthers = before.Where(s => !string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
            var afterOthers = after.Where(s => !string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (afterOthers.Count != beforeOthers.Count ||
                beforeOthers.Any(previous => afterOthers.All(current =>
                    !string.Equals(current.Id, previous.Id, StringComparison.OrdinalIgnoreCase) ||
                    System.Text.Json.JsonSerializer.Serialize(current) != System.Text.Json.JsonSerializer.Serialize(previous))) ||
                after.Count(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) != (expectTarget ? 1 : 0))
                throw new InvalidOperationException("DSH patch 修改影响了其他 MCP 条目，已放弃写入");
            var latestProfiles = CaptureProfiles();
            if (profiles.Count != latestProfiles.Count || profiles.Any(p =>
                !latestProfiles.TryGetValue(p.Key, out var bytes) || !p.Value.AsSpan().SequenceEqual(bytes)))
                throw new InvalidOperationException("DSH profile 配置在写入期间发生变化，请重试");
            DshMcpFileWriter.Write(ConfigPath, snapshot, edited);
        }
    }

    private Dictionary<string, byte[]> CaptureProfiles()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var profiles = Path.Combine(Home, "profiles");
        if (!Directory.Exists(profiles)) return result;
        foreach (var dir in Directory.EnumerateDirectories(profiles))
        {
            var path = Path.Combine(dir, "cordis.patch.yml");
            if (File.Exists(path)) result.Add(path, File.ReadAllBytes(path));
        }
        return result;
    }

    private static string SafeId(string value) =>
        value.Length <= 80 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            ? value : "[名称已隐藏]";
}
