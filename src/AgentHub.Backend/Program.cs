using AgentHub.Core.CodexConfigCore;
using AgentHub.Core.Platform;
using AgentHub.Core.ProxyCore;
using AgentHub.Hosting;
using AgentHub.Web;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentHub.Backend;

/// <summary>无界面后端入口：启动 Runtime 并保持进程，直到父进程退出或收到终止信号。</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (CodexCredentialGate.IsCredentialRequest(args))
        {
            CodexCredentialGate.Handle(args);
            return 0;
        }

        Console.OutputEncoding = Encoding.UTF8;

        AgentHubRuntime? rt = null;
        try
        {
            rt = AgentHubRuntime.Create(new RuntimeHostOptions
            {
                Log = m =>
                {
                    HubLog.Write(m);
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

            Console.WriteLine($"AGENTHUB_READY {{\"port\":{WebHostService.Port},\"pid\":{Environment.ProcessId},\"version\":\"{RuntimeVersion.Current}\"}}");
            rt.RunInitialScanInBackground();

            var exit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                exit.Set();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.Set();
            using var sigterm = OperatingSystem.IsWindows()
                ? null
                : PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    ctx.Cancel = true;
                    exit.Set();
                });

            int? parentPid = ParseParentPid(args);
            if (parentPid is int pid)
            {
                while (!exit.Wait(2000))
                {
                    try
                    {
                        using var parent = Process.GetProcessById(pid);
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
            try { HubLog.Write("[backend] " + ex); } catch { }
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
}
