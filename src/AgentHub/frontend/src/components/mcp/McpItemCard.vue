<script setup lang="ts">
import { computed, h } from 'vue'
import { NButton, NDropdown, NIcon, NSwitch, type DropdownOption } from 'naive-ui'
import { ChevronDown, MoreHorizontal } from 'lucide-vue-next'
import AgentMark from '../AgentMark.vue'
import {
  agentDisplayName,
  agentPendingKey,
  isSystemItem,
  type McpAgentStatus,
  type McpItem,
} from '../../mcp/types'

// 纯展示卡：各 Agent 以「图标 + 名称 + 小开关」chip 横向紧凑排布、自然换行；
// 技术信息收进默认收起的配置详情；写动作全部上报给页面层。
const props = defineProps<{
  item: McpItem
  detailOpen: boolean
  pendingKeys: Set<string>
  deleteLocked: boolean
  actionBusy: boolean
  writeDisabled: boolean
}>()
const emit = defineEmits<{
  'toggle-detail': []
  edit: []
  remove: []
  'toggle-agent': [agent: McpAgentStatus, wantOn: boolean]
  'open-menu': [event: MouseEvent]
}>()

const displayName = computed(() => props.item.alias || props.item.id)
const detectedAgents = computed(() => props.item.agents.filter((a) => a.detected))
const driftCount = computed(() => detectedAgents.value.filter((a) => a.presence === 'present' && a.drift).length)
const errorCount = computed(() => detectedAgents.value.filter((a) => a.presence === 'error').length)
const systemItem = computed(() => isSystemItem(props.item))

const moreOptions = computed<DropdownOption[]>(() => [
  { key: 'edit', label: '编辑', disabled: props.writeDisabled || props.actionBusy },
  { key: 'sep', separator: true },
  {
    key: 'remove',
    label: () => h('span', { class: 'mcp-remove-label' }, '移除'),
    disabled: props.writeDisabled || props.actionBusy || systemItem.value,
  },
])

function onMoreSelect(key: string | number) {
  if (key === 'edit' && !props.writeDisabled && !props.actionBusy) emit('edit')
  if (key === 'remove' && !props.writeDisabled && !props.actionBusy && !systemItem.value) emit('remove')
}

function isPending(agent: McpAgentStatus) {
  return props.pendingKeys.has(agentPendingKey(props.item.id, agent.agentId))
}

function presenceClass(presence: string) {
  switch (presence) {
    case 'present': return 'ok'
    case 'missing': return 'miss'
    case 'system': return 'sys'
    case 'unsupported': return 'off'
    case 'error': return 'error'
    default: return ''
  }
}

function rowTitle(agent: McpAgentStatus) {
  // DSH 写的是 home 共享层（对所有 profile 生效），运行中的实例要重启才加载
  const dshNote = agent.agentId === 'dsh' ? '\n写入后需重启 DSH 生效' : ''
  switch (agent.presence) {
    case 'present':
      return `${agentDisplayName(agent)} · ${agent.enabledOnAgent ? '已启用' : '已停用'}\n${agent.configPath}${dshNote}`
    case 'missing':
      return `${agentDisplayName(agent)} · 未配置\n${agent.configPath}${dshNote}`
    case 'error':
      return `${agentDisplayName(agent)} · ${agent.detail || '读取配置失败'}\n${agent.configPath}`
    default:
      return `${agentDisplayName(agent)} · ${agent.configPath}${dshNote}`
  }
}

const techText = computed(() =>
  props.item.transport === 'http' ? props.item.url || '（无 URL）' : props.item.command || '（无 command）')
const techTitle = computed(() =>
  props.item.transport === 'http' ? props.item.url || '' : props.item.command || '')
</script>

