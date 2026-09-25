using AgentHub.Core.ProxyCore;
using AgentHub.Core.TokenCore;

namespace AgentHub.Web;

/// <summary>手动刷新（/api/usage/scan）的编排，抽出来便于按真实入口验证。
/// 顺序：本地目录强制重读 → 扫描 → 后台远程刷新。强制重读失败不阻断扫描（坏文件按 R3 保留上次有效快照）。</summary>
public static class ManualScan
{
    public static async Task RunAsync(
        TokenService tokens,
        QuotaService quotas,
        AgentHubConfig config,
        Func<Task<ScanAllResult>>? usageScan)
    {
        quotas.InvalidateCache();

        // 第 5.3 节：手动刷新必须强制重读本地补丁（同长度/同修改时间的编辑也要生效）。
        // 在扫描与返回结果之前完成；远程刷新继续后台执行，不阻塞这里。
        try
        {
            PriceSyncService.Capture(
                config.Dashboard.PriceOverrides,
                config.Dashboard.CostCurrency,
                forceLocalReload: true);
        }
        catch (Exception)
        {
            // 坏文件不能让刷新整体失败：沿用上次有效快照，错误已记入目录状态
        }

        if (usageScan is not null) await usageScan();
        else tokens.ScanAll();
        PriceSyncService.RefreshInBackground();
    }
}
