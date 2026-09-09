export const SET_PAYGO = [
  { id: 'deepseek', name: 'DeepSeek', show: 'showQuotaDeepSeek' },
  { id: 'relay', name: 'Sub2API', show: 'showQuotaRelay' },
] as const

export const SET_AGENTS = [
  { id: 'dsh', name: 'DSH', windows: '会话 · 用量', show: 'showAgentDsh' },
  { id: 'trae', name: 'Trae', windows: '积分 · 用量', show: 'showQuotaTrae' },
  { id: 'workbuddy', name: 'WorkBuddy', windows: '积分 · 用量', show: 'showQuotaWorkBuddy' },
  { id: 'zcode', name: 'ZCode', windows: '5 小时 + 每周', show: 'showQuotaZcode' },
  { id: 'mimocode', name: 'MiMo', windows: '会话 · 用量', show: 'showAgentMimocode' },
  { id: 'cursor', name: 'Cursor', windows: '总用量 · Auto · API · Grok', show: 'showQuotaCursor' },
  { id: 'codex', name: 'Codex', windows: '5 小时 + 每周', show: 'showQuotaCodex' },
] as const

export type AgentId = (typeof SET_AGENTS)[number]['id']
export type AgentShowKey = (typeof SET_AGENTS)[number]['show']
export type PaygoShowKey = (typeof SET_PAYGO)[number]['show']

const AGENT_ALIAS: Record<string, AgentId> = {
  dsh: 'dsh',
  trae: 'trae',
  workbuddy: 'workbuddy',
  zcode: 'zcode',
  'zcode-5h': 'zcode',
  'zcode-week': 'zcode',
  mimocode: 'mimocode',
  cursor: 'cursor',
  'cursor-total': 'cursor',
  'cursor-auto': 'cursor',
  'cursor-api': 'cursor',
  'cursor-grok': 'cursor',
  codex: 'codex',
  'codex-5h': 'codex',
  'codex-7d': 'codex',
}

const DEFAULT_ORDER = SET_AGENTS.map((a) => a.id)

export function normalizeAgentOrder(raw: unknown): AgentId[] {
  const seen = new Set<AgentId>()
  const result: AgentId[] = []
  if (Array.isArray(raw)) {
    for (const id of raw) {
      if (typeof id !== 'string') continue
      const group = AGENT_ALIAS[id]
      if (!group || seen.has(group)) continue
      seen.add(group)
      result.push(group)
    }
  }
  for (const id of DEFAULT_ORDER) {
    if (seen.has(id)) continue
    result.push(id)
  }
  return result
}

export function moveItem<T>(list: T[], from: number, to: number): T[] {
  if (from === to || from < 0 || to < 0 || from >= list.length || to >= list.length) return list
  const next = list.slice()
  const [item] = next.splice(from, 1)
  next.splice(to, 0, item)
  return next
}
