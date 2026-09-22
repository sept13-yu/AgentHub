import { AGENT_COLOR, AGENT_NAME } from './agentMeta'

export interface QuotaWindow {
  name: string
  remain: number
  period: string
  hot: boolean
}

export type QuotaTile =
  | { kind: 'balance'; id: string; name: string; color: string; text: string; dueHint?: boolean; plan?: string }
  | { kind: 'windows'; id: string; name: string; color: string; span: 1 | 2; windows: QuotaWindow[]; plan?: string }

/** remain 条目归属的 Agent 组与短名（同组可多窗拼成一块砖）。动态 codex:{key}:* 不在此表。 */
const WINDOW_META: Record<string, { group: string; short: string }> = {
  'cursor-auto': { group: 'cursor', short: 'Auto' },
  'cursor-api': { group: 'cursor', short: 'API' },
  'cursor-grok': { group: 'cursor', short: 'Grok Bot' },
  'zcode-5h': { group: 'zcode', short: '5 小时' },
  'zcode-week': { group: 'zcode', short: '每周' },
  'codex-5h': { group: 'codex', short: '5 小时' },
  'codex-7d': { group: 'codex', short: '每周' },
  'qoder-credits': { group: 'qoder', short: '额度' },
  'qoder-calls': { group: 'qoder', short: '体验' },
  'qoder-cn-credits': { group: 'qoder-cn', short: '额度' },
  'qoder-cn-calls': { group: 'qoder-cn', short: '体验' },
}

/** 多账号 Codex：codex:live:5h / codex:auth-xxx:7d */
function codexWindow(id: string): { group: string; short: string } | null {
  const m = /^codex:([^:]+):(5h|7d)$/.exec(id)
  if (!m) return null
  return { group: `codex:${m[1]}`, short: m[2] === '5h' ? '5 小时' : '每周' }
}

function windowMeta(id: string): { group: string; short: string } | null {
  return WINDOW_META[id] ?? codexWindow(id)
}

export function toQuotaTiles(raw: unknown): QuotaTile[] {
  const body = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw as Record<string, unknown> : null
  const items = Array.isArray(body?.items) ? body.items : Array.isArray(raw) ? raw : []
  const list: Record<string, unknown>[] = []
  for (const item of items) {
    if (item && typeof item === 'object' && !Array.isArray(item))
      list.push(item as Record<string, unknown>)
  }

  const tiles: QuotaTile[] = []
  const used = new Set<number>()
  for (let i = 0; i < list.length; i++) {
    if (used.has(i)) continue
    const rec = list[i]
    const id = str(rec.id)
    const kind = str(rec.kind)
    if (kind === 'balance') {
      tiles.push(balanceTile(id, rec))
      continue
    }
    if (kind !== 'remain') continue
    const meta = windowMeta(id)
    if (!meta) continue

    const members: { id: string; rec: Record<string, unknown> }[] = []
    for (let j = i; j < list.length; j++) {
      if (used.has(j)) continue
      if (str(list[j].kind) !== 'remain') continue
      const rid = str(list[j].id)
      const rmeta = windowMeta(rid)
      if (!rmeta || rmeta.group !== meta.group) continue
      members.push({ id: rid, rec: list[j] })
      used.add(j)
    }

    const windows = members
      .map((m) => windowOf(m.id, m.rec))
      .filter((w): w is QuotaWindow => !!w)
    const label = str(rec.label)
    const isCodexAccount = meta.group.startsWith('codex:')
    tiles.push({
      kind: 'windows',
      id: meta.group,
      name: label || (isCodexAccount ? meta.group.slice(6) : AGENT_NAME[meta.group] ?? meta.group),
      color: AGENT_COLOR[isCodexAccount ? 'codex' : meta.group] ?? 'var(--idle)',
      span: 1,
      windows,
      plan: subscriptionPlan(str(rec.plan)) || undefined,
    })
  }
  return tiles
}

function balanceTile(id: string, rec: Record<string, unknown>): QuotaTile {
  const unit = str(rec.unit)
  return {
    kind: 'balance',
    id,
    name: str(rec.name) || AGENT_NAME[id] || id,
    color: AGENT_COLOR[id] ?? 'var(--src-relay)',
    text: formatBalance(num(rec.value), unit),
    dueHint: id === 'trae' || id === 'workbuddy',
    plan: subscriptionPlan(str(rec.plan)) || balanceKind(unit),
  }
}

function windowOf(id: string, rec: Record<string, unknown>): QuotaWindow {
  const remain = clamp(num(rec.remainPercent), 0, 100)
  return {
    name: windowMeta(id)?.short || str(rec.name) || id,
    remain,
    period: formatPeriod(str(rec.period)),
    hot: remain < 10,
  }
}

function balanceKind(unit: string): '余额' | '积分' {
  const u = unit.trim()
  if (u === 'CNY' || u === '¥' || u === 'USD' || u === '$') return '余额'
  return '积分'
}

/** 订阅套餐才进角标；「钱包余额 / 福利积分」这类品类名丢掉。 */
function subscriptionPlan(raw: string): string {
  const t = formatPlan(raw)
  if (!t) return ''
  if (/余额|积分/.test(t.replace(/\s+/g, ''))) return ''
  if (/^(wallet|balance|credits?|points?)$/i.test(t)) return ''
  return t
}

function formatPlan(raw: string): string {
  const t = raw.trim()
  if (!t) return ''
  if (/^[a-z][a-z0-9+._-]*$/i.test(t)) return t.toUpperCase()
  return t
}

function formatBalance(value: number, unit: string): string {
  const u = unit.trim()
  if (u === 'CNY' || u === '¥') return '¥' + value.toFixed(2)
  if (u === 'USD' || u === '$') return '$' + value.toFixed(2)
  const n = new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 2 }).format(value)
  if (!u || u === '积分') return n
  return `${n} ${u}`
}

function formatPeriod(period: string): string {
  if (!period) return '不限期'
  const end = period.includes('—') ? period.split('—').pop()!.trim() : period.trim()
  if (!end || end === 'never') return '不限期'
  const ms = Date.parse(end)
  if (Number.isNaN(ms)) return end
  const d = new Date(ms)
  // Qoder 等会用超远 expiresAt 表示无重置日；填「不限期」避免空 period 挤短进度条
  if (d.getFullYear() >= 2100) return '不限期'
  const now = new Date()
  const sameDay = d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth() && d.getDate() === now.getDate()
  if (sameDay) {
    return '重置 ' + d.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false })
  }
  const sameYear = d.getFullYear() === now.getFullYear()
  const date = sameYear
    ? `${d.getMonth() + 1}/${d.getDate()}`
    : `${d.getFullYear()}-${d.getMonth() + 1}-${d.getDate()}`
  return '重置 ' + date
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
