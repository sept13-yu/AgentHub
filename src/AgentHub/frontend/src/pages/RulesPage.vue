<script setup lang="ts">
import { computed, inject, onMounted, onUnmounted, ref, type Ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { NButton, NIcon, NInput, NSwitch, useMessage } from 'naive-ui'
import { ExternalLink, FolderOpen, RefreshCw, RotateCcw, Save, X } from 'lucide-vue-next'
import AhConfirm from '../components/AhConfirm.vue'
import AgentMark from '../components/AgentMark.vue'
import { get, post, put, WRITABLE } from '../api'
import { usePageHotkeys } from '../hotkeys'

const message = useMessage()
const pageLoading = inject<Ref<boolean>>('page-loading')
const readonly = !WRITABLE

type RuleState = 'notDetected' | 'needsFirstLaunch' | 'missing' | 'current' | 'needsSync' | 'busy' | 'unsupported' | 'conflict'
interface AgentRuleItem {
  agentId: string
  displayName: string
  detected: boolean
  status: RuleState
  rulePath: string | null
  message: string
  canWrite: boolean
}
interface PointerTemplate {
  path: string
  customized: boolean
  valid: boolean
  warnings: string[]
}
interface AgentRulesStatus {
  libraryRoot: string
  libraryRootExists: boolean
  sharedRulesPath: string
  agents: AgentRuleItem[]
  hasChanges: boolean
  enabled: boolean
  pointerTemplate: PointerTemplate
}
interface HubPayload { path: string; exists: boolean; enabled: boolean; content: string }
interface PointerPayload {
  path: string
  exists: boolean
  customized: boolean
  valid: boolean
  warnings: string[]
  content: string
}
interface ApplyResult { ok: boolean; items: { ok: boolean; message: string }[] }

const loaded = ref(false)
const loadError = ref('')
const busy = ref(false)
const pane = ref<'hub' | 'tpl'>('hub')
const status = ref<AgentRulesStatus | null>(null)
const hubPath = ref('')
const draft = ref('')
const snapshot = ref('')
const tplPath = ref('')
const tplDraft = ref('')
const tplSnapshot = ref('')
const tplExists = ref(false)
const libDraft = ref('')
const libSaved = ref('')
const confirmKind = ref<'enable' | 'disable' | 'move' | 'leave' | 'reload' | 'resetTpl' | ''>('')
const pendingLib = ref('')
let leaveResolve: ((ok: boolean) => void) | null = null
let ignoreFocusUntil = 0

const enabled = computed(() => !!status.value?.enabled)
const dirty = computed(() => loaded.value && draft.value !== snapshot.value)
const tplDirty = computed(() => loaded.value && tplDraft.value !== tplSnapshot.value)
const pageDirty = computed(() => dirty.value || tplDirty.value)
const libDirty = computed(() => loaded.value && normalizePath(libDraft.value) !== normalizePath(libSaved.value))
const canEdit = computed(() => enabled.value && !readonly)
const updateDisabled = computed(() => readonly || !enabled.value || busy.value || !status.value?.hasChanges)
const pointerTemplateLabel = computed(() => {
  const t = status.value?.pointerTemplate
  if (!t) return ''
  if (!t.customized) return '正在用嵌入默认，保存后写成自定义'
  if (t.valid) return '自定义（pointer-template.md）'
  return '自定义无效，已回退默认'
})
const pointerWarnings = computed(() => status.value?.pointerTemplate?.warnings ?? [])
const pointerInvalid = computed(() => {
  const t = status.value?.pointerTemplate
  return !!t?.customized && !t.valid
})

const confirmText = computed(() => ({
  enable: '打开后会改各家规则，写成去读左边这份。\n\n没有共用规则就先建一份。各家文件整份覆盖，先备份。',
  disable: '关掉后删掉各家指向这份规则的内容。共用规则文件还在，各家不再自动读它。',
  move: '资料目录要改位置。把旧的 Plans、SandBox 搬过去吗？同名文件跳过，不覆盖。',
  leave: '有未保存的修改。确定离开吗？未保存的修改将丢失。',
  reload: '文件在外面改过。放弃这里的修改，按磁盘上的重读？',
  resetTpl: '删掉自定义注入模板，各家按嵌入默认判等。已写进各家的要再点更新。',
  '': '',
}[confirmKind.value]))

const confirmOk = computed(() => ({
  enable: '打开并更新',
  disable: '删掉指向',
  move: '搬过去',
  leave: '离开',
  reload: '重读',
  resetTpl: '恢复默认',
  '': '确定',
}[confirmKind.value]))

function normalizePath(p: string) {
  return p.replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase()
}

function ruleStateText(s: RuleState) {
  return ({
    notDetected: '未发现', needsFirstLaunch: '需先启动', missing: '待更新', current: '已对齐',
    needsSync: '待更新', busy: '使用中', unsupported: '未支持', conflict: '冲突',
  } as Record<RuleState, string>)[s]
}

function setLoading(on: boolean) {
  if (pageLoading) pageLoading.value = on
}

function applyHub(hub: HubPayload) {
  hubPath.value = hub.path
  draft.value = hub.content
  snapshot.value = hub.content
}

function applyTpl(tpl: PointerPayload) {
  tplPath.value = tpl.path
  tplDraft.value = tpl.content
  tplSnapshot.value = tpl.content
  tplExists.value = tpl.exists
}

function applyStatus(s: AgentRulesStatus, keepLibDraft = false) {
  const keep = keepLibDraft && libDirty.value
  status.value = s
  libSaved.value = s.libraryRoot
  if (!keep) libDraft.value = s.libraryRoot
}

async function load() {
  loadError.value = ''
  setLoading(true)
  try {
    const [st, hub, tpl] = await Promise.all([
      get<AgentRulesStatus>('/api/agent-rules/status'),
      get<HubPayload>('/api/agent-rules/hub'),
      get<PointerPayload>('/api/agent-rules/pointer-template'),
    ])
    applyStatus(st)
    applyHub(hub)
    applyTpl(tpl)
    loaded.value = true
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    setLoading(false)
  }
}

function toastApply(r: ApplyResult, okText: string) {
  const failed = r.items.filter((x) => !x.ok)
  if (!r.ok) message.error(failed[0]?.message || '未完成')
  else if (failed.length) message.warning(failed[0].message)
  else message.success(okText)
}

async function runEnable() {
  confirmKind.value = ''
  busy.value = true
  setLoading(true)
  try {
    const r = await post<ApplyResult>('/api/agent-rules/enable')
    toastApply(r, '已打开并更新')
    await load()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '打开失败')
  } finally {
    busy.value = false
    setLoading(false)
  }
}

