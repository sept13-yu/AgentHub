using System.IO;
using System.Text.Json;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.SessionCore;

/// <summary>会话锁：只落 %APPDATA%\AgentHub\session-locks.json，键 agent:id。不写各家文件。</summary>
public sealed class SessionLockStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, bool> _map = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SessionLockStore()
    {
        _path = Path.Combine(AgentHubConfig.Dir, "session-locks.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _map = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_path)) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            _map = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Key(string agent, string id) => $"{agent}:{id}";

    public bool IsLocked(string agent, string id)
    {
        lock (_gate) return _map.TryGetValue(Key(agent, id), out var on) && on;
    }

    public void Set(string agent, string id, bool locked)
    {
        lock (_gate)
        {
            var key = Key(agent, id);
            if (locked) _map[key] = true;
            else _map.Remove(key);
            Persist();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_map, Opts));
        }
        catch (IOException) { }
    }
}
