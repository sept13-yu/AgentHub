<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ArrowDownRight, ArrowUpRight, ChevronDown, ChevronUp, Info } from 'lucide-vue-next'
import UsageBreakdownList from './UsageBreakdownList.vue'
import { costCurrency, toggleCostCurrency } from '../costCurrency'
import { displayCostText, RANGES, splitTokens, type RangeKey, type UsageView } from '../usageView'
import { agentRows, modelRows, usageSegments, type UsageDimension } from '../usageBreakdown'

const props = defineProps<{ usage: UsageView; range: RangeKey }>()
function storedDimension(): UsageDimension {
  try { return localStorage.getItem('agenthub-usage-dimension') === 'model' ? 'model' : 'agent' }
  catch { return 'agent' }
}
const dimension = ref<UsageDimension>(storedDimension())
const ranking = ref<HTMLElement | null>(null)
const selectedKey = ref<string | null>(null)
const highlightedKey = ref<string | null>(null)
const hoveredSegment = ref<string | null>(null)
const showAll = ref(false)
const showAllDetails = ref(false)
const rangeLabel = computed(() => RANGES.find(r => r.key === props.range)?.label ?? '')
const total = computed(() => splitTokens(props.usage.totalTokens))
const cost = computed(() => {
  const value = props.usage.cost
  return displayCostText(value?.kind === 'amount' ? { ...value, partial: false } : value)
})
const costHint = computed(() => costCurrency.value === 'CNY' ? '点击改为美元' : '点击改为人民币')
const partial = computed(() => props.usage.cost?.kind === 'none' || props.usage.cost?.kind === 'amount' && props.usage.cost.partial)
const rows = computed(() => dimension.value === 'agent' ? agentRows(props.usage.agents) : modelRows(props.usage.agents))
const visibleRows = computed(() => dimension.value === 'model' && !showAll.value ? rows.value.slice(0, 5) : rows.value)
const selected = computed(() => rows.value.find(row => row.key === selectedKey.value))
const details = computed(() => selected.value?.children ?? [])
const visibleDetails = computed(() => showAllDetails.value ? details.value : details.value.slice(0, 5))
const segments = computed(() => {
  const values = usageSegments(rows.value, dimension.value, props.usage.totalTokens)
  const sum = values.reduce((n, row) => n + row.tokens, 0)
  let offset = 0
  return values.map(row => {
    const length = sum ? row.tokens / sum * 100 : 0
    const segment = { ...row, length, offset }
    offset += length
    return segment
  })
})
const activeSegment = computed(() => hoveredSegment.value ?? segments.value.find(s => s.rowKeys.includes(highlightedKey.value ?? selectedKey.value ?? ''))?.key)
const ringHint = computed(() => {
  const row = rows.value.find(r => r.key === (highlightedKey.value ?? selectedKey.value))
  const segment = segments.value.find(s => s.key === activeSegment.value)
  const target = hoveredSegment.value ? segment : row ?? segment
  return target ? `${target.name} · ${share(target.tokens)}` : dimension.value === 'model' && rows.value.length > 5 ? '前 5 名 + 其他' : 'Token'
})

function share(tokens: number) {
  const value = props.usage.totalTokens > 0 ? tokens / props.usage.totalTokens * 100 : 0
  return value > 0 && value < 1 ? '<1%' : `${Math.round(value)}%`
}
function selectRow(key: string) {
  selectedKey.value = selectedKey.value === key ? null : key
  showAllDetails.value = false
}
function closeDetails() {
  const trigger = ranking.value?.querySelector<HTMLButtonElement>('[aria-expanded="true"]')
  selectedKey.value = null
  trigger?.focus()
}
function toggleModels() {
  showAll.value = !showAll.value
  if (!showAll.value && !rows.value.slice(0, 5).some(row => row.key === selectedKey.value)) selectedKey.value = null
}
function resetDetails() {
  selectedKey.value = null
  highlightedKey.value = null
  hoveredSegment.value = null
  showAllDetails.value = false
  showAll.value = false
}
watch(dimension, value => {
  resetDetails()
  try { localStorage.setItem('agenthub-usage-dimension', value) } catch { /* 隐私模式 */ }
})
watch(() => props.range, resetDetails)
watch(rows, value => {
  if (!value.some(row => row.key === selectedKey.value)) selectedKey.value = null
  if (!value.some(row => row.key === highlightedKey.value)) highlightedKey.value = null
})
</script>