async function runDisable() {
  confirmKind.value = ''
  busy.value = true
  setLoading(true)
  try {
    const r = await post<ApplyResult>('/api/agent-rules/disable')
    toastApply(r, '已关掉')
    await load()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '关掉失败')
  } finally {
    busy.value = false
    setLoading(false)
  }
}

async function runUpdate() {
  if (updateDisabled.value) return
  busy.value = true
  setLoading(true)
  try {
    const r = await post<ApplyResult>('/api/agent-rules/update')
    toastApply(r, '已更新')
    await load()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '更新失败')
  } finally {
    busy.value = false
    setLoading(false)
  }
}

async function saveHub() {
  if (!canEdit.value || !dirty.value || busy.value) return
  busy.value = true
  try {
    const hub = await put<HubPayload>('/api/agent-rules/hub', { content: draft.value })
    applyHub(hub)
    message.success('已保存')
  } catch (e) {
    message.error(e instanceof Error ? e.message : '保存失败')
  } finally {
    busy.value = false
  }
}

function discardHub() {
  draft.value = snapshot.value
}

async function saveTpl() {
  if (!canEdit.value || !tplDirty.value || busy.value) return
  busy.value = true
  try {
    const tpl = await put<PointerPayload>('/api/agent-rules/pointer-template', { content: tplDraft.value })
    applyTpl(tpl)
    const st = await get<AgentRulesStatus>('/api/agent-rules/status')
    applyStatus(st, true)
    message.success('已保存')
  } catch (e) {
    message.error(e instanceof Error ? e.message : '保存失败')
  } finally {
    busy.value = false
  }
}

function discardTpl() {
  tplDraft.value = tplSnapshot.value
}

