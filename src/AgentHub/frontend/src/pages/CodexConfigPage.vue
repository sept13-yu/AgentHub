<script setup lang="ts">
import { inject, onMounted, onUnmounted, ref, type Ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { NButton, NDropdown, NIcon, NInput, useMessage } from 'naive-ui'
import { Plus, RefreshCw } from 'lucide-vue-next'
import AhConfirm from '../components/AhConfirm.vue'
import CodexCurrentStatus from '../components/codex/CodexCurrentStatus.vue'
import CodexConnectionList from '../components/codex/CodexConnectionList.vue'
import CodexConnectionEditor from '../components/codex/CodexConnectionEditor.vue'
import { WRITABLE } from '../api'
import { usePageHotkeys } from '../hotkeys'
import { useCodexSnapshot } from '../codex/useCodexSnapshot'
import { useCodexActions } from '../codex/useCodexActions'
import { useCodexRelayForm } from '../codex/useCodexRelayForm'
import { useCodexSessionNotice } from '../codex/useCodexSessionNotice'

const message = useMessage()
const busy = ref(false)
const snapshot = useCodexSnapshot(inject<Ref<boolean>>('page-loading'))
const { status, loadError, lastReadAt, loading, writeDisabled, liveUnarchived, archiveLabel,
  authLoginText, currentLabel, officialRows, relayRows, profileLive } = snapshot
const actions = useCodexActions(snapshot, busy, () => editor.show.value, text => message.success(text))
const editor = useCodexRelayForm(snapshot, busy, () => actions.modalOpen.value, text => message.success(text))
const { show: editorShow, editing, form, errors, error: editorError, dirty, discardShow } = editor
const { applyShow, applyError, applyConfirmText, deleteShow, deleteText, deleteError,
  archiveShow, archiveName, archiveError, prepareShow, prepareError, prepareText } = actions
const { notice } = useCodexSessionNotice()
const anyModal = () => actions.modalOpen.value || editorShow.value || discardShow.value
const newOptions = [
  { label: '官方账号', key: 'official' },
  { label: '中转连接', key: 'relay' },
]
function create(key: string | number) {
  if (writeDisabled.value || busy.value || anyModal()) return
  if (key === 'official') {
    if (profileLive.value?.importable) actions.openArchive()
    else actions.openPrepare()
  } else if (key === 'relay') editor.open()
}
function prepareOther() {
  if (writeDisabled.value || busy.value || !archiveShow.value) return
  archiveShow.value = false
  actions.openPrepare()
}
async function refresh() {
  if (busy.value || loading.value || anyModal()) return
  if (await snapshot.load()) message.success('连接数据已刷新')
}
usePageHotkeys({ refresh })
onBeforeRouteLeave(() => editor.confirmDiscard())
let stopPoll: (() => void) | undefined
onMounted(() => {
  void snapshot.load()
  stopPoll = snapshot.startPoll(() => busy.value || anyModal())
  window.addEventListener('beforeunload', editor.onBeforeUnload)
})
onUnmounted(() => {
  stopPoll?.()
  window.removeEventListener('beforeunload', editor.onBeforeUnload)
  editor.resolveDiscard(false)
})
</script>

<template>
  <div class="codex-page">
    <div class="page-actions">
      <n-dropdown :options="newOptions" trigger="click" @select="create">
        <n-button type="primary" :disabled="writeDisabled || busy || anyModal()">
          <template #icon><n-icon :size="16" aria-hidden="true"><Plus :stroke-width="1.8" /></n-icon></template>
          新建
        </n-button>
      </n-dropdown>
      <n-button :loading="loading" :disabled="busy || loading || anyModal()" @click="refresh">
        <template #icon><n-icon :size="16" aria-hidden="true"><RefreshCw :stroke-width="1.8" /></n-icon></template>
        刷新
      </n-button>
    </div>
    <p v-if="!WRITABLE" class="banner">浏览器直连为只读。写入连接、保存登录副本请在 AgentHub 窗口中操作。</p>
    <p v-if="loadError" class="usage-error" role="alert">
      读取失败：{{ loadError }}{{ status ? '。保留上次成功读取的数据（' + lastReadAt + '），请重试。' : '。请点击刷新重试。' }}
    </p>
    <CodexCurrentStatus v-if="status" :status="status" :current-label="currentLabel"
      :auth-login-text="authLoginText" :live-unarchived="liveUnarchived" :notice="notice" />
    <p v-else-if="loading" class="empty">正在读取 Codex 连接…</p>
    <div v-if="status" class="connection-groups">
      <CodexConnectionList kind="official" :rows="officialRows" :busy="busy" :write-disabled="writeDisabled"
        @apply="actions.askApply" @delete="actions.askDelete" />
      <CodexConnectionList kind="relay" :rows="relayRows" :busy="busy" :write-disabled="writeDisabled"
        @apply="actions.askApply" @delete="actions.askDelete" @edit="editor.open" />
    </div>
    <CodexConnectionEditor :show="editorShow" :editing="editing" :form="form" :errors="errors"
      :error="editorError" :dirty="dirty" :busy="busy" :write-disabled="writeDisabled"
      :close-blocked="discardShow" @update:form="editor.updateForm" @close="editor.close" @save="editor.save" />
    <AhConfirm v-model:show="applyShow" title="写入 Codex 连接" :text="applyConfirmText"
      ok-text="确认写入" :loading="busy" :disabled="writeDisabled" :error="applyError" @confirm="actions.doApply" />
    <AhConfirm v-model:show="prepareShow" title="清空当前登录，准备新账号" :text="prepareText"
      ok-text="清空登录并切到官方" tone="danger" :loading="busy" :disabled="writeDisabled" :error="prepareError"
      :alt-text="liveUnarchived ? '先保存当前登录副本' : undefined"
      @alt="actions.openArchive(true)" @confirm="actions.doPrepare" />
    <AhConfirm v-model:show="archiveShow" :title="archiveLabel"
      text="将当前 ChatGPT 登录保存为副本，供以后切换使用。可填写便于辨认的名称。"
      ok-text="保存登录副本" alt-text="登录其他账号…" :loading="busy" :disabled="writeDisabled" :error="archiveError"
      @alt="prepareOther" @confirm="actions.doArchive">
      <label class="archive-name" for="codex-archive-name">副本名称（可选）</label>
      <n-input :input-props="{ id: 'codex-archive-name', name: 'profileName', autocomplete: 'off', spellcheck: false }" v-model:value="archiveName" :disabled="busy || writeDisabled"
        placeholder="默认使用账号邮箱" @keydown.enter.prevent="actions.doArchive" />
    </AhConfirm>
    <AhConfirm v-model:show="deleteShow" title="删除已保存记录" :text="deleteText" ok-text="删除"
      tone="danger" :loading="busy" :disabled="writeDisabled" :error="deleteError" @confirm="actions.doDelete" />
    <AhConfirm :show="discardShow" title="放弃未保存的修改？" text="中转连接的修改尚未保存。放弃后将丢失本次草稿。"
      ok-text="放弃修改" tone="danger" :mask-closable="false" @confirm="editor.resolveDiscard(true)"
      @update:show="value => { if (!value) editor.resolveDiscard(false) }" />
  </div>
</template>

<style scoped>
.codex-page { display: flex; flex-direction: column; gap: var(--sp-4); }
.page-actions { display: flex; justify-content: flex-end; gap: var(--sp-2); }
.connection-groups { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--sp-4); align-items: start; }
.connection-groups > * { min-width: 0; }
.codex-page > p { margin: 0; }
.banner, .usage-error { padding: var(--sp-3) var(--sp-4); border-radius: var(--r-in); font-size: var(--fs-small); line-height: 1.6; overflow-wrap: anywhere; }
.banner { color: var(--dim); background: var(--wash); }
.usage-error { color: var(--error-fg); background: var(--error-soft); }
.empty { color: var(--dim); font-size: var(--fs-small); }
.archive-name { display: block; margin-bottom: var(--sp-2); font-size: var(--fs-caption); color: var(--dim); }
@media (max-width: 1000px) { .connection-groups { grid-template-columns: minmax(0, 1fr); } }
</style>
