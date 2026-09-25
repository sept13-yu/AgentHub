<script setup lang="ts">
import { Box, ChevronDown, ChevronRight } from 'lucide-vue-next'
import AgentMark from './AgentMark.vue'
import { splitTokens } from '../usageView'
import type { BreakdownRow } from '../usageBreakdown'

defineProps<{ rows: BreakdownRow[]; total: number; selected?: string | null; expandable?: boolean }>()
defineEmits<{ select: [key: string]; highlight: [key: string | null] }>()

function description(row: BreakdownRow, total: number) {
  const share = total > 0 ? row.tokens / total * 100 : 0
  return `${row.name} · ${row.tokens.toLocaleString('zh-CN')} Token · ${share > 0 && share < 1 ? '<1' : Math.round(share)}%${row.noPrice ? ' · 暂无报价' : ''}`
}
</script>

<template>
  <ul class="breakdown-list">
    <li v-for="row in rows" :key="row.key">
      <component :is="expandable ? 'button' : 'div'" :type="expandable ? 'button' : undefined"
        class="usage-row" :class="{ selected: selected === row.key, interactive: expandable }"
        :title="description(row, total)" :aria-expanded="expandable ? selected === row.key : undefined"
        :aria-controls="expandable && selected === row.key ? 'usage-detail' : undefined"
        :tabindex="expandable ? undefined : 0"
        @click="expandable && $emit('select', row.key)"
        @mouseenter="$emit('highlight', row.key)" @mouseleave="$emit('highlight', null)"
        @focus="$emit('highlight', row.key)" @blur="$emit('highlight', null)">
        <AgentMark v-if="row.agentId" :id="row.agentId" />
        <Box v-else class="model-icon" :size="16" aria-hidden="true" />
        <span class="row-name">{{ row.name }}<small v-if="row.noPrice && !expandable" class="price-note">暂无报价</small></span>
        <span class="row-value num">{{ splitTokens(row.tokens).val }}<small>{{ splitTokens(row.tokens).unit }}</small></span>
        <component :is="selected === row.key ? ChevronDown : ChevronRight" v-if="expandable" :size="14" class="chevron" aria-hidden="true" />
      </component>
    </li>
  </ul>
</template>

<style scoped>
.breakdown-list { list-style: none; padding: 0; margin: 0; min-width: 0; }
.usage-row { box-sizing: border-box; display: flex; align-items: center; width: 100%; min-height: 40px; gap: var(--sp-3); padding: var(--sp-2); border: 0; border-radius: var(--r-in); background: transparent; color: var(--text); font: inherit; text-align: left; }
.interactive { cursor: pointer; }
.usage-row.selected, .usage-row:hover { background: var(--wash); }
.usage-row:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: -2px; }
.row-name { min-width: 0; flex: 1; overflow-wrap: anywhere; font-size: var(--fs-body); line-height: 1.5; }
.row-value { white-space: nowrap; font-size: var(--fs-body); font-weight: 500; }
.row-value small { margin-left: var(--sp-1); color: var(--dim); font-size: var(--fs-caption); font-weight: 400; }
.chevron, .model-icon { color: var(--dim); flex: none; }
.price-note { display: block; color: var(--dim); font-size: var(--fs-caption); font-weight: 400; }
@media (pointer: coarse) { .usage-row { min-height: 44px; } }
</style>
