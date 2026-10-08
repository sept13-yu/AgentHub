<script setup lang="ts">
import { computed, h } from 'vue'
import { NButton, NDropdown, NIcon, type DropdownOption } from 'naive-ui'
import { MoreHorizontal } from 'lucide-vue-next'
import type { ConfigRow } from '../../codex/types'

const props = defineProps<{
  kind: 'official' | 'relay'
  rows: ConfigRow[]
  busy: boolean
  writeDisabled: boolean
}>()
const emit = defineEmits<{ apply: [row: ConfigRow]; delete: [row: ConfigRow]; edit: [connectionId: string] }>()
const official = computed(() => props.kind === 'official')
const title = computed(() => official.value ? '官方账号' : '中转连接')
const disabled = computed(() => props.busy || props.writeDisabled)
function moreOptions(row: ConfigRow): DropdownOption[] {
  const options: DropdownOption[] = []
  if (!official.value && row.connectionId) options.push({ key: 'edit', label: '编辑', disabled: disabled.value })
  if (row.canDelete) {
    const label = official.value ? '删除登录副本' : '删除中转连接'
    options.push({ key: 'delete', label: () => h('span', { class: 'codex-delete-label' }, label), disabled: disabled.value })
  }
  return options
}
function selectMore(key: string | number, row: ConfigRow) {
  if (key === 'edit') edit(row)
  if (key === 'delete' && row.canDelete && !disabled.value) emit('delete', row)
}
function edit(row: ConfigRow) {
  if (!official.value && row.connectionId && !disabled.value) emit('edit', row.connectionId)
}
</script>

<template>
  <section class="card connection-list" :aria-labelledby="`codex-${kind}-title`">
    <div class="card-head list-head">
      <div class="heading">
        <h2 :id="`codex-${kind}-title`">{{ title }}<span class="count num">{{ rows.length }}</span></h2>
      </div>
    </div>
    <ul v-if="rows.length" class="rows">
      <li v-for="row in rows" :key="row.key" class="connection-row" :class="{ current: row.isCurrent }">
        <div class="row-content">
          <div class="row-title"><strong>{{ row.title }}</strong><span v-if="row.isCurrent" class="current-badge">当前配置</span></div>
          <p class="subtitle" :class="{ address: !official }">{{ row.subtitle }}</p>
          <p v-if="!official" class="key-state">{{ row.keySet ? '密钥已配置' : '密钥未配置' }}</p>
          <p v-if="row.savedUnapplied" class="saved-state">修改待应用</p>
        </div>
        <div class="row-actions">
          <NButton v-if="!row.isCurrent || (!official && row.savedUnapplied)" :disabled="disabled" @click="$emit('apply', row)">
            {{ row.isCurrent ? '应用修改' : '切换' }}
          </NButton>
          <NDropdown v-if="row.canDelete || (!official && row.connectionId)" trigger="click" :options="moreOptions(row)" @select="(key) => selectMore(key, row)">
            <NButton quaternary :disabled="disabled" :aria-label="`${row.title}的更多操作`">
              <template #icon><NIcon :component="MoreHorizontal" aria-hidden="true" /></template>
            </NButton>
          </NDropdown>
        </div>
      </li>
    </ul>
    <div v-else class="empty">
      <p>{{ official ? '暂无官方账号' : '暂无中转连接' }}</p>
    </div>
  </section>
</template>

<style scoped>
h2, p { margin: 0; }
h2 { display: flex; align-items: center; gap: var(--sp-2); font-size: var(--fs-card); font-weight: 600; }
.list-head { justify-content: space-between; gap: var(--sp-4); align-items: center; }
.heading { min-width: 0; }
.count { color: var(--dim); font-size: var(--fs-small); font-weight: 400; }
.rows { list-style: none; padding: 0; margin: 0; }
.connection-row { display: flex; align-items: center; gap: var(--sp-3); padding: var(--sp-3) var(--sp-5); }
.connection-row + .connection-row { border-top: 1px solid var(--stroke); }
.current { background: var(--accent-soft); }
.row-content { min-width: 0; flex: 1; }
.row-title { display: flex; align-items: center; flex-wrap: wrap; gap: var(--sp-2); }
.row-title strong { font-weight: 600; overflow-wrap: anywhere; }
.current-badge { color: var(--accent-solid); font-size: var(--fs-caption); flex-shrink: 0; }
.subtitle { margin-top: var(--sp-1); color: var(--dim); font-size: var(--fs-small); overflow-wrap: anywhere; }
.address { font-family: var(--mono); }
.key-state { color: var(--dim); font-size: var(--fs-small); margin-top: var(--sp-1); }
.saved-state { color: var(--warn); font-size: var(--fs-small); margin-top: var(--sp-1); }
.row-actions { display: flex; align-items: center; gap: var(--sp-2); flex-shrink: 0; }
.empty { padding: var(--sp-4) var(--sp-5); color: var(--dim); font-size: var(--fs-small); }
:global(.codex-delete-label) { color: var(--danger); }
@media (max-width: 700px) {
  .list-head { align-items: flex-start; flex-wrap: wrap; gap: var(--sp-3); }
  .connection-row { align-items: flex-start; flex-direction: column; gap: var(--sp-3); }
  .row-content { width: 100%; }
  .row-actions { flex-wrap: wrap; }
}
</style>