async function runResetTpl() {
  confirmKind.value = ''
  busy.value = true
  try {
    const tpl = await post<PointerPayload>('/api/agent-rules/pointer-template/reset')
    applyTpl(tpl)
    const st = await get<AgentRulesStatus>('/api/agent-rules/status')
    applyStatus(st, true)
    message.success('已恢复默认')
  } catch (e) {
    message.error(e instanceof Error ? e.message : '恢复失败')
  } finally {
    busy.value = false
  }
}

async function openHub() {
  if (readonly) return
  try {
    await post('/api/agent-rules/open-hub')
    ignoreFocusUntil = Date.now() + 800
  } catch (e) {
    message.error(e instanceof Error ? e.message : '打开失败')
  }
}

async function openTpl() {
  if (readonly) return
  try {
    await post('/api/agent-rules/open-pointer-template')
    ignoreFocusUntil = Date.now() + 800
    const [st, tpl] = await Promise.all([
      get<AgentRulesStatus>('/api/agent-rules/status'),
      get<PointerPayload>('/api/agent-rules/pointer-template'),
    ])
    applyStatus(st, true)
    if (!tplDirty.value) applyTpl(tpl)
  } catch (e) {
    message.error(e instanceof Error ? e.message : '打开失败')
  }
}

function canPreview(a: AgentRuleItem) {
  return !!a.rulePath && a.detected
    && a.status !== 'notDetected' && a.status !== 'missing' && a.status !== 'needsFirstLaunch'
}

async function openAgent(a: AgentRuleItem) {
  if (readonly || !canPreview(a)) return
  try {
    await post('/api/agent-rules/open-agent', { agentId: a.agentId })
    ignoreFocusUntil = Date.now() + 800
  } catch (e) {
    message.error(e instanceof Error ? e.message : '打开失败')
  }
}

async function browseLibrary() {
  if (readonly || busy.value) return
  try {
    const result = await post<{ path?: string; cancelled?: boolean }>('/api/settings/browse-folder', {
      initialPath: libDraft.value || libSaved.value,
    })
    if (!result.path) return
    libDraft.value = result.path
    askLibraryIfNeeded()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '选择目录失败')
  }
}

function askLibraryIfNeeded() {
  if (!libDirty.value) return
  pendingLib.value = libDraft.value
  confirmKind.value = 'move'
}

async function saveLibrary(move: boolean) {
  const path = pendingLib.value || libDraft.value
  confirmKind.value = ''
  busy.value = true
  setLoading(true)
  try {
    const r = await put<{ path: string; notes: string[] }>('/api/agent-rules/library', { path, move })
    libSaved.value = r.path
    libDraft.value = r.path
    if (r.notes?.length) message.warning(r.notes[0])
    else message.success('资料目录已保存')
    const [st, hub, tpl] = await Promise.all([
      get<AgentRulesStatus>('/api/agent-rules/status'),
      get<HubPayload>('/api/agent-rules/hub'),
      get<PointerPayload>('/api/agent-rules/pointer-template'),
    ])
    status.value = st
    if (!dirty.value) applyHub(hub)
    else hubPath.value = hub.path
    if (!tplDirty.value) applyTpl(tpl)
    else tplPath.value = tpl.path
  } catch (e) {
    message.error(e instanceof Error ? e.message : '保存资料目录失败')
    libDraft.value = libSaved.value
  } finally {
    busy.value = false
    setLoading(false)
  }
}

function onSwitch(on: boolean) {
  if (readonly || busy.value) return
  confirmKind.value = on ? 'enable' : 'disable'
}

function onConfirm() {
  if (confirmKind.value === 'enable') return void runEnable()
  if (confirmKind.value === 'disable') return void runDisable()
  if (confirmKind.value === 'move') return void saveLibrary(true)
  if (confirmKind.value === 'leave') {
    confirmKind.value = ''
    leaveResolve?.(true)
    leaveResolve = null
    return
  }
  if (confirmKind.value === 'reload') {
    confirmKind.value = ''
    void load()
  }
  if (confirmKind.value === 'resetTpl') return void runResetTpl()
}

function onAlt() {
  if (confirmKind.value === 'move') return void saveLibrary(false)
}

function onConfirmClose(show: boolean) {
  if (show) return
  if (confirmKind.value === 'move') libDraft.value = libSaved.value
  if (confirmKind.value === 'leave') {
    leaveResolve?.(false)
    leaveResolve = null
  }
  confirmKind.value = ''
}

