<script setup lang="ts">
import { computed, inject, nextTick, onMounted, onUnmounted, reactive, ref, watch, type Ref } from 'vue'
import { NButton, NIcon, NPopover, useMessage } from 'naive-ui'
import { ArrowDownRight, ArrowUpRight, CircleAlert, RefreshCw } from 'lucide-vue-next'
import AgentMark from '../components/AgentMark.vue'
import UsageHeatmap from '../components/UsageHeatmap.vue'
import { get, post, put, WRITABLE } from '../api'
import { moveItem } from '../settingsModel'
import { toQuotaTiles, type QuotaTile } from '../quotaView'
import { dashCache } from '../dashCache'
import { usePageHotkeys } from '../hotkeys'
import {
  formatTokens,
  RANGES,
  splitTokens,
  toUsageView,
  usageErrorView,
  visibleModels,
  type RangeKey,
} from '../usageView'

const message = useMessage()
const pageLoading = inject<Ref<boolean>>('page-loading')

const range = ref<RangeKey>(dashCache.range)
const expanded = ref<string | null>(dashCache.expanded)
const refreshing = ref(false)
const usage = ref(dashCache.usage)
const quotasReady = ref(dashCache.quotasReady)
const tiles = ref<QuotaTile[]>(dashCache.tiles)
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
  span2: boolean
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
const modelsEl = ref<HTMLElement | null>(null)
const modelsOverflow = ref(false)

const rangeLabel = computed(() => RANGES.find((r) => r.key === range.value)?.label ?? '')
const heroSplit = computed(() => splitTokens(usage.value.totalTokens))
const ringPaint = computed(() => {
  const parts = usage.value.agents.filter((a) => a.pct > 0)
  if (!parts.length) return { background: 'var(--wash)' }
  let acc = 0
  const stops: string[] = []
  for (const a of parts) {
    const from = acc
    acc = Math.min(100, acc + a.pct)
    stops.push(`${a.color} ${from}% ${acc}%`)
  }
  if (acc < 100) stops.push(`var(--wash) ${acc}% 100%`)
  return { background: `conic-gradient(from -90deg, ${stops.join(', ')})` }
})
const openAgent = computed(() => usage.value.agents.find((a) => a.id === expanded.value) ?? null)

// 热力图右侧的统计砖：全部由 usage.days 现算，口径与热力图一致（不含未来日）。
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
const shownModels = computed(() => {
  const agent = openAgent.value
  if (!agent) return []
  return modelsOverflow.value ? visibleModels(agent) : agent.models
})

async function measureModels() {
  modelsOverflow.value = false
  await nextTick()
  const el = modelsEl.value
  modelsOverflow.value = !!el && el.scrollHeight > el.clientHeight + 1
}

function setLoading(on: boolean) {
  if (pageLoading) pageLoading.value = on
}

function persist() {
  dashCache.range = range.value
  dashCache.expanded = expanded.value
  dashCache.usage = usage.value
  dashCache.quotasReady = quotasReady.value
  dashCache.tiles = tiles.value
  dashCache.primed = true
}

function pickRange(key: RangeKey) {
  if (key === range.value) return
  range.value = key
  expanded.value = null
  persist()
}

