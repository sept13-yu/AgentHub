<script setup lang="ts">
import { NButton, NCheckbox, NDrawer, NDrawerContent, NIcon, NInput } from 'naive-ui'
import { FileJson, X } from 'lucide-vue-next'
import { agentDisplayName, type McpAdapter, type McpAgentStatus } from '../../mcp/types'

// 统一配置编辑抽屉：纯展示，状态与保存逻辑在 useMcpEditor。
const props = defineProps<{
  show: boolean
  creating: boolean
  itemName: string
  name: string
  json: string
  targets: string[]
  requiredAgents: McpAgentStatus[]
  targetOptions: McpAdapter[]
  loaded: boolean
  loading: boolean
  saving: boolean
  loadError: string
  error: string
  dirty: boolean
  closeBlocked: boolean
}>()
const emit = defineEmits<{
  'update:name': [value: string]
  'update:json': [value: string]
  'toggle-target': [id: string, on: boolean]
  format: []
  'retry-load': []
  close: []
  save: []
}>()

function close() {
  if (!props.saving && !props.closeBlocked) emit('close')
}
function updateShow(show: boolean) {
  if (!show) close()
}
function save() {
  if (!props.saving && !props.closeBlocked) emit('save')
}
function setName(value: string) {
  if (!props.saving) emit('update:name', value)
}
function setJson(value: string) {
  if (!props.saving) emit('update:json', value)
}
</script>

<template>
  <NDrawer
    :show="show"
    width="min(560px, 100vw)"
    :aria-labelledby="creating ? 'mcp-editor-title-new' : 'mcp-editor-title'"
    :mask-closable="!saving && !closeBlocked"
    :close-on-esc="!saving && !closeBlocked"
    :trap-focus="!closeBlocked"
    @update:show="updateShow"
  >
    <NDrawerContent body-content-class="mcp-editor-body" :native-scrollbar="false" :closable="false">
      <template #header>
        <div class="editor-header">
          <h2 :id="creating ? 'mcp-editor-title-new' : 'mcp-editor-title'">
            {{ creating ? '新建 MCP' : `${itemName}：编辑统一配置` }}
          </h2>
          <NButton quaternary :disabled="saving || closeBlocked" aria-label="关闭 MCP 编辑器" @click="close">
            <template #icon><NIcon :component="X" aria-hidden="true" /></template>
          </NButton>
        </div>
      </template>

      <div v-if="!loading" class="mcp-form">
        <p v-if="error" class="form-error" role="alert">{{ error }}</p>
        <div v-if="loadError" class="form-error" role="alert">
          <span>{{ loadError }}</span>
          <NButton size="tiny" :disabled="saving" @click="emit('retry-load')">重新读取配置</NButton>
        </div>
        <div v-if="creating" class="field">
          <label for="mcp-editor-name">名称（Cursor 单对象无 id 时必填）</label>
          <NInput
            :value="name"
            :disabled="saving"
            placeholder="如 jira"
            :input-props="{ id: 'mcp-editor-name', name: 'mcpName', autocomplete: 'off', spellcheck: false }"
            @update:value="setName"
          />
        </div>
        <div class="field">
          <div class="json-head">
            <label for="mcp-editor-json">JSON（统一标准配置，单个 server）</label>
            <NButton size="tiny" quaternary :disabled="!loaded || saving" @click="emit('format')">
              <template #icon><NIcon :component="FileJson" aria-hidden="true" /></template>
              格式化
            </NButton>
          </div>
          <NInput
            :value="json"
            type="textarea"
            class="mcp-json"
            :disabled="!loaded || saving"
            :autosize="{ minRows: 12, maxRows: 24 }"
            placeholder='{"id":"jira","transport":"stdio",...}'
            :input-props="{ id: 'mcp-editor-json', name: 'mcpJson', spellcheck: false }"
            @update:value="setJson"
          />
        </div>
        <div v-if="!creating" class="targets">
          <span class="targets-label">同步到所有已配置 Agent</span>
          <div class="target-list">
            <NCheckbox v-for="a in requiredAgents" :key="a.agentId" checked disabled>
              {{ agentDisplayName(a) }}
            </NCheckbox>
            <span v-if="!requiredAgents.length" class="hint">当前仅保存标准配置，可在下方选择新增 Agent。</span>
          </div>
        </div>
        <div v-if="!creating || targetOptions.length" class="targets">
          <span class="targets-label">{{ creating ? '同步到' : '新增到' }}</span>
          <div class="target-list">
            <NCheckbox
              v-for="a in targetOptions"
              :key="a.agentId"
              :checked="targets.includes(a.agentId)"
              :disabled="saving || !loaded"
              @update:checked="(v: boolean) => emit('toggle-target', a.agentId, v)"
            >
              {{ agentDisplayName(a) }}
            </NCheckbox>
          </div>
        </div>
        <p class="hint">保存一份标准配置，并同步所有已配置 Agent 和新增所选 Agent。已有 Agent 的启用状态保留。密钥会明文写入本机各端配置。</p>
      </div>
      <p v-else class="hint">加载中…</p>

      <template #footer>
        <div class="editor-footer">
          <span class="draft-state" role="status">{{ saving ? '正在保存…' : dirty ? '有未保存的修改' : '' }}</span>
          <NButton :disabled="saving || closeBlocked" @click="close">取消</NButton>
          <NButton type="primary" :loading="saving" :disabled="saving || closeBlocked || !loaded" @click="save">
            保存并同步
          </NButton>
        </div>
      </template>
    </NDrawerContent>
  </NDrawer>
</template>

<style scoped>
h2, p { margin: 0; }
h2 { font-size: var(--fs-card); font-weight: 600; }
.editor-header { display: flex; width: 100%; align-items: center; justify-content: space-between; gap: var(--sp-4); }
.mcp-form { display: flex; flex-direction: column; gap: var(--sp-4); }
.field { display: flex; flex-direction: column; gap: var(--sp-2); }
.field label { font-size: var(--fs-body); font-weight: 500; }
.json-head { display: flex; align-items: center; justify-content: space-between; gap: var(--sp-2); }
.json-head label { min-width: 0; }
.form-error {
  display: flex; align-items: center; justify-content: space-between; gap: var(--sp-3);
  padding: var(--sp-3); border-radius: var(--r-in);
  color: var(--danger); background: var(--danger-soft);
  font-size: var(--fs-small); overflow-wrap: anywhere;
}
.targets { display: flex; flex-direction: column; gap: var(--sp-2); }
.targets-label { font-size: var(--fs-small); color: var(--dim); }
.target-list { display: flex; gap: var(--sp-2) var(--sp-4); flex-wrap: wrap; align-items: center; }
.mcp-json :deep(textarea) { font-family: var(--mono); font-size: var(--fs-caption); }
.hint { font-size: var(--fs-caption); color: var(--faint); }
.editor-footer { display: flex; width: 100%; align-items: center; flex-wrap: wrap; gap: var(--sp-2); }
.draft-state { margin-right: auto; font-size: var(--fs-small); color: var(--dim); }
@media (max-width: 440px) {
  .draft-state { width: 100%; }
}
</style>
