// MCP 页共享类型与纯函数；展示组件只消费这些，不直接调 API。

export interface McpAgentStatus {
  agentId: string
  displayName: string
  presence: string
  enabledOnAgent: boolean | null
  drift: boolean
  configPath: string
  detected: boolean
  detail: string | null
}

export interface McpItem {
  id: string
  alias: string | null
  note: string | null
  transport: string
  command: string | null
  args: string[]
  env: Record<string, string>
  url: string | null
  headers: Record<string, string>
  enabled: boolean
  inMother: boolean
  hasSecretRisk: boolean
  agents: McpAgentStatus[]
}

export interface McpAdapter {
  agentId: string
  displayName: string
  configPath: string
  detected: boolean
  count: number
  error: string | null
}

export interface McpPayload {
  motherPath: string
  motherEmpty: boolean
  targets: Record<string, boolean>
  adapters: McpAdapter[]
  items: McpItem[]
}

export interface McpPushResult {
  ok: boolean
  items: { name: string; agent: string; ok: boolean; error?: string }[]
  error?: string
}

// (item, agent) 组成 pending 集合的键；卡片展示与动作层必须用同一格式。
export function agentPendingKey(itemId: string, agentId: string): string {
  return JSON.stringify([itemId, agentId])
}

// DSH 写的是 home 共享层（对所有 profile 生效），运行中的实例要重启才加载新条目。
export function dshRestartSuffix(items: McpPushResult['items'] | undefined): string {
  return (items || []).some((x) => x.ok && x.agent === 'dsh') ? '；DSH 需重启生效' : ''
}

export function agentDisplayName(agent: { agentId: string; displayName: string }): string {
  return agent.agentId === 'dsh' ? 'DSH' : agent.displayName
}

// 已配置端 = 配置文件里存在该条目且适配器可读；这些端必须跟着标准配置同步。
export function presentAgentIds(item: McpItem): string[] {
  return item.agents.filter((a) => a.presence === 'present' && a.detected).map((a) => a.agentId)
}

export function isSystemItem(item: McpItem): boolean {
  return item.agents.some((a) => a.presence === 'system')
}

export function configFileName(path: string): string {
  if (!path) return ''
  const parts = path.replace(/\\/g, '/').split('/')
  return parts[parts.length - 1] || path
}
