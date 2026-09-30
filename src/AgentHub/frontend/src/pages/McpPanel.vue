<script setup lang="ts">
import { computed, inject, onMounted, ref, type Ref } from 'vue'
import { NButton, NCheckbox, NIcon, NInput, NModal, NSwitch, useMessage } from 'naive-ui'
import {
  FileJson,
  Plus,
  RefreshCw,
} from 'lucide-vue-next'
import { get, post, WRITABLE } from '../api'
import { ahMenuKey, type AhMenuItem } from '../ahMenu'
import { usePageHotkeys } from '../hotkeys'
import AhConfirm from '../components/AhConfirm.vue'
import AgentMark from '../components/AgentMark.vue'

const message = useMessage()
const pageLoading = inject<Ref<boolean>>('page-loading')
const menu = inject(ahMenuKey)
const readonly = !WRITABLE

const RISK_ACK_KEY = 'agenthub.mcp.pushRiskAck'

interface AgentStatus {
  agentId: string
  displayName: string
  presence: string
  enabledOnAgent: boolean | null
  drift: boolean
  configPath: string
  detected: boolean
  detail: string | null
}
interface McpItem {
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
  agents: AgentStatus[]
}
interface AdapterInfo {
  agentId: string
  displayName: string
  configPath: string
  detected: boolean
  count: number
  error: string | null
}
interface McpPayload {
  motherPath: string
  motherEmpty: boolean
  targets: Record<string, boolean>
  adapters: AdapterInfo[]
  items: McpItem[]
}
interface PushResult {
  ok: boolean
  items: { name: string; agent: string; ok: boolean; error?: string }[]
  error?: string
}

const data = ref<McpPayload | null>(null)
const q = ref('')
const editShow = ref(false)
const editLoading = ref(false)
const editSaving = ref(false)
const editLoaded = ref(false)
const editError = ref('')
const editingExisting = ref(false)
const editingItem = ref<McpItem | null>(null)
const busyAgentKey = ref('')
const riskConfirmShow = ref(false)
let riskConfirmResolve: ((ok: boolean) => void) | null = null

const jsonText = ref('')
const createName = ref('')
const editTargets = ref<string[]>([])
let editRequestId = 0

const deleteShow = ref(false)
const deleteItem = ref<McpItem | null>(null)
const deleteTargets = ref<string[]>([])
const deleteBusy = ref(false)

const NEW_TEMPLATE = `{
  "id": "",
  "transport": "stdio",
  "command": "",
  "args": [],
  "env": {},
  "enabled": true,
  "alias": "",
  "note": ""
}`

const filtered = computed(() => {
  const list = data.value?.items ?? []
  const needle = q.value.trim().toLowerCase()
  if (!needle) return list
  return list.filter((i) =>
    i.id.toLowerCase().includes(needle)
    || (i.alias || '').toLowerCase().includes(needle)
    || (i.note || '').toLowerCase().includes(needle)
    || (i.command || '').toLowerCase().includes(needle)
    || (i.url || '').toLowerCase().includes(needle))
})

const deletePresentAgents = computed(() =>
  (deleteItem.value?.agents ?? []).filter((a) => a.presence === 'present' && a.detected))

const detectedAdapters = computed(() => (data.value?.adapters ?? []).filter((a) => a.detected))
const writableAdapters = computed(() => detectedAdapters.value.filter((a) => !a.error))
const adapterErrors = computed(() => detectedAdapters.value.filter((a) => !!a.error))
const requiredEditAgents = computed(() => editingItem.value?.agents.filter((a) => a.presence === 'present' && a.detected) ?? [])
const optionalEditAdapters = computed(() => writableAdapters.value.filter((a) =>
  !requiredEditAgents.value.some((present) => present.agentId === a.agentId)))

function agentName(agent: { agentId: string; displayName: string }) {
  return agent.agentId === 'dsh' ? 'DSH' : agent.displayName
}

function presentAgentIds(item: McpItem) {
  return item.agents.filter((a) => a.presence === 'present' && a.detected).map((a) => a.agentId)
}

function cardAgents(item: McpItem) {
  return item.agents.filter((a) => a.detected)
}

function defaultEditTargets(item?: McpItem | null) {
  if (item) {
    return presentAgentIds(item)
  }
  return writableAdapters.value.map((a) => a.agentId)
}

