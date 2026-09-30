using System.Security.Cryptography;
using System.Text;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.McpCore.Adapters;

internal static class DshMcpFileWriter
{
    internal sealed record Snapshot(bool Exists, string Text, byte[] Bytes, Encoding Encoding);

    internal static Snapshot Read(string path)
    {
        if (!File.Exists(path)) return new Snapshot(false, "", [], new UTF8Encoding(false));
        var bytes = File.ReadAllBytes(path);
        var encoding = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? new UTF8Encoding(true, true) : new UTF8Encoding(false, true);
        try
        {
            var offset = encoding.GetPreamble().Length;
            return new Snapshot(true, encoding.GetString(bytes, offset, bytes.Length - offset), bytes, encoding);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("DSH 配置编码无效");
        }
    }

    internal static void Write(string path, Snapshot snapshot, string text)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, ".cordis.patch." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = snapshot.Encoding.GetPreamble().Concat(snapshot.Encoding.GetBytes(text)).ToArray();
            File.WriteAllBytes(tmp, bytes);
            var existsNow = File.Exists(path);
            if (existsNow != snapshot.Exists ||
                (existsNow && !File.ReadAllBytes(path).AsSpan().SequenceEqual(snapshot.Bytes)))
                throw new InvalidOperationException("DSH 配置在写入期间发生变化，请重试");
            if (snapshot.Exists)
            {
                Backup(snapshot.Bytes);
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(snapshot.Bytes))
                    throw new InvalidOperationException("DSH 配置在备份期间发生变化，请重试");
                File.Replace(tmp, path, null);
            }
            else File.Move(tmp, path);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static void Backup(byte[] snapshot)
    {
        var root = Path.Combine(AgentHubConfig.LocalDataDir, "McpBackups", "dsh", "shared");
        Directory.CreateDirectory(root);
        var name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".yml";
        using var file = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write);
        file.Write(snapshot);
    }
}
