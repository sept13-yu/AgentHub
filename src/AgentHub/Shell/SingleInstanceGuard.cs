using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AgentHub.Shell;

/// <summary>单实例互斥（方案 §3：Local\AgentHub.SingleInstance）。
/// 二次启动先发命名事件、再按窗口标题唤醒主窗；若对方已无主窗（僵死占锁、托盘也不见），
/// 杀掉残留进程并接管，避免双击 exe 静默退出。
/// 拿到锁后也会清掉已放锁但未退出的残留 AgentHub.exe。</summary>
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

    /// <summary>true = 本进程成为首实例；false = 已把已有实例唤到前台，调用方应退出。</summary>
    public bool TryAcquire()
    {
        if (TryOwnMutex())
        {
            KillOtherInstances();
            StartActivateWait();
            return true;
        }

        SignalExistingInstance();
        if (TryActivateOtherMainWindow())
            return false;

        KillOtherInstances();
        Thread.Sleep(700);
        ReleaseMutexHandle();

        if (TryOwnMutex())
        {
            StartActivateWait();
            return true;
        }
        return false;
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
            var hwnd = FindMainWindow(p.Id);
            if (hwnd == IntPtr.Zero) continue;
            ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
            AllowSetForegroundWindow(p.Id);
            SetForegroundWindow(hwnd);
            return true;
        }
        return false;
    }

    private static void KillOtherInstances()
    {
        foreach (var p in OtherAgentHubProcesses())
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch (Exception) { }
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