onBeforeRouteLeave(() => {
  if (!pageDirty.value) return true
  confirmKind.value = 'leave'
  return new Promise<boolean>((resolve) => { leaveResolve = resolve })
})

function onBeforeUnload(e: BeforeUnloadEvent) {
  if (readonly || !pageDirty.value) return
  e.preventDefault()
  e.returnValue = ''
}

async function onWindowFocus() {
  if (Date.now() < ignoreFocusUntil) return
  if (!loaded.value || busy.value || confirmKind.value) return
  try {
    const [st, hub, tpl] = await Promise.all([
      get<AgentRulesStatus>('/api/agent-rules/status'),
      get<HubPayload>('/api/agent-rules/hub'),
      get<PointerPayload>('/api/agent-rules/pointer-template'),
    ])
    applyStatus(st, true)
    const hubChanged = hub.content !== snapshot.value
    const tplChanged = tpl.content !== tplSnapshot.value
    if ((dirty.value && hubChanged) || (tplDirty.value && tplChanged)) {
      confirmKind.value = 'reload'
      return
    }
    if (!dirty.value) applyHub(hub)
    if (!tplDirty.value) applyTpl(tpl)
  } catch { /* 回页读盘失败保持现状 */ }
}

usePageHotkeys({
  refresh: () => {
    if (pageDirty.value) {
      message.warning('有未保存的修改，先保存或放弃')
      return
    }
    void load()
  },
})

onMounted(async () => {
  await load()
  window.addEventListener('beforeunload', onBeforeUnload)
  window.addEventListener('focus', onWindowFocus)
})
onUnmounted(() => {
  window.removeEventListener('beforeunload', onBeforeUnload)
  window.removeEventListener('focus', onWindowFocus)
})
</script>

