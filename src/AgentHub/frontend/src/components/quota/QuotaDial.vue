<script setup lang="ts">
import { computed } from 'vue'
import QuotaDialDefs from './QuotaDialDefs.vue'
import QuotaDialCase from './QuotaDialCase.vue'
import { quotaDialBrand, quotaDialLabel, quotaDialReset, type QuotaDialAppearance } from '../../quotaDial'
import { formatResetExact, type QuotaWindowRow } from '../../quotaWindows'

const props = defineProps<{ rows: (QuotaWindowRow | undefined)[]; slotIds: string[]; appearance: QuotaDialAppearance; stale: boolean; ready: boolean; now: number; expanded: boolean }>()
defineEmits<{ toggle: []; drag: [event: MouseEvent] }>()
const available = computed(() => props.rows.filter((row): row is QuotaWindowRow => !!row))
const brand = computed(() => quotaDialBrand(available.value))
const slots = computed(() => [props.rows[0], props.rows[1]])
const mixed = computed(() => brand.value === 'AgentHub')
const footer = computed(() => {
  if (!props.ready) return { label: '正在读取', value: '额度信息' }
  if (props.stale) return { label: '未更新', value: available.value.length ? '上次成功额度' : '暂时无法读取' }
  if (!available.value.length) return props.slotIds.length ? { label: '额度暂不可用', value: '等待刷新' } : { label: '暂无固定额度', value: '点击旋钮选择' }
  return quotaDialReset(available.value, props.now)
})
const accessible = computed(() => available.value.length
  ? available.value.map(row => `${row.name}剩余${Math.round(row.remain)}%，${formatResetExact(row.resetMs, props.now, row.period)}`).join('；') + (props.stale ? '；刷新失败，显示上次数据' : '')
  : (props.ready ? (props.slotIds.length ? '固定额度暂不可用，等待刷新；可点击旋钮更换' : '暂无固定额度，请点击旋钮选择') : '正在读取额度'))

function label(index: number): string {
  const row = slots.value[index]
  if (!row) return props.slotIds[index] ? '暂不可用' : '未选择'
  const duplicate = available.value.length === 2 && quotaDialLabel(available.value[0], mixed.value) === quotaDialLabel(available.value[1], mixed.value)
  return quotaDialLabel(row, mixed.value, duplicate)
}

function arcStyle(index: number) {
  const row = slots.value[index]
  return {
    strokeDashoffset: String(100 - (row ? props.appearance.arcMode === 'full' ? 100 : row.remain : 0)),
    visibility: !row || (props.appearance.arcMode === 'quota' && row.remain <= 0) ? 'hidden' as const : 'visible' as const,
  }
}
</script>

