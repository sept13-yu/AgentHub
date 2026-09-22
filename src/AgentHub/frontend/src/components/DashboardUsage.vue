<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { NPopover } from 'naive-ui'
import { ArrowDownRight, ArrowUpRight, ChevronLeft, ChevronRight } from 'lucide-vue-next'
import AgentMark from './AgentMark.vue'
import { costCurrency, toggleCostCurrency } from '../costCurrency'
import { displayCostText, formatTokens, RANGES, splitTokens, type RangeKey, type UsageView } from '../usageView'

const props = defineProps<{ usage: UsageView; range: RangeKey }>()
const card = ref<HTMLElement | null>(null)
const models = ref<HTMLElement | null>(null)
const stageHeight = ref(600)
const modelsWidth = ref(0)
let observer: ResizeObserver | undefined
const page = ref(0)
const rangeLabel = computed(() => RANGES.find((r) => r.key === props.range)?.label ?? '')
const total = computed(() => splitTokens(props.usage.totalTokens))
const cost = computed(() => {
  const value = props.usage.cost
  return displayCostText(value?.kind === 'amount' ? { ...value, partial: false } : value)
})
const costHint = computed(() => costCurrency.value === 'CNY' ? '点击改为美元' : '点击改为人民币')
const partial = computed(() => props.usage.cost?.kind === 'none' || props.usage.cost?.kind === 'amount' && props.usage.cost.partial)
const ranked = computed(() => props.usage.agents.flatMap((a) => a.models.map((m) => ({
  ...m, key: `${a.id}\0${m.name}`, agentId: a.id, agent: a.name,
}))).sort((a, b) => b.tokens - a.tokens || a.name.localeCompare(b.name, 'zh') || a.agentId.localeCompare(b.agentId)))

// 高度预算来自独立的页面滚动容器，避免观察卡片自身高度导致行数反复切换。
// 每行 44px，另留标题、表头、页脚 88px；不为填满高屏无限增加信息密度。
const rowLimit = computed(() => Math.max(4, Math.min(7, Math.floor((stageHeight.value * 0.54 - 88) / 44))))
const columns = computed(() => modelsWidth.value >= 740 && ranked.value.length > rowLimit.value ? 2 : 1)
const rowsPerColumn = computed(() => Math.min(rowLimit.value, Math.max(1, Math.ceil(ranked.value.length / columns.value))))
const pageSize = computed(() => rowsPerColumn.value * columns.value)
const pageCount = computed(() => Math.max(1, Math.ceil(ranked.value.length / pageSize.value)))
const pageRows = computed(() => ranked.value.slice(page.value * pageSize.value, (page.value + 1) * pageSize.value))
const groups = computed(() => Array.from({ length: columns.value }, (_, i) =>
  pageRows.value.slice(i * rowsPerColumn.value, (i + 1) * rowsPerColumn.value)))
const pageLabel = computed(() => `${page.value * pageSize.value + 1}–${Math.min((page.value + 1) * pageSize.value, ranked.value.length)} / 共 ${ranked.value.length} 个`)
const layoutStyle = computed(() => ({
  '--ring-size': `${180 + (rowLimit.value - 4) * 20}px`,
  '--model-rows': rowsPerColumn.value,
  '--model-columns': columns.value,
}))

watch(pageSize, (next, previous) => {
  page.value = Math.min(pageCount.value - 1, Math.floor(page.value * previous / next))
})
watch(pageCount, (count) => { page.value = Math.min(page.value, count - 1) })
watch(() => props.range, () => { page.value = 0 })

function share(tokens: number) {
  const value = props.usage.totalTokens > 0 ? tokens / props.usage.totalTokens * 100 : 0
  return value > 0 && value < 1 ? '<1%' : `${Math.round(value)}%`
}
const ring = computed(() => {
  const agents = props.usage.agents.filter((a) => a.tokens > 0)
  const sum = agents.reduce((n, a) => n + a.tokens, 0)
  let end = 0
  const stops = agents.map((a) => {
    const start = end
    end += a.tokens / sum * 100
    return `${a.color} ${start}% ${end}%`
  })
  return { background: stops.length ? `conic-gradient(from -90deg, ${stops.join(',')})` : 'var(--wash)' }
})

