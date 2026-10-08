<script setup lang="ts">
import { nextTick, ref, watch, type ComponentPublicInstance } from 'vue'
import { NButton, NDrawer, NDrawerContent, NIcon, NInput, NSwitch, type InputInst } from 'naive-ui'
import { X } from 'lucide-vue-next'
import type { RelayFieldErrors, RelayForm } from '../../codex/types'

const props = defineProps<{
  show: boolean
  editing: boolean
  form: RelayForm
  errors: RelayFieldErrors
  error: string
  dirty: boolean
  busy: boolean
  writeDisabled: boolean
  closeBlocked?: boolean
}>()
const emit = defineEmits<{ 'update:form': [form: RelayForm]; close: []; save: [] }>()
type TextField = Exclude<keyof RelayForm, 'supportsWebSockets' | 'keySet'>
// 只依赖 focus 能力做错误聚焦；NInput 实例类型与模板 ref 的宽类型不直接兼容，
// 运行时收窄出 focus 函数，卸载（null）时清理引用（T01）。
type Focusable = Pick<InputInst, 'focus'>
const inputs: Partial<Record<TextField, Focusable>> = {}
function setInputRef(key: TextField) {
  return (el: Element | ComponentPublicInstance | null) => {
    const focus = el && !(el instanceof Element) ? (el as { focus?: unknown }).focus : undefined
    inputs[key] = typeof focus === 'function' ? { focus: () => focus() } : undefined
  }
}
const advancedOpen = ref(false)
const advancedFields: { key: TextField; label: string; placeholder: string; url?: boolean }[] = [
  { key: 'defaultModel', label: '模型', placeholder: '留空使用 Codex 默认模型' },
  { key: 'userAgent', label: 'User-Agent', placeholder: '留空使用默认值' },
  { key: 'originator', label: 'Originator', placeholder: '留空使用默认值' },
  { key: 'usageBaseUrl', label: '额度查询地址', placeholder: 'https://example.com（可选）', url: true },
]
function update<K extends keyof RelayForm>(key: K, value: RelayForm[K]) {
  emit('update:form', { ...props.form, [key]: value })
}
function close() {
  if (!props.busy && !props.closeBlocked) emit('close')
}
function updateShow(show: boolean) {
  if (!show) close()
}
function save() {
  if (!props.busy && !props.writeDisabled && !props.closeBlocked) emit('save')
}
watch(() => props.show, (show) => { if (show) advancedOpen.value = false })
watch(() => props.errors, async (errors) => {
  if (!props.show) return
  const first = (['name', 'baseUrl', 'apiKey', ...advancedFields.map((field) => field.key)] as TextField[]).find((key) => errors[key])
  if (!first) return
  if (advancedFields.some((field) => field.key === first)) advancedOpen.value = true
  await nextTick()
  if (props.show && !props.closeBlocked) inputs[first]?.focus()
}, { deep: true, flush: 'post' })
</script>

