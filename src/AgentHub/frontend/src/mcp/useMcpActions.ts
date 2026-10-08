import { computed, ref } from 'vue'
import type { MessageApi } from 'naive-ui'
import { post } from '../api'
import {
  agentPendingKey,
  dshRestartSuffix,
  isSystemItem,
  presentAgentIds,
  type McpAgentStatus,
  type McpItem,
  type McpPushResult,
} from './types'
import type { McpSnapshotState } from './useMcpSnapshot'

const RISK_ACK_KEY = 'agenthub.mcp.pushRiskAck'

// 卡片上的写动作：逐端启停/添加、移除，以及明文密钥写入的一次会话风险确认。
export function useMcpActions(snapshot: McpSnapshotState, msg: MessageApi, readonly: boolean) {
  const riskShow = ref(false)
  let riskResolve: ((ok: boolean) => void) | null = null
  let riskPromise: Promise<boolean> | null = null

  function isRiskAcked() {
    try {
      return sessionStorage.getItem(RISK_ACK_KEY) === '1'
    } catch {
      return false
    }
  }

  function ensureRiskAck(): Promise<boolean> {
    if (isRiskAcked()) return Promise.resolve(true)
    if (riskPromise) return riskPromise
    riskShow.value = true
    riskPromise = new Promise((resolve) => { riskResolve = resolve })
    return riskPromise
  }

  function resolveRisk(ok: boolean) {
    if (ok) {
      try {
        sessionStorage.setItem(RISK_ACK_KEY, '1')
      } catch { /* ignore */ }
    }
    riskShow.value = false
    const resolve = riskResolve
    riskResolve = null
    riskPromise = null
    resolve?.(ok)
  }

  function onRiskShowUpdate(show: boolean) {
    if (!show) resolveRisk(false)
  }

  // 同一项的启停/添加按 (item, agent) 互斥；不同 Agent 之间互不阻塞。
  const pendingAgentKeys = ref(new Set<string>())

  // 有写请求在途（移除/启停/添加）时不允许离页或换标签：
  // 否则旧请求的结果会写进已卸载的实例，用户丢失所选端、错误与重试入口（R02）。
  const writePending = computed(() => deleteBusy.value || pendingAgentKeys.value.size > 0)

  // 整页重载闸门的动作侧判定：写请求在途时拦下并标记 beforeunload；
  // 返回 true 表示已处理，页面回调不必再查编辑草稿（R07）。
  function blockBeforeUnload(event: BeforeUnloadEvent): boolean {
    if (!writePending.value) return false
    event.preventDefault()
    event.returnValue = ''
    return true
  }

  const deleteShow = ref(false)
  const deleteItem = ref<McpItem | null>(null)
  const deleteTargets = ref<string[]>([])
  const deleteBusy = ref(false)
  const deleteError = ref('')

  const deletePresentAgents = computed(() =>
    (deleteItem.value?.agents ?? []).filter((a) => a.presence === 'present' && a.detected))

  function itemHasPendingSwitch(item: McpItem) {
    return item.agents.some((a) => pendingAgentKeys.value.has(agentPendingKey(item.id, a.agentId)))
  }

  async function doPush(names: string[], targets: string[]): Promise<McpPushResult> {
    const r = await post<McpPushResult>('/api/mcp/push', { names, targets })
    const items = r.items || []
    const fail = items.filter((x) => !x.ok)
    const okCount = items.filter((x) => x.ok && x.agent !== '*').length
    const suffix = dshRestartSuffix(items)
    // 顶层失败也要带上逐端真实原因与成功数：源配置错误只出现在 items[*].error，
    // 合并同步时可能一端成功另一端失败，只报 r.error 会丢明细和 DSH 重启提示（R08）。
    const reasons = [r.error, ...fail.map((x) => `${x.agent}：${x.error || '推送失败'}`)].filter(Boolean).join('；')
    const detail = reasons ? `；${reasons}` : ''
    if (!r.ok) msg.error(`推送未完成：成功 ${okCount}${detail}${suffix}`)
    else if (fail.length) msg.warning(`推送完成：成功 ${okCount}，失败 ${fail.length}${detail}${suffix}`)
    else if (okCount === 0) msg.info('没有需要推送的缺失端')
    else msg.success(`已推送到 ${okCount} 处${suffix}`)
    return r
  }

  // present 端切换启停；missing 端 wantOn 表示“添加并启用”。system/unsupported/error 没有可用开关。
  async function toggleAgent(item: McpItem, agent: McpAgentStatus, wantOn: boolean) {
    if (readonly) return
    if (agent.presence === 'system' || agent.presence === 'unsupported' || agent.presence === 'error') return
    if (!wantOn && agent.presence === 'missing') return

    const key = agentPendingKey(item.id, agent.agentId)
    if (pendingAgentKeys.value.has(key) || (deleteShow.value && deleteItem.value?.id === item.id)) return
    pendingAgentKeys.value.add(key)
    try {
      if (wantOn && agent.presence === 'missing') {
        if (!(await ensureRiskAck())) return
        await doPush([item.id], [agent.agentId])
        await snapshot.load()
        return
      }
      if (agent.presence === 'present') {
        await post('/api/mcp/agent-enable', { agent: agent.agentId, name: item.id, enabled: wantOn })
        if (agent.agentId === 'dsh') msg.info('DSH 需重启生效')
        await snapshot.load()
      }
    } catch (e) {
      msg.error(e instanceof Error ? e.message : '操作失败')
    } finally {
      pendingAgentKeys.value.delete(key)
    }
  }

  function askDelete(item: McpItem) {
    if (readonly || deleteBusy.value || isSystemItem(item)) return
    if (itemHasPendingSwitch(item)) {
      msg.info('此 MCP 正在更新启用状态，请完成后再移除')
      return
    }
    deleteError.value = ''
    deleteItem.value = item
    deleteTargets.value = presentAgentIds(item)
    deleteShow.value = true
  }

  async function confirmDelete() {
    if (!deleteItem.value || readonly || deleteBusy.value) return
    if (itemHasPendingSwitch(deleteItem.value)) return
    const targets = [...deleteTargets.value]
    if (!targets.length) {
      deleteError.value = '请至少选择一个端'
      return
    }
    deleteBusy.value = true
    deleteError.value = ''
    try {
      const r = await post<McpPushResult>('/api/mcp/remove', {
        name: deleteItem.value.id,
        fromMother: true,
        targets,
      })
      const fail = (r.items || []).filter((x) => !x.ok)
      if (!r.ok || fail.length) {
        const details = [r.error, ...fail.map((x) => `${x.agent}：${x.error || '移除失败'}`)].filter(Boolean).join('；')
        deleteError.value = `移除未完成：${details || '请检查配置后重试'}${dshRestartSuffix(r.items)}`
        await snapshot.load()
        return
      }
      msg.success(`已从所选端移除${dshRestartSuffix(r.items)}`)
      deleteShow.value = false
      deleteItem.value = null
      await snapshot.load()
    } catch (e) {
      deleteError.value = e instanceof Error ? e.message : '移除失败'
    } finally {
      deleteBusy.value = false
    }
  }

  function toggleDeleteTarget(id: string, on: boolean) {
    if (deleteBusy.value) return
    const set = new Set(deleteTargets.value)
    if (on) set.add(id)
    else set.delete(id)
    deleteTargets.value = [...set]
  }

  async function openAgent(agentId: string) {
    if (readonly) return
    try {
      const r = await post<{ ok: boolean; path?: string }>('/api/mcp/open', { agent: agentId })
      if (r.path) msg.success(`已定位 ${r.path.replace(/\\/g, '/').split('/').pop() || r.path}`)
    } catch (e) {
      msg.error(e instanceof Error ? e.message : '打开失败')
    }
  }

  return {
    riskShow, ensureRiskAck, resolveRisk, onRiskShowUpdate,
    pendingAgentKeys, itemHasPendingSwitch, writePending, blockBeforeUnload,
    deleteShow, deleteItem, deleteTargets, deleteBusy, deleteError, deletePresentAgents,
    askDelete, confirmDelete, toggleDeleteTarget, toggleAgent, openAgent,
  }
}

export type McpActionsState = ReturnType<typeof useMcpActions>
