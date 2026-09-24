using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using AgentHub.Core.Platform;
using Microsoft.Win32;

namespace AgentHub.Shell;

/// <summary>Windows Inno 安装版下载完整 Setup；便携版仅提示手动下载。</summary>
public static class AppUpdate
{
    const string InstallRegistryKey = @"Software\AgentHub";
    const string InstallRegistryValue = "InstallPath";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    static readonly object ProgressGate = new();
    static int _busy;
    static CancellationTokenSource? _downloadCancellation;
    static UpdateProgressSnapshot _progress = new();
    static string? _readyPath;
    static UpdateFeedAsset? _readyAsset;
    static string? _readyVersion;

    public static bool Busy => Volatile.Read(ref _busy) != 0;
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    public static UpdateProgressSnapshot Progress
    {
        get { lock (ProgressGate) return _progress; }
    }

    public static AppUpdateStatus Snapshot() => new()
    {
        installed = IsInnoInstalled(),
        busy = Busy,
        current = CurrentVersion,
    };

    public static async Task<AppUpdateStatus> CheckAsync()
    {
        if (!TryBegin()) return AlreadyBusy();
        SetProgress(true, 0, "checking", "正在检查更新…");
        try
        {
            var feed = await UpdateFeed.GetLatestAsync().ConfigureAwait(false);
            SetProgress(false, 0, "done");
            return Compose(feed);
        }
        catch (Exception ex)
        {
            SetProgress(false, 0, "error", ex.Message);
            return Fail(ex.Message);
        }
        finally
        {
            End();
        }
    }