<template>
  <section class="card usage-card" aria-labelledby="usage-title">
    <div class="card-body">
      <div class="usage-heading">
        <div class="heading-label">
          <h2 id="usage-title">用量分布</h2>
          <div v-if="!usage.error && usage.delta" class="delta" :class="usage.delta.kind">
            <span class="hint">{{ usage.delta.vs }}</span>
            <ArrowUpRight v-if="usage.delta.kind === 'up'" :size="14" aria-hidden="true" />
            <ArrowDownRight v-else-if="usage.delta.kind === 'down'" :size="14" aria-hidden="true" />
            <span class="num">{{ usage.delta.text }}</span>
          </div>
        </div>
        <div class="dimension-switch" role="group" aria-label="用量统计维度">
          <button type="button" :aria-pressed="dimension === 'agent'" @click="dimension = 'agent'">按 Agent</button>
          <button type="button" :aria-pressed="dimension === 'model'" @click="dimension = 'model'">按模型</button>
        </div>
      </div>
      <p v-if="usage.error" class="usage-error" role="alert">{{ usage.error }}</p>
      <template v-else>
        <div class="usage-layout" :class="{ 'all-models': dimension === 'model' && showAll }">
          <div class="ring-col">
            <div class="donut">
              <svg viewBox="0 0 100 100" role="img" :aria-label="`${rangeLabel}用量 ${total.val}${total.unit} Token，${dimension === 'agent' ? '按 Agent' : '按模型'}分布`">
                <circle class="ring-track" cx="50" cy="50" r="43" />
                <circle v-for="segment in segments" :key="segment.key" cx="50" cy="50" r="43" pathLength="100"
                  class="ring-segment" :class="{ muted: activeSegment && activeSegment !== segment.key }"
                  :style="{ stroke: segment.color }" :stroke-dasharray="`${segment.length} ${100 - segment.length}`"
                  :stroke-dashoffset="-segment.offset" transform="rotate(-90 50 50)"
                  @mouseenter="hoveredSegment = segment.key" @mouseleave="hoveredSegment = null">
                  <title>{{ segment.name }} · {{ share(segment.tokens) }}</title>
                </circle>
              </svg>
              <div class="donut-core">
                <span class="hint">{{ rangeLabel }}用量</span>
                <strong class="hero num">{{ total.val }}<small>{{ total.unit }}</small></strong>
              </div>
            </div>
            <div class="ring-hint hint" :title="ringHint">{{ ringHint }}</div>
            <div v-if="usage.cost" class="cost">
              <span class="hint">估算费用</span>
              <button v-if="usage.cost.kind === 'amount'" type="button" :aria-label="costHint" :title="costHint" class="cost-toggle num" @click="toggleCostCurrency">{{ cost }}</button>
              <span v-else class="hint">{{ cost }}</span>
              <span v-if="partial" tabindex="0" role="img" aria-label="部分模型暂无报价，费用估算不完整" title="部分模型暂无报价，费用估算不完整"><Info :size="14" aria-hidden="true" /></span>
            </div>
          </div>
          <div ref="ranking" class="ranking">
            <UsageBreakdownList v-if="rows.length" :rows="visibleRows" :total="usage.totalTokens" :selected="selectedKey" expandable @select="selectRow" @highlight="highlightedKey = $event" />
            <p v-else class="hint">{{ usage.totalTokens > 0 ? '暂无模型明细' : '当前时间范围暂无用量记录' }}</p>
            <button v-if="dimension === 'model' && rows.length > 5" type="button" class="text-button show-all" :aria-expanded="showAll" @click="toggleModels">
              {{ showAll ? '收起模型列表' : `查看全部 ${rows.length} 个模型` }}<ChevronUp v-if="showAll" :size="14" /><ChevronDown v-else :size="14" />
            </button>
          </div>
        </div>
        <section v-if="selected" id="usage-detail" class="usage-detail" aria-labelledby="detail-title">
          <div class="detail-heading">
            <h3 id="detail-title">{{ selected.name }}<span class="hint">{{ dimension === 'agent' ? '模型细分' : '来源分布' }}</span></h3>
            <button type="button" class="text-button" @click="closeDetails">收起<ChevronUp :size="14" /></button>
          </div>
          <p v-if="selected.noPrice" class="hint">部分用量暂无报价，未计入完整费用估算</p>
          <UsageBreakdownList v-if="details.length" :rows="visibleDetails" :total="selected.tokens" />
          <p v-else class="hint">暂无模型明细</p>
          <button v-if="details.length > 5" type="button" class="text-button show-all" :aria-expanded="showAllDetails" @click="showAllDetails = !showAllDetails">
            {{ showAllDetails ? '收起明细列表' : `查看全部 ${details.length} 项` }}<ChevronUp v-if="showAllDetails" :size="14" /><ChevronDown v-else :size="14" />
          </button>
        </section>
      </template>
    </div>
  </section>