onMounted(() => {
  const stage = card.value?.closest('.stage')
  observer = new ResizeObserver(() => {
    stageHeight.value = stage?.clientHeight || window.innerHeight
    modelsWidth.value = models.value?.clientWidth || 0
  })
  if (stage) observer.observe(stage)
  if (card.value) observer.observe(card.value)
  if (models.value) observer.observe(models.value)
})
// 空态 / 请求失败恢复后 models 节点会替换，重新观察真实可用宽度。
watch(models, (next, previous) => {
  if (previous) observer?.unobserve(previous)
  if (next) {
    observer?.observe(next)
    modelsWidth.value = next.clientWidth
  }
}, { flush: 'post' })
onUnmounted(() => observer?.disconnect())
</script>

<template>
  <section ref="card" class="card usage-card" aria-labelledby="usage-title" :style="layoutStyle">
    <div class="card-body">
      <div class="usage-heading">
        <h2 id="usage-title">用量分布</h2>
        <div v-if="!usage.error && usage.delta" class="delta" :class="usage.delta.kind">
          <span class="hint">{{ usage.delta.vs }}</span>
          <ArrowUpRight v-if="usage.delta.kind === 'up'" :size="14" aria-hidden="true" /><ArrowDownRight v-else-if="usage.delta.kind === 'down'" :size="14" aria-hidden="true" />
          <span class="num">{{ usage.delta.text }}</span>
        </div>
      </div>
      <p v-if="usage.error" class="usage-error" role="alert">{{ usage.error }}</p>
      <div v-else class="usage-layout" :class="{ 'is-empty': !usage.agents.length }">
        <div class="overview" :class="{ 'many-agents': usage.agents.length > 5 }">
          <div class="ring-col">
            <div class="donut" role="img" :aria-label="`${rangeLabel}用量 ${total.val}${total.unit} Token`" :style="ring">
              <div class="donut-core">
                <span class="hint">{{ rangeLabel }}用量</span>
                <strong class="hero num">{{ total.val }}<small>{{ total.unit }}</small></strong>
                <span class="hint">Token</span>
              </div>
            </div>
            <div v-if="usage.cost" class="cost">
              <span class="hint">总估算成本</span>
              <button v-if="usage.cost.kind === 'amount'" type="button" :aria-label="costHint" :title="costHint" class="cost-toggle num" @click="toggleCostCurrency">{{ cost }}</button>
              <span v-else class="hint">{{ cost }}</span>
            </div>
          </div>
          <ul v-if="usage.agents.length" class="legend" aria-label="来源用量图例">
            <li v-for="a in usage.agents" :key="a.id">
              <i class="dot" :style="{ background: a.color }" aria-hidden="true" />
              <div><span class="agent-name">{{ a.name }}</span><span class="agent-detail num">{{ formatTokens(a.tokens) }} <span class="separator">·</span> {{ share(a.tokens) }}</span></div>
            </li>
          </ul>
          <p v-else class="hint">当前时间范围暂无用量记录</p>
        </div>
        <section v-if="usage.agents.length" ref="models" class="models" aria-labelledby="models-title">
          <div class="models-heading"><h3 id="models-title">模型用量</h3><span class="hint">占总用量</span></div>
          <div v-if="ranked.length" class="model-columns">
            <div v-for="(group, index) in groups" :key="index" class="model-column">
              <table v-if="group.length" class="model-table" :aria-label="columns === 2 ? `模型用量第 ${index + 1} 栏` : '模型用量'">
                <thead><tr><th scope="col">模型</th><th scope="col">Token</th><th scope="col">占比</th></tr></thead>
                <tbody><tr v-for="m in group" :key="m.key">
                  <td>
                    <n-popover trigger="click" placement="bottom-start">
                      <template #trigger>
                        <button type="button" class="model-name" :aria-label="`${m.agent}：${m.name}${m.noPrice ? '，暂无牌价' : ''}，查看详情`">
                          <AgentMark :id="m.agentId" />
                          <span class="model-label">{{ m.name }}</span>
                          <span v-if="m.noPrice" class="no-price" aria-hidden="true">*</span>
                        </button>
                      </template>
                      <div class="model-detail"><strong>{{ m.name }}</strong><span>来源：{{ m.agent }}</span><span v-if="m.noPrice">暂无牌价，未计入完整成本估算</span></div>
                    </n-popover>
                  </td>
                  <td class="num">{{ formatTokens(m.tokens) }}</td><td class="num">{{ share(m.tokens) }}</td>
                </tr></tbody>
              </table>
            </div>
          </div>
          <p v-else class="hint model-empty">暂无模型明细</p>
          <nav v-if="ranked.length" class="model-pager" aria-label="模型分页">
            <span class="num" aria-live="polite">{{ pageCount === 1 ? `共 ${ranked.length} 个模型` : pageLabel }}</span>
            <template v-if="pageCount > 1">
              <button type="button" aria-label="上一页模型" :disabled="page === 0" @click="page--"><ChevronLeft :size="16" /></button>
              <button type="button" aria-label="下一页模型" :disabled="page >= pageCount - 1" @click="page++"><ChevronRight :size="16" /></button>
            </template>
          </nav>
        </section>
      </div>
      <p v-if="!usage.error && partial" class="price-note hint">部分模型暂无报价</p>
    </div>
  </section>
