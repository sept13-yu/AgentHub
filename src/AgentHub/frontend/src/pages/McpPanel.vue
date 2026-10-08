<script setup lang="ts">
import { computed, inject, onMounted, onUnmounted, ref, type Ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { NButton, NCheckbox, NIcon, NInput, useMessage } from 'naive-ui'
import { Plus, RefreshCw } from 'lucide-vue-next'
import { WRITABLE } from '../api'
import { ahMenuKey, type AhMenuItem } from '../ahMenu'
import { usePageHotkeys } from '../hotkeys'
import AhConfirm from '../components/AhConfirm.vue'
import AgentMark from '../components/AgentMark.vue'
import McpItemCard from '../components/mcp/McpItemCard.vue'
import McpEditor from '../components/mcp/McpEditor.vue'
import { agentDisplayName, configFileName, isSystemItem, type McpItem } from '../mcp/types'
import { useMcpSnapshot } from '../mcp/useMcpSnapshot'
import { useMcpActions } from '../mcp/useMcpActions'
import { useMcpEditor } from '../mcp/useMcpEditor'

const message = useMessage()
const pageLoading = inject<Ref<boolean>>('page-loading')
const menu = inject(ahMenuKey)
const readonly = !WRITABLE

const snapshot = useMcpSnapshot(pageLoading, message)
const actions = useMcpActions(snapshot, message, readonly)
const editor = useMcpEditor(snapshot, actions, message, readonly)

const { data, loadError, detectedAdapters, adapterErrors } = snapshot
const { pendingAgentKeys, deleteShow, deleteItem, deleteTargets, deleteBusy, deleteError, deletePresentAgents } = actions
const {
  show: editorShow, creating, itemName, name: editorName, json: editorJson, targets: editorTargets,
  requiredAgents, targetOptions, loading: editorLoading, loaded: editorLoaded, saving: editorSaving,
  loadError: editorLoadError, saveError: editorSaveError, dirty: editorDirty, discardShow,
} = editor

const q = ref('')
const detailIds = ref(new Set<string>())

const filtered = computed(() => {
  const list = data.value?.items ?? []
  const needle = q.value.trim().toLowerCase()
  if (!needle) return list
  return list.filter((i) =>
    i.id.toLowerCase().includes(needle)
    || (i.alias || '').toLowerCase().includes(needle)
    || (i.note || '').toLowerCase().includes(needle)
    || (i.command || '').toLowerCase().includes(needle)
    || (i.url || '').toLowerCase().includes(needle))
})

// 无项目、搜索无匹配、读取失败三种空态分开，不把失败当空列表。
const emptyText = computed(() => {
  if (!data.value) return snapshot.loading.value ? '正在读取 MCP…' : ''
  if (filtered.value.length) return ''
  const needle = q.value.trim()
  if (needle) return `没有匹配「${needle}」的 MCP 项`
  return adapterErrors.value.length ? '暂无可显示的 MCP 项，请先处理上方读取错误' : '暂无 MCP 项'
})

function toggleDetail(id: string) {
  const set = new Set(detailIds.value)
  if (set.has(id)) set.delete(id)
  else set.add(id)
  detailIds.value = set
}

function onCardMenu(e: MouseEvent, item: McpItem) {
  if (!menu) return
  const busy = editorSaving.value || deleteBusy.value
  const items: AhMenuItem[] = [
    { key: 'edit', label: '编辑', disabled: readonly || busy, handler: () => editor.openEdit(item) },
    { key: 'sep', separator: true },
    {
      key: 'del',
      label: '移除',
      danger: true,
      disabled: readonly || busy || isSystemItem(item),
      handler: () => actions.askDelete(item),
    },
  ]
  menu.open(e, items)
}

async function refresh() {
  if (!(await snapshot.load())) return
  const n = data.value?.items.length ?? 0
  if (adapterErrors.value.length) message.warning(`已刷新，共 ${n} 项；${adapterErrors.value.length} 个配置读取失败`)
  else message.success(`已刷新，共 ${n} 项`)
}

// 离页/换标签统一闸门：移除、启停、添加等写请求在途时一律挡下（R02），
// 未提交的风险确认不算在途，可以取消后离开。
async function canLeave(): Promise<boolean> {
  if (actions.writePending.value) {
    message.info('正在移除或更新启用状态，请等待完成再离开')
    return false
  }
  return editor.confirmDiscard()
}

usePageHotkeys({
  refresh: () => { void refresh() },
})
onBeforeRouteLeave(() => canLeave())

// 整页重载闸门：动作在途由 actions 拦截，草稿/保存沿用编辑器检查；不涉及 WPF 进程退出。
function onBeforeUnload(event: BeforeUnloadEvent) {
  if (actions.blockBeforeUnload(event)) return
  editor.onBeforeUnload(event)
}

onMounted(() => {
  void snapshot.load()
  window.addEventListener('beforeunload', onBeforeUnload)
})
onUnmounted(() => {
  snapshot.invalidate()
  window.removeEventListener('beforeunload', onBeforeUnload)
  editor.resolveDiscard(false)
  actions.resolveRisk(false)
})

defineExpose({ canLeave })
</script>

<template>
  <teleport defer to="#chrome-actions">
    <n-button :disabled="readonly || editorSaving || deleteBusy" @click="editor.openCreate()">
      <template #icon><n-icon><Plus :size="16" :stroke-width="1.8" /></n-icon></template>
      新建
    </n-button>
    <n-button @click="refresh">
      <template #icon><n-icon><RefreshCw :size="16" :stroke-width="1.8" /></n-icon></template>
      刷新
    </n-button>
  </teleport>

  <div class="mcp">
    <div class="mcp-filter">
      <n-input v-model:value="q" class="mcp-search" placeholder="搜索 MCP…" clearable aria-label="搜索 MCP" />
    </div>

    <p v-if="readonly" class="mcp-banner">浏览器只读。编辑、保存和同步配置请到 AgentHub 桌面操作。</p>

    <p v-if="loadError" class="mcp-read-errors" role="alert">
      读取失败：{{ loadError }}。{{ data ? '保留当前列表，请重试。' : '请点击刷新重试。' }}
    </p>

    <div v-if="adapterErrors.length" class="mcp-read-errors" role="alert">
      <p v-for="a in adapterErrors" :key="a.agentId">
        {{ agentDisplayName(a) }}：{{ a.error }}。请检查 {{ a.configPath }}
      </p>
    </div>

    <div v-if="emptyText" class="mcp-empty">{{ emptyText }}</div>

    <div v-else class="mcp-grid">
      <McpItemCard
        v-for="item in filtered"
        :key="item.id"
        :item="item"
        :detail-open="detailIds.has(item.id)"
        :pending-keys="pendingAgentKeys"
        :delete-locked="deleteShow && deleteItem?.id === item.id"
        :action-busy="editorSaving || deleteBusy"
        :write-disabled="readonly"
        @toggle-detail="toggleDetail(item.id)"
        @edit="editor.openEdit(item)"
        @remove="actions.askDelete(item)"
        @toggle-agent="(agent, wantOn) => actions.toggleAgent(item, agent, wantOn)"
        @open-menu="($event: MouseEvent) => onCardMenu($event, item)"
      />
    </div>

    <details v-if="detectedAdapters.length" class="mcp-files">
      <summary>配置文件</summary>
      <div class="mcp-adapters">
        <button
          v-for="a in detectedAdapters"
          :key="a.agentId"
          type="button"
          class="mcp-adapter"
          :disabled="readonly"
          :title="a.configPath"
          @click="actions.openAgent(a.agentId)"
        >
          <AgentMark :id="a.agentId" />
          <b>{{ agentDisplayName(a) }}</b>
          <span class="mcp-adapter-file">{{ configFileName(a.configPath) }}</span>
        </button>
      </div>
    </details>
  </div>

  <McpEditor
    :show="editorShow"
    :creating="creating"
    :item-name="itemName"
    :name="editorName"
    :json="editorJson"
    :targets="editorTargets"
    :required-agents="requiredAgents"
    :target-options="targetOptions"
    :loaded="editorLoaded"
    :loading="editorLoading"
    :saving="editorSaving"
    :load-error="editorLoadError"
    :error="editorSaveError"
    :dirty="editorDirty"
    :close-blocked="discardShow"
    @update:name="editor.setName"
    @update:json="editor.setJson"
    @toggle-target="editor.toggleTarget"
    @format="editor.formatJson"
    @retry-load="editor.retryLoad"
    @close="editor.close"
    @save="editor.save"
  />

  <AhConfirm
    :show="deleteShow"
    title="移除 MCP"
    tone="danger"
    :loading="deleteBusy"
    :error="deleteError"
    :text="deleteItem ? `从所选端移除「${deleteItem.alias || deleteItem.id}」？` : ''"
    ok-text="移除"
    @update:show="(v: boolean) => (deleteShow = v)"
    @confirm="actions.confirmDelete"
  >
    <div class="mcp-targets" style="margin-bottom: 12px">
      <n-checkbox
        v-for="a in deletePresentAgents"
        :key="a.agentId"
        :checked="deleteTargets.includes(a.agentId)"
        :disabled="readonly || deleteBusy"
        @update:checked="(v: boolean) => actions.toggleDeleteTarget(a.agentId, v)"
      >
        {{ agentDisplayName(a) }}
      </n-checkbox>
    </div>
  </AhConfirm>

  <AhConfirm
    :show="discardShow"
    title="放弃未保存的修改？"
    text="MCP 配置的修改尚未保存。放弃后将丢失本次草稿。"
    ok-text="放弃修改"
    tone="danger"
    :mask-closable="false"
    @confirm="editor.resolveDiscard(true)"
    @update:show="(v: boolean) => { if (!v) editor.resolveDiscard(false) }"
  />

  <AhConfirm
    :show="actions.riskShow.value"
    text="推送/写入含密钥时会明文写入本机各 Agent 配置。确认后本会话不再提示。"
    @update:show="actions.onRiskShowUpdate"
    @confirm="actions.resolveRisk(true)"
  />
</template>

<style scoped>
.mcp { container: mcp / inline-size; display: flex; flex-direction: column; gap: var(--sp-4); }
/* align-items:start：卡片按内容顶端对齐，一张卡展开配置详情不把同行邻卡拉高 */
.mcp-grid { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--sp-4); align-items: start; }
@container mcp (min-width: 900px) { .mcp-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@container mcp (min-width: 1500px) { .mcp-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
.mcp-filter { display: flex; }
.mcp-search { width: min(360px, 100%); }
.mcp-banner { margin: 0; padding: var(--sp-3) var(--sp-4); border-radius: var(--r-in); color: var(--dim); background: var(--wash); font-size: var(--fs-small); }
.mcp-read-errors { margin: 0; padding: var(--sp-3); border: 1px solid var(--danger); border-radius: var(--r-in); color: var(--danger); background: var(--danger-soft); font-size: var(--fs-small); overflow-wrap: anywhere; }
.mcp-read-errors p { margin: 0; }
.mcp-read-errors p + p { margin-top: var(--sp-2); }
.mcp-empty { color: var(--empty-fg); font-size: var(--fs-small); padding: var(--sp-4) 0; }
.mcp-files { border-top: 1px solid var(--stroke); padding-top: var(--sp-4); }
.mcp-files summary { cursor: pointer; font-size: var(--fs-small); color: var(--dim); width: fit-content; }
.mcp-files summary:hover { color: var(--text); }
.mcp-files[open] summary { margin-bottom: var(--sp-3); }
.mcp-adapters { display: flex; flex-wrap: wrap; gap: var(--sp-2); }
.mcp-adapter {
  display: inline-flex; align-items: center; gap: 6px;
  height: var(--h-control); padding: 0 10px;
  border: 1px solid var(--stroke); border-radius: var(--r-in);
  background: var(--surface); color: var(--dim); cursor: pointer; font: inherit; font-size: var(--fs-small);
}
.mcp-adapter:hover { color: var(--text); border-color: var(--stroke-strong); }
.mcp-adapter:disabled { cursor: default; }
.mcp-adapter b { font-weight: 500; color: var(--text); }
.mcp-adapter-file { color: var(--faint); font-family: var(--mono); font-size: var(--fs-caption); }
.mcp-adapter :deep(.src-ico) { width: 16px; height: 16px; }
.mcp-targets { display: flex; gap: var(--sp-2) var(--sp-4); flex-wrap: wrap; align-items: center; }
</style>
