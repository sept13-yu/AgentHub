<script setup lang="ts">
import { computed, inject, nextTick, onMounted, onUnmounted, reactive, ref, watch, type Ref } from 'vue'
import { NButton, NIcon, NPopover, useMessage } from 'naive-ui'
import { ChevronDown, CircleAlert, RefreshCw } from 'lucide-vue-next'
import AgentMark from '../components/AgentMark.vue'
import UsageHeatmap from '../components/UsageHeatmap.vue'
import DashboardUsage from '../components/DashboardUsage.vue'
import { get, post, put, WRITABLE } from '../api'
import { moveItem } from '../settingsModel'
import { toQuotaTiles, type QuotaTile } from '../quotaView'
import { dashCache } from '../dashCache'
import { usePageHotkeys } from '../hotkeys'
import {
  formatTokens,
  RANGES,
  toUsageView,
  usageErrorView,
  type RangeKey,
} from '../usageView'

const message = useMessage()
const pageLoading = inject<Ref<boolean>>('page-loading')

const range = ref<RangeKey>(dashCache.range)
const refreshing = ref(false)
const usage = ref(dashCache.usage)
const quotasReady = ref(dashCache.quotasReady)
const tiles = ref<QuotaTile[]>(dashCache.tiles)
/** stale 磁盘缓存只补拉有限次，避免后台刷新慢时打满本地 API */
let staleRetries = 0
const due = reactive<Record<string, { ready: boolean; loading: boolean; error: string; text: string }>>({})
const gridEl = ref<HTMLElement | null>(null)
const press = ref<{ id: string; x: number; y: number; el: HTMLElement; pointerId: number } | null>(null)
const drag = ref<{
  id: string
  offX: number
  offY: number
  x: number
  y: number
  w: number
  h: number
  html: string
  isBalance: boolean
  settling: boolean
} | null>(null)
const savedOrder = ref(dashCache.tiles.map((t) => t.id).join('\n'))
const canSort = computed(() => WRITABLE && tiles.value.length > 1)
const ghostStyle = computed(() => {
  const d = drag.value
  if (!d) return {}
  return {
    left: `${d.x}px`,
    top: `${d.y}px`,
    width: `${d.w}px`,
    height: `${d.h}px`,
  }
})
const historyOpen = ref(dashCache.historyOpen)
const quotaGroups = computed(() => [
  { kind: 'windows', tiles: tiles.value.filter((t) => t.kind === 'windows') },
  { kind: 'balance', tiles: tiles.value.filter((t) => t.kind === 'balance') },
].filter((group) => group.tiles.length))
const exhaustedCount = computed(() => tiles.value.reduce((sum, t) =>
  sum + (t.kind === 'windows' ? t.windows.filter((w) => w.remain === 0).length : 0), 0))
// 历史统计由 usage.days 现算，口径与热力图一致（不含未来日）。
const heatStats = computed(() => {
  const days = usage.value.days ?? []
  let total = 0
  let peak = 0
  let peakDate = ''
  const active = new Set<string>()
  for (const d of days) {
    total += d.tokens
    if (d.tokens > 0) {
      active.add(d.date)
      if (d.tokens > peak) {
        peak = d.tokens
        peakDate = d.date
      }
    }
  }
  const stamps = [...active]
    .map((s) => {
      const [y, m, d] = s.split('-').map(Number)
      return new Date(y, m - 1, d).getTime()
    })
    .sort((a, b) => a - b)
  const dayMs = 86_400_000
  let best = 0
  let run = 0
  let prev = 0
  for (const t of stamps) {
    run = t - prev === dayMs ? run + 1 : 1
    if (run > best) best = run
    prev = t
  }
  return {
    total: formatTokens(total),
    active: `${active.size} 天`,
    peak: formatTokens(peak),
    peakDate: peakDate ? `峰值出现在 ${peakDate.replaceAll('-', '/')}` : '',
    streak: `${best} 天`,
  }
})
function toggleHistory(event: Event) {
  historyOpen.value = (event.currentTarget as HTMLDetailsElement).open
  dashCache.historyOpen = historyOpen.value
}

