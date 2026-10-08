// MCP 草稿 JSON 的双向解析：raw → 编辑器文本（toEditJson），草稿 → 保存体（parseMcpEntry）。
// 只做字段与类型转换，不持有编辑器状态。

export const NEW_TEMPLATE = `{
  "id": "",
  "transport": "stdio",
  "command": "",
  "args": [],
  "env": {},
  "enabled": true,
  "alias": "",
  "note": ""
}`

// 只拼编辑器需要的字段，不展示列表里的掩码配置。
export function toEditJson(raw: Record<string, unknown>) {
  const out: Record<string, unknown> = {
    id: raw.id,
    transport: raw.transport || 'stdio',
    enabled: raw.enabled !== false,
  }
  if (raw.command != null) out.command = raw.command
  if (Array.isArray(raw.args)) out.args = raw.args
  if (raw.env && typeof raw.env === 'object') out.env = raw.env
  if (raw.url) out.url = raw.url
  if (raw.headers && typeof raw.headers === 'object') out.headers = raw.headers
  if (raw.alias) out.alias = raw.alias
  if (raw.note) out.note = raw.note
  if (raw.startupTimeoutSec != null) out.startupTimeoutSec = raw.startupTimeoutSec
  if (raw.timeoutMs != null) out.timeoutMs = raw.timeoutMs
  if (raw.explicitType) out.explicitType = raw.explicitType
  return out
}

// 后端只接受字符串/字符串数组/字符串映射；类型不符的值会在保存时被置空或忽略，
// 与其静默损失已提供的配置，不如在提交前明确拒绝（R06）。
function requireStringMap(value: unknown, label: string): Record<string, string> {
  if (value == null) return {}
  if (typeof value !== 'object' || Array.isArray(value)) throw new Error(`${label} 需为对象`)
  const out: Record<string, string> = {}
  for (const [key, val] of Object.entries(value as Record<string, unknown>)) {
    if (typeof val !== 'string') throw new Error(`${label}.${key} 的值需为字符串`)
    out[key] = val
  }
  return out
}

// 兼容 Cursor 风格：mcpServers 包一层或单对象无 id（用 fallbackName 补齐）。
// mcpServers 解包必须发生在补名称之前：部分失败重试时 fallbackName 已由后端名称填上，
// 若仍把包装对象当顶层解析，会跳过解包构造出 command=null 的空体覆盖配置（R01）。
export function parseMcpEntry(text: string, fallbackName: string): Record<string, unknown> {
  const obj = JSON.parse(text) as Record<string, unknown>
  if (!obj || typeof obj !== 'object' || Array.isArray(obj))
    throw new Error('需要一个 JSON 对象')

  if (!obj.id && !obj.name && obj.mcpServers && typeof obj.mcpServers === 'object' && !Array.isArray(obj.mcpServers)) {
    const servers = obj.mcpServers as Record<string, unknown>
    const keys = Object.keys(servers)
    if (keys.length !== 1) throw new Error('mcpServers 包装需只包含一个 server')
    const one = servers[keys[0]]
    if (!one || typeof one !== 'object' || Array.isArray(one)) throw new Error('mcpServers 里的 server 需为 JSON 对象')
    return parseMcpEntry(JSON.stringify({ id: keys[0], ...(one as Record<string, unknown>) }), fallbackName)
  }

  let id = String(obj.id || obj.name || fallbackName || '').trim()

  const looksCursorValue = !obj.id && !obj.name && (obj.command || obj.url || obj.args || obj.env)
  if (looksCursorValue) {
    id = fallbackName.trim()
    if (!id) throw new Error('Cursor 格式请填写名称，或在 JSON 中加 id')
  }

  if (!id) throw new Error('缺少 id / name')

  const command = obj.command
  const url = obj.url
  // command/url 一旦出现就必须是字符串；String() 转换会让数字/布尔通过校验，
  // 后端却把它们读成 null，完整替换母本时启动配置被置空。
  if (command != null && typeof command !== 'string') throw new Error('command 需为字符串')
  if (url != null && typeof url !== 'string') throw new Error('url 需为字符串')

  const transportRaw = String(obj.transport || '').toLowerCase()
  const hasUrl = typeof url === 'string' && !!url.trim()
  const transport = transportRaw === 'http' || hasUrl ? 'http' : 'stdio'
  const enabled = obj.enabled !== false && obj.disabled !== true

  const body: Record<string, unknown> = {
    name: id,
    id,
    transport,
    enabled,
    command: command ?? null,
    url: url ?? null,
    alias: obj.alias ?? null,
    note: obj.note ?? null,
  }
  if (obj.args != null) {
    if (!Array.isArray(obj.args) || (obj.args as unknown[]).some((a) => typeof a !== 'string'))
      throw new Error('args 需为字符串数组')
    body.args = obj.args
  } else body.args = []
  body.env = requireStringMap(obj.env, 'env')
  body.headers = requireStringMap(obj.headers, 'headers')
  if (obj.startupTimeoutSec != null) body.startupTimeoutSec = obj.startupTimeoutSec
  if (obj.timeoutMs != null) body.timeoutMs = obj.timeoutMs
  if (obj.explicitType || obj.type) body.explicitType = obj.explicitType || obj.type
  // 提交前阻断明显不可用的配置：stdio 缺命令或 http 缺地址会覆盖掉已成功端的启动配置。
  if (transport === 'stdio' && !(typeof command === 'string' && command.trim()))
    throw new Error('stdio 配置需要填写 command')
  if (transport === 'http' && !hasUrl) throw new Error('http 配置需要填写 url')
  return body
}