<template>
  <article class="mcp-card" @contextmenu="emit('open-menu', $event)">
    <header class="mcp-card-head">
      <div class="mcp-title">
        <b :title="displayName">{{ displayName }}</b>
        <code v-if="item.alias">{{ item.id }}</code>
      </div>
      <n-dropdown trigger="click" :options="moreOptions" @select="onMoreSelect">
        <n-button size="small" quaternary :aria-label="`${displayName} 的更多操作`">
          <template #icon><n-icon :component="MoreHorizontal" aria-hidden="true" /></template>
        </n-button>
      </n-dropdown>
    </header>
    <p v-if="item.note" class="mcp-note" :title="item.note">{{ item.note }}</p>

    <div class="mcp-agents">
      <span
        v-for="a in detectedAgents"
        :key="a.agentId"
        class="mcp-agent"
        :class="presenceClass(a.presence)"
        :title="rowTitle(a)"
      >
        <AgentMark :id="a.agentId" />
        <span class="name">{{ agentDisplayName(a) }}</span>
        <span v-if="a.presence === 'present' && a.drift" class="drift" title="配置与标准配置有差异，编辑统一配置可同步">待同步</span>
        <n-switch
          v-if="a.presence === 'present'"
          size="small"
          :value="!!a.enabledOnAgent"
          :disabled="writeDisabled || deleteLocked || isPending(a)"
          :loading="isPending(a)"
          :aria-label="`${agentDisplayName(a)} · ${displayName} 启停`"
          @update:value="(v: boolean) => emit('toggle-agent', a, v)"
        />
        <n-button
          v-else-if="a.presence === 'missing'"
          size="tiny"
          :disabled="writeDisabled || deleteLocked || isPending(a)"
          :loading="isPending(a)"
          :aria-label="`在 ${agentDisplayName(a)} 添加 ${displayName}`"
          @click="emit('toggle-agent', a, true)"
        >
          添加
        </n-button>
        <span v-else-if="a.presence === 'system'" class="state">系统</span>
        <span v-else-if="a.presence === 'unsupported'" class="state">未安装</span>
        <span v-else-if="a.presence === 'error'" class="state error">读取失败</span>
      </span>
    </div>

    <div class="mcp-foot">
      <span v-if="driftCount || errorCount" class="mcp-issues">
        <template v-if="driftCount">待同步 {{ driftCount }}</template>
        <template v-if="driftCount && errorCount"> · </template>
        <template v-if="errorCount">读取失败 {{ errorCount }}</template>
      </span>
      <button
        type="button"
        class="mcp-detail-toggle"
        :aria-expanded="detailOpen ? 'true' : 'false'"
        :aria-label="`${detailOpen ? '收起' : '展开'} ${displayName} 的配置详情`"
        @click="emit('toggle-detail')"
      >
        配置详情
        <n-icon :component="ChevronDown" :size="14" aria-hidden="true" class="mcp-detail-icon" :class="{ 'is-open': detailOpen }" />
      </button>
    </div>

    <div v-if="detailOpen" class="mcp-detail">
      <span class="tag">{{ item.transport }}</span>
      <span v-if="item.hasSecretRisk" class="tag risk">含密钥</span>
      <code class="mcp-tech" :title="techTitle">{{ techText }}</code>
    </div>
  </article>
</template>

<style scoped>
.mcp-card {
  display: flex; flex-direction: column; gap: var(--sp-2);
  border: 1px solid var(--stroke); border-radius: var(--r-card);
  padding: var(--sp-4); background: var(--surface); min-width: 0;
}
.mcp-card-head { display: flex; align-items: center; gap: var(--sp-2); min-height: 24px; }
.mcp-title { flex: 1; min-width: 0; display: flex; align-items: baseline; gap: 8px; flex-wrap: wrap; }
.mcp-title b { font-size: var(--fs-card); font-weight: 600; overflow-wrap: anywhere; }
.mcp-title code { font-size: var(--fs-caption); color: var(--faint); }
.mcp-note {
  margin: 0; font-size: var(--fs-small); color: var(--dim);
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.mcp-agents { display: flex; flex-wrap: wrap; gap: var(--sp-2); margin-top: 2px; }
.mcp-agent {
  display: inline-flex; align-items: center; gap: 6px;
  padding: 3px 8px; border-radius: 999px;
  background: var(--wash); font-size: var(--fs-caption); min-width: 0; max-width: 100%;
}
.mcp-agent .name { color: var(--text); white-space: nowrap; }
.mcp-agent.miss { background: transparent; border: 1px dashed var(--stroke-strong); }
.mcp-agent.sys { background: transparent; outline: 1px dashed var(--stroke-strong); }
.mcp-agent.off { background: transparent; }
.mcp-agent.error { background: var(--danger-soft); }
.mcp-agent .state { color: var(--dim); white-space: nowrap; }
.mcp-agent .state.error { color: var(--danger); }
.mcp-agent .drift { color: var(--warn); font-weight: 600; white-space: nowrap; }
.mcp-agent :deep(.src-ico) { width: 14px; height: 14px; }
.mcp-foot { display: flex; align-items: center; justify-content: space-between; gap: var(--sp-3); margin-top: 2px; min-width: 0; }
.mcp-issues { font-size: var(--fs-caption); color: var(--warn); min-width: 0; }
.mcp-detail-toggle {
  display: inline-flex; align-items: center; gap: 2px;
  padding: 2px 6px; margin-left: auto;
  border: 0; border-radius: var(--r-in);
  background: none; color: var(--faint); font: inherit; font-size: var(--fs-caption);
  cursor: pointer; flex: none; white-space: nowrap;
}
.mcp-detail-toggle:hover { color: var(--text); background: var(--wash); }
.mcp-detail-toggle:focus-visible { outline: 1px solid var(--focus-ring); outline-offset: 1px; }
.mcp-detail-icon { transition: transform var(--dur) linear; }
.mcp-detail-icon.is-open { transform: rotate(180deg); }
.mcp-detail { display: flex; align-items: baseline; gap: 6px; min-width: 0; }
.tag {
  font-size: var(--fs-caption); padding: 0 6px; height: 20px; line-height: 20px;
  border-radius: 999px; background: var(--wash); color: var(--dim); flex: none;
}
.tag.risk { background: var(--danger-soft); color: var(--danger); }
.mcp-tech {
  font-size: var(--fs-caption); color: var(--faint); font-family: var(--mono);
  overflow-wrap: anywhere; min-width: 0;
}
:global(.mcp-remove-label) { color: var(--danger); }
</style>