function setLoading(on: boolean) {
  if (pageLoading) pageLoading.value = on
}

function persist() {
  dashCache.range = range.value
  dashCache.usage = usage.value
  dashCache.quotasReady = quotasReady.value
  dashCache.tiles = tiles.value
  dashCache.primed = true
}

function pickRange(key: RangeKey) {
  if (key === range.value) return
  range.value = key
  persist()
}

function remainColor(remain: number): string {
  if (remain < 10) return 'var(--danger)'
  if (remain < 50) return 'var(--warn)'
  return 'var(--ok)'
}

function errMessage(e: unknown): string {
  return e instanceof Error ? e.message : '请求失败'
}

async function loadUsage() {
  try {
    usage.value = toUsageView(await get(`/api/usage?range=${range.value}`), range.value)
    persist()
  } catch (e) {
    usage.value = usageErrorView(errMessage(e))
    persist()
  }
}

async function loadQuotas(force = false) {
  try {
    const raw = (await get(force ? '/api/quotas?force=true' : '/api/quotas')) as { stale?: boolean }
    const next = toQuotaTiles(raw)
    if (drag.value) {
      const map = new Map(next.map((t) => [t.id, t]))
      const keep = tiles.value.map((t) => map.get(t.id) ?? t)
      const seen = new Set(keep.map((t) => t.id))
      tiles.value = keep.concat(next.filter((t) => !seen.has(t.id)))
    } else {
      tiles.value = next
      savedOrder.value = tileOrderKey(next)
    }
    quotasReady.value = true
    persist()
    for (const id of Object.keys(due)) delete due[id]
    // 启动首次拿到的是盘上旧值（stale）：有限次补拉，等服务端后台刷新完成换新
    if (!force && raw.stale && staleRetries < 2) {
      staleRetries++
      window.setTimeout(() => { void loadQuotas(false) }, 1500 * staleRetries)
    }
    if (!raw.stale) staleRetries = 0
  } catch {
    if (!quotasReady.value) tiles.value = []
  }
}

function dueText(id: string): string {
  return due[id]?.text || ''
}

function formatDueDate(raw: string): string {
  const ms = Date.parse(raw + 'T00:00:00')
  if (Number.isNaN(ms)) return raw
  const d = new Date(ms)
  const now = new Date()
  return d.getFullYear() === now.getFullYear()
    ? `${d.getMonth() + 1}/${d.getDate()}`
    : `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()}`
}

async function loadDue(id: string) {
  const cur = due[id]
  if (cur?.loading || cur?.ready) return
  due[id] = { ready: false, loading: true, error: '', text: '读取中' }
  try {
    const r = await get<{ id: string; date?: string | null; amount?: number | null; error?: string }>(
      `/api/quotas/expiry?id=${id}`,
    )
    if (r.error) {
      due[id] = { ready: false, loading: false, error: r.error, text: r.error }
      return
    }
    if (!r.date || r.amount == null) {
      due[id] = { ready: true, loading: false, error: '', text: '近期没有到期' }
      return
    }
    const n = new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 2 }).format(r.amount)
    due[id] = { ready: true, loading: false, error: '', text: `${formatDueDate(r.date)} 到期 ${n} 积分` }
  } catch (e) {
    due[id] = { ready: false, loading: false, error: errMessage(e), text: errMessage(e) }
  }
}

async function onRefresh() {
  if (refreshing.value) return
  refreshing.value = true
  setLoading(true)
  let scanErr: unknown = null
  try {
    await post('/api/usage/scan')
  } catch (e) {
    const status = e && typeof e === 'object' && 'status' in e ? Number((e as { status?: number }).status) : 0
    if (status !== 403) scanErr = e
  }
  // 扫描接口只等本地源入库（秒回）；额度 force 换新放后台，瓷砖到了就替换，不卡转圈
  await loadUsage()
  void loadQuotas(true)
  setLoading(false)
  refreshing.value = false
  if (scanErr) message.error(errMessage(scanErr))
  else message.success('已刷新')
}