    public static async Task<AppUpdateStatus> ApplyAsync()
    {
        if (!TryBegin()) return AlreadyBusy();
        SetProgress(true, 0, "checking", "正在检查更新…");
        using var cancellation = new CancellationTokenSource();
        lock (ProgressGate) _downloadCancellation = cancellation;
        string? partPath = null;
        try
        {
            var feed = await UpdateFeed.GetLatestAsync(cancellation.Token).ConfigureAwait(false);
            var status = Compose(feed);
            if (!status.canApply)
            {
                SetProgress(false, 0, "done", status.message);
                return status;
            }

            var asset = feed.assets.winX64;
            var updateDir = Path.Combine(Path.GetTempPath(), "AgentHub.Updates", feed.version);
            Directory.CreateDirectory(updateDir);
            var finalPath = Path.Combine(updateDir, asset.fileName);
            partPath = finalPath + ".part";
            if (File.Exists(partPath)) File.Delete(partPath);
            if (!await MatchesAsync(finalPath, asset, cancellation.Token).ConfigureAwait(false))
            {
                SetProgress(true, 0, "downloading", "正在下载安装包…");
                await DownloadAsync(feed.DownloadUri(asset), partPath, asset, cancellation.Token)
                    .ConfigureAwait(false);
                File.Move(partPath, finalPath, overwrite: true);
            }

            lock (ProgressGate)
            {
                _readyPath = finalPath;
                _readyAsset = asset;
                _readyVersion = feed.version;
            }
            SetProgress(false, 100, "ready", "安装包已准备好。确认后将打开安装向导。");
            return new AppUpdateStatus
            {
                installed = true,
                canApply = true,
                current = CurrentVersion,
                latest = feed.version,
                releaseUrl = feed.ReleasePage,
                message = "安装包已准备好。",
            };
        }
        catch (OperationCanceledException)
        {
            SetProgress(false, 0, "error", "已取消下载。");
            return Fail("已取消下载。");
        }
        catch (Exception ex)
        {
            SetProgress(false, 0, "error", ex.Message);
            return Fail(ex.Message);
        }
        finally
        {
            if (partPath is not null)
            {
                try { File.Delete(partPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            lock (ProgressGate) _downloadCancellation = null;
            End();
        }
    }

    public static void Cancel()
    {
        lock (ProgressGate) _downloadCancellation?.Cancel();
    }

    public static async Task<AppUpdateStatus> LaunchAsync()
    {
        if (!TryBegin()) return AlreadyBusy();
        try
        {
            string? path;
            UpdateFeedAsset? asset;
            string? version;
            lock (ProgressGate)
            {
                path = _readyPath;
                asset = _readyAsset;
                version = _readyVersion;
            }
            if (!IsInnoInstalled() || path is null || asset is null || version is null)
                return Fail("请先下载并校验安装包。");

            SetProgress(true, 100, "verifying", "正在校验安装包…");
            if (!await MatchesAsync(path, asset, CancellationToken.None).ConfigureAwait(false))
            {
                SetProgress(false, 0, "error", "安装包校验失败，请重新下载。");
                return Fail("安装包校验失败，请重新下载。");
            }

            SetProgress(true, 100, "launching", "正在启动安装向导…");
            var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            });
            if (process is null) throw new InvalidOperationException("安装向导启动失败。");
            SetProgress(false, 100, "done", "安装向导已启动，AgentHub 即将退出。");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                var app = System.Windows.Application.Current;
                if (app is not null)
                    await app.Dispatcher.InvokeAsync(app.Shutdown);
            });
            return new AppUpdateStatus
            {
                installed = true,
                current = CurrentVersion,
                latest = version,
                message = "安装向导已启动，AgentHub 即将退出。",
            };
        }
        catch (Exception ex)
        {
            SetProgress(false, 0, "error", ex.Message);
            return Fail("无法启动安装向导：" + ex.Message);
        }
        finally
        {
            End();
        }
    }

    static async Task DownloadAsync(Uri url, string partPath, UpdateFeedAsset asset, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dest = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            downloaded += read;
            if (downloaded > asset.size) throw new InvalidDataException("安装包大小与发布清单不符。");
            hash.AppendData(buffer, 0, read);
            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            var percent = (int)(downloaded * 100 / asset.size);
            SetProgress(true, Math.Min(percent, 99), "downloading", $"正在下载安装包… {percent}%");
        }
        await dest.FlushAsync(ct).ConfigureAwait(false);
        SetProgress(true, 100, "verifying", "正在校验安装包…");
        var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (downloaded != asset.size ||
            !string.Equals(actualHash, asset.sha256, StringComparison.Ordinal))
            throw new InvalidDataException("安装包校验失败，请重试。");
    }

    static async Task<bool> MatchesAsync(string path, UpdateFeedAsset asset, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.size) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, useAsync: true);
        var digest = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(digest), asset.sha256, StringComparison.Ordinal);
    }

    static AppUpdateStatus Compose(UpdateFeedManifest feed)
    {
        var installed = IsInnoInstalled();
        var newer = UpdateFeed.IsNewer(feed.version, CurrentVersion);
        return new AppUpdateStatus
        {
            installed = installed,
            canApply = installed && newer,
            needsInstaller = !installed && newer,
            current = CurrentVersion,
            latest = feed.version,
            releaseUrl = feed.ReleasePage,
            message = newer ? "有最新版本 " + feed.version : "已是最新版本。",
        };
    }

    static bool IsInnoInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InstallRegistryKey);
        if (key?.GetValue(InstallRegistryValue) is not string path) return false;
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current)) return false;
        var expected = Path.GetFullPath(Path.Combine(path, "AgentHub.exe"));
        return string.Equals(Path.GetFullPath(current), expected, StringComparison.OrdinalIgnoreCase);
    }

    static AppUpdateStatus AlreadyBusy() => new()
    {
        installed = IsInnoInstalled(),
        busy = true,
        current = CurrentVersion,
        message = "正在处理更新，请稍候。",
    };

    static AppUpdateStatus Fail(string error) => new()
    {
        installed = IsInnoInstalled(),
        current = CurrentVersion,
        error = error,
    };

    static bool TryBegin() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    static void End() => Interlocked.Exchange(ref _busy, 0);

    static void SetProgress(bool running, int percent, string phase, string? message = null)
    {
        lock (ProgressGate)
            _progress = new UpdateProgressSnapshot
            {
                running = running,
                percent = Math.Clamp(percent, 0, 100),
                phase = phase,
                message = message,
            };
    }
}

public sealed class WindowsAppUpdateService : IAppUpdateService
{
    public bool IsSupported => true;
    public UpdateProgressSnapshot Progress => AppUpdate.Progress;
    public AppUpdateStatus Snapshot() => AppUpdate.Snapshot();
    public Task<AppUpdateStatus> CheckAsync() => AppUpdate.CheckAsync();
    public Task<AppUpdateStatus> ApplyAsync() => AppUpdate.ApplyAsync();
    public Task<AppUpdateStatus> LaunchAsync() => AppUpdate.LaunchAsync();
    public void Cancel() => AppUpdate.Cancel();
}