<template>
  <teleport defer to="#chrome-tabs">
    <div class="ah-seg" role="group" aria-label="编辑内容">
      <button type="button" :aria-pressed="pane === 'hub' ? 'true' : 'false'" @click="pane = 'hub'">共用规则</button>
      <button type="button" :aria-pressed="pane === 'tpl' ? 'true' : 'false'" @click="pane = 'tpl'">注入模板</button>
    </div>
  </teleport>
  <teleport defer to="#chrome-extra">
    <label class="chrome-sw">
      <span>统一管理</span>
      <n-switch
        :value="enabled"
        :disabled="readonly || busy"
        size="small"
        @update:value="onSwitch"
      />
    </label>
  </teleport>
  <teleport defer to="#chrome-actions">
    <n-button type="primary" :disabled="updateDisabled" :loading="busy" @click="runUpdate">
      <template #icon><n-icon><RefreshCw :size="16" :stroke-width="1.8" /></n-icon></template>
      更新
    </n-button>
  </teleport>

  <p v-if="readonly" class="banner">浏览器直连为只读，改规则请在 AgentHub 窗口内操作。</p>
  <p v-if="loadError" class="usage-error">读取失败：{{ loadError }}</p>

  <div v-else-if="loaded" class="rules">
    <div class="libbar">
      <span class="lbl">资料目录</span>
      <div class="path-control">
        <n-input
          :disabled="readonly || busy"
          :spellcheck="false"
          v-model:value="libDraft"
          @blur="askLibraryIfNeeded"
        />
        <n-button :disabled="readonly || busy" @click="browseLibrary">
          <template #icon><n-icon><FolderOpen :size="16" :stroke-width="1.8" /></n-icon></template>
          浏览
        </n-button>
      </div>
      <p class="hint">保存时建 Plans、SandBox。改位置会问要不要搬走。</p>
    </div>

    <div class="stage">
      <section class="card hub-card">
        <div class="card-head">
          {{ pane === 'hub' ? '这份规则' : '注入模板' }}
          <span class="hint">{{ pane === 'hub' ? '保存后各家都按这份' : '改各家指针正文和差异行' }}</span>
        </div>
        <div v-show="pane === 'hub'" class="card-body hub-body">
          <p class="file-path">{{ hubPath }}</p>
          <textarea
            class="editor"
            spellcheck="false"
            :disabled="!canEdit || busy"
            v-model="draft"
          />
          <div class="hub-acts">
            <n-button :disabled="readonly || busy" @click="openHub">
              <template #icon><n-icon><ExternalLink :size="16" :stroke-width="1.8" /></n-icon></template>
              打开
            </n-button>
            <n-button v-if="dirty" :disabled="readonly || busy" @click="discardHub">
              <template #icon><n-icon><X :size="16" :stroke-width="1.8" /></n-icon></template>
              放弃修改
            </n-button>
            <n-button v-if="dirty" type="primary" :disabled="!canEdit || busy" @click="saveHub">
              <template #icon><n-icon><Save :size="16" :stroke-width="1.8" /></n-icon></template>
              保存
            </n-button>
          </div>
        </div>
        <div v-show="pane === 'tpl'" class="card-body hub-body">
          <p class="file-path" :title="tplPath">{{ tplPath }}</p>
          <p class="tpl-state" :class="{ 'is-bad': pointerInvalid }">{{ pointerTemplateLabel }}</p>
          <p v-for="w in pointerWarnings" :key="w" class="tpl-warn">{{ w }}</p>
          <textarea
            class="editor"
            spellcheck="false"
            :disabled="!canEdit || busy"
            v-model="tplDraft"
          />
          <div class="hub-acts">
            <n-button v-if="tplExists" :disabled="!canEdit || busy" @click="confirmKind = 'resetTpl'">
              <template #icon><n-icon><RotateCcw :size="16" :stroke-width="1.8" /></n-icon></template>
              恢复默认
            </n-button>
            <n-button :disabled="readonly || busy" @click="openTpl">
              <template #icon><n-icon><ExternalLink :size="16" :stroke-width="1.8" /></n-icon></template>
              打开
            </n-button>
            <n-button v-if="tplDirty" :disabled="readonly || busy" @click="discardTpl">
              <template #icon><n-icon><X :size="16" :stroke-width="1.8" /></n-icon></template>
              放弃修改
            </n-button>
            <n-button v-if="tplDirty" type="primary" :disabled="!canEdit || busy" @click="saveTpl">
              <template #icon><n-icon><Save :size="16" :stroke-width="1.8" /></n-icon></template>
              保存
            </n-button>
          </div>
        </div>
      </section>

      <section class="card list-card">
        <div class="card-head">各家规则 <span class="hint">改模板后点更新才写入</span></div>
        <div class="card-body">
          <div
            v-for="a in status?.agents"
            :key="a.agentId"
            class="rule-line"
            :class="{ 'is-muted': !a.detected }"
          >
            <span class="rule-agent">
              <AgentMark :id="a.agentId" />
              {{ a.displayName }}
            </span>
            <span class="rule-path" :title="a.rulePath || a.message">{{ a.rulePath || a.message }}</span>
            <button
              type="button"
              class="icon-quiet"
              :disabled="readonly || !canPreview(a)"
              :title="a.rulePath ? '用默认编辑器打开对照' : '还没有这家的规则文件'"
              @click="openAgent(a)"
            >
              <n-icon :size="16" :stroke-width="1.8"><ExternalLink /></n-icon>
            </button>
            <span class="rule-state" :class="'is-' + a.status">{{ ruleStateText(a.status) }}</span>
          </div>
          <p v-if="!enabled" class="off-note">关掉后不能改各家</p>
        </div>
      </section>
    </div>
  </div>

  <AhConfirm
    :show="!!confirmKind"
    :text="confirmText"
    :ok-text="confirmOk"
    :alt-text="confirmKind === 'move' ? '只改路径' : undefined"
    :hide-cancel="confirmKind === 'move'"
    @update:show="onConfirmClose"
    @confirm="onConfirm"
    @alt="onAlt"
  />
</template>

