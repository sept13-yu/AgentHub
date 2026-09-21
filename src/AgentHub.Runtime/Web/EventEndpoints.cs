using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentHub.Web;

public static class EventEndpoints
{
    /// <summary>只读、无鉴权：事件不含任何密钥或路径，浏览器直连也可订阅。</summary>
    public static void MapEventEndpoints(this WebApplication app, EventHub hub)
    {
        app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            try
            {
                ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                await ctx.Response.WriteAsync("retry: 3000\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                using var _ = hub.Subscribe(out var reader);
                using var ping = new PeriodicTimer(TimeSpan.FromSeconds(20));
                var pingTask = ping.WaitForNextTickAsync(ctx.RequestAborted).AsTask();
                var readTask = reader.WaitToReadAsync(ctx.RequestAborted).AsTask();
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    var done = await Task.WhenAny(pingTask, readTask);
                    if (done == pingTask)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ctx.RequestAborted);
                        pingTask = ping.WaitForNextTickAsync(ctx.RequestAborted).AsTask();
                    }
                    else
                    {
                        if (!await readTask) break;
                        while (reader.TryRead(out var frame))
                            await ctx.Response.WriteAsync(frame, ctx.RequestAborted);
                        readTask = reader.WaitToReadAsync(ctx.RequestAborted).AsTask();
                    }
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // 客户端关页/断开是常态，不记错误
            }
        });
    }
}