// 壳层推送：后台收尾（Cursor CSV + 会话索引）完成后自动补数据，无需手动刷新
function onPushRefresh() {
  void loadUsage()
  void loadQuotas(false)
}

watch(range, async () => {
  setLoading(true)
  await loadUsage()
  setLoading(false)
})

function onResize() {
  if (drag.value) readSlots()
}

function tileOrderKey(list: QuotaTile[] = tiles.value): string {
  return list.map((t) => t.id).join('\n')
}

function sortEase(): number {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 260
}

type SlotBox = { id: string; x: number; y: number; w: number; h: number }
let slots: SlotBox[] = []
let lockId: string | null = null
let hitRaf = 0
let hitAt: { x: number; y: number } | null = null

function readSlots() {
  const grid = gridEl.value
  if (!grid) {
    slots = []
    return
  }
  slots = []
  for (const el of grid.querySelectorAll<HTMLElement>('[data-qid]')) {
    const id = el.dataset.qid
    if (!id) continue
    slots.push({ id, x: el.offsetLeft, y: el.offsetTop, w: el.offsetWidth, h: el.offsetHeight })
  }
}

function slotHit(cx: number, cy: number): string | null {
  const grid = gridEl.value
  if (!grid || !slots.length) return null
  const g = grid.getBoundingClientRect()
  for (const s of slots) {
    const left = g.left + s.x
    const top = g.top + s.y
    if (cx >= left && cx < left + s.w && cy >= top && cy < top + s.h) return s.id
  }
  return null
}

function layoutPoint(el: HTMLElement): { left: number; top: number } {
  const grid = gridEl.value
  if (!grid) {
    const r = el.getBoundingClientRect()
    return { left: r.left, top: r.top }
  }
  const g = grid.getBoundingClientRect()
  return { left: g.left + el.offsetLeft, top: g.top + el.offsetTop }
}

function onTilePointerDown(id: string, e: PointerEvent) {
  if (!canSort.value || e.button !== 0) return
  if (e.target instanceof Element && e.target.closest('button, a, input, textarea')) return
  const el = e.currentTarget as HTMLElement
  press.value = { id, x: e.clientX, y: e.clientY, el, pointerId: e.pointerId }
  el.setPointerCapture(e.pointerId)
}

function onTilePointerMove(e: PointerEvent) {
  const p = press.value
  if (p && !drag.value) {
    if (Math.hypot(e.clientX - p.x, e.clientY - p.y) < 5) return
    const rect = p.el.getBoundingClientRect()
    drag.value = {
      id: p.id,
      offX: e.clientX - rect.left,
      offY: e.clientY - rect.top,
      x: rect.left,
      y: rect.top,
      w: rect.width,
      h: rect.height,
      html: p.el.innerHTML,
      isBalance: p.el.classList.contains('is-balance'),
      settling: false,
    }
    press.value = null
    document.documentElement.classList.add('ah-tile-sorting')
    void nextTick(() => {
      readSlots()
      lockId = p.id
    })
  }
  const d = drag.value
  if (!d || d.settling) return
  d.x = e.clientX - d.offX
  d.y = e.clientY - d.offY
  hitAt = { x: e.clientX, y: e.clientY }
  if (hitRaf) return
  hitRaf = requestAnimationFrame(() => {
    hitRaf = 0
    if (hitAt) hitReorder(hitAt.x, hitAt.y)
  })
}

function onTilePointerUp() {
  press.value = null
  if (drag.value) void finishDrag()
}

function hitReorder(cx: number, cy: number) {
  const d = drag.value
  if (!d || d.settling) return
  const hit = slotHit(cx, cy)
  if (lockId) {
    if (hit === lockId || hit === d.id) return
    lockId = null
  }
  if (!hit || hit === d.id) return
  const from = tiles.value.findIndex((t) => t.id === d.id)
  const to = tiles.value.findIndex((t) => t.id === hit)
  if (from < 0 || to < 0 || from === to) return
  if (tiles.value[from].kind !== tiles.value[to].kind) return
  lockId = hit
  flipMove(from, to)
}

