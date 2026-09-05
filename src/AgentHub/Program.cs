using Velopack;

namespace AgentHub;

/// <summary>真正的进程入口。Velopack 安装/卸载钩子必须在 WPF 起来之前处理完并退出。</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
