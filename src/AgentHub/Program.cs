namespace AgentHub;

/// <summary>真正的进程入口。</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 尽早挂钩：非 UI 未处理异常默认杀进程且不留痕，托盘常驻必须能区分异常终止
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            AgentHub.Core.ProxyCore.HubLog.Write(
                $"[exit] unhandled-domain terminating={e.IsTerminating} {ex?.GetType().Name}: {ex?.Message}");
            if (ex?.StackTrace is { Length: > 0 } st)
                AgentHub.Core.ProxyCore.HubLog.Write("[exit] stack " + st.ReplaceLineEndings(" | "));
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            var ex = e.Exception?.GetBaseException();
            AgentHub.Core.ProxyCore.HubLog.Write(
                $"[exit] unobserved-task {ex?.GetType().Name}: {ex?.Message}");
        };

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
