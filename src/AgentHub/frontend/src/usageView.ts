import { agentColor, agentName } from './agentMeta'
import { tokenUnit } from './tokenUnit'

export type RangeKey = 'today' | '7d' | 'month'

export const RANGES: { key: RangeKey; label: string; vs: string }[] = [
  { key: 'today', label: '今天', vs: '较昨日' },
  { key: '7d', label: '近 7 天', vs: '较上周' },
  { key: 'month', label: '本月', vs: '较上月' },
]

export interface UsageModel {
  name: string
  tokens: number
  pct: number
}

export interface UsageAgent {
  id: string
  name: string
  color: string
  tokens: number
  pct: number
  models: UsageModel[]
}

export interface UsageDay {
  date: string
  tokens: number
}

export interface UsageView {
  error: string | null
  totalTokens: number
  hero: string
  unit: string
  delta: { kind: 'up' | 'down' | 'flat'; vs: string; text: string } | null
  cost: { text: string } | null
  agents: UsageAgent[]
  days: UsageDay[]
}

export function emptyUsageView(): UsageView {
  return { error: null, totalTokens: 0, hero: '0', unit: '', delta: null, cost: null, agents: [], days: [] }
}

export function usageErrorView(message: string): UsageView {
  return { ...emptyUsageView(), error: message || '用量读取失败' }
}

export function formatTokens(n: number): string {
  const { val, unit } = splitTokens(n)
  return unit ? val + unit : val
}

export function splitTokens(n: number): { val: string; unit: string } {
  n = Number(n) || 0
  if (n < 0) n = 0
  if (tokenUnit.value === 'zh') {
    if (n >= 1e12) return { val: trimNum(n / 1e12, 2), unit: '万亿' }
    if (n >= 1e8) return { val: trimNum(n / 1e8, 2), unit: '亿' }
    if (n >= 1e7) return { val: trimNum(n / 1e7, 2), unit: '千万' }
    if (n >= 1e6) return { val: trimNum(n / 1e6, 2), unit: '百万' }
    if (n >= 1e4) return { val: trimNum(n / 1e4, 1), unit: '万' }
    return { val: String(Math.round(n)), unit: '' }
  }
  if (n >= 1e12) return { val: trimNum(n / 1e12, 2), unit: 'T' }
  if (n >= 1e9) return { val: trimNum(n / 1e9, 2), unit: 'B' }
  if (n >= 1e6) return { val: trimNum(n / 1e6, 2), unit: 'M' }
  if (n >= 1e3) return { val: trimNum(n / 1e3, 1), unit: 'K' }
  return { val: String(Math.round(n)), unit: '' }
}

export function visibleModels(agent: UsageAgent): UsageModel[] {
  if (agent.models.length <= 3) return agent.models
  const rest = agent.models.slice(3)
  const tokens = rest.reduce((sum, m) => sum + m.tokens, 0)
  const pct = rest.reduce((sum, m) => sum + m.pct, 0)
  return [...agent.models.slice(0, 3), { name: '其他', tokens, pct }]
}

export function toUsageView(raw: unknown, range: RangeKey = 'today'): UsageView {
  const body = asRecord(raw)
  if (!body) return usageErrorView('用量响应无法识别')
  const total = asRecord(body.total) ?? {}
  const tokens = num(total.tokens)
  const { val, unit } = splitTokens(tokens)
  const agents = readAgents(body.byAgent)
  const vs = RANGES.find((r) => r.key === range)?.vs ?? '较昨日'
  return {
    error: null,
    totalTokens: tokens,
    hero: val,
    unit,
    delta: readDelta(tokens, total.prevTokens, vs),
    cost: readCost(total),
    agents,
    days: readDays(body.days),
  }
}

function readDays(raw: unknown): UsageDay[] {
  if (!Array.isArray(raw)) return []
  const rows: UsageDay[] = []
  for (const item of raw) {
    const rec = asRecord(item)
    if (!rec) continue
    const date = str(rec.date)
    if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) continue
    const tokens = num(rec.tokens)
    if (tokens <= 0) continue
    rows.push({ date, tokens })
  }
  return rows
}

function readAgents(raw: unknown): UsageAgent[] {
  if (!Array.isArray(raw)) return []
  const rows: Omit<UsageAgent, 'pct'>[] = []
  for (const item of raw) {
    const rec = asRecord(item)
    if (!rec) continue
    const id = str(rec.id)
    if (!id) continue
    const tokens = num(rec.tokens)
    if (tokens <= 0) continue
    const models = readModels(rec.models)
    rows.push({ id, name: agentName(id), color: agentColor(id), tokens, models })
  }
  const shares = pctShares(rows.map((r) => r.tokens))
  return rows.map((r, i) => ({ ...r, pct: shares[i] ?? 0 }))
}

function readModels(raw: unknown): UsageModel[] {
  if (!Array.isArray(raw)) return []
  const rows: { name: string; tokens: number }[] = []
  for (const item of raw) {
    const rec = asRecord(item)
    if (!rec) continue
    const name = str(rec.name) || 'unknown'
    const tokens = num(rec.tokens)
    if (tokens <= 0) continue
    rows.push({ name, tokens })
  }
  rows.sort((a, b) => b.tokens - a.tokens)
  const shares = pctShares(rows.map((r) => r.tokens))
  return rows.map((r, i) => ({ ...r, pct: shares[i] ?? 0 }))
}

function readDelta(tokens: number, prevRaw: unknown, vs: string): UsageView['delta'] {
  if (prevRaw == null) return null
  const prev = num(prevRaw)
  if (prev <= 0) return null
  if (tokens === prev) return { kind: 'flat', vs, text: '持平' }
  const pct = Math.round(((tokens - prev) / prev) * 100)
  if (pct === 0) return { kind: 'flat', vs, text: '持平' }
  if (pct > 0) return { kind: 'up', vs, text: `${pct}%` }
  return { kind: 'down', vs, text: `${Math.abs(pct)}%` }
}

function readCost(total: Record<string, unknown>): UsageView['cost'] {
  if (total.cost == null)
    return total.costPartial === true ? { text: '无报价' } : null
  const cost = num(total.cost)
  const currency = str(total.currency) || 'CNY'
  const symbol = currency === 'USD' ? '$' : '¥'
  const text = symbol + cost.toFixed(2)
  return { text: total.costPartial === true ? text + ' · 部分无报价' : text }
}

function pctShares(values: number[]): number[] {
  const sum = values.reduce((a, b) => a + b, 0)
  if (sum <= 0) return values.map(() => 0)
  const raw = values.map((v) => (v / sum) * 100)
  const floors = raw.map(Math.floor)
  let rest = 100 - floors.reduce((a, b) => a + b, 0)
  const order = raw
    .map((v, i) => ({ i, frac: v - Math.floor(v) }))
    .sort((a, b) => b.frac - a.frac)
  const out = floors.slice()
  for (let k = 0; k < order.length && rest > 0; k++, rest--) out[order[k].i] += 1
  return out
}

function trimNum(n: number, digits: number): string {
  return n.toFixed(digits).replace(/\.?0+$/, '')
}

function asRecord(v: unknown): Record<string, unknown> | null {
  return v && typeof v === 'object' && !Array.isArray(v) ? v as Record<string, unknown> : null
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