function toggleAgent(id: string) {
  expanded.value = expanded.value === id ? null : id
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
    if (expanded.value && !usage.value.agents.some((a) => a.id === expanded.value))
      expanded.value = null
    persist()
  } catch (e) {
    usage.value = usageErrorView(errMessage(e))
    expanded.value = null
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
    // 启动首次拿到的是盘上旧值（stale）：再拉一次，等服务端后台刷新完成换新值
    if (!force && raw.stale) void loadQuotas(false)
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

watch([() => openAgent.value?.id, () => openAgent.value?.models.length], () => {
  void measureModels()
})

function onResize() {
  void measureModels()
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
      span2: p.el.classList.contains('span-2'),
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
  if (dashCache.primed) {
    void measureModels()
    return
  }
  // 首屏遮罩只等本地 usage（毫秒级）；额度后台补齐，不卡启动
  setLoading(true)
  void loadQuotas(false)
  void loadUsage().finally(() => {
    setLoading(false)
    void measureModels()
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
    <n-button type="primary" :loading="refreshing" @click="onRefresh">
      <template #icon><n-icon><RefreshCw :size="16" :stroke-width="1.8" /></n-icon></template>
      刷新
    </n-button>
  </teleport>

  <section class="card" aria-labelledby="usage-title">
    <div class="card-body">
      <p v-if="usage.error" class="usage-error">{{ usage.error }}</p>
      <div v-else class="usage-stack">
      <div class="split">
        <div class="ring-col">
          <div class="donut-wrap">
            <div class="donut-track" aria-hidden="true" />
            <div class="donut-ring" aria-hidden="true" :style="ringPaint" />
            <div class="donut-core">
              <span id="usage-title" class="hero num">{{ heroSplit.val }}</span>
              <span class="unit">{{ heroSplit.unit ? heroSplit.unit + ' · ' : '' }}{{ rangeLabel }}</span>
            </div>
          </div>
          <div class="ring-meta">
            <div v-if="usage.delta" class="delta" :class="usage.delta.kind">
              <n-icon v-if="usage.delta.kind === 'up'" :size="14"><ArrowUpRight :stroke-width="2.4" /></n-icon>
              <n-icon v-else-if="usage.delta.kind === 'down'" :size="14"><ArrowDownRight :stroke-width="2.4" /></n-icon>
              <span class="vs">{{ usage.delta.vs }}</span>
              <span class="num">{{ usage.delta.text }}</span>
            </div>
            <div v-if="usage.cost" class="cost num">{{ usage.cost.text }}</div>
          </div>
        </div>
        <div v-if="usage.agents.length">
          <div class="agents">
            <button
              v-for="a in usage.agents"
              :key="a.id"
              type="button"
              class="agent"
              :aria-expanded="expanded === a.id ? 'true' : 'false'"
              @click="toggleAgent(a.id)"
            >
              <AgentMark :id="a.id" />
              <span class="agent-name">{{ a.name }}</span>
              <span class="bar" aria-hidden="true"><i :style="{ '--c': a.color, '--w': a.pct + '%' }" /></span>
              <span class="agent-stat num">{{ formatTokens(a.tokens) }} · {{ a.pct }}%</span>
            </button>
          </div>
          <template v-if="openAgent">
            <div class="models-label">{{ openAgent.name }} 的模型</div>
            <div ref="modelsEl" class="models">
              <span v-for="m in shownModels" :key="m.name" class="model">
                <i :style="{ '--c': openAgent.color }" />{{ m.name }} {{ m.pct }}% · {{ formatTokens(m.tokens) }}
              </span>
            </div>
          </template>
        </div>
      </div>
      <div class="heat-row">
        <UsageHeatmap :days="usage.days" />
        <div class="heat-stats">
          <div class="hstat">
            <span class="hstat-lbl">近半年合计</span>
            <span class="hstat-val num">{{ heatStats.total }}</span>
          </div>
          <div class="hstat">
            <span class="hstat-lbl">活跃天数</span>
            <span class="hstat-val num">{{ heatStats.active }}</span>
          </div>
          <div class="hstat">
            <span class="hstat-lbl" :title="heatStats.peakDate">单日峰值</span>
            <span class="hstat-val num">{{ heatStats.peak }}</span>
          </div>
          <div class="hstat">
            <span class="hstat-lbl">最长连续</span>
            <span class="hstat-val num">{{ heatStats.streak }}</span>
          </div>
        </div>
      </div>
      </div>
    </div>
  </section>

  <section v-if="quotasReady" class="card" aria-labelledby="quota-title">
    <div class="card-head">
      <span id="quota-title">额度</span>
      <span v-if="canSort" class="hint">拖动排序</span>
    </div>
    <div v-if="tiles.length" class="card-body">
      <div ref="gridEl" class="qtiles" :class="{ 'is-sorting': !!drag }">
        <div
          v-for="q in tiles"
          :key="q.id"
          class="qtile"
          :data-qid="q.id"
          :class="{
            'span-2': q.kind !== 'balance' && q.span === 2,
            'is-origin': drag?.id === q.id,
            'can-sort': canSort,
          }"
          @pointerdown="onTilePointerDown(q.id, $event)"
          @pointermove="onTilePointerMove($event)"
          @pointerup="onTilePointerUp"
          @pointercancel="onTilePointerUp"
        >
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
          <div class="q-house">
            <AgentMark :id="q.id" />
            <span class="q-name">{{ q.name }}</span>
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
      </div>
    </div>
  </section>
  <Teleport to="body">
    <div
      v-if="drag"
      class="qtile qtile-float"
      :class="{ 'is-settle': drag.settling }"
      :style="ghostStyle"
      v-html="drag.html"
    />
  </Teleport>
</template>

<style scoped>
.usage-stack {
  display: flex;
  flex-direction: column;
  gap: var(--sp-6);
}
.heat-row {
  display: flex;
  align-items: stretch;
  flex-wrap: wrap;
  gap: var(--sp-5);
  padding-top: var(--sp-5);
  box-shadow: var(--rule-hi);
}
.heat-row :deep(.heat) {
  width: auto;
  flex: none;
}
.heat-stats {
  flex: 1 1 300px;
  min-width: 280px;
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--sp-3);
}
.hstat {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-4);
  min-width: 0;
  min-height: 48px;
  padding: 0 var(--sp-4);
  border: 1px solid var(--stroke);
  border-radius: var(--r-card);
  background: var(--surface);
}
.hstat-lbl {
  font-size: var(--fs-caption);
  color: var(--faint);
  flex: none;
}
.hstat-val {
  font-size: var(--fs-card);
  font-weight: 600;
  letter-spacing: -0.01em;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.split {
  display: grid;
  grid-template-columns: 240px minmax(0, 1fr);
  gap: var(--sp-7);
  align-items: center;
}
.ring-col {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--sp-3);
}
.donut-wrap {
  width: 200px;
  height: 200px;
  position: relative;
}
.donut-track,
.donut-ring {
  position: absolute;
  inset: 0;
  border-radius: 50%;
  -webkit-mask: radial-gradient(farthest-side, transparent calc(100% - 16px), #000 calc(100% - 15px));
  mask: radial-gradient(farthest-side, transparent calc(100% - 16px), #000 calc(100% - 15px));
}
.donut-track {
  background: var(--wash);
}
.donut-core {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  text-align: center;
}
.donut-core .hero {
  font-size: var(--fs-hero);
  font-weight: 600;
  letter-spacing: -0.03em;
  line-height: 1;
}
.donut-core .unit {
  font-size: var(--fs-caption);
  color: var(--faint);
  margin-top: var(--sp-1);
}
.ring-meta {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--sp-1);
}
.delta {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: var(--fs-body);
  font-weight: 500;
}
.delta .vs {
  font-weight: 400;
  color: var(--faint);
}
.delta.up {
  color: var(--warn);
}
.delta.down {
  color: var(--ok);
}
.delta.flat {
  color: var(--dim);
  font-weight: 400;
}
.cost {
  font-size: var(--fs-body);
  color: var(--dim);
}
.usage-error {
  margin: 0;
  padding: var(--sp-3) var(--sp-4);
  color: var(--error-fg);
  background: var(--error-soft);
  border-radius: var(--r-in);
  font-size: var(--fs-body);
}

.tabs {
  display: inline-flex;
  align-items: stretch;
  gap: var(--sp-4);
  height: var(--h-control);
}
.tabs button {
  border: 0;
  background: transparent;
  color: var(--dim);
  font: inherit;
  font-size: var(--fs-small);
  padding: 0;
  height: var(--h-control);
  cursor: pointer;
  box-shadow: inset 0 -2px 0 transparent;
  transition:
    color var(--dur) linear,
    box-shadow var(--dur) linear;
}
.tabs button:hover,
.tabs button:active {
  color: var(--text);
}
.tabs button[aria-pressed='true'] {
  color: var(--text);
  font-weight: 500;
  box-shadow: inset 0 -2px 0 var(--accent-solid);
}

.agents {
  display: flex;
  flex-direction: column;
  gap: var(--sp-2);
}
.agent {
  display: grid;
  grid-template-columns: var(--icon) 88px minmax(0, 1fr) auto;
  align-items: center;
  gap: var(--sp-3);
  width: 100%;
  height: var(--h-row);
  padding: 0 var(--sp-3);
  border: 1px solid var(--stroke);
  border-radius: 999px;
  background: var(--surface);
  color: inherit;
  font: inherit;
  cursor: pointer;
  text-align: left;
  transition:
    background var(--dur) linear,
    border-color var(--dur) linear;
}
.agent:hover {
  background: var(--surface-hi);
}
.agent:active,
.agent[aria-expanded='true'] {
  border-color: var(--stroke-strong);
  background: var(--surface-hi);
}
.dot {
  width: var(--sp-2);
  height: var(--sp-2);
  border-radius: 50%;
  flex: none;
  background: var(--c);
  box-shadow: 0 0 0 1px var(--dot-ring);
}
.agent-name {
  font-weight: 500;
  font-size: var(--fs-small);
}
.agent .bar {
  height: 4px;
  border-radius: var(--r-pill);
  background: var(--wash);
  overflow: hidden;
}
.agent .bar i {
  display: block;
  height: 100%;
  width: var(--w);
  background: var(--c);
  box-shadow: inset 0 0 0 1px var(--dot-ring);
}
.agent-stat {
  font-size: var(--fs-caption);
  color: var(--dim);
  min-width: 96px;
  text-align: right;
}

.models-label {
  font-size: var(--fs-caption);
  color: var(--faint);
  margin: var(--sp-4) 0 var(--sp-2);
}
.models {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-2);
  max-height: calc(var(--h-control) * 2 + var(--sp-2));
  overflow: auto;
}
.model {
  height: var(--h-control);
  padding: 0 var(--sp-3);
  border: 1px solid var(--stroke);
  background: var(--surface);
  border-radius: 999px;
  display: inline-flex;
  align-items: center;
  gap: var(--sp-2);
  font-size: var(--fs-caption);
  color: var(--dim);
}
.model i {
  width: var(--sp-2);
  height: var(--sp-2);
  border-radius: 50%;
  background: var(--c);
  box-shadow: 0 0 0 1px var(--dot-ring);
}

:global(html.ah-tile-sorting) {
  cursor: grabbing;
}
.qtiles {
  position: relative;
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--sp-3);
}
.qtile {
  position: relative;
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: var(--sp-2);
  min-width: 0;
  min-height: 64px;
  padding: var(--sp-3) var(--sp-4);
  border: 1px solid var(--stroke);
  border-radius: var(--r-card);
  background: var(--surface);
}
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
  border-style: dashed;
  border-color: var(--stroke-strong);
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
.q-corner {
  position: absolute;
  top: 6px;
  right: 6px;
  display: flex;
  align-items: center;
  gap: 4px;
  max-width: calc(100% - 12px);
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
.qtile.span-2 {
  grid-column: span 2;
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
  display: flex;
  align-items: center;
  gap: var(--sp-2);
}
.q-name {
  font-size: var(--fs-small);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.q-metric {
  font-size: var(--fs-metric);
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
  grid-template-columns: 52px minmax(0, 1fr) 36px 72px;
  align-items: center;
  gap: var(--sp-2);
  font-size: var(--fs-caption);
  color: var(--dim);
}
.qwin-name {
  color: var(--text);
}
.qbar {
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
  white-space: nowrap;
}

@media (max-width: 1279px) {
  .split {
    grid-template-columns: 1fr;
  }
  .qtiles {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