function flipMove(from: number, to: number) {
  const grid = gridEl.value
  const first = new Map<string, { left: number; top: number }>()
  if (grid) {
    for (const el of grid.querySelectorAll<HTMLElement>('[data-qid]')) {
      const id = el.dataset.qid
      if (id) first.set(id, { left: el.getBoundingClientRect().left, top: el.getBoundingClientRect().top })
    }
  }
  tiles.value = moveItem(tiles.value, from, to)
  persist()
  void nextTick(() => {
    readSlots()
    if (!grid) return
    const ms = sortEase()
    for (const el of grid.querySelectorAll<HTMLElement>('[data-qid]')) {
      const id = el.dataset.qid
      if (!id) continue
      for (const a of el.getAnimations()) a.cancel()
      const prev = first.get(id)
      if (!prev) continue
      const next = layoutPoint(el)
      const dx = prev.left - next.left
      const dy = prev.top - next.top
      if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5) continue
      el.animate(
        [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'none' }],
        { duration: ms, easing: 'cubic-bezier(.2, .7, .2, 1)' },
      )
    }
  })
}

async function finishDrag() {
  const d = drag.value
  if (!d) return
  if (hitRaf) {
    cancelAnimationFrame(hitRaf)
    hitRaf = 0
  }
  hitAt = null
  lockId = null
  d.settling = true
  await nextTick()
  const slot = gridEl.value?.querySelector<HTMLElement>(`[data-qid="${d.id}"]`)
  const dest = slot?.getBoundingClientRect()
  const ms = sortEase()
  if (dest && ms) {
    d.x = dest.left
    d.y = dest.top
    d.w = dest.width
    d.h = dest.height
    await new Promise((r) => window.setTimeout(r, ms))
  }
  document.documentElement.classList.remove('ah-tile-sorting')
  drag.value = null
  if (!WRITABLE) return
  const next = tileOrderKey()
  if (next === savedOrder.value) return
  void saveQuotaOrder(next)
}

async function saveQuotaOrder(orderKey: string) {
  try {
    await put('/api/settings', { dashboard: { quotaOrder: tiles.value.map((t) => t.id) } })
    savedOrder.value = orderKey
    persist()
  } catch (e) {
    message.error(errMessage(e))
    const ids = savedOrder.value.split('\n').filter(Boolean)
    if (!ids.length) return
    const map = new Map(tiles.value.map((t) => [t.id, t]))
    const restored: QuotaTile[] = []
    for (const id of ids) {
      const tile = map.get(id)
      if (tile) restored.push(tile)
    }
    for (const tile of tiles.value) {
      if (!ids.includes(tile.id)) restored.push(tile)
    }
    tiles.value = restored
    persist()
  }
}

usePageHotkeys({
  refresh: () => { void onRefresh() },
})

onMounted(() => {
  window.addEventListener('agenthub-refresh', onPushRefresh)
  window.addEventListener('resize', onResize)
  staleRetries = 0
  if (dashCache.primed) {
    return
  }
  // 首屏遮罩只等本地 usage（毫秒级）；额度后台补齐，不卡启动
  setLoading(true)
  void loadQuotas(false)
  void loadUsage().finally(() => {
    setLoading(false)
  })
})

onUnmounted(() => {
  window.removeEventListener('agenthub-refresh', onPushRefresh)
  window.removeEventListener('resize', onResize)
  document.documentElement.classList.remove('ah-tile-sorting')
  if (hitRaf) {
    cancelAnimationFrame(hitRaf)
    hitRaf = 0
  }
  hitAt = null
  lockId = null
  if (drag.value && tileOrderKey() !== savedOrder.value)
    void saveQuotaOrder(tileOrderKey())
  drag.value = null
  press.value = null
})
</script>

