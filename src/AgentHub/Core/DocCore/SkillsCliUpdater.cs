using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentHub.Core.DocCore;

public sealed record SkillsCliStatus(bool Available, string Message, string? Executable = null);
public sealed record SkillCliResult(bool Ok, string Output);

public sealed class SkillsCliUpdater
{
    public const string PackageVersion = "1.5.19";
    /// <summary>vercel-labs/skills 把用户级 <c>~/.agents/skills</c> 登记在这个 agent 名下。</summary>
    public const string SharedAgentsDirectory = "cline";
    private static readonly Regex OwnerRepo = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,38}/[A-Za-z0-9][A-Za-z0-9._-]{0,99}(@[A-Za-z0-9][A-Za-z0-9._-]{0,99})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "www.github.com", "gitlab.com", "www.gitlab.com", "skills.sh",
    };

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
    public Task<SkillCliResult> UpdateAsync(
        IReadOnlyList<string> names, CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        var status = Detect();
        if (!status.Available || status.Executable is null)
            return Task.FromResult(new SkillCliResult(false, status.Message));
        if (names.Count == 0)
            return Task.FromResult(new SkillCliResult(true, "没有要检查的 Skill"));

        var args = new List<string> { "-y", $"skills@{PackageVersion}", "update" };
        args.AddRange(names);
        args.Add("-g");
        args.Add("-y");
        return RunAsync(status.Executable, args, TimeSpan.FromMinutes(10), "更新", cancellationToken, onLine);
    }

    /// <summary>装到 <c>~/.agents/skills</c> 真实目录，并写入 skills lock，便于之后检查更新。</summary>
    public Task<SkillCliResult> AddAsync(
        string source, CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        var status = Detect();
        if (!status.Available || status.Executable is null)
            return Task.FromResult(new SkillCliResult(false, status.Message));
        if (!TryNormalizeSource(source, out var normalized, out var error))
            return Task.FromResult(new SkillCliResult(false, error));

        var args = new List<string>
        {
            "-y", $"skills@{PackageVersion}", "add", normalized,
            "-g", "--copy", "-a", SharedAgentsDirectory, "-y",
        };
        if (!HasExplicitSkill(normalized))
        {
            args.Add("--skill");
            args.Add("*");
        }
        return RunAsync(status.Executable, args, TimeSpan.FromMinutes(10), "安装", cancellationToken, onLine);
    }

    public Task<SkillCliResult> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var status = Detect();
        if (!status.Available || status.Executable is null)
            return Task.FromResult(new SkillCliResult(false, status.Message));
        var args = new List<string>
        {
            "-y", $"skills@{PackageVersion}", "remove", name,
            "-g", "-a", SharedAgentsDirectory, "-y",
        };
        return RunAsync(status.Executable, args, TimeSpan.FromMinutes(2), "删除", cancellationToken);
    }

    public static bool TryNormalizeSource(string? raw, out string source, out string error)
    {
        source = "";
        error = "";
        var text = raw?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "请填写 owner/repo 或 GitHub / GitLab / skills.sh 地址";
            return false;
        }
        if (text.Length > 400 || text.StartsWith('-') || text.Contains('\0'))
        {
            error = "来源格式无效";
            return false;
        }
        if (LooksLikeLocalPath(text))
        {
            error = "只接受远程仓库地址，不安装本地路径";
            return false;
        }
        if (OwnerRepo.IsMatch(text))
        {
            source = text;
            return true;
        }
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && AllowedHosts.Contains(uri.Host)
            && uri.AbsolutePath.Length > 1)
        {
            source = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return true;
        }
        error = "只接受 owner/repo、owner/repo@skill 或 GitHub / GitLab / skills.sh 的 https 地址";
        return false;
    }

    private static bool HasExplicitSkill(string source) =>
        source.Contains('@', StringComparison.Ordinal)
        || source.Contains("/tree/", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeLocalPath(string text) =>
        text.StartsWith("./", StringComparison.Ordinal)
        || text.StartsWith(".\\", StringComparison.Ordinal)
        || text.StartsWith("../", StringComparison.Ordinal)
        || text.StartsWith("..\\", StringComparison.Ordinal)
        || text.StartsWith("~", StringComparison.Ordinal)
        || text.StartsWith("/", StringComparison.Ordinal)
        || text.StartsWith("\\\\", StringComparison.Ordinal)
        || (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':');

    private static async Task<SkillCliResult> RunAsync(
        string executable, IReadOnlyList<string> args, TimeSpan limit, string verb,
        CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        var psi = CreateNpx(executable, args);
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
            return new(false, $"{verb}超时（{(int)limit.TotalMinutes} 分钟）");
        }
        var output = ((await stdout) + Environment.NewLine + (await stderr)).Trim();
        if (output.Length > 4000) output = output[^4000..];
        return new(process.ExitCode == 0, output.Length == 0
            ? (process.ExitCode == 0 ? $"{verb}完成" : $"npx 退出码 {process.ExitCode}")
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