<style scoped>
.banner {
  margin: 0 0 var(--sp-4);
  padding: var(--sp-3) var(--sp-4);
  color: var(--dim);
  background: var(--wash);
  border-radius: var(--r-in);
  font-size: var(--fs-small);
}
.usage-error {
  margin: 0;
  padding: var(--sp-3) var(--sp-4);
  color: var(--error-fg);
  background: var(--error-soft);
  border-radius: var(--r-in);
}
.chrome-sw {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-2);
  font-size: var(--fs-small);
}
.rules {
  display: flex;
  flex-direction: column;
  gap: var(--sp-4);
  width: 100%;
  min-width: 0;
  flex: 1 1 auto;
  min-height: 0;
}
.libbar {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: var(--sp-3);
  align-items: center;
  padding: 10px 14px;
  background: var(--surface);
  border: 1px solid var(--stroke);
  border-radius: var(--r-card);
}
.libbar .lbl { font-weight: 500; white-space: nowrap; }
.libbar .hint {
  grid-column: 1 / -1;
  margin: 0;
  font-size: var(--fs-caption);
  color: var(--faint);
}
.path-control {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: var(--sp-2);
  min-width: 0;
}
.stage {
  display: grid;
  grid-template-columns: minmax(0, 1.35fr) minmax(300px, 1fr);
  gap: var(--sp-5);
  align-items: stretch;
  flex: 1 1 auto;
  min-height: 0;
}
.hub-card,
.list-card {
  display: flex;
  flex-direction: column;
  min-height: 0;
  height: 100%;
}
.list-card .card-body {
  flex: 1 1 auto;
  min-height: 0;
  overflow: auto;
}
.ah-seg {
  display: inline-flex;
  align-items: center;
  height: var(--h-control);
  padding: 2px;
  background: var(--wash);
  border: 1px solid var(--stroke-strong);
  border-radius: var(--r-in);
}
.ah-seg button {
  height: 26px;
  min-width: 88px;
  padding: 0 var(--sp-4);
  border: 0;
  border-radius: var(--r-pill);
  background: transparent;
  color: var(--dim);
  font: inherit;
  font-size: var(--fs-small);
  cursor: pointer;
}
.ah-seg button:hover { color: var(--text); background: var(--seg-on-bg); }
.ah-seg button[aria-pressed='true'] {
  color: var(--accent-solid);
  background: var(--accent-soft);
  font-weight: 600;
}
.hub-body {
  display: flex;
  flex-direction: column;
  gap: var(--sp-3);
  flex: 1;
  min-height: 0;
}
.file-path {
  margin: 0;
  font-size: var(--fs-caption);
  color: var(--faint);
  font-family: var(--mono);
}
.tpl-state {
  margin: 0;
  font-size: var(--fs-caption);
  color: var(--faint);
}
.tpl-state.is-bad { color: var(--warn); }
.tpl-warn {
  margin: 0;
  font-size: var(--fs-caption);
  color: var(--warn);
}
.editor {
  flex: 1;
  width: 100%;
  min-height: 360px;
  resize: vertical;
  padding: 10px 12px;
  border: 1px solid var(--stroke);
  border-radius: var(--r-in);
  background: var(--bg);
  color: var(--text);
  font-family: var(--mono);
  font-size: var(--fs-small);
  line-height: 1.55;
}
.editor:disabled { color: var(--disabled-fg); }
.hub-acts { display: flex; justify-content: flex-end; gap: var(--sp-2); }
.rule-line {
  display: grid;
  grid-template-columns: 120px minmax(0, 1fr) var(--h-icon-btn) 64px;
  gap: var(--sp-2);
  align-items: center;
  min-height: var(--h-row);
  box-shadow: var(--rule-hi);
}
.icon-quiet {
  width: var(--h-icon-btn);
  height: var(--h-icon-btn);
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--dim);
  border-radius: var(--r-in);
  cursor: pointer;
  display: inline-grid;
  place-items: center;
}
.icon-quiet:hover:not(:disabled) { color: var(--text); background: var(--wash); }
.icon-quiet:disabled { color: var(--disabled-fg); cursor: not-allowed; }
.rule-line.is-muted { opacity: .55; }
.rule-agent { display: inline-flex; align-items: center; gap: var(--sp-2); min-width: 0; }
.rule-path {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--fs-caption);
  color: var(--faint);
  font-family: var(--mono);
}
.rule-state { justify-self: end; font-size: var(--fs-caption); color: var(--faint); }
.rule-state.is-current { color: var(--ok); }
.rule-state.is-needsSync,
.rule-state.is-missing,
.rule-state.is-busy { color: var(--warn); }
.rule-state.is-conflict { color: var(--error-fg); }
.off-note {
  margin: var(--sp-3) 0 0;
  font-size: var(--fs-caption);
  color: var(--faint);
}
@media (max-width: 1279px) {
  .stage { grid-template-columns: 1fr; }
  .libbar { grid-template-columns: 1fr; }
}
</style>