function toggleEditTarget(id: string, on: boolean) {
  if (!on && requiredEditAgents.value.some((a) => a.agentId === id)) return
  const set = new Set(editTargets.value)
  if (on) set.add(id)
  else set.delete(id)
  editTargets.value = [...set]
}

function toggleDeleteTarget(id: string, on: boolean) {
  const set = new Set(deleteTargets.value)
  if (on) set.add(id)
  else set.delete(id)
  deleteTargets.value = [...set]
}


function setLoading(on: boolean) {
  if (pageLoading) pageLoading.value = on
}

function presenceClass(p: string) {
  switch (p) {
    case 'present': return 'ok'
    case 'missing': return 'miss'
    case 'system': return 'sys'
    case 'unsupported': return 'off'
    case 'error': return 'error'
    default: return ''
  }
}

function agentChipTitle(a: AgentStatus) {
  const name = agentName(a)
  // DSH 写的是 home 共享层（对所有 profile 生效），运行中的实例要重启才加载
  const dshNote = a.agentId === 'dsh' ? '\n写入后需重启 DSH 生效' : ''
  switch (a.presence) {
    case 'present':
      return `${name} · 已有（${a.enabledOnAgent ? '开' : '关'}）\n${a.configPath}${dshNote}`
    case 'missing':
      return `${name} · 缺失（点开写入）\n${a.configPath}${dshNote}`
    case 'system':
      return `${name} · 系统`
    case 'unsupported':
      return `${name} · 未装`
    case 'error':
      return `${name} · ${a.detail || '读取配置失败'}\n${a.configPath}`
    default:
      return name
  }
}

function configFileName(path: string) {
  if (!path) return ''
  const parts = path.replace(/\\/g, '/').split('/')
  return parts[parts.length - 1] || path
}

function isSystemItem(item: McpItem) {
  return item.agents.some((a) => a.presence === 'system')
}

function agentSwitchOn(a: AgentStatus) {
  if (a.presence === 'present') return !!a.enabledOnAgent
  return false
}

function isRiskAcked() {
  try {
    return sessionStorage.getItem(RISK_ACK_KEY) === '1'
  } catch {
    return false
  }
}

function ensureRiskAck(): Promise<boolean> {
  if (isRiskAcked()) return Promise.resolve(true)
  riskConfirmShow.value = true
  return new Promise((resolve) => {
    riskConfirmResolve = resolve
  })
}

function onRiskConfirm() {
  try {
    sessionStorage.setItem(RISK_ACK_KEY, '1')
  } catch { /* ignore */ }
  riskConfirmShow.value = false
  const resolve = riskConfirmResolve
  riskConfirmResolve = null
  resolve?.(true)
}

function onRiskShowUpdate(show: boolean) {
  riskConfirmShow.value = show
  if (!show && riskConfirmResolve) {
    const resolve = riskConfirmResolve
    riskConfirmResolve = null
    resolve(false)
  }
}

async function load() {
  setLoading(true)
  try {
    data.value = await get<McpPayload>('/api/mcp')
  } catch (e) {
    message.error(e instanceof Error ? e.message : '读取失败')
  } finally {
    setLoading(false)
  }
}

async function refresh() {
  await load()
  const n = data.value?.items.length ?? 0
  if (adapterErrors.value.length) message.warning(`已刷新，共 ${n} 项；${adapterErrors.value.length} 个配置读取失败`)
  else message.success(`已刷新，共 ${n} 项`)
}

// 写入结果里含 dsh 时提醒重启（共享 patch 不热加载新增/删除条目）
function dshRestartSuffix(items: PushResult['items']) {
  return (items || []).some((x) => x.ok && x.agent === 'dsh') ? '；DSH 需重启生效' : ''
}

async function doPush(names: string[], targets?: string[], source?: string) {
  const r = await post<PushResult>('/api/mcp/push', {
    names,
    ...(targets?.length ? { targets } : {}),
    ...(source ? { source } : {}),
  })
  const fail = (r.items || []).filter((x) => !x.ok)
  const okCount = (r.items || []).filter((x) => x.ok && x.agent !== '*').length
  if (fail.length) message.warning(`推送完成：成功 ${okCount}，失败 ${fail.length}${dshRestartSuffix(r.items)}`)
  else if (okCount === 0) message.info('没有需要推送的缺失端')
  else message.success(`已推送到 ${okCount} 处${dshRestartSuffix(r.items)}`)
  return r
}

