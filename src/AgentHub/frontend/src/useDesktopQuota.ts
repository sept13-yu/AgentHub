import { computed, onMounted, onUnmounted, ref } from 'vue'
import { get } from './api'
import { host, sendDesktopQuotaMessage } from './hostBridge'
import { applyCssVars } from './tokens'
import { theme } from './theme'
import { normalizeQuotaDialAppearance, type QuotaDialAppearance } from './quotaDial'
import {
  MAX_PINNED_WINDOWS,
  nextPinnedIds,
  normalizePinnedIds,
  quotaPayloadStale,
  reduceQuotaPoll,
  toQuotaWindows,
  togglePinned,
  visiblePinnedIds,
  type QuotaWindowRow,
} from './quotaWindows'

export function useDesktopQuota() {
  const expanded = ref(false)
  const pinned = ref<string[] | null>(null)
  const rows = ref<QuotaWindowRow[]>([])
  const stale = ref(false)
  const ready = ref(false)
  const now = ref(Date.now())
  const appearance = ref(normalizeQuotaDialAppearance())
  const hostError = ref('')
  const knownNames = new Map<string, string>()

  const shownIds = computed(() => visiblePinnedIds(pinned.value, rows.value))
  const shown = computed(() => {
    const map = new Map(rows.value.map((row) => [row.id, row]))
    return shownIds.value.map((id) => map.get(id))
  })
  const unavailable = computed(() => shownIds.value.filter(id => !rows.value.some(row => row.id === id))
    .map(id => ({ id, name: knownNames.get(id) || '已固定额度暂不可用' })))
  const pinFull = computed(() => shownIds.value.length >= MAX_PINNED_WINDOWS)
  const staleTitle = '刷新失败，仍显示上次成功的额度'

  let timer = 0
  let clock = 0
  let staleTries = 0
  let stateReady = !window.__AGENTHUB_DESKTOP_QUOTA__
  let refreshFlight: Promise<void> | null = null
  let hostFlight = Promise.resolve()
  let pendingHostMessages = 0
  // 卸载后旧响应不得写状态、发宿主消息或重新调度（C03）
  let disposed = false

  function post(msg: string) {
    if (disposed) return
    pendingHostMessages++
    // 滑块连续输入也按顺序落盘，旧宿主回执不能覆盖尚未发出的新值。
    hostFlight = hostFlight.then(async () => {
      if (disposed) return
      await sendDesktopQuotaMessage(msg)
      if (!disposed) hostError.value = ''
    }).catch(() => {
      if (!disposed) hostError.value = '窗口设置未同步，请重试'
    }).finally(() => {
      pendingHostMessages--
      if (!disposed && !pendingHostMessages && host() === 'tauri') readState()
    })
  }

  function startDrag(event: MouseEvent) {
    if (disposed || host() !== 'tauri' || event.button !== 0) return
    event.preventDefault()
    // 拖动必须跟随按下事件立即交给系统，不能排在设置保存后面。
    void sendDesktopQuotaMessage('dq:drag-start').catch(() => {
      if (!disposed) hostError.value = '窗口拖动失败，请重试'
    })
  }

  function applyThemeName(next: string) {
    if (next !== 'light' && next !== 'dark') return
    theme.value = next
    applyCssVars(document.documentElement, next)
  }

  function readState(): boolean {
    const state = window.__AGENTHUB_DQ_STATE__
    if (!state) return false
    if (typeof state.expanded === 'boolean') expanded.value = state.expanded
    if ('windowIds' in state) pinned.value = normalizePinnedIds(state.windowIds ?? null)
    if (state.theme) applyThemeName(state.theme)
    appearance.value = normalizeQuotaDialAppearance(state)
    return true
  }

  function onStateEvent() {
    if (disposed) return
    if (pendingHostMessages) {
      const nextTheme = window.__AGENTHUB_DQ_STATE__?.theme
      if (nextTheme) applyThemeName(nextTheme)
      return
    }
    readState()
  }

  function waitForState(): Promise<void> {
    if (!window.__AGENTHUB_DESKTOP_QUOTA__ || readState()) {
      stateReady = true
      return Promise.resolve()
    }
    return new Promise((resolve) => {
      let settled = false
      const timerId = window.setTimeout(done, 1500)
      function done() {
        if (settled) return
        settled = true
        window.clearTimeout(timerId)
        window.removeEventListener('agenthub-dq-state', done)
        // 等待中卸载：不再读注入状态，resolve 后的 refresh 由 disposed 兜底
        if (disposed) { resolve(); return }
        readState()
        stateReady = true
        resolve()
      }
      window.addEventListener('agenthub-dq-state', done)
    })
  }

  function applyHostData(data: unknown) {
    if (data === 'theme:light' || data === 'theme:dark') applyThemeName(String(data).slice(6))
  }

  // WebView2 宿主消息的合同是 { data?: unknown }（T02）
  function onHostMessage(ev: { data?: unknown }) {
    applyHostData(ev.data)
  }

  // agenthub-theme 是 DOM CustomEvent，载荷在 detail（T02）
  function onThemeEvent(event: Event) {
    applyHostData((event as CustomEvent).detail)
  }

  async function load() {
    let result: { ok: true; rows: QuotaWindowRow[]; stale: boolean } | { ok: false }
    try {
      const raw = await get('/api/quotas')
      result = { ok: true, rows: toQuotaWindows(raw), stale: quotaPayloadStale(raw) }
    } catch {
      result = { ok: false }
    }
    // C03：卸载后旧响应直接丢弃，不写状态、不发宿主消息、不参与重试计数
    if (disposed) return
    const next = reduceQuotaPoll(rows.value, result)
    rows.value = next.rows
    next.rows.forEach(row => knownNames.set(row.id, row.name))
    stale.value = next.stale
    ready.value = true
    const fresh = result.ok && !result.stale
    const pin = nextPinnedIds(pinned.value, next.rows, fresh)
    pinned.value = pin.pinned
    if (pin.changed && pin.pinned) post('dq:windows:' + JSON.stringify(pin.pinned))
    if (fresh) staleTries = 0
    else staleTries++
  }

  function schedule(ms: number) {
    if (disposed) return
    window.clearTimeout(timer)
    timer = window.setTimeout(() => { void refresh() }, ms)
  }

  async function refresh() {
    if (disposed || !stateReady) return
    if (refreshFlight) return refreshFlight
    refreshFlight = (async () => {
      await load()
      const delay = stale.value ? (staleTries <= 2 ? 1500 * staleTries : 8000) : 60_000
      schedule(delay)
    })().finally(() => { refreshFlight = null })
    return refreshFlight
  }

  function setExpanded(next: boolean) {
    if (expanded.value === next) return
    expanded.value = next
    post(next ? 'dq:expanded' : 'dq:compact')
  }

  function closeWidget() {
    post('dq:close')
  }

  function setAppearance(patch: Partial<QuotaDialAppearance>) {
    appearance.value = normalizeQuotaDialAppearance({ ...appearance.value, ...patch })
    post('dq:appearance:' + JSON.stringify(appearance.value))
  }

  function onToggle(id: string) {
    const next = togglePinned(pinned.value, rows.value, id)
    if (pinned.value && next.length === pinned.value.length && next.every((item, i) => item === pinned.value![i])) return
    pinned.value = next
    post('dq:windows:' + JSON.stringify(next))
  }

  function isPinned(id: string): boolean {
    return shownIds.value.includes(id)
  }

  function onPushRefresh() {
    void refresh()
  }

  function onVisible() {
    if (document.visibilityState === 'visible') void refresh()
  }

  onMounted(() => {
    document.documentElement.classList.add('desktop-quota')
    window.chrome?.webview?.addEventListener?.('message', onHostMessage)
    window.addEventListener('agenthub-theme', onThemeEvent)
    window.addEventListener('agenthub-dq-state', onStateEvent)
    window.addEventListener('agenthub-refresh', onPushRefresh)
    document.addEventListener('visibilitychange', onVisible)
    clock = window.setInterval(() => { now.value = Date.now() }, 30_000)
    void waitForState().then(() => refresh())
  })

  onUnmounted(() => {
    document.documentElement.classList.remove('desktop-quota')
    disposed = true
    window.clearTimeout(timer)
    window.clearInterval(clock)
    window.chrome?.webview?.removeEventListener?.('message', onHostMessage)
    window.removeEventListener('agenthub-theme', onThemeEvent)
    window.removeEventListener('agenthub-dq-state', onStateEvent)
    window.removeEventListener('agenthub-refresh', onPushRefresh)
    document.removeEventListener('visibilitychange', onVisible)
  })

  return { expanded, rows, shown, shownIds, unavailable, pinFull, stale, staleTitle, ready, now, appearance, hostError, startDrag, setAppearance, refresh, setExpanded, closeWidget, onToggle, isPinned }
}
