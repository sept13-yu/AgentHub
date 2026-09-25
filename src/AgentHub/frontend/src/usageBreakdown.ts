import type { UsageAgent } from './usageView'

export type UsageDimension = 'agent' | 'model'
export interface BreakdownRow {
  key: string
  name: string
  tokens: number
  color: string
  agentId?: string
  modelId?: string
  noPrice?: boolean
  children?: BreakdownRow[]
}

const modelColors = ['var(--accent-solid)', 'var(--src-codex)', 'var(--src-mimocode)', 'var(--src-zcode)', 'var(--src-cursor)']
const byUsage = (a: BreakdownRow, b: BreakdownRow) => b.tokens - a.tokens || a.name.localeCompare(b.name, 'zh')

export function agentRows(agents: UsageAgent[]): BreakdownRow[] {
  return agents.filter(a => a.tokens > 0).map(a => ({
    key: a.id, name: a.name, agentId: a.id, tokens: a.tokens, color: a.color,
    children: a.models.filter(m => m.tokens > 0).map(m => ({
      key: m.id || m.name, name: m.name, tokens: m.tokens, color: a.color, noPrice: m.noPrice, modelId: m.id,
    })).sort(byUsage),
  })).sort(byUsage)
}

// 按稳定 modelId 合并；name 仅展示。
export function modelRows(agents: UsageAgent[]): BreakdownRow[] {
  const models = new Map<string, BreakdownRow>()
  for (const agent of agents) {
    for (const model of agent.models) {
      if (model.tokens <= 0) continue
      const id = model.id || model.name
      let row = models.get(id)
      if (!row) {
        row = { key: id, name: model.name, modelId: id, tokens: 0, color: '', children: [] }
        models.set(id, row)
      }
      row.tokens += model.tokens
      row.noPrice ||= model.noPrice
      const source = row.children!.find(s => s.key === agent.id)
      if (source) source.tokens += model.tokens
      else row.children!.push({ key: agent.id, name: agent.name, agentId: agent.id, tokens: model.tokens, color: agent.color })
    }
  }
  return [...models.values()].sort(byUsage).map((row, i) => ({
    ...row, color: modelColors[i % modelColors.length]!, children: row.children!.sort(byUsage),
  }))
}

export interface UsageSegment {
  key: string
  name: string
  tokens: number
  color: string
  rowKeys: string[]
}

export function usageSegments(rows: BreakdownRow[], dimension: UsageDimension, total: number): UsageSegment[] {
  const top = dimension === 'model' ? rows.slice(0, 5) : rows
  const segments: UsageSegment[] = top.map(row => ({ ...row, key: `row:${row.key}`, rowKeys: [row.key] }))
  const rest = dimension === 'model' ? rows.slice(5) : []
  if (rest.length) segments.push({
    key: 'other', name: '其他模型', tokens: rest.reduce((sum, row) => sum + row.tokens, 0),
    color: 'var(--idle)', rowKeys: rest.map(row => row.key),
  })
  const missing = total - rows.reduce((sum, row) => sum + row.tokens, 0)
  if (missing > 0) segments.push({ key: 'missing', name: '未细分用量', tokens: missing, color: 'var(--stroke-strong)', rowKeys: [] })
  return segments
}
