using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace AgentHub.Web;

/// <summary>进程内事件扇出：每个 SSE 连接一条有界 Channel，满了丢最旧（刷新事件可合并，丢了无害）。</summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subs = new();

    public IDisposable Subscribe(out ChannelReader<string> reader)
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _subs[id] = ch;
        reader = ch.Reader;
        return new Unsub(() => { if (_subs.TryRemove(id, out var c)) c.Writer.TryComplete(); });
    }

    /// <summary>name 只允许 [a-z-]；data 序列化为单行 JSON。可从任意线程调用。</summary>
    public void Publish(string name, object? data = null)
    {
        var json = data is null ? "{}" : JsonSerializer.Serialize(data);
        var frame = "event: " + name + "\ndata: " + json + "\n\n";
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(frame);
    }

    private sealed class Unsub(Action a) : IDisposable { public void Dispose() => a(); }
}