</template>

<style scoped>
.usage-card { container: usage / inline-size; }
h2, h3 { margin: 0; font-size: var(--fs-card); font-weight: 600; }
.usage-heading, .heading-label, .detail-heading { display: flex; align-items: center; gap: var(--sp-3); }
.usage-heading { justify-content: space-between; flex-wrap: wrap; margin-bottom: var(--sp-4); }
.heading-label { flex-wrap: wrap; }
.hint { color: var(--dim); font-size: var(--fs-caption); }
.dimension-switch { display: flex; padding: 3px; border: 1px solid var(--stroke); border-radius: var(--r-in); }
.dimension-switch button { min-height: 28px; padding: var(--sp-1) var(--sp-3); border: 0; border-radius: var(--r-in); background: transparent; color: var(--dim); font: inherit; font-size: var(--fs-small); cursor: pointer; }
.dimension-switch button[aria-pressed='true'] { background: var(--accent-soft); color: var(--accent-solid); }
button:focus-visible, .cost span:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: 2px; }
.usage-layout { display: grid; grid-template-columns: minmax(240px, .65fr) minmax(0, 1.35fr); align-items: center; gap: clamp(24px, 4cqi, 64px); max-width: 1160px; margin-inline: auto; }
.all-models { align-items: start; }
.ring-col { display: flex; flex-direction: column; align-items: center; gap: var(--sp-2); min-width: 0; }
.donut { width: 220px; height: 220px; position: relative; }
.donut svg { width: 100%; height: 100%; overflow: visible; }
.ring-track, .ring-segment { fill: none; stroke-width: 8; }
.ring-track { stroke: var(--wash); }
.ring-segment { transition: opacity var(--dur); }
.ring-segment.muted { opacity: .24; }
.donut-core { position: absolute; inset: 30px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: var(--sp-2); pointer-events: none; }
.hero { font-size: var(--fs-hero); font-weight: 600; line-height: 1.1; letter-spacing: -.03em; }
.hero small { margin-left: var(--sp-1); font-size: var(--fs-small); font-weight: 400; letter-spacing: 0; }
.ring-hint { max-width: 100%; min-height: 18px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.cost { display: flex; align-items: center; justify-content: center; flex-wrap: wrap; gap: var(--sp-2); }
.cost span[role='img'] { display: flex; color: var(--dim); }
.cost-toggle { border: 0; padding: 0; background: transparent; color: var(--text); font: inherit; font-size: var(--fs-small); cursor: pointer; }
.ranking { min-width: 0; }
.text-button { display: inline-flex; align-items: center; justify-content: center; gap: var(--sp-1); border: 0; background: transparent; color: var(--dim); font: inherit; font-size: var(--fs-caption); min-height: 32px; cursor: pointer; border-radius: var(--r-in); padding: var(--sp-1) var(--sp-2); }
.text-button:hover { color: var(--accent-solid); background: var(--wash); }
.show-all { display: flex; margin: var(--sp-2) 0 0 auto; }
.usage-detail { margin-top: var(--sp-5); border-top: 1px solid var(--stroke); padding-top: var(--sp-3); }
.detail-heading { justify-content: space-between; margin-bottom: var(--sp-2); }
.detail-heading h3 { min-width: 0; overflow-wrap: anywhere; }
.detail-heading h3 .hint { margin-left: var(--sp-2); font-weight: 400; }
.detail-heading button { flex: none; }
.delta { display: inline-flex; align-items: center; gap: var(--sp-1); font-size: var(--fs-caption); }
.delta.up { color: var(--warn); }
.delta.down { color: var(--ok); }
.delta.flat { color: var(--dim); }
.usage-error { margin: 0; padding: var(--sp-3); background: var(--error-soft); color: var(--error-fg); border-radius: var(--r-in); }
@container usage (max-width: 560px) {
  .usage-layout { grid-template-columns: minmax(0, 1fr); gap: var(--sp-4); }
  .donut { width: 190px; height: 190px; }
}
@media (pointer: coarse) { .dimension-switch button, .text-button { min-height: 44px; } }
@media (prefers-reduced-motion: reduce) { .ring-segment { transition: none; } }
</style>