<template>
  <div class="quota-dial" :data-stale="stale || undefined" :style="{ width: appearance.size + 'px', height: appearance.size + 'px', flexBasis: appearance.size + 'px', '--dial-glow-opacity': appearance.glow }">
    <svg class="cr-dial" viewBox="0 0 440 440" role="img" :aria-label="accessible" @mousedown="$emit('drag', $event)">
      <QuotaDialDefs />
      <g>
        <QuotaDialCase />
          <g class="cr-channels" fill="none" stroke-linecap="round" aria-hidden="true">
            <use href="#cr-left-path" stroke="var(--dial-tube)" stroke-opacity=".14" stroke-width="19" transform="translate(0 .6)"/>
            <use href="#cr-right-path" stroke="var(--dial-tube-1)" stroke-opacity=".14" stroke-width="19" transform="translate(0 .6)"/>
            <use href="#cr-left-path" stroke="var(--dial-tube-2)" stroke-width="17"/>
            <use href="#cr-right-path" stroke="var(--dial-tube-3)" stroke-width="17"/>
            <use href="#cr-left-path" stroke="var(--dial-tube-4)" stroke-width="11"/>
            <use href="#cr-right-path" stroke="var(--dial-tube-5)" stroke-width="11"/>
          </g>
          <g fill="none" stroke-linecap="round" aria-hidden="true">
            <path class="cr-progress cr-l cr-halo" :style="arcStyle(0)" d="M 159.85 86.96 A 132 132 0 0 0 139 325.32" pathLength="100" stroke="var(--dial-tube-6)" stroke-width="13" filter="url(#cr-glow)"/>
            <path class="cr-progress cr-r cr-halo" :style="arcStyle(1)" d="M 250.15 86.96 A 132 132 0 0 1 271 325.32" pathLength="100" stroke="var(--dial-tube-7)" stroke-width="13" filter="url(#cr-glow)"/>
            <path class="cr-progress cr-l" :style="arcStyle(0)" d="M 159.85 86.96 A 132 132 0 0 0 139 325.32" pathLength="100" stroke="url(#cr-green-tube)" stroke-width="9.2"/>
            <path class="cr-progress cr-r" :style="arcStyle(1)" d="M 250.15 86.96 A 132 132 0 0 1 271 325.32" pathLength="100" stroke="url(#cr-blue-tube)" stroke-width="9.2"/>
            <path class="cr-progress cr-l" :style="arcStyle(0)" d="M 159.85 86.96 A 132 132 0 0 0 139 325.32" pathLength="100" stroke="var(--dial-tube-8)" stroke-width=".8" stroke-opacity=".19" transform="translate(-.9 -.9)"/>
            <path class="cr-progress cr-r" :style="arcStyle(1)" d="M 250.15 86.96 A 132 132 0 0 1 271 325.32" pathLength="100" stroke="var(--dial-tube-9)" stroke-width=".8" stroke-opacity=".22" transform="translate(-.9 -.9)"/>
          </g>

        <g class="cr-lettering" filter="url(#cr-text-shadow)">
          <text x="185" y="153" class="cr-brand" :class="{ 'cr-brand-wide': brand.length > 6 }" text-anchor="middle">{{ brand }}</text>
          <rect x="225" y="138" width="44" height="19" rx="2.2" fill="var(--dial-badge)" fill-opacity=".08" stroke="var(--dial-badge-rim)" stroke-opacity=".14" stroke-width=".7" />
          <text x="247" y="152" class="cr-plan" :class="{ 'cr-stale': stale }" text-anchor="middle">{{ stale ? '旧数据' : '剩余' }}</text>
          <path d="M 205 171 V 247" stroke="url(#cr-divider)" stroke-width="1" />
          <path d="M 206 172 V 247" stroke="var(--dial-divider-shadow)" stroke-opacity=".22" stroke-width="1" />
          <g v-for="(row, i) in slots" :key="i">
            <title v-if="row">{{ row.name }} · {{ formatResetExact(row.resetMs, now, row.period) }}</title>
            <text :x="i ? 262 : 151" y="213" class="cr-value" :class="[i ? 'cr-week-color' : 'cr-hour-color', { 'cr-three-digit': row && Math.round(row.remain) >= 100 }]" text-anchor="middle">
              <tspan>{{ row ? Math.round(row.remain) : '—' }}</tspan><tspan v-if="row" class="cr-percent" dx="2">%</tspan>
            </text>
            <text :x="i ? 262 : 151" y="241" class="cr-label" :class="{ 'cr-label-long': label(i).length > 7 }" text-anchor="middle">{{ label(i) }}</text>
          </g>
          <text x="205" y="284" class="cr-safety" text-anchor="middle">{{ footer.label }}</text>
          <text x="205" y="315" class="cr-time" :class="{ 'cr-time-long': footer.value.length > 6 }" text-anchor="middle">{{ footer.value }}</text>
        </g>
      </g>
    </svg>
    <button class="cr-crown-hit" type="button" :aria-label="expanded ? '收起额度选择' : '选择额度'" :aria-expanded="expanded" aria-controls="quota-options" :title="expanded ? '收起额度选择' : '选择额度、刷新或关闭'" @click="$emit('toggle')" />
  </div>
</template>

<style scoped>
.quota-dial { max-width: 100%; flex: 0 0 auto; align-self: center; position: relative; user-select: none; }
.cr-dial { display: block; width: 100%; height: auto; overflow: visible; cursor: grab; }
.cr-dial, .cr-dial * { app-region: drag; -webkit-app-region: drag; }
.cr-lettering { font-family: var(--font); font-weight: 400; }
.cr-brand { font-size: 23px; font-weight: 400; fill: var(--dial-text); }
.cr-brand-wide { font-size: 19px; }
.cr-plan { font-size: 13px; fill: var(--dial-plan); }
.cr-stale { fill: var(--warn); }
.cr-value { font-size: 53px; font-weight: 400; letter-spacing: -1.3px; font-variant-numeric: tabular-nums; }
.cr-three-digit { font-size: 45px; }
.cr-percent { font-size: 25px; letter-spacing: -.5px; }
.cr-hour-color { fill: var(--dial-left-value); }
.cr-week-color { fill: var(--dial-right-value); }
.cr-label { font-size: 17px; fill: var(--dial-label); }
.cr-label-long { font-size: 13px; }
.cr-safety { font-size: 16px; fill: var(--dial-muted); }
.cr-time { font-size: 29px; font-weight: 350; fill: var(--dial-time); font-variant-numeric: tabular-nums; letter-spacing: .1px; }
.cr-time-long { font-size: 21px; }
.cr-halo { opacity: var(--dial-glow-opacity); }
.cr-progress { stroke-dasharray: 100 100; transition: stroke-dashoffset 650ms cubic-bezier(.2,.65,.3,1); }
.cr-crown-hit { position: absolute; left: 84%; top: 40%; width: 10%; height: 17%; background: transparent; border: 0; padding: 0; border-radius: 8px; cursor: pointer; app-region: no-drag; -webkit-app-region: no-drag; }
.quota-dial:has(.cr-crown-hit:hover) :deep(.cr-crown-art) { filter: brightness(1.12); }
.quota-dial:has(.cr-crown-hit:active) :deep(.cr-crown-art) { transform: translateX(-1px); }
.cr-crown-hit:focus-visible { outline: 2px solid var(--dial-focus); outline-offset: 2px; box-shadow: none; }
@media (prefers-reduced-motion: reduce) { .cr-progress { transition: none; } }
</style>
