namespace AgentHub.Backend;

/// <summary>无界面后端入口：启动 Runtime 并保持进程，直到父进程退出或收到终止信号。</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (CodexCredentialGateAdapter.IsCredentialRequest(args))
        {
            CodexCredentialGateAdapter.Handle(args);
            return 0;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        AgentHub.Hosting.AgentHubRuntime? rt = null;
        try
        {
            rt = AgentHub.Hosting.AgentHubRuntime.Create(new AgentHub.Hosting.RuntimeHostOptions
            {
                Log = m =>
                {
                    AgentHub.Core.ProxyCore.HubLog.Write(m);
                    Console.Error.WriteLine(m);
                },
            });
            rt.Start();
            try
            {
                rt.Ready.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("AGENTHUB_START_FAILED " + ex.Message);
                return 3;
            }

            Console.WriteLine($"AGENTHUB_READY {{\"port\":{AgentHub.Web.WebHostService.Port},\"pid\":{Environment.ProcessId},\"version\":\"{AgentHub.Core.Platform.RuntimeVersion.Current}\"}}");
            rt.RunInitialScanInBackground();

            var exit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                exit.Set();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.Set();

            int? parentPid = ParseParentPid(args);
            if (parentPid is int pid)
            {
                while (!exit.Wait(2000))
                {
                    try
                    {
                        using var parent = System.Diagnostics.Process.GetProcessById(pid);
                        if (parent.HasExited) break;
                    }
                    catch (ArgumentException)
                    {
                        break;
                    }
                }
            }
            else
            {
                exit.Wait();
            }

            rt.Stop();
            return 0;
        }
        catch (Exception ex)
        {
            try { AgentHub.Core.ProxyCore.HubLog.Write("[backend] " + ex); } catch { }
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            rt?.Dispose();
        }
    }

    private static int? ParseParentPid(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--parent-pid" && int.TryParse(args[i + 1], out var pid) && pid > 0)
                return pid;
        }
        return null;
    }

    private static class CodexCredentialGateAdapter
    {
        public static bool IsCredentialRequest(string[] args) =>
            AgentHub.Core.CodexConfigCore.CodexCredentialGate.IsCredentialRequest(args);

        public static void Handle(string[] args) =>
            AgentHub.Core.CodexConfigCore.CodexCredentialGate.Handle(args);
    }
}
