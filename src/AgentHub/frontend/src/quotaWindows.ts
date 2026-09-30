/** 桌面额度悬浮窗与仪表盘共用的额度窗口解析。数据是 /api/quotas 的 remain 条目，不另起口径。 */

export const MAX_PINNED_WINDOWS = 2

export interface QuotaWindowRow {
  id: string
  name: string
  remain: number
  period: string
  resetMs: number | null
}

export function quotaPeriodEnd(period: string): string {
  const text = period.trim()
  if (!text) return ''
  return text.includes('—') ? text.split('—').pop()!.trim() : text
}

/** 供应商重置时刻。不限期、解析不出、2100 年及以后的哨兵日期返回 null。 */
export function parseQuotaReset(period: string): number | null {
  const end = quotaPeriodEnd(period)
  if (!end || end === 'never') return null
  const ms = Date.parse(end)
  if (Number.isNaN(ms)) return null
  if (new Date(ms).getFullYear() >= 2100) return null
  return ms
}

export function quotaPayloadStale(raw: unknown): boolean {
  return !!raw && typeof raw === 'object' && !Array.isArray(raw) && (raw as { stale?: boolean }).stale === true
}

export function toQuotaWindows(raw: unknown): QuotaWindowRow[] {
  const rows: QuotaWindowRow[] = []
  for (const rec of quotaItems(raw)) {
    if (str(rec.kind) !== 'remain') continue
    const id = str(rec.id)
    if (!id) continue
    const period = str(rec.period)
    rows.push({
      id,
      name: windowTitle(rec, id),
      remain: clamp(num(rec.remainPercent), 0, 100),
      period,
      resetMs: parseQuotaReset(period),
    })
  }
  return rows
}

/** 轮询失败留上次成功值；stale 响应用服务端带回的旧值。 */
export function reduceQuotaPoll(
  prev: QuotaWindowRow[],
  result: { ok: true; rows: QuotaWindowRow[]; stale: boolean } | { ok: false },
): { rows: QuotaWindowRow[]; stale: boolean } {
  if (!result.ok) return { rows: prev, stale: true }
  if (result.stale) return { rows: result.rows.length ? result.rows : prev, stale: true }
  return { rows: result.rows, stale: false }
}

/** null = 还没选过。新鲜结果才落默认或丢掉已不可查询的窗口。 */
export function nextPinnedIds(
  pinned: string[] | null,
  rows: QuotaWindowRow[],
  fresh: boolean,
): { pinned: string[] | null; changed: boolean } {
  if (!fresh) return { pinned, changed: false }
  if (pinned == null) {
    if (rows.length === 0) return { pinned: null, changed: false }
    return { pinned: rows.slice(0, MAX_PINNED_WINDOWS).map((row) => row.id), changed: true }
  }
  const live = new Set(rows.map((row) => row.id))
  const next = pinned.filter((id) => live.has(id)).slice(0, MAX_PINNED_WINDOWS)
  const changed = next.length !== pinned.length || next.some((id, index) => id !== pinned[index])
  return { pinned: next, changed }
}

export function visiblePinnedIds(pinned: string[] | null, rows: QuotaWindowRow[]): string[] {
  if (pinned != null) return pinned.slice(0, MAX_PINNED_WINDOWS)
  return rows.slice(0, MAX_PINNED_WINDOWS).map((row) => row.id)
}

export function normalizePinnedIds(raw: string[] | null | undefined): string[] | null {
  if (raw == null) return null
  const seen = new Set<string>()
  const out: string[] = []
  for (const id of raw) {
    const text = id.trim()
    if (!text || seen.has(text)) continue
    seen.add(text)
    out.push(text)
    if (out.length === MAX_PINNED_WINDOWS) break
  }
  return out
}

export function togglePinned(pinned: string[] | null, rows: QuotaWindowRow[], id: string): string[] {
  const cur = pinned != null ? pinned.slice(0, MAX_PINNED_WINDOWS) : rows.slice(0, MAX_PINNED_WINDOWS).map((row) => row.id)
  const index = cur.indexOf(id)
  if (index >= 0) cur.splice(index, 1)
  else if (cur.length < MAX_PINNED_WINDOWS) cur.push(id)
  return cur
}

/** 距真实重置时刻的倒计时，不用窗口名义时长。解析不出的 period 原样显示，和仪表盘同一口径。 */
export function formatResetCountdown(resetMs: number | null, now = Date.now(), period = ''): string {
  if (resetMs == null) return unresolvedPeriod(period)
  let diff = resetMs - now
  if (diff <= 0) return '即将重置'
  const minute = 60_000
  const hour = 60 * minute
  const day = 24 * hour
  const days = Math.floor(diff / day)
  diff -= days * day
  const hours = Math.floor(diff / hour)
  diff -= hours * hour
  const minutes = Math.floor(diff / minute)
  if (days > 0) return hours > 0 ? `${days}天 ${hours}小时后重置` : `${days}天后重置`
  if (hours > 0) return minutes > 0 ? `${hours}小时 ${minutes}分钟后重置` : `${hours}小时后重置`
  if (minutes > 0) return `${minutes}分钟后重置`
  return '即将重置'
}

export function formatResetExact(resetMs: number | null, now = Date.now(), period = ''): string {
  if (resetMs == null) return unresolvedPeriod(period)
  const when = new Date(resetMs)
  const today = new Date(now)
  const hm = `${pad(when.getHours())}:${pad(when.getMinutes())}`
  const date = when.getFullYear() === today.getFullYear()
    ? `${when.getMonth() + 1}月${when.getDate()}日`
    : `${when.getFullYear()}年${when.getMonth() + 1}月${when.getDate()}日`
  return `重置于 ${date} ${hm}`
}

export function remainBarColor(remain: number): string {
  if (remain < 10) return 'var(--danger)'
  if (remain < 50) return 'var(--warn)'
  return 'var(--ok)'
}

function windowTitle(rec: Record<string, unknown>, id: string): string {
  const name = str(rec.name) || id
  const label = str(rec.label)
  if (!label || label === name) return name
  return `${label} · ${name}`
}

function quotaItems(raw: unknown): Record<string, unknown>[] {
  const body = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw as Record<string, unknown> : null
  const items = Array.isArray(body?.items) ? body.items : Array.isArray(raw) ? raw : []
  const list: Record<string, unknown>[] = []
  for (const item of items) {
    if (item && typeof item === 'object' && !Array.isArray(item))
      list.push(item as Record<string, unknown>)
  }
  return list
}

function unresolvedPeriod(period: string): string {
  const end = quotaPeriodEnd(period)
  if (!end || end === 'never') return '不限期'
  const ms = Date.parse(end)
  if (Number.isNaN(ms)) return end
  return '不限期'
}

function pad(n: number): string {
  return String(n).padStart(2, '0')
}

function clamp(n: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, n))
}

function str(v: unknown): string {
  return typeof v === 'string' ? v : ''
}

function num(v: unknown): number {
  if (typeof v === 'number' && Number.isFinite(v)) return v
  if (typeof v === 'string' && v) {
    const n = Number(v)
    return Number.isFinite(n) ? n : 0
  }
  return 0
}
