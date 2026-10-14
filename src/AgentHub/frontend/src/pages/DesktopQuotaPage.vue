<script setup lang="ts">
import { ref } from 'vue'
import { RefreshCw, X } from 'lucide-vue-next'
import QuotaDial from '../components/quota/QuotaDial.vue'
import { formatResetCountdown, MAX_PINNED_WINDOWS } from '../quotaWindows'
import { useDesktopQuota } from '../useDesktopQuota'

const { expanded, rows, shown, shownIds, unavailable, pinFull, stale, staleTitle, ready, now, appearance, hostError, refreshing, startDrag, setAppearance, refresh, setExpanded, closeWidget, onToggle, isPinned } = useDesktopQuota()
const settingsTab = ref<'quota' | 'appearance'>('quota')
</script>

<template>
  <main class="dq" :style="{ width: (expanded ? Math.max(360, appearance.size) : appearance.size) + 'px', '--dq-size': appearance.size + 'px' }" @keydown.esc="expanded ? setExpanded(false) : closeWidget()">
    <QuotaDial :rows="shown" :slot-ids="shownIds" :appearance="appearance" :stale="stale" :ready="ready" :now="now" :expanded="expanded" :refreshing="refreshing" @toggle="setExpanded(!expanded)" @refresh="refresh(true)" @drag="startDrag" />
    <section v-if="expanded" id="quota-options" class="dq-options" aria-label="桌面额度设置">
      <header class="dq-head">
        <strong>桌面额度</strong>
        <span v-if="hostError" class="dq-stale" role="status" :title="hostError">操作失败，请重试</span>
        <span v-if="stale" class="dq-stale" :title="staleTitle">未更新</span>
        <span class="dq-spacer" />
        <button type="button" aria-label="刷新额度" title="刷新额度" :aria-busy="refreshing" @click="refresh(true)"><RefreshCw :size="15" :class="{ spin: refreshing }" /></button>
        <button type="button" aria-label="收起设置" title="收起设置" @click="setExpanded(false)"><X :size="16" /></button>
      </header>
      <div class="dq-tabs" aria-label="额度设置分类">
        <button type="button" :aria-pressed="settingsTab === 'quota'" @click="settingsTab = 'quota'">额度</button>
        <button type="button" :aria-pressed="settingsTab === 'appearance'" @click="settingsTab = 'appearance'">外观</button>
      </div>
      <template v-if="settingsTab === 'quota'">
      <div class="dq-hint">
        <span>选择两侧显示的额度</span>
        <span>{{ shownIds.length }} / {{ MAX_PINNED_WINDOWS }}</span>
      </div>
      <div class="dq-list">
        <label v-for="missing in unavailable" :key="missing.id" class="dq-opt" :title="missing.id">
          <input type="checkbox" checked @change="onToggle(missing.id)" />
          <span class="dq-detail"><span class="dq-name">{{ missing.name }}</span><span class="dq-when">暂不可用，保留选择等待恢复</span></span>
          <b>—</b>
        </label>
        <p v-if="!ready" class="dq-empty">正在读取额度…</p>
        <p v-else-if="!rows.length" class="dq-empty">{{ stale ? '额度暂时读不到，可稍后刷新' : '暂无可查询的额度窗口' }}</p>
        <label v-for="row in rows" :key="row.id" class="dq-opt" :class="{ off: pinFull && !isPinned(row.id) }">
          <input type="checkbox" :checked="isPinned(row.id)" :disabled="pinFull && !isPinned(row.id)" @change="onToggle(row.id)" />
          <span class="dq-detail">
            <span class="dq-name" :title="row.name">{{ row.name }}</span>
            <span class="dq-when">{{ formatResetCountdown(row.resetMs, now, row.period) }}</span>
          </span>
          <span class="dq-side" :class="shownIds.indexOf(row.id) === 0 ? 'left' : 'right'">{{ isPinned(row.id) ? (shownIds.indexOf(row.id) === 0 ? '左' : '右') : '' }}</span>
          <b class="num">{{ Math.round(row.remain) }}%</b>
        </label>
      </div>
      </template>
      <div v-else class="dq-appearance">
        <label class="dq-range">
          <span>显示尺寸 <b>{{ appearance.size }} px</b></span>
          <input aria-label="显示尺寸" type="range" min="260" max="460" step="10" :value="appearance.size" @input="setAppearance({ size: Number(($event.target as HTMLInputElement).value) })" />
        </label>
        <label class="dq-range">
          <span>弧线柔光 <b>{{ Math.round(appearance.glow / .6 * 100) }}%</b></span>
          <input aria-label="弧线柔光" type="range" min="0" max="0.6" step="0.02" :value="appearance.glow" @input="setAppearance({ glow: Number(($event.target as HTMLInputElement).value) })" />
        </label>
        <label class="dq-mode">弧线模式
          <select aria-label="弧线模式" :value="appearance.arcMode" @change="setAppearance({ arcMode: ($event.target as HTMLSelectElement).value as 'quota' | 'full' })">
            <option value="quota">按剩余额度</option>
            <option value="full">完整装饰弧</option>
          </select>
        </label>
        <button type="button" class="dq-reset" @click="setAppearance({ size: 360, glow: .12, arcMode: 'quota' })">恢复默认</button>
      </div>
      <footer class="dq-foot">
        <span v-if="settingsTab === 'quota'">{{ pinFull ? '取消一项后可更换' : '按选择顺序从左到右显示' }}</span>
        <button type="button" class="dq-close" @click="closeWidget">关闭桌面额度</button>
      </footer>
    </section>
  </main>
