import { computed, ref } from 'vue'
import type { MessageApi } from 'naive-ui'
import { get, post } from '../api'
import {
  dshRestartSuffix,
  presentAgentIds,
  type McpItem,
  type McpPushResult,
} from './types'
import { NEW_TEMPLATE, parseMcpEntry, toEditJson } from './parseMcpEntry'
import type { McpSnapshotState } from './useMcpSnapshot'

// 统一配置编辑草稿：一份 JSON + 目标端选择；离开（取消/Esc/遮罩/路由/标签切换/关窗）前先 confirmDiscard。
export function useMcpEditor(
  snapshot: McpSnapshotState,
  riskAck: { ensureRiskAck: () => Promise<boolean> },
  msg: MessageApi,
  readonly: boolean,
) {
  const show = ref(false)
  const creating = ref(false)
  const item = ref<McpItem | null>(null)
  const name = ref('')
  const json = ref('')
  const targets = ref<string[]>([])
  const loading = ref(false)
  const loaded = ref(false)
  const saving = ref(false)
  const loadError = ref('')
  const saveError = ref('')
  const discardShow = ref(false)
  let requestId = 0
  let baseline = ''
  let discardResolve: ((ok: boolean) => void) | null = null
  let discardPromise: Promise<boolean> | null = null

  const itemName = computed(() => item.value?.alias || item.value?.id || name.value)
  // 编辑既有 MCP 时，所有已配置端必须跟随同步，不能取消勾选逃过。
  const requiredAgents = computed(() => item.value?.agents.filter((a) => a.presence === 'present' && a.detected) ?? [])
  const targetOptions = computed(() =>
    creating.value
      ? snapshot.writableAdapters.value
      : snapshot.writableAdapters.value.filter((a) =>
        !requiredAgents.value.some((present) => present.agentId === a.agentId)))
  const dirty = computed(() => {
    if (!show.value || !loaded.value) return false
    return JSON.stringify({ name: name.value, json: json.value, targets: targets.value }) !== baseline
  })

  function setBaseline() {
    baseline = JSON.stringify({ name: name.value, json: json.value, targets: targets.value })
  }

  function openCreate() {
    if (readonly || saving.value || show.value) return
    requestId++
    creating.value = true
    item.value = null
    loading.value = false
    loaded.value = true
    loadError.value = ''
    saveError.value = ''
    name.value = ''
    json.value = NEW_TEMPLATE
    targets.value = snapshot.writableAdapters.value.map((a) => a.agentId)
    setBaseline()
    show.value = true
  }

  async function openEdit(target: McpItem) {
    if (readonly || saving.value) return
    const id = ++requestId
    loading.value = true
    loaded.value = false
    loadError.value = ''
    saveError.value = ''
    json.value = ''
    show.value = true
    creating.value = false
    item.value = target
    name.value = target.id
    targets.value = presentAgentIds(target)
    setBaseline()
    try {
      const raw = await get<Record<string, unknown>>(`/api/mcp/raw?name=${encodeURIComponent(target.id)}`)
      if (id !== requestId || !show.value) return
      json.value = JSON.stringify(toEditJson(raw), null, 2)
      loaded.value = true
      setBaseline()
    } catch (e) {
      if (id === requestId) loadError.value = e instanceof Error ? e.message : '读取配置失败'
    } finally {
      if (id === requestId) loading.value = false
    }
  }

  async function retryLoad() {
    if (item.value && !saving.value) await openEdit(item.value)
  }

  function toggleTarget(id: string, on: boolean) {
    if (saving.value || !loaded.value) return
    if (!on && requiredAgents.value.some((a) => a.agentId === id)) return
    const set = new Set(targets.value)
    if (on) set.add(id)
    else set.delete(id)
    targets.value = [...set]
  }

  // 页面 v-model 的入向 setter：保存中不接受修改。
  function setName(value: string) {
    if (!saving.value) name.value = value
  }

  function setJson(value: string) {
    if (!saving.value) json.value = value
  }

  function formatJson() {
    try {
      const obj = JSON.parse(json.value)
      json.value = JSON.stringify(obj, null, 2)
    } catch {
      msg.warning('JSON 无效，无法格式化')
    }
  }

  async function save() {
    if (readonly || loading.value || saving.value || !loaded.value) return
    let body: Record<string, unknown>
    try {
      body = parseMcpEntry(json.value, name.value)
      if (!creating.value && body.name !== name.value) throw new Error('已有 MCP 请保留原名称')
    } catch (e) {
      msg.warning(e instanceof Error ? e.message : 'JSON 无效')
      return
    }
    const mergedTargets = [...new Set([...targets.value, ...requiredAgents.value.map((a) => a.agentId)])]
    if (creating.value && !mergedTargets.length) {
      msg.warning('请至少选择一个目标端')
      return
    }
    saving.value = true
    saveError.value = ''
    try {
      if (!(await riskAck.ensureRiskAck())) return
      const r = await post<McpPushResult>('/api/mcp/upsert-agents', { ...body, targets: mergedTargets })
      const fail = (r.items || []).filter((x) => !x.ok)
      const okCount = (r.items || []).filter((x) => x.ok && x.agent !== '*' && x.agent !== 'mother').length
      if (!r.ok || fail.length) {
        // 部分失败：保留草稿，逐端列出原因；母本可能已写入，刷新列表并切到既有项继续修。
        const details = [r.error, ...fail.map((x) => `${x.agent}：${x.error || '同步失败'}`)].filter(Boolean).join('；')
        saveError.value = `保存或同步未完成：${details || '请检查配置后重试'}${dshRestartSuffix(r.items)}`
        await snapshot.load()
        const current = snapshot.data.value?.items.find((i) => i.id === body.name)
        if (current) {
          creating.value = false
          item.value = current
          name.value = current.id
        }
        return
      }
      msg.success(`已保存标准配置并同步 ${okCount} 个 Agent${dshRestartSuffix(r.items)}`)
      show.value = false
      await snapshot.load()
    } catch (e) {
      saveError.value = e instanceof Error ? e.message : '保存失败'
    } finally {
      saving.value = false
    }
  }

  function confirmDiscard(): Promise<boolean> {
    if (saving.value) {
      msg.info('正在保存，请稍后再操作')
      return Promise.resolve(false)
    }
    if (discardPromise) return discardPromise
    if (!dirty.value) return Promise.resolve(true)
    discardShow.value = true
    discardPromise = new Promise((resolve) => { discardResolve = resolve })
    return discardPromise
  }

  function resolveDiscard(ok: boolean) {
    discardShow.value = false
    const resolve = discardResolve
    discardResolve = null
    discardPromise = null
    resolve?.(ok)
  }

  async function close() {
    if (await confirmDiscard()) show.value = false
  }

  function onBeforeUnload(event: BeforeUnloadEvent) {
    if (!dirty.value && !saving.value) return
    event.preventDefault()
    event.returnValue = ''
  }

  return {
    show, creating, item, name, json, targets, loading, loaded, saving,
    loadError, saveError, dirty, discardShow, itemName, requiredAgents, targetOptions,
    openCreate, openEdit, retryLoad, toggleTarget, setName, setJson, formatJson, save,
    confirmDiscard, resolveDiscard, close, onBeforeUnload,
  }
}

export type McpEditorState = ReturnType<typeof useMcpEditor>