<template>
  <teleport defer to="#chrome-tabs">
    <div class="tabs" role="group" aria-label="用量区间">
      <button
        v-for="r in RANGES"
        :key="r.key"
        type="button"
        :aria-pressed="range === r.key ? 'true' : 'false'"
        @click="pickRange(r.key)"
      >
        {{ r.label }}
      </button>
    </div>
  </teleport>
  <teleport defer to="#chrome-actions">
    <n-button :loading="refreshing" @click="onRefresh">
      <template #icon><n-icon><RefreshCw :size="16" :stroke-width="1.8" /></n-icon></template>
      刷新
    </n-button>
  </teleport>

  <DashboardUsage :usage="usage" :range="range" />

  <section v-if="quotasReady" class="card quota-card" aria-labelledby="quota-title">
    <div class="card-head">
      <h2 id="quota-title">可用额度</h2>
      <span v-if="exhaustedCount" class="quota-alert">{{ exhaustedCount }} 项已用尽</span>
    </div>
    <div v-if="tiles.length" class="card-body">
      <div ref="gridEl" class="qtiles" :class="{ 'is-sorting': !!drag, 'has-groups': quotaGroups.length > 1 }">
        <div v-for="group in quotaGroups" :key="group.kind" class="quota-section" :class="{ 'balance-group': group.kind === 'balance' }">
        <div v-if="group.kind === 'balance'" class="balance-heading">余额与积分</div>
        <div class="quota-group" :class="{ 'balance-tiles': group.kind === 'balance' }">
        <template v-for="q in group.tiles" :key="q.id">
        <div
          class="qtile"
          :data-qid="q.id"
          :class="{
            'is-balance': q.kind === 'balance',
            'is-origin': drag?.id === q.id,
            'can-sort': canSort,
          }"
          @pointerdown="onTilePointerDown(q.id, $event)"
          @pointermove="onTilePointerMove($event)"
          @pointerup="onTilePointerUp"
          @pointercancel="onTilePointerUp"
        >
          <div class="q-header">
          <div class="q-house">
            <AgentMark :id="q.id" />
            <n-popover trigger="click" placement="top-start">
              <template #trigger>
                <button type="button" class="q-name" :title="q.name" :aria-label="`${q.name}，查看完整名称`">{{ q.name }}</button>
              </template>
              <span class="q-full-name">{{ q.name }}</span>
            </n-popover>
          </div>
          <div class="q-corner">
            <span v-if="q.plan" class="q-plan">{{ q.plan }}</span>
            <n-popover
              v-if="q.kind === 'balance' && q.dueHint"
              trigger="click"
              placement="top-end"
              @update:show="(on: boolean) => { if (on) loadDue(q.id) }"
            >
              <template #trigger>
                <button type="button" class="q-due" draggable="false" :aria-label="q.name + ' 最近到期'">
                  <n-icon :size="14"><CircleAlert :stroke-width="1.8" /></n-icon>
                </button>
              </template>
              <span class="q-due-tip">{{ dueText(q.id) || '读取中' }}</span>
            </n-popover>
          </div>
          </div>
          <template v-if="q.kind === 'balance'">
            <b class="q-metric num">{{ q.text }}</b>
          </template>
          <template v-else>
            <div v-for="w in q.windows" :key="w.name" class="qwin" :class="{ 'is-hot': w.hot }">
              <span class="qwin-name">{{ w.name }}</span>
              <span class="qbar" aria-hidden="true">
                <i :style="{ width: w.remain + '%', background: remainColor(w.remain) }" />
              </span>
              <b class="q-num num">{{ Math.round(w.remain) }}%</b>
              <span class="qwin-period">{{ w.period }}</span>
            </div>
          </template>
        </div>
        </template>
        </div>
        </div>
      </div>
    </div>
    <p v-else class="quota-empty">暂无可用额度，可在设置中检查已启用来源。</p>
  </section>

  <details v-if="!usage.error" class="history" :open="historyOpen" @toggle="toggleHistory">
    <summary><span>近半年活动</span><span class="history-summary num">{{ heatStats.active }}活跃 · 合计 {{ heatStats.total }}</span><ChevronDown :size="16" aria-hidden="true" /></summary>
    <div class="heat-body">
      <UsageHeatmap :days="usage.days" />
      <div class="heat-stats source-hint">
        <span :title="heatStats.peakDate">单日峰值 <b class="num">{{ heatStats.peak }}</b></span>
        <span>最长连续 <b class="num">{{ heatStats.streak }}</b></span>
      </div>
    </div>
  </details>

  <Teleport to="body">
    <div
      v-if="drag"
      class="qtile qtile-float"
      :class="{ 'is-settle': drag.settling, 'is-balance': drag.isBalance }"
      :style="ghostStyle"
      v-html="drag.html"
    />
  </Teleport>
