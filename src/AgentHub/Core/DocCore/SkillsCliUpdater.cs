using System.Diagnostics;
using System.IO;

namespace AgentHub.Core.DocCore;

public sealed record SkillsCliStatus(bool Available, string Message, string? Executable = null);
public sealed record SkillCliResult(bool Ok, string Output);

public sealed class SkillsCliUpdater
{
    public const string PackageVersion = "1.5.19";
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);

    public SkillsCliStatus Detect()
    {
        var executable = FindNpx();
        return executable is null
            ? new(false, "未检测到 Node.js / npx")
            : new(true, $"skills@{PackageVersion}", executable);
    }

    public async Task<SkillCliResult> UpdateAsync(string name, CancellationToken cancellationToken)
    {
        var status = Detect();
        if (!status.Available || status.Executable is null)
            return new(false, status.Message);
        if (!await UpdateGate.WaitAsync(0, cancellationToken))
            return new(false, "已有 Skill 更新正在执行");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var isCommandScript = OperatingSystem.IsWindows()
                && status.Executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo
            {
                FileName = isCommandScript
                    ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                    : status.Executable,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (isCommandScript)
            {
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/s");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add($"\"{status.Executable}\" -y skills@{PackageVersion} update {name} -g -y");
            }
            else
            {
                foreach (var arg in new[] { "-y", $"skills@{PackageVersion}", "update", name, "-g", "-y" })
                    psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 npx");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
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
        finally
        {
            UpdateGate.Release();
        }
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