<template>
  <NDrawer :show="show" width="min(520px, 100vw)" aria-labelledby="codex-relay-editor-title" :mask-closable="!busy && !closeBlocked"
    :close-on-esc="!busy && !closeBlocked" :trap-focus="!closeBlocked" @update:show="updateShow">
    <NDrawerContent body-content-class="codex-editor-body" :native-scrollbar="false">
      <template #header>
        <div class="editor-header">
          <h2 id="codex-relay-editor-title">{{ editing ? '编辑中转连接' : '添加中转连接' }}</h2>
          <NButton quaternary :disabled="busy || closeBlocked" aria-label="关闭中转编辑器" @click="close">
            <template #icon><NIcon :component="X" aria-hidden="true" /></template>
          </NButton>
        </div>
      </template>
      <form id="codex-relay-form" class="relay-form" novalidate @submit.prevent="save">
        <div class="field">
          <label for="codex-relay-name">名称<span class="required">必填</span></label>
          <NInput :ref="setInputRef('name')" :value="form.name" :disabled="busy || writeDisabled"
            :status="errors.name ? 'error' : undefined" placeholder="便于辨认的连接名称"
            :input-props="{ id: 'codex-relay-name', name: 'connectionName', autocomplete: 'off', spellcheck: false, 'aria-required': true, 'aria-invalid': !!errors.name, 'aria-describedby': errors.name ? 'codex-relay-name-error' : undefined }"
            @update:value="update('name', $event)" />
          <p v-if="errors.name" id="codex-relay-name-error" class="field-error">{{ errors.name }}</p>
        </div>
        <div class="field">
          <label for="codex-relay-url">服务地址<span class="required">必填</span></label>
          <NInput :ref="setInputRef('baseUrl')" :value="form.baseUrl" :disabled="busy || writeDisabled"
            :status="errors.baseUrl ? 'error' : undefined" placeholder="https://example.com/v1"
            :input-props="{ id: 'codex-relay-url', name: 'baseUrl', autocomplete: 'url', inputmode: 'url', spellcheck: false, 'aria-required': true, 'aria-invalid': !!errors.baseUrl, 'aria-describedby': errors.baseUrl ? 'codex-relay-url-error' : undefined }"
            @update:value="update('baseUrl', $event)" />
          <p v-if="errors.baseUrl" id="codex-relay-url-error" class="field-error">{{ errors.baseUrl }}</p>
        </div>
        <div class="field">
          <label for="codex-relay-key">API Key<span v-if="!editing || !form.keySet" class="required">必填</span></label>
          <NInput :ref="setInputRef('apiKey')" :value="form.apiKey" type="password" show-password-on="click"
            :disabled="busy || writeDisabled" :status="errors.apiKey ? 'error' : undefined"
            :placeholder="editing && form.keySet ? '留空保持已保存的密钥' : '输入 API Key'"
            :input-props="{ id: 'codex-relay-key', name: 'apiKey', autocomplete: 'off', spellcheck: false, 'aria-required': !editing || !form.keySet, 'aria-invalid': !!errors.apiKey, 'aria-describedby': 'codex-relay-key-help' + (errors.apiKey ? ' codex-relay-key-error' : '') }"
            @update:value="update('apiKey', $event)" />
          <p id="codex-relay-key-help" class="field-hint">{{ editing && form.keySet ? '密钥已配置。留空保持，填写则替换。' : '密钥仅用于此连接，不会在列表中显示。' }}</p>
          <p v-if="errors.apiKey" id="codex-relay-key-error" class="field-error">{{ errors.apiKey }}</p>
        </div>
        <details :open="advancedOpen" class="advanced" @toggle="advancedOpen = ($event.target as HTMLDetailsElement).open">
          <summary>高级选项<span>模型、WebSocket 与请求标识</span></summary>
          <div class="advanced-fields">
            <div v-for="field in advancedFields" :key="field.key" class="field">
              <label :for="`codex-relay-${field.key}`">{{ field.label }}</label>
              <NInput :ref="setInputRef(field.key)" :value="form[field.key]" :disabled="busy || writeDisabled"
                :status="errors[field.key] ? 'error' : undefined" :placeholder="field.placeholder"
                :input-props="{ id: `codex-relay-${field.key}`, name: field.key, autocomplete: field.url ? 'url' : 'off', inputmode: field.url ? 'url' : 'text', spellcheck: false, 'aria-invalid': !!errors[field.key], 'aria-describedby': errors[field.key] ? `codex-relay-${field.key}-error` : undefined }"
                @update:value="update(field.key, $event)" />
              <p v-if="errors[field.key]" :id="`codex-relay-${field.key}-error`" class="field-error">{{ errors[field.key] }}</p>
            </div>
            <div class="websocket-field">
              <div><label id="codex-relay-websocket-label">WebSocket</label><p class="field-hint">仅在服务支持时启用。</p></div>
              <NSwitch :value="form.supportsWebSockets" :disabled="busy || writeDisabled" aria-labelledby="codex-relay-websocket-label" @update:value="update('supportsWebSockets', $event)" />
            </div>
          </div>
        </details>
        <p v-if="error" class="submit-error" role="alert">{{ error }}</p>
        <p v-if="writeDisabled" class="field-hint">当前无法写入，请检查桌面连接或配置状态。</p>
      </form>
      <template #footer>
        <div class="editor-footer">
          <span class="draft-state" role="status">{{ busy ? '正在保存…' : dirty ? '有未保存的修改' : '' }}</span>
          <NButton :disabled="busy || closeBlocked" @click="close">取消</NButton>
          <NButton type="primary" attr-type="submit" form="codex-relay-form" :loading="busy" :disabled="busy || writeDisabled || closeBlocked">
            {{ editing ? '保存修改' : '保存中转' }}
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
.relay-form { display: flex; flex-direction: column; gap: var(--sp-5); }
.field-hint { font-size: var(--fs-small); color: var(--dim); }
.field { display: flex; flex-direction: column; gap: var(--sp-2); }
label { font-size: var(--fs-body); font-weight: 500; }
.required { margin-left: var(--sp-2); font-size: var(--fs-caption); color: var(--dim); font-weight: 400; }
.field-error, .submit-error { color: var(--danger); font-size: var(--fs-small); overflow-wrap: anywhere; }
.submit-error { background: var(--danger-soft); padding: var(--sp-3); border-radius: var(--r-in); }
.advanced { border-top: 1px solid var(--stroke); padding-top: var(--sp-4); }
summary { cursor: pointer; font-weight: 500; }
summary span { margin-left: var(--sp-2); font-size: var(--fs-small); font-weight: 400; color: var(--dim); }
.advanced-fields { display: flex; flex-direction: column; gap: var(--sp-4); margin-top: var(--sp-4); }
.websocket-field { display: flex; align-items: center; justify-content: space-between; gap: var(--sp-4); }
.editor-footer { display: flex; width: 100%; align-items: center; flex-wrap: wrap; gap: var(--sp-2); }
.draft-state { margin-right: auto; font-size: var(--fs-small); color: var(--dim); }
@media (max-width: 440px) {
  .draft-state { width: 100%; }
  summary span { display: block; margin-left: var(--sp-4); }
}
</style>
