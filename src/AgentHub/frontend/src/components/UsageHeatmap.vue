<script setup lang="ts">
import { computed, ref } from 'vue'
import { formatTokens, type UsageDay } from '../usageView'

const WEEKS = 26
const WD = ['一', '二', '三', '四', '五', '六', '日'] as const

const props = defineProps<{ days: UsageDay[] }>()

type Cell = { date: string; tokens: number; future: boolean; today: boolean }

const hover = ref<Cell | null>(null)

function startOfDay(d: Date): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate())
}

function mondayOf(d: Date): Date {
  const x = startOfDay(d)
  x.setDate(x.getDate() - ((x.getDay() + 6) % 7))
  return x
}

function ymd(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

function addDays(d: Date, n: number): Date {
  const x = startOfDay(d)
  x.setDate(x.getDate() + n)
  return x
}

const board = computed(() => {
  const map = new Map(props.days.map((d) => [d.date, d.tokens]))
  const today = startOfDay(new Date())
  const start = mondayOf(addDays(today, -(WEEKS - 1) * 7))
  const cells: Cell[] = []
  for (let w = 0; w < WEEKS; w++) {
    for (let r = 0; r < 7; r++) {
      const d = addDays(start, w * 7 + r)
      const date = ymd(d)
      const future = d.getTime() > today.getTime()
      cells.push({
        date,
        tokens: future ? 0 : map.get(date) ?? 0,
        future,
        today: date === ymd(today),
      })
    }
  }

  const vals = cells.filter((c) => !c.future && c.tokens > 0).map((c) => c.tokens).sort((a, b) => a - b)
  const cut = (p: number) => {
    if (!vals.length) return 0
    return vals[Math.min(vals.length - 1, Math.floor(vals.length * p))]
  }
  const t1 = cut(0.25)
  const t2 = cut(0.5)
  const t3 = cut(0.75)
  const levelOf = (t: number) => {
    if (t <= 0) return 0
    if (t3 > 0 && t >= t3) return 4
    if (t2 > 0 && t >= t2) return 3
    if (t1 > 0 && t >= t1) return 2
    return 1
  }

  const months: string[] = Array.from({ length: WEEKS }, () => '')
  for (let w = 0; w < WEEKS; w++) {
    for (let r = 0; r < 7; r++) {
      const d = addDays(start, w * 7 + r)
      if (d.getDate() !== 1) continue
      months[w] = `${d.getMonth() + 1}月`
      break
    }
  }

  const monthMarks = months
    .map((label, week) => (label ? { week, label } : null))
    .filter((x): x is { week: number; label: string } => !!x)

  return { cells, levelOf, months, monthMarks }
})

function tip(c: Cell): string {
  const [y, m, d] = c.date.split('-')
  const when = `${Number(y)}/${Number(m)}/${Number(d)}`
  return c.tokens > 0 ? `${when} · ${formatTokens(c.tokens)}` : `${when} · 无用量`
}
</script>

<template>
  <div class="heat" role="img" aria-label="近半年每日用量" @mouseleave="hover = null">
    <div class="heat-scroll">
      <div class="heat-board">
        <div class="heat-months" aria-hidden="true">
          <span
            v-for="mo in board.monthMarks"
            :key="mo.week"
            class="heat-mo"
            :style="{ gridColumn: mo.week + 1 }"
          >{{ mo.label }}</span>
        </div>
        <div class="heat-wd" aria-hidden="true">
          <span v-for="(w, i) in WD" :key="w">{{ i % 2 === 0 ? w : '' }}</span>
        </div>
        <div class="heat-grid">
          <span
            v-for="c in board.cells"
            :key="c.date"
            class="heat-cell"
            :class="{ today: c.today, future: c.future }"
            :data-lv="c.future ? 0 : board.levelOf(c.tokens)"
            :aria-label="c.future ? undefined : tip(c)"
            @mouseenter="hover = c.future ? null : c"
          />
        </div>
      </div>
    </div>
    <span v-if="hover" class="heat-tip num">{{ tip(hover) }}</span>
  </div>
</template>

<style scoped>
.heat {
  --heat-n: 26;
  --heat-gap: 3px;
  --heat-label: 14px;
  --heat-gutter: 6px;
  --heat-cell: 11px;
  display: grid;
  grid-template-columns: minmax(0, max-content) minmax(0, 1fr);
  grid-template-rows: 1fr;
  align-items: end;
  column-gap: var(--sp-4);
  width: 100%;
  min-width: 0;
}
.heat-scroll {
  grid-column: 1;
  grid-row: 1;
  min-width: 0;
  overflow-x: auto;
  overflow-y: hidden;
  scrollbar-width: thin;
}
.heat-board {
  display: grid;
  grid-template-columns: var(--heat-label) max-content;
  grid-template-rows: 18px auto;
  column-gap: var(--heat-gutter);
  row-gap: 4px;
  width: max-content;
  /* 末列月份标签（如 9月）会溢出网格右缘，预留空间避免触发横向滚动 */
  padding-right: 28px;
}
.heat-months,
.heat-grid {
  display: grid;
  grid-template-columns: repeat(var(--heat-n), var(--heat-cell));
  column-gap: var(--heat-gap);
}
.heat-months {
  grid-column: 2;
  grid-row: 1;
  align-items: end;
  overflow: visible;
}
.heat-mo {
  justify-self: start;
  width: max-content;
  min-width: 0;
  overflow: visible;
  font-size: var(--fs-caption);
  color: var(--faint);
  line-height: 1;
  white-space: nowrap;
}
.heat-wd {
  grid-column: 1;
  grid-row: 2;
  display: grid;
  grid-template-rows: repeat(7, var(--heat-cell));
  row-gap: var(--heat-gap);
  align-items: center;
  font-size: 10px;
  line-height: 1;
  color: var(--faint);
}
.heat-grid {
  grid-column: 2;
  grid-row: 2;
  grid-auto-flow: column;
  grid-template-rows: repeat(7, var(--heat-cell));
  row-gap: var(--heat-gap);
}
.heat-cell {
  display: block;
  width: var(--heat-cell);
  height: var(--heat-cell);
  flex: none;
  border-radius: 2px;
  background: var(--wash);
}
.heat-cell[data-lv='1'] { background: color-mix(in srgb, var(--accent-solid) 28%, var(--surface)); }
.heat-cell[data-lv='2'] { background: color-mix(in srgb, var(--accent-solid) 48%, var(--surface)); }
.heat-cell[data-lv='3'] { background: color-mix(in srgb, var(--accent-solid) 72%, var(--surface)); }
.heat-cell[data-lv='4'] { background: var(--accent-solid); }
.heat-cell.today {
  box-shadow: inset 0 0 0 1px var(--accent-line);
}
.heat-cell.future {
  opacity: 0.28;
}
.heat-tip {
  grid-column: 2;
  grid-row: 1;
  align-self: end;
  justify-self: end;
  padding-left: 20px;
  font-size: var(--fs-caption);
  color: var(--text);
  white-space: nowrap;
}
</style>
