<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ChevronDown, ChevronUp, X } from 'lucide-vue-next'
import { get } from '../api'
import { applyCssVars } from '../tokens'
import { theme } from '../theme'
import {
  MAX_PINNED_WINDOWS,
  formatResetCountdown,
  formatResetExact,
  nextPinnedIds,
  normalizePinnedIds,
  quotaPayloadStale,
  reduceQuotaPoll,
  remainBarColor,
  toQuotaWindows,
  togglePinned,
  visiblePinnedIds,
  type QuotaWindowRow,
} from '../quotaWindows'

const expanded = ref(false)
const pinned = ref<string[] | null>(null)
const rows = ref<QuotaWindowRow[]>([])
const stale = ref(false)
const ready = ref(false)
const now = ref(Date.now())

const shownIds = computed(() => visiblePinnedIds(pinned.value, rows.value))
const shown = computed(() => {
  const map = new Map(rows.value.map((row) => [row.id, row]))
  return shownIds.value.map((id) => map.get(id)).filter((row): row is QuotaWindowRow => !!row)
})
const pinFull = computed(() => shownIds.value.length >= MAX_PINNED_WINDOWS)
const staleTitle = '刷新失败，仍显示上次成功的额度'

let timer = 0
let clock = 0
let staleTries = 0
let stateReady = !window.__AGENTHUB_DESKTOP_QUOTA__
let refreshFlight: Promise<void> | null = null

function post(msg: string) {
  try { window.chrome?.webview?.postMessage(msg) } catch { /* 浏览器预览没有壳 */ }
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
  return true
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
      readState()
      stateReady = true
      resolve()
    }
    window.addEventListener('agenthub-dq-state', done)
  })
}

function onHostMessage(event: Event) {
  const data = (event as CustomEvent | { data?: unknown }).data ?? (event as CustomEvent).detail
  if (data === 'theme:light' || data === 'theme:dark') applyThemeName(String(data).slice(6))
}

async function load() {
  let result: { ok: true; rows: QuotaWindowRow[]; stale: boolean } | { ok: false }
  try {
    const raw = await get('/api/quotas')
    result = { ok: true, rows: toQuotaWindows(raw), stale: quotaPayloadStale(raw) }
  } catch {
    result = { ok: false }
  }
  const next = reduceQuotaPoll(rows.value, result)
  rows.value = next.rows
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
  window.clearTimeout(timer)
  timer = window.setTimeout(() => { void refresh() }, ms)
}

async function refresh() {
  if (!stateReady) return
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
  window.chrome?.webview?.addEventListener?.('message', onHostMessage)
  window.addEventListener('agenthub-refresh', onPushRefresh)
  window.addEventListener('agenthub-theme', onHostMessage)
  document.addEventListener('visibilitychange', onVisible)
  clock = window.setInterval(() => { now.value = Date.now() }, 30_000)
  void waitForState().then(() => refresh())
})

onUnmounted(() => {
  window.clearTimeout(timer)
  window.clearInterval(clock)
  window.removeEventListener('agenthub-refresh', onPushRefresh)
  window.removeEventListener('agenthub-theme', onHostMessage)
  document.removeEventListener('visibilitychange', onVisible)
})
</script>

<template>
  <div class="dq" :class="expanded ? 'is-expanded' : 'is-compact'" :data-stale="stale || undefined">
    <header class="dq-head">
      <strong v-if="expanded">桌面额度</strong>
      <span v-else class="dq-grab">桌面额度</span>
      <span v-if="stale" class="dq-stale" :title="staleTitle">
        <i />
        <span v-if="expanded">未更新</span>
      </span>
      <span class="dq-spacer" />
      <button type="button" class="dq-icon" :aria-label="expanded ? '收起' : '展开'" @click="setExpanded(!expanded)">
        <ChevronUp v-if="expanded" :size="16" :stroke-width="1.8" />
        <ChevronDown v-else :size="16" :stroke-width="1.8" />
      </button>
      <button type="button" class="dq-icon" aria-label="关闭桌面额度" @click="closeWidget">
        <X :size="16" :stroke-width="1.8" />
      </button>
    </header>

    <section class="dq-pinned" :aria-label="expanded ? '已固定额度' : '额度剩余'">
      <p v-if="ready && !shown.length" class="dq-empty">
        {{ stale && !rows.length ? '额度暂时读不到' : '暂无可查询的额度窗口' }}
      </p>
      <article v-for="row in shown" :key="row.id" class="dq-card">
        <template v-if="expanded">
          <div class="dq-line">
            <span class="dq-name" :title="row.name">{{ row.name }}</span>
          </div>
          <p class="dq-hero num">
            <b :class="{ hot: row.remain < 10 }">{{ Math.round(row.remain) }}</b>
            <small>% 剩余</small>
          </p>
          <div class="dq-bar" role="meter" :aria-valuenow="Math.round(row.remain)" aria-valuemin="0" aria-valuemax="100" :aria-label="`${row.name} 剩余 ${Math.round(row.remain)}%`">
            <i :style="{ width: row.remain + '%', background: remainBarColor(row.remain) }" />
          </div>
          <p class="dq-when">{{ formatResetExact(row.resetMs, now, row.period) }}</p>
        </template>
        <template v-else>
          <div class="dq-line">
            <span class="dq-name" :title="row.name">{{ row.name }}</span>
            <b class="dq-pct num" :class="{ hot: row.remain < 10 }">{{ Math.round(row.remain) }}<small>%</small></b>
          </div>
          <p class="dq-count">{{ formatResetCountdown(row.resetMs, now, row.period) }}</p>
          <div class="dq-bar" role="meter" :aria-valuenow="Math.round(row.remain)" aria-valuemin="0" aria-valuemax="100" :aria-label="`${row.name} 剩余 ${Math.round(row.remain)}%`">
            <i :style="{ width: row.remain + '%', background: remainBarColor(row.remain) }" />
          </div>
        </template>
      </article>
    </section>

    <section v-if="expanded" class="dq-pick" aria-label="选择固定窗口">
      <h2>固定窗口 <span>最多 {{ MAX_PINNED_WINDOWS }} 个</span></h2>
      <p v-if="ready && !rows.length" class="dq-empty">暂无可选窗口</p>
      <label v-for="row in rows" :key="row.id" class="dq-opt" :class="{ off: pinFull && !isPinned(row.id) }">
        <input
          type="checkbox"
          :checked="isPinned(row.id)"
          :disabled="pinFull && !isPinned(row.id)"
          @change="onToggle(row.id)"
        />
        <span class="dq-name" :title="row.name">{{ row.name }}</span>
        <b class="num">{{ Math.round(row.remain) }}%</b>
      </label>
    </section>
  </div>
