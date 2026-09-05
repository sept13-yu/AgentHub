import dsh from './assets/agents/deepseek.svg'
import trae from './assets/agents/trae.svg'
import workbuddy from './assets/agents/workbuddy.png'
import zcode from './assets/agents/zcode.svg'
import cursor from './assets/agents/cursor.svg'
import codex from './assets/agents/codex.png'
import relay from './assets/agents/relay.svg'

export const AGENT_ICON: Record<string, string> = {
  dsh,
  deepseek: dsh,
  trae,
  workbuddy,
  zcode,
  cursor,
  codex,
  relay,
}

export const AGENT_COLOR: Record<string, string> = {
  dsh: 'var(--src-dsh)',
  deepseek: 'var(--src-dsh)',
  trae: 'var(--src-trae)',
  workbuddy: 'var(--src-wb)',
  zcode: 'var(--src-zcode)',
  cursor: 'var(--src-cursor)',
  codex: 'var(--src-codex)',
  relay: 'var(--src-relay)',
}

export const AGENT_NAME: Record<string, string> = {
  dsh: 'DSH',
  deepseek: 'DeepSeek',
  trae: 'Trae',
  workbuddy: 'WorkBuddy',
  zcode: 'ZCode',
  cursor: 'Cursor',
  codex: 'Codex',
  relay: 'Sub2API',
}

export function agentName(id: string): string {
  return AGENT_NAME[id] ?? id
}

export function agentIcon(id: string | null | undefined): string | undefined {
  if (!id) return undefined
  return AGENT_ICON[id]
}

export function agentColor(id: string): string {
  return AGENT_COLOR[id] ?? 'var(--idle)'
}