</template>

<style scoped>
.quota-card { container: quota / inline-size; }
h2 { margin: 0; font-size: var(--fs-card); font-weight: 600; }
.source-hint { color: var(--dim); font-size: var(--fs-caption); }
.tabs { display: inline-flex; align-items: center; gap: var(--sp-1); }
.tabs button { border: 0; border-radius: var(--r-in); background: transparent; color: var(--dim); font: inherit; font-size: var(--fs-small); padding: 0 var(--sp-3); height: var(--h-control); cursor: pointer; }
.tabs button:hover { color: var(--text); }
.tabs button[aria-pressed='true'] { color: var(--accent-solid); background: var(--accent-soft); }
.quota-card > .card-head { border-bottom: 0; flex-wrap: wrap; padding-bottom: 0; }
.quota-alert { color: var(--danger); margin-left: auto; font-size: var(--fs-caption); font-weight: 400; }
.quota-empty { margin: 0; padding: var(--pad-card); color: var(--dim); }
.history { border-top: 1px solid var(--stroke); flex-shrink: 0; }
.history summary { display: flex; align-items: center; flex-wrap: wrap; gap: var(--sp-2) var(--sp-4); padding: var(--sp-4) 0; list-style: none; cursor: pointer; }
.history summary::-webkit-details-marker { display: none; }
.history-summary { color: var(--dim); font-size: var(--fs-caption); margin-left: auto; }
.history[open] summary svg { transform: rotate(180deg); }
.heat-body { display: flex; flex-direction: column; gap: var(--sp-3); padding-bottom: var(--sp-3); }
.heat-body :deep(.heat) { width: 100%; min-width: 0; container-type: inline-size; }
.heat-body :deep(.heat-board) { --heat-cell: clamp(8px, calc((100cqi - 123px) / 26), 16px); }
.heat-stats { display: flex; flex-wrap: wrap; gap: var(--sp-3) var(--sp-6); }
.heat-stats b { font-weight: 500; }
:global(html.ah-tile-sorting) {
  cursor: grabbing;
}
.qtiles {
  position: relative;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--sp-6) var(--sp-7);
  align-items: start;
}
.quota-group { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr)); gap: var(--sp-6) var(--sp-7); align-items: start; min-width: 0; }
.quota-section { min-width: 0; }
.balance-tiles { grid-template-columns: repeat(auto-fit, minmax(min(100%, 160px), 1fr)); gap: var(--sp-4); }
.balance-heading { border-top: 1px solid var(--stroke); padding-top: var(--sp-4); margin-bottom: var(--sp-4); color: var(--dim); font-size: var(--fs-caption); }
@container quota (min-width: 1120px) {
  .qtiles.has-groups { grid-template-columns: minmax(0, 3fr) minmax(220px, 1fr); }
  .has-groups .balance-group { border-left: 1px solid var(--stroke); padding-left: var(--sp-6); }
  .has-groups .balance-heading { border-top: 0; padding-top: var(--sp-4); }
}
.qtile {
  position: relative;
  display: flex;
  flex-direction: column;
  min-width: 0;
  gap: var(--sp-3);
  padding: var(--sp-4) 0 0;
  border-top: 1px solid var(--stroke);
  background: var(--surface);
}
.qtile.is-balance { border-top: 0; padding-top: 0; }
.qtile.can-sort {
  cursor: grab;
  touch-action: none;
}
.qtile.can-sort .q-due {
  cursor: pointer;
  touch-action: auto;
}
.qtiles.is-sorting .qtile {
  user-select: none;
  cursor: grabbing;
}
.qtile.is-origin {
  background: var(--wash);
  outline: 1px dashed var(--stroke-strong);
  outline-offset: 4px;
}
.qtile.is-origin > * {
  visibility: hidden;
}
.qtile-float {
  position: fixed;
  z-index: 80;
  margin: 0;
  box-sizing: border-box;
  pointer-events: none;
  transform: scale(1.04);
  transform-origin: center;
  border-color: var(--accent-solid);
}
.qtile-float.is-settle {
  transform: none;
  transition:
    left 260ms cubic-bezier(0.2, 0.7, 0.2, 1),
    top 260ms cubic-bezier(0.2, 0.7, 0.2, 1),
    width 260ms cubic-bezier(0.2, 0.7, 0.2, 1),
    height 260ms cubic-bezier(0.2, 0.7, 0.2, 1),
    transform 260ms cubic-bezier(0.2, 0.7, 0.2, 1);
}
@media (prefers-reduced-motion: reduce) {
  .qtile-float {
    transform: none;
  }
  .qtile-float.is-settle {
    transition: none;
  }
}
.q-header { display: flex; align-items: center; gap: var(--sp-2); min-width: 0; }
.q-corner {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 4px;
  max-width: 48%;
}
.q-due {
  display: grid;
  place-items: center;
  width: 18px;
  height: 18px;
  padding: 0;
  border: 0;
  border-radius: var(--r-in);
  background: var(--wash);
  color: var(--dim);
  cursor: pointer;
  flex: none;
}
.q-due:hover,
.q-due:focus-visible {
  color: var(--text);
  background: var(--seg-on-bg);
}
.q-due-tip {
  font-size: var(--fs-caption);
  color: var(--text);
}
.q-plan {
  display: inline-flex;
  align-items: center;
  height: 18px;
  padding: 0 var(--sp-2);
  border-radius: var(--r-pill);
  background: var(--accent-soft);
  color: var(--accent-solid);
  font-size: var(--fs-caption);
  font-weight: 600;
  letter-spacing: 0.02em;
  line-height: 1;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.q-house {
  flex: 1;
  min-width: 0;
  min-height: 20px;
  display: flex;
  align-items: center;
  gap: var(--sp-2);
}
.q-name {
  border: 0;
  padding: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  text-align: left;
  cursor: pointer;
  font-size: var(--fs-small);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.q-name:hover { color: var(--accent-solid); }
.q-full-name { display: block; max-width: min(320px, 75vw); overflow-wrap: anywhere; }
.q-metric {
  font-size: var(--fs-card);
  font-weight: 600;
  letter-spacing: -0.02em;
  line-height: 1.1;
}
.q-num {
  font-size: var(--fs-small);
  font-weight: 600;
}
.qwin {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto;
  align-items: center;
  gap: var(--sp-2);
  min-height: 28px;
  font-size: var(--fs-caption);
  color: var(--dim);
}
.qwin-name {
  color: var(--text);
}
.qbar {
  grid-column: 1 / -1;
  grid-row: 2;
  height: 4px;
  border-radius: var(--r-pill);
  background: var(--wash);
  overflow: hidden;
}
.qbar i {
  display: block;
  height: 100%;
}
.qwin.is-hot .q-num {
  color: var(--danger);
}
.qwin.is-hot .qbar i {
  background: var(--danger);
}
.qwin-period {
  color: var(--faint);
  text-align: right;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: normal;
  overflow-wrap: anywhere;
}


</style>