</template>

<style scoped>
.dq {
  box-sizing: border-box;
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--bg, #18181c);
  color: var(--text, #f1f3f5);
  font-family: "Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI", "PingFang SC", "Noto Sans SC", sans-serif;
  user-select: none;
  overflow: hidden;
}
:root[data-theme="light"] .dq {
  background: var(--bg, #f5f6f8);
  color: var(--text, #1b1f24);
}
.dq-head, .dq-head * { app-region: drag; -webkit-app-region: drag; }
.dq.is-compact .dq-pinned, .dq.is-compact .dq-pinned * { app-region: drag; -webkit-app-region: drag; }
.dq-head button, .dq-head button * { app-region: no-drag; -webkit-app-region: no-drag; }
.dq-pick, .dq-pick * { app-region: no-drag; -webkit-app-region: no-drag; }
.dq-head {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 4px 0 12px;
  cursor: grab;
}
.dq-head strong, .dq-grab {
  font-size: 13px;
  font-weight: 600;
  letter-spacing: 0.01em;
}
.dq.is-compact .dq-grab {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
.dq-spacer { flex: 1; }
.dq-stale {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--warn, #f0a020);
  font-size: 12px;
  font-weight: 500;
}
.dq-stale i {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
}
.dq-icon {
  width: 26px;
  height: 26px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--dim, #b7bdc6);
  cursor: pointer;
}
.dq-icon:hover { background: var(--wash, rgba(255, 255, 255, 0.06)); color: var(--text, #f1f3f5); }
.dq-pinned {
  flex: 0 0 auto;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 0 12px 8px;
  min-height: 0;
}
.dq.is-compact .dq-pinned { flex: 1; gap: 4px; padding-bottom: 8px; }
.dq-card { min-width: 0; }
.dq.is-expanded .dq-card + .dq-card {
  border-top: 1px solid var(--stroke, #2e2e36);
  padding-top: 8px;
}
.dq-line { display: flex; align-items: baseline; gap: 8px; min-width: 0; }
.dq-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13px;
  font-weight: 600;
}
.dq-pct { font-size: 22px; font-weight: 600; line-height: 1; color: var(--text, #f1f3f5); }
.dq-pct.hot, .dq-hero b.hot { color: var(--danger, #e85a73); }
.dq-pct small, .dq-hero small { font-size: 12px; font-weight: 500; margin-left: 2px; color: var(--dim, #b7bdc6); }
.dq-count, .dq-when {
  margin: 0 0 3px;
  color: var(--dim, #b7bdc6);
  font-size: 12px;
  line-height: 1.25;
}
.dq-hero { margin: 2px 0 6px; line-height: 1; }
.dq-hero b { font-size: 28px; font-weight: 600; letter-spacing: -0.03em; }
.dq-bar {
  height: 4px;
  border-radius: 99px;
  background: var(--stroke, #2e2e36);
  overflow: hidden;
}
.dq-bar i { display: block; height: 100%; border-radius: inherit; }
.dq-empty { margin: 8px 0; color: var(--dim, #b7bdc6); font-size: 12px; }
.dq-pick {
  flex: 1;
  min-height: 0;
  overflow: auto;
  border-top: 1px solid var(--stroke, #2e2e36);
  padding: 8px 8px 10px;
}
.dq-pick h2 {
  position: sticky;
  top: 0;
  margin: 0 4px 6px;
  padding-bottom: 4px;
  background: var(--bg, #18181c);
  font-size: 12px;
  font-weight: 600;
  color: var(--dim, #b7bdc6);
}
.dq-pick h2 span { font-weight: 400; }
.dq-opt {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 28px;
  padding: 0 4px;
  border-radius: 6px;
  cursor: pointer;
}
.dq-opt:hover { background: var(--wash, rgba(255, 255, 255, 0.06)); }
.dq-opt.off { opacity: 0.45; cursor: default; }
.dq-opt input { margin: 0; accent-color: var(--accent-solid, #2ec4b6); }
.dq-opt b { font-size: 12px; font-weight: 600; color: var(--dim, #b7bdc6); }
</style>