</template>

<style scoped>
.usage-card { container: usage / inline-size; }
h2, h3 { margin: 0; font-size: var(--fs-card); font-weight: 600; }
.usage-heading { display: flex; align-items: center; flex-wrap: wrap; gap: var(--sp-2) var(--sp-4); margin-bottom: var(--sp-5); }
h3 { font-size: var(--fs-small); }
.hint { color: var(--dim); font-size: var(--fs-caption); }
.usage-layout { display: grid; grid-template-columns: clamp(380px, 32cqi, 440px) minmax(0, 1fr); gap: clamp(24px, 3cqi, 48px); align-items: center; }
.overview { display: grid; grid-template-columns: var(--ring-size) minmax(120px, 1fr); gap: var(--sp-5); align-items: center; }
.ring-col { display: flex; flex-direction: column; align-items: center; gap: var(--sp-4); min-width: 0; }
.donut { width: var(--ring-size); aspect-ratio: 1; border-radius: 50%; position: relative; }
.donut-core { position: absolute; inset: 14px; background: var(--surface); border-radius: 50%; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: var(--sp-2); }
.hero { font-size: var(--fs-hero); font-weight: 600; line-height: 1.1; letter-spacing: -0.03em; }
.hero small { margin-left: var(--sp-1); font-size: var(--fs-small); font-weight: 400; letter-spacing: 0; }
.cost { display: flex; align-items: baseline; justify-content: center; gap: var(--sp-2); flex-wrap: wrap; }
.cost-toggle { border: 0; padding: 0; background: transparent; color: var(--text); font: inherit; font-size: var(--fs-small); cursor: pointer; }
.legend { list-style: none; margin: 0; padding: 0 0 var(--sp-7); display: grid; gap: var(--sp-4); min-width: 0; }
.legend li { display: flex; gap: var(--sp-2); min-width: 0; align-items: flex-start; }
.dot { width: 7px; height: 7px; border-radius: 50%; flex: none; margin-top: 7px; }
.agent-name { display: block; font-size: var(--fs-body); font-weight: 500; overflow-wrap: anywhere; }
.agent-detail { display: block; margin-top: 2px; color: var(--dim); font-size: var(--fs-caption); white-space: nowrap; }
.separator { padding-inline: 2px; color: var(--faint); }
.many-agents { grid-template-columns: minmax(0, 1fr); gap: var(--sp-4); }
.many-agents .legend { grid-template-columns: repeat(2, minmax(0, 1fr)); padding-bottom: 0; gap: var(--sp-3) var(--sp-5); }
.models { min-width: 0; }
.models-heading { display: flex; justify-content: space-between; align-items: center; gap: var(--sp-3); margin-bottom: var(--sp-2); }
.model-columns { display: grid; grid-template-columns: repeat(var(--model-columns), minmax(0, 1fr)); gap: var(--sp-6); min-height: calc(28px + var(--model-rows) * 44px); }
.model-table { width: 100%; table-layout: fixed; border-collapse: collapse; font-size: var(--fs-small); }
.model-table th { height: 28px; font-weight: 400; color: var(--dim); text-align: left; }
.model-table td { height: 44px; padding: 0; vertical-align: middle; }
.model-table tbody tr { position: relative; }
.model-table tbody tr:has(.model-name:focus-visible) { background: var(--wash); outline: 2px solid var(--accent-solid); outline-offset: -2px; }
@media (hover: hover) { .model-table tbody tr:hover { background: var(--wash); } }
.model-table th:nth-child(2), .model-table td:nth-child(2) { width: 84px; text-align: right; white-space: nowrap; }
.model-table th:last-child, .model-table td:last-child { width: 48px; text-align: right; white-space: nowrap; color: var(--dim); }
.model-table td:nth-child(2) { font-size: var(--fs-body); font-weight: 500; }
.model-name { display: flex; align-items: center; gap: var(--sp-2); width: 100%; border: 0; padding: 0 var(--sp-2) 0 0; background: transparent; text-align: left; color: var(--text); font: inherit; cursor: pointer; }
.model-name { min-height: 40px; font-size: var(--fs-body); border-radius: var(--r-in); }
/* 扩展原生按钮的命中区域到整行，数字区域同样支持点击，键盘仍只有一个焦点。 */
.model-name::after { content: ''; position: absolute; inset: 0; }
.model-name:focus-visible { outline: none; }
.model-name:hover .model-label, .model-name:focus-visible .model-label { color: var(--accent-solid); }
.model-label { min-width: 0; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow: hidden; overflow-wrap: anywhere; line-height: 18px; }
.no-price { color: var(--danger); flex: none; }
.model-detail { display: flex; flex-direction: column; gap: var(--sp-2); max-width: min(320px, 75vw); overflow-wrap: anywhere; font-size: var(--fs-small); }
.model-pager { display: flex; justify-content: flex-end; align-items: center; gap: var(--sp-2); min-height: 28px; margin-top: var(--sp-3); font-size: var(--fs-caption); color: var(--dim); }
.model-pager button { width: 28px; height: 28px; display: grid; place-items: center; border: 0; border-radius: var(--r-in); background: transparent; color: var(--text); cursor: pointer; }
.model-pager button:hover:not(:disabled) { background: var(--wash); }
.model-pager button:disabled { color: var(--faint); opacity: 0.5; cursor: default; }
.price-note { margin: var(--sp-3) 0 0; text-align: right; }
.delta { display: inline-flex; align-items: center; gap: var(--sp-1); font-size: var(--fs-caption); }
.delta.up { color: var(--warn); }
.delta.down { color: var(--ok); }
.delta.flat { color: var(--dim); }
.usage-error { margin: 0; padding: var(--sp-3); background: var(--error-soft); color: var(--error-fg); border-radius: var(--r-in); }
.is-empty { grid-template-columns: 1fr; }
.is-empty .overview { grid-template-columns: var(--ring-size) minmax(0, 1fr); }
@container usage (max-width: 860px) {
  .usage-layout { grid-template-columns: minmax(0, 1fr); gap: var(--sp-6); }
  .overview { grid-template-columns: var(--ring-size) minmax(0, 240px); justify-content: center; }
  .many-agents { grid-template-columns: var(--ring-size) minmax(0, 1fr); }
  .models { border-top: 1px solid var(--stroke); padding-top: var(--sp-4); }
}
@container usage (max-width: 450px) {
  .overview, .many-agents, .is-empty .overview { grid-template-columns: minmax(0, 1fr); }
  .legend, .many-agents .legend { grid-template-columns: repeat(auto-fit, minmax(116px, 1fr)); padding: 0; gap: var(--sp-4); }
  .donut { width: 180px; }
  .model-table th:nth-child(2), .model-table td:nth-child(2) { width: 70px; }
  .model-table th:last-child, .model-table td:last-child { width: 42px; }
}
@media (pointer: coarse) { .model-pager button { width: 44px; height: 44px; } }
</style>