async function onAgentSwitch(item: McpItem, agent: AgentStatus, wantOn: boolean) {
  if (readonly) return
  if (agent.presence === 'system' || agent.presence === 'unsupported' || agent.presence === 'error') return
  if (!wantOn && agent.presence === 'missing') return

  const key = `${item.id}:${agent.agentId}`
  busyAgentKey.value = key
  try {
    if (wantOn && agent.presence === 'missing') {
      if (!(await ensureRiskAck())) return
      await doPush([item.id], [agent.agentId])
      await load()
      return
    }
    if (agent.presence === 'present') {
      await post('/api/mcp/agent-enable', { agent: agent.agentId, name: item.id, enabled: wantOn })
      if (agent.agentId === 'dsh') message.info('DSH 需重启生效')
      await load()
      return
    }
  } catch (e) {
    message.error(e instanceof Error ? e.message : '操作失败')
  } finally {
    busyAgentKey.value = ''
  }
}

async function openAgent(agent: string) {
  if (readonly) return
  try {
    const r = await post<{ ok: boolean; path?: string }>('/api/mcp/open', { agent })
    if (r.path) message.success(`已定位 ${configFileName(r.path)}`)
  } catch (e) {
    message.error(e instanceof Error ? e.message : '打开失败')
  }
}

function prettyJson() {
  try {
    const obj = JSON.parse(jsonText.value)
    jsonText.value = JSON.stringify(obj, null, 2)
  } catch {
    message.warning('JSON 无效，无法格式化')
  }
}

function openCreate() {
  if (readonly || editSaving.value) return
  editRequestId++
  editingExisting.value = false
  editingItem.value = null
  editLoading.value = false
  editLoaded.value = true
  editError.value = ''
  createName.value = ''
  jsonText.value = NEW_TEMPLATE
  editTargets.value = defaultEditTargets(null)
  editShow.value = true
}