</template>

<style>
html.desktop-quota, html.desktop-quota body, html.desktop-quota #app { background: transparent !important; }
</style>

<style scoped>
.dq { max-width: 100%; height: 100%; display: flex; flex-direction: column; color: var(--dial-text); background: transparent; user-select: none; }
.dq-options { position: relative; z-index: 2; height: 250px; min-height: 0; flex: 0 0 250px; margin: calc(var(--dq-size) * -0.10) 12px 12px; background: var(--dial-face-2); color: var(--dial-text); border: 1px solid var(--dial-hardware-6); border-radius: 16px; overflow: hidden; display: flex; flex-direction: column; color-scheme: dark; app-region: no-drag; -webkit-app-region: no-drag; }
.dq-options, .dq-options * { app-region: no-drag; -webkit-app-region: no-drag; }
.dq-head { display: flex; align-items: center; gap: 4px; padding: 8px 8px 5px 12px; flex: 0 0 auto; }
.dq-head strong { font-size: 13px; font-weight: 500; }
.dq-spacer { flex: 1; }
.dq-stale { color: var(--warn); font-size: 11px; margin-left: 4px; }
.dq-head button { width: 28px; height: 28px; display: inline-flex; align-items: center; justify-content: center; border: 0; border-radius: 6px; background: transparent; color: var(--dial-muted); cursor: pointer; }
.dq-head button:hover { background: var(--dial-hardware-7); color: var(--dial-text); }
.dq-head button :deep(.spin) { animation: dq-spin 700ms linear infinite; transform-origin: center; }
.dq-hint { display: flex; justify-content: space-between; gap: 8px; padding: 0 12px 7px; color: var(--dial-muted); font-size: 12px; }
.dq-list { flex: 1; min-height: 0; overflow: auto; padding: 0 6px; }
.dq-opt { display: flex; align-items: center; gap: 8px; min-height: 44px; padding: 5px 6px; border-radius: 7px; cursor: pointer; }
.dq-opt:hover { background: var(--dial-hardware-7); }
.dq-opt.off { opacity: .5; cursor: default; }
.dq-opt input { flex: 0 0 auto; margin: 0; accent-color: var(--dial-left-value); }
.dq-detail { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 1px; }
.dq-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; font-weight: 500; }
.dq-when { color: var(--dial-muted); font-size: 11px; }
.dq-opt b { font-size: 13px; font-weight: 500; min-width: 34px; text-align: right; }
.dq-side { width: 12px; font-size: 11px; }
.dq-side.left { color: var(--dial-left-value); }
.dq-side.right { color: var(--dial-right-value); }
.dq-empty { margin: 16px 6px; color: var(--dial-muted); font-size: 12px; }
.dq-foot { flex: 0 0 auto; display: flex; align-items: center; justify-content: space-between; gap: 8px; color: var(--dial-muted); font-size: 11px; padding: 6px 12px 8px; border-top: 1px solid var(--dial-hardware-6); }
.dq-close { margin-left: auto; background: transparent; color: var(--dial-muted); border: 0; font: inherit; font-size: 12px; padding: 0; cursor: pointer; }
.dq-close:hover { color: var(--dial-text); }
.dq-tabs { display: flex; gap: 14px; padding: 0 12px 7px; }
.dq-tabs button { border: 0; padding: 2px 0; background: transparent; color: var(--dial-muted); font: inherit; font-size: 12px; cursor: pointer; }
.dq-tabs button[aria-pressed='true'] { color: var(--dial-text); box-shadow: 0 1px 0 var(--dial-hardware-9); }
.dq-appearance { flex: 1; min-height: 0; overflow: auto; padding: 0 12px 10px; display: flex; flex-direction: column; gap: 9px; font-size: 12px; }
.dq-range { display: flex; flex-direction: column; gap: 3px; }
.dq-range span { display: flex; justify-content: space-between; }
.dq-range b { font-weight: 400; color: var(--dial-muted); }
.dq-range input { margin: 0; width: 100%; accent-color: var(--dial-left-value); }
.dq-mode { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.dq-mode select { color: var(--dial-text); background: var(--dial-face); border: 1px solid var(--dial-hardware-6); border-radius: 5px; padding: 3px 5px; }
.dq-reset { align-self: flex-start; background: transparent; color: var(--dial-muted); border: 0; font: inherit; padding: 0; cursor: pointer; }
.dq-reset:hover { color: var(--dial-text); }
.dq-options button:focus-visible, .dq-options input:focus-visible, .dq-options select:focus-visible { outline: 2px solid var(--dial-focus); outline-offset: 2px; }
@keyframes dq-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .dq-head button :deep(.spin) { animation: none; } }
</style>
