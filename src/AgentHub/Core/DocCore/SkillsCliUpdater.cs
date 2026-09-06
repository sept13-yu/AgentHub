using System.Diagnostics;
using System.IO;
using System.Text;

namespace AgentHub.Core.DocCore;

public sealed record SkillsCliStatus(bool Available, string Message, string? Executable = null);
public sealed record SkillCliResult(bool Ok, string Output);

public sealed class SkillsCliUpdater
{
    public const string PackageVersion = "1.5.19";

    public SkillsCliStatus Detect()
    {
        var executable = FindNpx();
        return executable is null
            ? new(false, "未检测到 Node.js / npx")
            : new(true, $"skills@{PackageVersion}", executable);
    }

    public Task<SkillCliResult> UpdateAsync(
        string name, CancellationToken cancellationToken, Action<string>? onLine = null) =>
        UpdateAsync([name], cancellationToken, onLine);

    /// <summary>一次 <c>skills update</c>：CLI 按 lock hash 对照远端，已是最新的不会重装。</summary>
    public async Task<SkillCliResult> UpdateAsync(
        IReadOnlyList<string> names, CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        var status = Detect();
        if (!status.Available || status.Executable is null)
            return new(false, status.Message);
        if (names.Count == 0)
            return new(true, "没有要检查的 Skill");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var args = new List<string> { "-y", $"skills@{PackageVersion}", "update" };
        args.AddRange(names);
        args.Add("-g");
        args.Add("-y");
        var psi = CreateNpx(status.Executable, args);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 npx");
        var stdout = ReadPipe(process.StandardOutput, onLine, timeout.Token);
        var stderr = ReadPipe(process.StandardError, onLine, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            return new(false, "更新超时（5 分钟）");
        }
        var output = ((await stdout) + Environment.NewLine + (await stderr)).Trim();
        if (output.Length > 4000) output = output[^4000..];
        return new(process.ExitCode == 0, output.Length == 0
            ? (process.ExitCode == 0 ? "更新完成" : $"npx 退出码 {process.ExitCode}")
            : output);
    }

    private static async Task<string> ReadPipe(StreamReader reader, Action<string>? onLine, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            var text = line.Trim();
            if (text.Length > 0) onLine?.Invoke(text);
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(line);
        }
        return sb.ToString();
    }

    /// <summary>Windows 上 .cmd 不能直接 CreateProcess；再经 cmd /c 套引号会把路径连引号一起当命令名。
    /// 优先 node.exe + npx-cli.js，避免 cmd 解析。</summary>
    internal static ProcessStartInfo CreateNpx(string npx, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (TryResolveNodeCli(npx, out var node, out var cli))
        {
            psi.FileName = node;
            psi.ArgumentList.Add(cli);
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            return psi;
        }

        if (OperatingSystem.IsWindows()
            && npx.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var quotedArgs = string.Join(' ', args.Select(QuoteCmd));
            psi.Arguments = $"/d /c \"\"{npx}\" {quotedArgs}\"";
            return psi;
        }

        psi.FileName = npx;
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    private static bool TryResolveNodeCli(string npxPath, out string node, out string cli)
    {
        node = "";
        cli = "";
        var dir = Path.GetDirectoryName(npxPath);
        if (string.IsNullOrEmpty(dir)) return false;
        var nodeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var nodePath = Path.Combine(dir, nodeName);
        var cliPath = Path.Combine(dir, "node_modules", "npm", "bin", "npx-cli.js");
        if (!File.Exists(nodePath) || !File.Exists(cliPath)) return false;
        node = nodePath;
        cli = cliPath;
        return true;
    }

    private static string QuoteCmd(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(static c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^'))
            return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string? FindNpx()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "npx.cmd", "npx.exe" } : new[] { "npx" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var name in names)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npx.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "npx.cmd"),
            })
                if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
