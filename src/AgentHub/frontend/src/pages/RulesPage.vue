<script setup lang="ts">
import { computed, inject, onMounted, onUnmounted, ref, type Ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { NButton, NIcon, NInput, NSwitch, useMessage } from 'naive-ui'
import { ExternalLink, FolderOpen, RefreshCw, Save, X } from 'lucide-vue-next'
import AhConfirm from '../components/AhConfirm.vue'
import AgentMark from '../components/AgentMark.vue'
import { get, post, put, WRITABLE } from '../api'
import { CAN_PICK_FOLDER, pickFolder } from '../hostBridge'
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
interface SharedSource {
  exists: boolean
  valid: boolean
  willMigrate: boolean
  warnings: string[]
}
interface AgentRulesStatus {
  libraryRoot: string
  libraryRootExists: boolean
  sharedRulesPath: string
  agents: AgentRuleItem[]
  hasChanges: boolean
  enabled: boolean
  source: SharedSource
}
interface AgentChip {
  agentId: string
  displayName: string
}
interface StructuredHub {
  path: string
  exists: boolean
  enabled: boolean
  valid: boolean
  shared: string
  extras: Record<string, string>
  orphans: Record<string, string>
  agents: AgentChip[]
}
interface ApplyResult { ok: boolean; items: { ok: boolean; message: string }[] }

const loaded = ref(false)
const loadError = ref('')
const busy = ref(false)
const status = ref<AgentRulesStatus | null>(null)
const hubPath = ref('')
const sharedDraft = ref('')
const sharedSnapshot = ref('')
const extrasDraft = ref<Record<string, string>>({})
const extrasSnapshot = ref<Record<string, string>>({})
const orphansDraft = ref<Record<string, string>>({})
const orphansSnapshot = ref<Record<string, string>>({})
const chips = ref<AgentChip[]>([])
const selectedAgent = ref('')
const libDraft = ref('')
const libSaved = ref('')
const sharedMarkError = ref(false)
const extraMarkError = ref(false)
const confirmKind = ref<'enable' | 'disable' | 'move' | 'leave' | 'reload' | 'restore' | ''>('')
const pendingLib = ref('')
let leaveResolve: ((ok: boolean) => void) | null = null
let ignoreFocusUntil = 0

const EXTRA_MARK_RE = /<!--\s*extra:[a-z][a-z0-9]*\s*-->/i

const enabled = computed(() => !!status.value?.enabled)
const dirty = computed(() => {
  if (!loaded.value) return false
  if (sharedDraft.value !== sharedSnapshot.value) return true
  const ids = new Set([...Object.keys(extrasDraft.value), ...Object.keys(extrasSnapshot.value)])
  for (const id of ids) {
    if ((extrasDraft.value[id] ?? '') !== (extrasSnapshot.value[id] ?? '')) return true
  }
  const oids = new Set([...Object.keys(orphansDraft.value), ...Object.keys(orphansSnapshot.value)])
  for (const id of oids) {
    if ((orphansDraft.value[id] ?? '') !== (orphansSnapshot.value[id] ?? '')) return true
  }
  return false
})
const pageDirty = computed(() => dirty.value)
const libDirty = computed(() => loaded.value && normalizePath(libDraft.value) !== normalizePath(libSaved.value))
const canEdit = computed(() => enabled.value && !readonly)
const updateDisabled = computed(() =>
  readonly || !enabled.value || busy.value || dirty.value || !status.value?.hasChanges)
const sourceBad = computed(() => {
  const s = status.value?.source
  return !!s && s.exists && !s.valid && !s.willMigrate
})
const sourceWarnings = computed(() => status.value?.source?.warnings ?? [])
const orphanKeys = computed(() => Object.keys(orphansDraft.value))
const selectedChip = computed(() =>
  chips.value.find((c) => c.agentId === selectedAgent.value) || chips.value[0] || null)
const extraDraft = computed({
  get() {
    const id = selectedAgent.value
    return id ? (extrasDraft.value[id] ?? '') : ''
  },
  set(v: string) {
    const id = selectedAgent.value
    if (!id) return
    extrasDraft.value = { ...extrasDraft.value, [id]: v }
  },
})

const confirmText = computed(() => ({
  enable: '打开后会改各家规则，写成母本全文加自家差异块。\n\n没有共用规则就先建一份。各家文件整份覆盖，先备份。',
  disable: '关掉后删掉各家指向这份规则的内容。共用规则文件还在，各家不再自动读它。',
  move: '资料目录要改位置。把旧的 Plans、SandBox 搬过去吗？同名文件跳过，不覆盖。',
  leave: '有未保存的修改。确定离开吗？未保存的修改将丢失。',
  reload: '文件在外面改过。放弃这里的修改，按磁盘上的重读？',
  restore: '恢复后共用和各家差异都会清空（资料目录设置保留）。此操作可再编辑，但当前正文会丢。',
  '': '',
}[confirmKind.value]))

const confirmOk = computed(() => ({
  enable: '打开并更新',
  disable: '删掉指向',
  move: '搬过去',
  leave: '离开',
  reload: '重读',
  restore: '恢复空模板',
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

function cloneMap(m: Record<string, string> | null | undefined): Record<string, string> {
  const out: Record<string, string> = {}
  if (!m) return out
  for (const [k, v] of Object.entries(m)) out[k] = v ?? ''
  return out
}

function applyStructured(hub: StructuredHub) {
  hubPath.value = hub.path
  sharedDraft.value = hub.shared ?? ''
  sharedSnapshot.value = hub.shared ?? ''
  extrasDraft.value = cloneMap(hub.extras)
  extrasSnapshot.value = cloneMap(hub.extras)
  orphansDraft.value = cloneMap(hub.orphans)
  orphansSnapshot.value = cloneMap(hub.orphans)
  chips.value = hub.agents?.length ? hub.agents : []
  if (!selectedAgent.value || !chips.value.some((c) => c.agentId === selectedAgent.value)) {
    selectedAgent.value = chips.value[0]?.agentId || ''
  }
  sharedMarkError.value = false
  extraMarkError.value = false
}

function applyStatus(s: AgentRulesStatus, keepLibDraft = false) {
  const keep = keepLibDraft && libDirty.value
  status.value = s
  libSaved.value = s.libraryRoot
  if (!keep) libDraft.value = s.libraryRoot
}

function hasExtraContent(id: string) {
  return !!(extrasDraft.value[id] ?? '').trim()
}

function selectAgent(id: string) {
  selectedAgent.value = id
  extraMarkError.value = false
}

async function load() {
  loadError.value = ''
  setLoading(true)
  try {
    const [st, hub] = await Promise.all([
      get<AgentRulesStatus>('/api/agent-rules/status'),
      get<StructuredHub>('/api/agent-rules/hub-structured'),
    ])
    applyStatus(st)
    applyStructured(hub)
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

function validateMarks(): boolean {
  sharedMarkError.value = EXTRA_MARK_RE.test(sharedDraft.value)
  extraMarkError.value = false
  for (const body of Object.values(extrasDraft.value)) {
    if (EXTRA_MARK_RE.test(body)) {
      extraMarkError.value = true
      break
    }
  }
  if (!extraMarkError.value) {
    for (const body of Object.values(orphansDraft.value)) {
      if (EXTRA_MARK_RE.test(body)) {
        extraMarkError.value = true
        break
      }
    }
  }
  if (sharedMarkError.value || extraMarkError.value) {
    message.error('请删掉 <!-- extra:… -->，用下方芯片编辑各家差异')
    return false
  }
  return true
}

async function saveHub() {
  if (!canEdit.value || !dirty.value || busy.value) return
  if (!validateMarks()) return
  busy.value = true
  try {
    const hub = await put<StructuredHub>('/api/agent-rules/hub-structured', {
      shared: sharedDraft.value,
      extras: extrasDraft.value,
      orphans: orphansDraft.value,
    })
    applyStructured(hub)
    const st = await get<AgentRulesStatus>('/api/agent-rules/status')
    applyStatus(st, true)
    message.success('母本已保存，请到右侧更新各家')
  } catch (e) {
    message.error(e instanceof Error ? e.message : '保存失败')
  } finally {
    busy.value = false
  }
}

function discardHub() {
  sharedDraft.value = sharedSnapshot.value
  extrasDraft.value = cloneMap(extrasSnapshot.value)
  orphansDraft.value = cloneMap(orphansSnapshot.value)
  sharedMarkError.value = false
  extraMarkError.value = false
}

async function restoreEmpty() {
  confirmKind.value = ''
  if (!canEdit.value || busy.value) return
  busy.value = true
  setLoading(true)
  try {
    const hub = await post<StructuredHub>('/api/agent-rules/hub-restore-empty')
    applyStructured(hub)
    const st = await get<AgentRulesStatus>('/api/agent-rules/status')
    applyStatus(st, true)
    message.success('已恢复空模板，请到右侧更新各家')
  } catch (e) {
    message.error(e instanceof Error ? e.message : '恢复失败')
  } finally {
    busy.value = false
    setLoading(false)
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
  if (readonly || busy.value || !CAN_PICK_FOLDER) return
  try {
    const path = await pickFolder(libDraft.value || libSaved.value)
    if (!path) return
    libDraft.value = path
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
    const [st, hub] = await Promise.all([
      get<AgentRulesStatus>('/api/agent-rules/status'),
      get<StructuredHub>('/api/agent-rules/hub-structured'),
    ])
    status.value = st
    if (!dirty.value) applyStructured(hub)
    else hubPath.value = hub.path
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
  if (confirmKind.value === 'restore') return void restoreEmpty()
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
    const [st, hub] = await Promise.all([
      get<AgentRulesStatus>('/api/agent-rules/status'),
      get<StructuredHub>('/api/agent-rules/hub-structured'),
    ])
    applyStatus(st, true)
    const hubChanged =
      (hub.shared ?? '') !== sharedSnapshot.value
      || Object.keys({ ...hub.extras, ...extrasSnapshot.value }).some(
        (k) => (hub.extras?.[k] ?? '') !== (extrasSnapshot.value[k] ?? ''),
      )
      || Object.keys({ ...hub.orphans, ...orphansSnapshot.value }).some(
        (k) => (hub.orphans?.[k] ?? '') !== (orphansSnapshot.value[k] ?? ''),
      )
    if (dirty.value && hubChanged) {
      confirmKind.value = 'reload'
      return
    }
    if (!dirty.value) applyStructured(hub)
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
    <n-button
      type="primary"
      :disabled="updateDisabled"
      :loading="busy"
      :title="dirty ? '母本未保存，请先保存再更新' : undefined"
      @click="runUpdate"
    >
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
        <n-button :disabled="readonly || busy || !CAN_PICK_FOLDER" @click="browseLibrary">
          <template #icon><n-icon><FolderOpen :size="16" :stroke-width="1.8" /></n-icon></template>
          浏览
        </n-button>
      </div>
      <p class="hint">保存时建 Plans、SandBox。改位置会问要不要搬走。</p>
    </div>

    <div class="stage">
      <section class="card hub-card">
        <div class="card-head">
          这份规则
          <span class="hint">共享正文注入各家；下面按 Agent 写差异。保存母本后，再点右侧「更新」。</span>
        </div>
        <div class="card-body hub-body">
          <p class="file-path">{{ hubPath }}</p>
          <p v-if="status?.source?.willMigrate" class="src-warn">
            母本是旧版格式，点「更新」迁移（正文保留，管理说明收进注释头、不再注入各家）
          </p>
          <p v-else-if="sourceBad" class="src-warn is-bad">母本无效（缺管理注释头），各家按内置种子渲染</p>
          <p v-for="w in sourceWarnings" :key="w" class="src-warn">{{ w }}</p>
          <p v-if="orphanKeys.length" class="src-warn">
            母本含未识别差异块（{{ orphanKeys.join('、') }}），已保留，保存时写回
          </p>

          <div class="editor-block">
            <div class="editor-label">共用</div>
            <textarea
              class="editor shared-editor"
              :class="{ 'is-error': sharedMarkError }"
              spellcheck="false"
              :disabled="!canEdit || busy"
              placeholder="写各家都会遵守的规则。不用写 <!-- extra --> 或管理注释。"
              v-model="sharedDraft"
              @input="sharedMarkError = false"
            />
          </div>

          <div class="editor-block extras-block">
            <div class="editor-label">各家差异</div>
            <div class="chip-row">
              <button
                v-for="c in chips"
                :key="c.agentId"
                type="button"
                class="agent-chip agent-chip--icon"
                :class="{ 'is-active': c.agentId === selectedAgent }"
                :title="c.displayName"
                :aria-label="c.displayName"
                :disabled="!canEdit || busy"
                @click="selectAgent(c.agentId)"
              >
                <AgentMark :id="c.agentId" />
                
                <span v-if="hasExtraContent(c.agentId)" class="chip-dot" title="有差异" />
              </button>
            </div>
            <div class="editor-label sub">
              {{ selectedChip ? selectedChip.displayName : '选中 Agent' }} 的附加条款
            </div>
            <textarea
              class="editor extra-editor"
              :class="{ 'is-error': extraMarkError }"
              spellcheck="false"
              :disabled="!canEdit || busy || !selectedAgent"
              placeholder="留空表示这家没有额外条款"
              v-model="extraDraft"
              @input="extraMarkError = false"
            />
          </div>

          <div class="hub-acts">
            <n-button :disabled="readonly || busy" @click="openHub">
              <template #icon><n-icon><ExternalLink :size="16" :stroke-width="1.8" /></n-icon></template>
              打开
            </n-button>
            <n-button
              :disabled="!canEdit || busy"
              @click="confirmKind = 'restore'"
            >
              恢复空模板…
            </n-button>
            <n-button v-if="dirty" :disabled="readonly || busy" @click="discardHub">
              <template #icon><n-icon><X :size="16" :stroke-width="1.8" /></n-icon></template>
              放弃修改
            </n-button>
            <n-button type="primary" :disabled="!canEdit || busy || !dirty" @click="saveHub">
              <template #icon><n-icon><Save :size="16" :stroke-width="1.8" /></n-icon></template>
              保存母本
            </n-button>
          </div>
        </div>
      </section>

      <section class="card list-card">
        <div class="card-head">各家规则 <span class="hint">先保存母本，再点更新才写入</span></div>
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
          <p v-else-if="dirty" class="off-note">母本未保存，更新已禁用</p>
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
.src-warn {
  margin: 0;
  font-size: var(--fs-caption);
  color: var(--warn);
}
.src-warn.is-bad { color: var(--error-fg); }
.editor-block {
  display: flex;
  flex-direction: column;
  gap: var(--sp-2);
  min-height: 0;
}
.editor-block:first-of-type { flex: 1 1 auto; }
.editor-block.extras-block { flex: 0 0 auto; }
.editor-label {
  font-size: var(--fs-small);
  font-weight: 500;
  color: var(--dim);
}
.editor-label.sub {
  font-weight: 400;
  color: var(--faint);
}
.editor {
  width: 100%;
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
.shared-editor {
  flex: 1;
  min-height: 200px;
}
.extra-editor {
  min-height: 120px;
}
.editor:disabled { color: var(--disabled-fg); }
.editor.is-error {
  border-color: var(--error-fg);
}
.chip-row {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-2);
}
.agent-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: var(--h-icon-btn);
  padding: 0 10px;
  border: 1px solid var(--stroke);
  border-radius: 999px;
  background: var(--surface);
  color: var(--dim);
  font-size: var(--fs-caption);
  cursor: pointer;
  position: relative;
}
.agent-chip--icon {
  width: var(--h-icon-btn);
  padding: 0;
  justify-content: center;
  gap: 0;
}
.agent-chip--icon .chip-dot {
  position: absolute;
  top: 2px;
  right: 2px;
}
.agent-chip:hover:not(:disabled) {
  color: var(--text);
  background: var(--wash);
}
.agent-chip.is-active {
  color: var(--text);
  border-color: var(--primary, var(--ok));
  background: var(--wash);
}
.agent-chip:disabled {
  color: var(--disabled-fg);
  cursor: not-allowed;
}
.chip-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--ok);
  flex: none;
}
.hub-acts { display: flex; justify-content: flex-end; gap: var(--sp-2); flex-wrap: wrap; }
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