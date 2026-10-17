import type { QuotaWindowRow } from './quotaWindows'

export interface QuotaDialAppearance {
  size: number
  glow: number
  arcMode: 'quota' | 'full'
}

export function normalizeQuotaDialAppearance(value: Partial<QuotaDialAppearance> = {}): QuotaDialAppearance {
  return {
    size: Number.isFinite(value.size) ? Math.max(180, Math.min(460, Math.round(value.size!))) : 360,
    glow: Number.isFinite(value.glow) ? Math.max(0, Math.min(.6, value.glow!)) : .12,
    arcMode: value.arcMode === 'full' ? 'full' : 'quota',
  }
}

export function quotaDialProvider(row: QuotaWindowRow): string {
  if (/^codex(?::|-)/i.test(row.id)) return 'Codex'
  if (/^cursor-/i.test(row.id)) return 'Cursor'
  return ''
}

export function quotaDialBrand(rows: QuotaWindowRow[]): string {
  const brands = rows.map(quotaDialProvider)
  return brands.length && brands[0] && brands.every(brand => brand === brands[0]) ? brands[0] : 'AgentHub'
}

export function quotaDialLabel(row: QuotaWindowRow | undefined, mixed: boolean, distinguishAccount = false): string {
  if (!row) return '未选择'
  const cursor: Record<string, string> = { 'cursor-total': '总用量', 'cursor-auto': 'Auto', 'cursor-api': 'API', 'cursor-grok': 'Grok Bot' }
  const provider = quotaDialProvider(row)
  const short = cursor[row.id] || row.name.split(' · ').pop() || row.name
  let label = mixed && provider ? `${provider} ${short}` : short
  if (distinguishAccount && provider === 'Codex' && row.name.includes(' · ')) {
    const account = row.name.split(' · ')[0]
    const compact = account.length > 6 ? account.slice(0, 3) + '…' + account.slice(-2) : account
    label = `${compact} ${short}`
  }
  return Array.from(label).length > 12 ? Array.from(label).slice(0, 11).join('') + '…' : label
}

export function quotaDialReset(rows: QuotaWindowRow[], now: number): { label: string; value: string } {
  const resets = rows.map(row => row.resetMs).filter((ms): ms is number => ms !== null && Number.isFinite(ms))
  if (!resets.length) return { label: '重置时间', value: '暂无时间' }
  const next = Math.min(...resets)
  if (next <= now) return { label: '下次重置', value: '即将重置' }
  const date = new Date(next)
  const current = new Date(now)
  const clock = `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
  const sameDay = date.getFullYear() === current.getFullYear() && date.getMonth() === current.getMonth() && date.getDate() === current.getDate()
  return { label: '下次重置', value: sameDay ? clock : `${date.getMonth() + 1}/${date.getDate()} ${clock}` }
}