function toEditJson(raw: Record<string, unknown>) {
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

async function openEdit(item: McpItem) {
  if (readonly || editSaving.value) return
  const requestId = ++editRequestId
  editLoading.value = true
  editLoaded.value = false
  editError.value = ''
  jsonText.value = ''
  editShow.value = true
  editingExisting.value = true
  editingItem.value = item
  createName.value = item.id
  editTargets.value = defaultEditTargets(item)
  try {
    const raw = await get<Record<string, unknown>>(`/api/mcp/raw?name=${encodeURIComponent(item.id)}`)
    if (requestId !== editRequestId || !editShow.value) return
    jsonText.value = JSON.stringify(toEditJson(raw), null, 2)
    editLoaded.value = true
  } catch (e) {
    if (requestId === editRequestId) editError.value = e instanceof Error ? e.message : '读取配置失败'
  } finally {
    if (requestId === editRequestId) editLoading.value = false
  }
}

function parseServerEntry(text: string): Record<string, unknown> {
  const obj = JSON.parse(text) as Record<string, unknown>
  if (!obj || typeof obj !== 'object' || Array.isArray(obj))
    throw new Error('需要一个 JSON 对象')

  // Full server entry
  let id = String(obj.id || obj.name || createName.value || '').trim()

  // Cursor-style: maybe wrapped as { "mcpServers": { "name": {...} } }
  if (!id && obj.mcpServers && typeof obj.mcpServers === 'object') {
    const servers = obj.mcpServers as Record<string, unknown>
    const keys = Object.keys(servers)
    if (keys.length === 1) {
      id = keys[0]
      const one = servers[id]
      if (one && typeof one === 'object')
        return parseServerEntry(JSON.stringify({ id, ...(one as object) }))
    }
  }

  // Cursor single server value without id — need name field
  const looksCursorValue = !obj.id && !obj.name && (obj.command || obj.url || obj.args || obj.env)
  if (looksCursorValue) {
    id = createName.value.trim()
    if (!id) throw new Error('Cursor 格式请填写名称，或在 JSON 中加 id')
  }

  if (!id) throw new Error('缺少 id / name')

  const transportRaw = String(obj.transport || '').toLowerCase()
  const hasUrl = !!(obj.url && String(obj.url).trim())
  const transport = transportRaw === 'http' || hasUrl ? 'http' : 'stdio'
  const enabled = obj.enabled !== false && obj.disabled !== true

  const body: Record<string, unknown> = {
    name: id,
    id,
    transport,
    enabled,
    command: obj.command ?? null,
    url: obj.url ?? null,
    alias: obj.alias ?? null,
    note: obj.note ?? null,
  }
  if (Array.isArray(obj.args)) body.args = obj.args
  else body.args = []
  if (obj.env && typeof obj.env === 'object') body.env = obj.env
  else body.env = {}
  if (obj.headers && typeof obj.headers === 'object') body.headers = obj.headers
  else body.headers = {}
  if (obj.startupTimeoutSec != null) body.startupTimeoutSec = obj.startupTimeoutSec
  if (obj.timeoutMs != null) body.timeoutMs = obj.timeoutMs
  if (obj.explicitType || obj.type) body.explicitType = obj.explicitType || obj.type
  return body
}

async function saveEdit() {
  if (readonly || editLoading.value || editSaving.value || !editLoaded.value) return
  let body: Record<string, unknown>
  try {
    body = parseServerEntry(jsonText.value)
    if (editingExisting.value && body.name !== createName.value) throw new Error('已有 MCP 请保留原名称')
  } catch (e) {
    message.warning(e instanceof Error ? e.message : 'JSON 无效')
    return
  }
  const targets = [...new Set([...editTargets.value, ...requiredEditAgents.value.map((a) => a.agentId)])]
  if (!editingExisting.value && !targets.length) {
    message.warning('请至少选择一个目标端')
    return
  }
  editSaving.value = true
  editError.value = ''
  try {
    if (!(await ensureRiskAck())) return
    const r = await post<PushResult>('/api/mcp/upsert-agents', {
      ...body,
      targets,
    })
    const fail = (r.items || []).filter((x) => !x.ok)
    const okCount = (r.items || []).filter((x) => x.ok && x.agent !== '*' && x.agent !== 'mother').length
    if (!r.ok || fail.length) {
      const details = [r.error, ...fail.map((x) => `${x.agent}：${x.error || '同步失败'}`)].filter(Boolean).join('；')
      editError.value = `保存或同步未完成：${details || '请检查配置后重试'}${dshRestartSuffix(r.items)}`
      await load()
      const current = data.value?.items.find((item) => item.id === body.name)
      if (current) {
        editingExisting.value = true
        editingItem.value = current
        createName.value = current.id
      }
      return
    }
    message.success(`已保存标准配置并同步 ${okCount} 个 Agent${dshRestartSuffix(r.items)}`)
    editShow.value = false
    await load()
  } catch (e) {
    editError.value = e instanceof Error ? e.message : '保存失败'
  } finally {
    editSaving.value = false
  }
}


function onCardMenu(e: MouseEvent, item: McpItem) {
  if (!menu) return
  const items: AhMenuItem[] = [
    {
      key: 'edit',
      label: '编辑',
      disabled: readonly,
      handler: () => openEdit(item),
    },
    { key: 'sep', separator: true },
    {
      key: 'del',
      label: '删除',
      danger: true,
      disabled: readonly || isSystemItem(item),
      handler: () => askDelete(item),
    },
  ]
  menu.open(e, items)
}

function askDelete(item: McpItem) {
  if (readonly || isSystemItem(item)) return
  deleteItem.value = item
  deleteTargets.value = presentAgentIds(item)
  deleteShow.value = true
}

async function confirmDelete() {
  if (!deleteItem.value || readonly) return
  const targets = [...deleteTargets.value]
  if (!targets.length) {
    message.warning('请至少选择一个端')
    return
  }
  deleteBusy.value = true
  try {
    const r = await post<PushResult>('/api/mcp/remove', {
      name: deleteItem.value.id,
      fromMother: true,
      targets,
    })
    const fail = (r.items || []).filter((x) => !x.ok && x.agent !== 'mother')
    if (fail.length) message.warning(`从所选端移除部分失败（${fail.length}）${dshRestartSuffix(r.items)}`)
    else message.success(`已从所选端移除${dshRestartSuffix(r.items)}`)
    deleteShow.value = false
    deleteItem.value = null
    await load()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '删除失败')
  } finally {
    deleteBusy.value = false
  }
}

