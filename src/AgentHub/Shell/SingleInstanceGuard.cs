using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Shell;

/// <summary>单实例互斥（Local\AgentHub.SingleInstance）。
/// 二次启动只唤醒已有主窗后退出；<b>不杀</b>同名进程，避免误杀凭据子进程或其它会话。
/// 找不到窗且本机 API 也探活失败时记日志并让位，不接管、不 Kill。</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\AgentHub.SingleInstance";
    private const string ActivateEventName = @"Local\AgentHub.Activate";
    private const string MainWindowTitle = "AgentHub";

    private Mutex? _mutex;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _registration;
    private bool _owned;

    /// <summary>收到二次启动激活请求时触发（回调在线程池线程，订阅方自行切回 UI 线程）。</summary>
    public event Action? Activated;

    /// <summary>true = 本进程成为首实例；false = 已把已有实例唤到前台（或无法确认），调用方应退出。</summary>
    public bool TryAcquire()
    {
        if (TryOwnMutex())
        {
            LogOtherInstances("first-instance");
            StartActivateWait();
            HubLog.Write($"[single-instance] acquired pid={Environment.ProcessId}");
            return true;
        }

        SignalExistingInstance();
        if (TryActivateOtherMainWindow())
        {
            HubLog.Write("[single-instance] yield: activated existing window");
            return false;
        }

        // 主窗可能仍在 OnStartup/WebView 初始化：再等一拍
        Thread.Sleep(1200);
        SignalExistingInstance();
        if (TryActivateOtherMainWindow())
        {
            HubLog.Write("[single-instance] yield: activated existing window (retry)");
            return false;
        }
        if (IsLocalApiAlive())
        {
            HubLog.Write("[single-instance] yield: local api alive, no window yet");
            return false;
        }

        // 不 Kill、不接管：残留占锁只能人工处理，误杀主实例代价更高
        HubLog.Write("[single-instance] yield: no window and health failed; will not kill other AgentHub processes");
        LogOtherInstances("yield-no-kill");
        ReleaseMutexHandle();
        return false;
    }

    /// <summary>本机 18780 仍在听，说明已有实例活着。</summary>
    private static bool IsLocalApiAlive()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(2000) };
            using var resp = http.GetAsync("http://127.0.0.1:18780/health").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryOwnMutex()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            if (_mutex.WaitOne(0))
            {
                _owned = true;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
            return true;
        }
        return false;
    }

    private void StartActivateWait()
    {
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => Activated?.Invoke(),
            null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static void SignalExistingInstance()
    {
        try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); }
        catch (Exception) { /* 首实例尚未创建事件 */ }
    }

    private static bool TryActivateOtherMainWindow()
    {
        foreach (var p in OtherAgentHubProcesses())
        {
            try
            {
                var hwnd = FindMainWindow(p.Id);
                if (hwnd == IntPtr.Zero) continue;
                ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
                AllowSetForegroundWindow(p.Id);
                SetForegroundWindow(hwnd);
                return true;
            }
            finally
            {
                p.Dispose();
            }
        }
        return false;
    }

    private static void LogOtherInstances(string phase)
    {
        foreach (var p in OtherAgentHubProcesses())
        {
            try
            {
                HubLog.Write($"[single-instance] {phase}: other AgentHub pid={p.Id}");
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static List<Process> OtherAgentHubProcesses()
    {
        var mine = Environment.ProcessId;
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName("AgentHub"))
        {
            if (p.Id == mine) { p.Dispose(); continue; }
            list.Add(p);
        }
        return list;
    }

    private static IntPtr FindMainWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var wpid);
            if (wpid != (uint)pid) return true;
            if (GetWindow(h, GwOwner) != IntPtr.Zero) return true;
            var title = GetTitle(h);
            if (!string.Equals(title, MainWindowTitle, StringComparison.Ordinal)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static string GetTitle(IntPtr h)
    {
        var len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        _ = GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private void ReleaseMutexHandle()
    {
        _registration?.Unregister(null);
        _registration = null;
        _activate?.Dispose();
        _activate = null;
        if (_mutex is not null)
        {
            if (_owned)
            {
                try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
                _owned = false;
            }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    public void Dispose() => ReleaseMutexHandle();

    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const int GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
}
