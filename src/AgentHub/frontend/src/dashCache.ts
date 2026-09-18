import type { QuotaTile } from './quotaView'
import type { RangeKey, UsageView } from './usageView'
import { emptyUsageView } from './usageView'

/**
 * 仪表盘离开后再回来沿用这份，不自动重扫。
 * 「点刷新 / 壳层推送 / 设置变更」才写新值或失效。
 */
export const dashCache = {
  primed: false,
  range: 'today' as RangeKey,
  expanded: null as string | null,
  usage: emptyUsageView() as UsageView,
  quotasReady: false,
  tiles: [] as QuotaTile[],
}

/** 设置保存成功后调用：下次进入仪表盘强制重拉。 */
export function invalidateDashCache() {
  dashCache.primed = false
  dashCache.quotasReady = false
}