usePageHotkeys({
  refresh: () => { void refresh() },
})

onMounted(() => { void load() })
</script>

<template>
  <teleport defer to="#chrome-actions">
    <n-input v-model:value="q" class="mcp-search" placeholder="搜索名称/备注" clearable />
    <n-button :disabled="readonly" @click="openCreate">
      <template #icon><n-icon><Plus :size="16" :stroke-width="1.8" /></n-icon></template>
      新建
    </n-button>
    <n-button @click="refresh">
      <template #icon><n-icon><RefreshCw :size="16" :stroke-width="1.8" /></n-icon></template>
      刷新
    </n-button>
  </teleport>

  <div class="mcp">

    <div class="mcp-adapters" v-if="detectedAdapters.length">
      <button
        v-for="a in detectedAdapters"
        :key="a.agentId"
        type="button"
        class="mcp-adapter"
        :disabled="readonly"
        :title="a.configPath"
        @click="openAgent(a.agentId)"
      >
        <AgentMark :id="a.agentId" />
        <b>{{ agentName(a) }}</b>
        <span class="mcp-adapter-file">{{ configFileName(a.configPath) }}</span>
      </button>
    </div>

    <p v-if="readonly" class="hint">浏览器只读。编辑、保存和同步配置请到 AgentHub 桌面操作。</p>

    <div v-if="adapterErrors.length" class="mcp-read-errors" role="alert">
      <p v-for="a in adapterErrors" :key="a.agentId">
        {{ agentName(a) }}：{{ a.error }}。请检查 {{ a.configPath }}
      </p>
    </div>

    <div v-if="!filtered.length" class="docs-empty">
      {{ adapterErrors.length ? '暂无可显示的 MCP 项，请先处理上方读取错误' : '暂无 MCP 项' }}
    </div>

    <div v-else class="mcp-grid">
    <article
      v-for="item in filtered"
      :key="item.id"
      class="mcp-card"
      @contextmenu="onCardMenu($event, item)"
    >
      <header class="mcp-card-head" @dblclick="!readonly && openEdit(item)">
        <div class="mcp-title">
          <b>{{ item.alias || item.id }}</b>
          <code v-if="item.alias">{{ item.id }}</code>
          <span class="tag">{{ item.transport }}</span>
          <span v-if="item.hasSecretRisk" class="tag risk">含密钥</span>
        </div>
        <n-button size="small" :disabled="readonly" :title="readonly ? '请到 AgentHub 桌面操作' : '编辑 MCP'" @click.stop="openEdit(item)">编辑</n-button>
      </header>
      <p v-if="item.note" class="mcp-note">{{ item.note }}</p>
      <p class="mcp-cmd" :title="item.transport === 'http' ? item.url || '' : item.command || ''">
        <template v-if="item.transport === 'http'">{{ item.url || '（无 URL）' }}</template>
        <template v-else>{{ item.command || '（无 command）' }}</template>
      </p>
      <div class="mcp-agents">
        <div
          v-for="a in cardAgents(item)"
          :key="a.agentId"
          class="mcp-agent"
          :class="presenceClass(a.presence)"
          :title="agentChipTitle(a)"
        >
          <AgentMark :id="a.agentId" />
          <span class="name">{{ agentName(a) }}</span>
          <button v-if="a.drift" type="button" class="drift" :disabled="readonly" :title="readonly ? '请到 AgentHub 桌面操作' : '编辑统一配置并同步所有已配置 Agent'" @click.stop="openEdit(item)">待同步</button>
          <span v-if="a.presence === 'error'" class="mcp-agent-error">读取失败</span>
          <n-switch
            v-if="a.presence === 'present' || a.presence === 'missing'"
            size="small"
            :value="agentSwitchOn(a)"
            :disabled="readonly || busyAgentKey === `${item.id}:${a.agentId}`"
            @update:value="(v: boolean) => onAgentSwitch(item, a, v)"
          />
        </div>
      </div>
    </article>
    </div>
  </div>

  <n-modal
    v-model:show="editShow"
    preset="card"
    :title="editingExisting ? `${createName}：编辑统一配置` : '新建 MCP'"
    style="width: min(720px, 94vw)"
    :mask-closable="!editSaving"
    :close-on-esc="!editSaving"
    :closable="!editSaving"
  >
    <div class="mcp-form" v-if="!editLoading">
      <p v-if="editError" class="mcp-read-errors" role="alert">{{ editError }}</p>
      <n-button v-if="!editLoaded && editingItem" @click="openEdit(editingItem)">重新读取配置</n-button>
      <label v-if="!editingExisting" class="mcp-name-field">
        名称（Cursor 单对象无 id 时必填）
        <n-input v-model:value="createName" :disabled="editSaving" placeholder="如 jira" />
      </label>
      <label>
        JSON（统一标准配置，单个 server）
        <n-input
          v-model:value="jsonText"
          type="textarea"
          class="mcp-json"
          :disabled="!editLoaded || editSaving"
          :autosize="{ minRows: 14, maxRows: 28 }"
          placeholder='{"id":"jira","transport":"stdio",...}'
        />
      </label>
      <div v-if="editingExisting" class="mcp-form-row mcp-targets">
        <span class="mcp-targets-label">同步到所有已配置 Agent</span>
        <n-checkbox v-for="a in requiredEditAgents" :key="a.agentId" checked disabled>{{ agentName(a) }}</n-checkbox>
        <span v-if="!requiredEditAgents.length" class="hint">当前仅保存标准配置，可在下方选择新增 Agent。</span>
      </div>
      <div class="mcp-form-row mcp-targets" v-if="!editingExisting || optionalEditAdapters.length">
        <span class="mcp-targets-label">{{ editingExisting ? '新增到' : '同步到' }}</span>
        <n-checkbox
          v-for="a in (editingExisting ? optionalEditAdapters : writableAdapters)"
          :key="a.agentId"
          :checked="editTargets.includes(a.agentId)"
          :disabled="readonly || editSaving || !editLoaded"
          @update:checked="(v: boolean) => toggleEditTarget(a.agentId, v)"
        >
          {{ agentName(a) }}
        </n-checkbox>
      </div>
      <p class="hint">保存一份标准配置，并同步所有已配置 Agent 和新增所选 Agent。已有 Agent 的启用状态保留。密钥会明文写入本机各端配置。</p>
      <div class="mcp-form-actions">
        <n-button :disabled="!editLoaded || editSaving" @click="prettyJson">
          <template #icon><n-icon><FileJson :size="16" :stroke-width="1.8" /></n-icon></template>
          格式化
        </n-button>
        <n-button :disabled="editSaving" @click="editShow = false">取消</n-button>
        <n-button type="primary" :disabled="readonly || !editLoaded" :loading="editSaving" @click="saveEdit">保存并同步</n-button>
      </div>
    </div>
    <p v-else class="hint">加载中…</p>
  </n-modal>

  <AhConfirm
    :show="deleteShow"
    :text="deleteItem ? `从所选端移除「${deleteItem.alias || deleteItem.id}」？` : ''"
    ok-text="移除"
    @update:show="(v: boolean) => (deleteShow = v)"
    @confirm="confirmDelete"
  >
    <div class="mcp-form-row mcp-targets" style="margin-bottom: 12px">
      <n-checkbox
        v-for="a in deletePresentAgents"
        :key="a.agentId"
        :checked="deleteTargets.includes(a.agentId)"
        :disabled="readonly"
        @update:checked="(v: boolean) => toggleDeleteTarget(a.agentId, v)"
      >
        {{ agentName(a) }}
      </n-checkbox>
    </div>
  </AhConfirm>

  <AhConfirm
    :show="riskConfirmShow"
    text="推送/写入含密钥时会明文写入本机各 Agent 配置。确认后本会话不再提示。"
    @update:show="onRiskShowUpdate"
    @confirm="onRiskConfirm"
  />
