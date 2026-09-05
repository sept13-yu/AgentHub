import type { QuotaTile } from './quotaView'
import type { RangeKey, UsageView } from './usageView'
import { emptyUsageView } from './usageView'

/** 仪表盘离开后再回来沿用这份，不自动重扫。点刷新才写新值。 */
export const dashCache = {
  primed: false,
  range: 'today' as RangeKey,
  expanded: null as string | null,
  usage: emptyUsageView() as UsageView,
  quotasReady: false,
  tiles: [] as QuotaTile[],
}
