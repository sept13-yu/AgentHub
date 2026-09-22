import dsh from './assets/agents/deepseek.svg'
import trae from './assets/agents/trae.svg'
import workbuddy from './assets/agents/workbuddy.png'
import zcode from './assets/agents/zcode.svg'
import cursor from './assets/agents/cursor.svg'
import codex from './assets/agents/codex.png'
import mimocode from './assets/agents/mimocode.png'
import relay from './assets/agents/relay.svg'
import qoder from './assets/agents/qoder.svg'
import qoderCn from './assets/agents/qoder-cn.svg'
import grok from './assets/agents/grok.svg'

export const AGENT_ICON: Record<string, string> = {
  dsh,
  deepseek: dsh,
  trae,
  workbuddy,
  zcode,
  cursor,
  'cursor-cloud': cursor,
  codex,
  mimocode,
  grok,
  relay,
  qoder,
  'qoder-cn': qoderCn,
}

export const AGENT_COLOR: Record<string, string> = {
  dsh: 'var(--src-dsh)',
  deepseek: 'var(--src-dsh)',
  trae: 'var(--src-trae)',
  workbuddy: 'var(--src-wb)',
  zcode: 'var(--src-zcode)',
  cursor: 'var(--src-cursor)',
  'cursor-cloud': 'var(--src-cursor)',
  codex: 'var(--src-codex)',
  mimocode: 'var(--src-mimocode)',
  grok: 'var(--src-grok)',
  relay: 'var(--src-relay)',
  qoder: 'var(--src-qoder)',
  'qoder-cn': 'var(--src-qoder-cn)',
}

export const AGENT_NAME: Record<string, string> = {
  dsh: 'DSH',
  deepseek: 'DeepSeek',
  trae: 'Trae',
  workbuddy: 'WorkBuddy',
  zcode: 'ZCode',
  cursor: 'Cursor',
  'cursor-cloud': 'Cursor · 云端',
  codex: 'Codex',
  mimocode: 'MiMo',
  grok: 'Grok',
  relay: 'Sub2API',
  qoder: 'Qoder',
  'qoder-cn': 'Qoder CN',
}

function normalizeAgentId(id: string): string {
  if (id.startsWith('codex:') || id.startsWith('codex-')) return 'codex'
  if (id === 'cursor-cloud') return 'cursor'
  return id
}

export function agentName(id: string): string {
  const nid = normalizeAgentId(id)
  return AGENT_NAME[nid] ?? AGENT_NAME[id] ?? id
}

export function agentIcon(id: string | null | undefined): string | undefined {
  if (!id) return undefined
  return AGENT_ICON[normalizeAgentId(id)] ?? AGENT_ICON[id]
}

export function agentColor(id: string): string {
  const nid = normalizeAgentId(id)
  return AGENT_COLOR[nid] ?? AGENT_COLOR[id] ?? 'var(--idle)'
}