</template>

<style scoped>
.mcp { container: mcp / inline-size; display: flex; flex-direction: column; gap: var(--sp-4); }
.mcp-grid { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--sp-4); }
@container mcp (min-width: 900px) { .mcp-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@container mcp (min-width: 1500px) { .mcp-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
.mcp-search { width: 180px; }
.mcp-adapters { display: flex; flex-wrap: wrap; gap: var(--sp-2); padding-bottom: var(--sp-4); border-bottom: 1px solid var(--stroke); }
.mcp-adapter {
  display: inline-flex; align-items: center; gap: 6px;
  height: var(--h-control); padding: 0 10px;
  border: 1px solid var(--stroke); border-radius: var(--r-in);
  background: var(--surface); color: var(--dim); cursor: pointer; font: inherit; font-size: var(--fs-small);
}
.mcp-adapter:hover { color: var(--text); border-color: var(--stroke-strong); }
.mcp-adapter b { font-weight: 500; color: var(--text); }
.mcp-adapter-file { color: var(--faint); font-family: var(--mono); font-size: var(--fs-caption); }
.mcp-read-errors { padding: var(--sp-3); border: 1px solid var(--danger); border-radius: var(--r-in); color: var(--danger); background: var(--danger-soft); font-size: var(--fs-small); overflow-wrap: anywhere; }
.mcp-read-errors p { margin: 0; }
.mcp-read-errors p + p { margin-top: var(--sp-2); }
.mcp-card {
  border: 1px solid var(--stroke); border-radius: var(--r-card);
  padding: var(--sp-5); background: var(--surface); min-width: 0;
}
.mcp-card-head { display: flex; align-items: center; gap: var(--sp-2); flex-wrap: wrap; cursor: default; }
.mcp-title { flex: 1; min-width: 0; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.mcp-title b { font-size: var(--fs-card); font-weight: 600; overflow-wrap: anywhere; }
.mcp-title code { font-size: var(--fs-caption); color: var(--faint); }
.tag {
  font-size: var(--fs-caption); padding: 0 6px; height: 20px; line-height: 20px;
  border-radius: 999px; background: var(--wash); color: var(--dim);
}
.tag.risk { background: var(--danger-soft); color: var(--danger); }
.mcp-note { margin: 4px 0 0; font-size: var(--fs-small); color: var(--dim); }
.mcp-cmd {
  margin: var(--sp-2) 0 0; font-size: var(--fs-caption); color: var(--faint);
  font-family: var(--mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.mcp-agents { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 180px), 1fr)); gap: var(--sp-2); margin-top: var(--sp-4); }
.mcp-agent {
  display: inline-flex; align-items: center; gap: 6px;
  padding: var(--sp-2); border-radius: var(--r-in); background: var(--wash); font-size: var(--fs-caption); min-width: 0;
}
.mcp-agent .name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.mcp-agent :deep(.src-ico) { width: 14px; height: 14px; }
.mcp-adapter :deep(.src-ico) { width: 16px; height: 16px; }
.mcp-agent.ok { background: var(--wash); }
.mcp-agent.miss { opacity: 0.85; }
.mcp-agent.sys { outline: 1px dashed var(--stroke-strong); }
.mcp-agent.error { background: var(--danger-soft); color: var(--danger); }
.mcp-agent-error { white-space: nowrap; }
.mcp-agent .drift { padding: 0; border: 0; background: none; color: var(--warn); font: inherit; font-weight: 600; white-space: nowrap; cursor: pointer; }
.mcp-agent .drift:hover { text-decoration: underline; }
.mcp-agent .drift:disabled { cursor: default; }
.mcp-agent .drift:focus-visible { outline: 1px solid var(--warn); outline-offset: 3px; }
.mcp-form { display: flex; flex-direction: column; gap: var(--sp-3); }
.mcp-form label { display: flex; flex-direction: column; gap: 4px; font-size: var(--fs-small); color: var(--dim); }
.mcp-form-row { display: flex; gap: var(--sp-4); flex-wrap: wrap; }
.mcp-targets { align-items: center; gap: var(--sp-2); }
.mcp-targets-label { font-size: var(--fs-small); color: var(--dim); margin-right: 4px; }
.mcp-form-actions { display: flex; justify-content: flex-end; gap: var(--sp-2); }
.mcp-json :deep(textarea) { font-family: var(--mono); font-size: var(--fs-caption); }
.hint { font-size: var(--fs-caption); color: var(--faint); margin: 0; }
.docs-empty { color: var(--empty-fg); font-size: var(--fs-small); padding: var(--sp-4) 0; }
</style>
