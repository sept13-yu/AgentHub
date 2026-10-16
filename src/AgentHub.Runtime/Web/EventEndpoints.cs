using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace AgentHub.Web;

public static class EventEndpoints
{
    /// <summary>只读、无鉴权：事件不含任何密钥或路径，浏览器直连也可订阅。</summary>
    public static void MapEventEndpoints(this WebApplication app, EventHub hub)
    {
        app.MapGet("/api/events", async (HttpContext ctx, IHostApplicationLifetime lifetime) =>
        {
            // SSE 不会自行结束；服务退出时主动断开，避免 Kestrel 等满优雅关闭超时。
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
                ctx.RequestAborted, lifetime.ApplicationStopping);
            var cancellation = stopping.Token;
            try
            {
                ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                await ctx.Response.WriteAsync("retry: 3000\n\n", cancellation);
                await ctx.Response.Body.FlushAsync(cancellation);

                using var _ = hub.Subscribe(out var reader);
                using var ping = new PeriodicTimer(TimeSpan.FromSeconds(20));
                var pingTask = ping.WaitForNextTickAsync(cancellation).AsTask();
                var readTask = reader.WaitToReadAsync(cancellation).AsTask();
                while (!cancellation.IsCancellationRequested)
                {
                    var done = await Task.WhenAny(pingTask, readTask);
                    if (done == pingTask)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", cancellation);
                        pingTask = ping.WaitForNextTickAsync(cancellation).AsTask();
                    }
                    else
                    {
                        if (!await readTask) break;
                        while (reader.TryRead(out var frame))
                            await ctx.Response.WriteAsync(frame, cancellation);
                        readTask = reader.WaitToReadAsync(cancellation).AsTask();
                    }
                    await ctx.Response.Body.FlushAsync(cancellation);
                }
            }
            catch (OperationCanceledException)
            {
                // 客户端关页/断开是常态，不记错误
            }
        });
    }
}
