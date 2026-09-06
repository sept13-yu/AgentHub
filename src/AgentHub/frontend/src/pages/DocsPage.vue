<script setup lang="ts">
import { computed, inject, onMounted, onUnmounted, ref, watch, type Ref } from 'vue'
import { NButton, NIcon, NInput, NSwitch, useMessage } from 'naive-ui'
import { Archive, ArrowRightLeft, ChevronDown, CloudDownload, Eraser, ExternalLink, FolderOpen, RefreshCw } from 'lucide-vue-next'
import { get, post, WRITABLE } from '../api'
import { agentName } from '../agentMeta'
import AgentMark from '../components/AgentMark.vue'
import { formatMonthDay, formatWhen } from '../format'
import AhConfirm from '../components/AhConfirm.vue'

const message = useMessage()
const pageLoading = inject<Ref<boolean>>('page-loading')
const readonly = !WRITABLE

type Kind = 'skills' | 'library'
interface SkillItem {
  kind: 'skill'
  name: string
  displayName: string
  description: string | null
  path: string
  relPath: string
  sizeBytes: number
  modifiedUtc: string
  enabled: boolean
  conflict?: boolean
  state: 'enabled' | 'disabled' | 'external' | 'modified' | 'legacyLink' | 'conflict'
  canEnable: boolean
  canDisable: boolean
  canManage: boolean
  canUpdate: boolean
}
interface LibItem {
  kind: 'library'
  name: string
  path: string
  relPath: string
  sizeBytes: number
  modifiedUtc: string
  agentId: string | null
  project: string
}
interface DocsPayload {
  skillsRoot: string
  skillsStore: string
  libraryRoot: string
  skillsRootExists: boolean
  skillsStoreExists: boolean
  libraryRootExists: boolean
  skillsHint: string | null
  libraryHint: string | null
  skillsCli: { available: boolean; message: string }
  updateableCount?: number
  skillsUpdate?: SkillsUpdate
  skills: SkillItem[]
  library: LibItem[]
}
interface SkillsUpdate {
  running: boolean
  total: number
  index: number
  ok: number
  failed: number
  skipped?: number
  currentName: string | null
  detail: string | null
  errors: string[]
}
interface LegacyStatus { linkCount: number; storeCount: number; canClean: boolean; errors: string[] }

const kind = ref<Kind>('skills')
const q = ref('')
const data = ref<DocsPayload | null>(null)
const picked = ref<SkillItem | LibItem | null>(null)
const preview = ref<{ content: string; path: string } | null>(null)
const folded = ref<Set<string>>(new Set())
const legacy = ref<LegacyStatus | null>(null)
const confirmShow = ref(false)
const confirmText = ref('')
let confirmAction: (() => Promise<void>) | null = null

const skills = computed(() => data.value?.skills ?? [])
const library = computed(() => data.value?.library ?? [])
const onSkills = computed(() => skills.value.filter((s) => ['enabled', 'modified', 'legacyLink'].includes(s.state)))
const conflictSkills = computed(() => skills.value.filter((s) => s.conflict))
// 单块展示：启用的排前，归档的垫底（各自内部保持后端原序）
const sortedSkills = computed(() =>
  [...skills.value].sort((a, b) => Number(b.enabled) - Number(a.enabled)),
)
const toggling = ref(new Set<string>())
const progress = ref<SkillsUpdate | null>(null)
const updateableCount = computed(() => data.value?.updateableCount ?? 0)
const updateRunning = computed(() => !!progress.value?.running)
let pollTimer = 0
let sawRunning = false
const groups = computed(() => {
  const map = new Map<string, LibItem[]>()
  for (const item of library.value) {
    const key = item.project || '其他'
    if (!map.has(key)) map.set(key, [])
    map.get(key)!.push(item)
  }
  const names = [...map.keys()].filter((n) => n !== '其他').sort((a, b) => a.localeCompare(b, 'zh'))
  if (map.has('其他')) names.push('其他')
  return names.map((name) => ({
    name,
    items: (map.get(name) ?? []).slice().sort((a, b) => b.modifiedUtc.localeCompare(a.modifiedUtc)),
  }))
})

function setLoading(on: boolean) {
  if (pageLoading) pageLoading.value = on
}

async function refresh() {
  await load()
  if (!data.value) return
  if (kind.value === 'skills')
    message.success(`技能 ${skills.value.length} 个，使用中 ${onSkills.value.length}`)
  else
    message.success(library.value.length ? `方案 ${library.value.length} 篇` : '没有方案')
}

async function load() {
  setLoading(true)
  try {
    const params = new URLSearchParams({ kind: kind.value })
    if (q.value.trim()) params.set('q', q.value.trim())
    data.value = await get<DocsPayload>(`/api/docs?${params}`)
    if (picked.value) {
      const still = kind.value === 'skills'
        ? skills.value.find((s) => s.relPath === picked.value!.relPath)
        : library.value.find((s) => s.path === picked.value!.path)
      if (!still) {
        picked.value = null
        preview.value = null
      } else if (still !== picked.value) {
        // 换成新列表里的对象，保证 enabled 等字段跟手（开关切换后预览状态同步）
        picked.value = still
      }
    }
    if (kind.value === 'skills') {
      legacy.value = await get<LegacyStatus>('/api/docs/skills/legacy')
      if (data.value?.skillsUpdate) progress.value = data.value.skillsUpdate
      if (data.value?.skillsUpdate?.running) startPoll()
    }
  } catch (e) {
    message.error(e instanceof Error ? e.message : '读取失败')
  } finally {
    setLoading(false)
  }
}

async function openPreview(item: SkillItem | LibItem) {
  picked.value = item
  try {
    const r = await get<{ content: string; path: string }>(`/api/docs/preview?path=${encodeURIComponent(item.path)}`)
    preview.value = r
  } catch (e) {
    preview.value = null
    message.error(e instanceof Error ? e.message : '预览失败')
  }
}

async function openFile() {
  if (readonly || !picked.value) return
  try {
    await post('/api/docs/open', { path: picked.value.path })
  } catch (e) {
    message.error(e instanceof Error ? e.message : '打开失败')
  }
}

async function toggleSkill(skill: SkillItem, on: boolean) {
  if (readonly || skill.enabled === on || toggling.value.has(skill.relPath)) return
  toggling.value.add(skill.relPath)
  try {
    await post(on ? '/api/docs/skills/enable' : '/api/docs/skills/disable', { name: skill.relPath })
    message.success(on ? '已启用' : '已停用')
    await load()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '操作失败')
  } finally {
    toggling.value.delete(skill.relPath)
  }
}

function stopPoll() {
  if (pollTimer) {
    window.clearInterval(pollTimer)
    pollTimer = 0
  }
}

function toastFinish(p: SkillsUpdate) {
  if (p.failed && p.errors[0]) message.error(p.errors[0])
  else if (p.ok) message.success(`已更新 ${p.ok} 个${p.skipped ? `，跳过 ${p.skipped} 个已是最新` : ''}`)
  else if ((p.skipped ?? 0) > 0 || p.total > 0) message.success('都已是最新')
  else message.success('没有需要检查的 Skill')
}

function startPoll() {
  if (pollTimer) return
  pollTimer = window.setInterval(() => { void tickProgress() }, 1000)
}

async function tickProgress() {
  try {
    const p = await get<SkillsUpdate>('/api/docs/skills/update-progress')
    progress.value = p
    if (p.running) sawRunning = true
    else if (sawRunning) {
      sawRunning = false
      stopPoll()
      toastFinish(p)
      await load()
    }
  } catch { /* 下一秒再问 */ }
}

async function beginUpdate(names?: string[]) {
  if (readonly || kind.value !== 'skills' || updateRunning.value) return
  progress.value = {
    running: true,
    total: names?.length ?? updateableCount.value,
    index: 0,
    ok: 0,
    failed: 0,
    currentName: names?.[0] ?? null,
    detail: '开始更新…',
    errors: [],
  }
  sawRunning = true
  startPoll()
  try {
    const r = await post<{
      updated: number
      skipped: number
      errors?: string[]
      alreadyRunning?: boolean
      progress?: SkillsUpdate
    }>('/api/docs/skills/update', names ? { names } : {})
    if (r.progress) progress.value = r.progress
    if (r.alreadyRunning) return
    sawRunning = false
    stopPoll()
    if (r.errors?.length) message.error(r.errors[0])
    else if (r.updated > 0) message.success(`已更新 ${r.updated} 个${r.skipped ? `，跳过 ${r.skipped} 个已是最新` : ''}`)
    else if (r.skipped > 0) message.success('都已是最新')
    else message.success('没有需要检查的 Skill')
    await load()
  } catch (e) {
    sawRunning = false
    stopPoll()
    if (progress.value) progress.value = { ...progress.value, running: false }
    message.error(e instanceof Error ? e.message : '更新失败')
  }
}

async function updateSkills() {
  await beginUpdate()
}

async function skillAction(path: string, body: unknown, success: string) {
  if (readonly || !picked.value || picked.value.kind !== 'skill') return
  const skill = picked.value as SkillItem
  toggling.value.add(skill.relPath)
  try {
    await post(path, body)
    message.success(success)
    await load()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '操作失败')
  } finally {
    toggling.value.delete(skill.relPath)
  }
}

function manageSkill(skill: SkillItem) {
  return skillAction('/api/docs/skills/manage', { name: skill.relPath }, '已收进仓库')
}

function updateOne(skill: SkillItem) {
  return beginUpdate([skill.relPath])
}

function resolveSkill(skill: SkillItem, action: 'keepLocalAsStore' | 'restoreFromStore') {
  askConfirm(
    action === 'keepLocalAsStore'
      ? '将用当前启用副本覆盖持久仓，并备份旧仓库版本。'
      : '将用持久仓覆盖当前启用副本，并备份本地版本。',
    () => skillAction('/api/docs/skills/resolve', { name: skill.relPath, action },
      action === 'keepLocalAsStore' ? '已保留本地版本' : '已恢复仓库版本'),
  )
}

function askConfirm(text: string, action: () => Promise<void>) {
  confirmText.value = text
  confirmAction = action
  confirmShow.value = true
}

async function runConfirmed() {
  confirmShow.value = false
  const action = confirmAction
  confirmAction = null
  if (action) await action()
}

function migrateLegacy() {
  askConfirm('将把旧 All-Skills 内容复制到 AgentHub 持久仓，并把启用联接替换为真实目录。旧仓不会自动删除。', migrateLegacyNow)
}

async function migrateLegacyNow() {
  if (readonly) return
  setLoading(true)
  try {
    const r = await post<{ updated: number; errors: string[] }>('/api/docs/skills/legacy/migrate')
    if (r.errors.length) message.warning(r.errors[0])
    else message.success(`已迁移 ${r.updated} 个启用 Skill`)
    await load()
  } catch (e) { message.error(e instanceof Error ? e.message : '迁移失败') }
  finally { setLoading(false) }
}

function cleanLegacy() {
  askConfirm('只会删除已与新持久仓逐项校验一致、且不再被引用的旧 Skill 副本。', cleanLegacyNow)
}

async function cleanLegacyNow() {
  if (readonly) return
  try {
    await post('/api/docs/skills/legacy/clean')
    message.success('旧 Skill 仓已清理')
    await load()
  } catch (e) { message.error(e instanceof Error ? e.message : '清理失败') }
}

async function openRoot(rootKind: 'active' | 'store' | 'library') {
  if (readonly) return
  try { await post('/api/docs/open-root', { kind: rootKind }) }
  catch (e) { message.error(e instanceof Error ? e.message : '打开目录失败') }
}

function skillStateText(state: SkillItem['state']): string {
  return ({
    enabled: '已启用', disabled: '未启用', external: '本机已有', modified: '本地有修改',
    legacyLink: '旧联接', conflict: '冲突',
  })[state]
}

function skillCardTag(state: SkillItem['state']): string {
  return state === 'enabled' || state === 'disabled' ? '' : skillStateText(state)
}

function toggleFold(name: string) {
  const next = new Set(folded.value)
  if (next.has(name)) next.delete(name)
  else next.add(name)
  folded.value = next
}

function pickKind(id: Kind) {
  kind.value = id
  picked.value = null
  preview.value = null
}

watch(kind, () => { void load() })
let qTimer = 0
watch(q, () => {
  window.clearTimeout(qTimer)
  qTimer = window.setTimeout(() => { void load() }, 280)
})

onMounted(() => { void load() })
onUnmounted(() => { stopPoll() })
</script>

<template>
  <teleport defer to="#chrome-tabs">
    <div class="ah-tabs" role="group" aria-label="资料">
      <button type="button" :aria-pressed="kind === 'skills' ? 'true' : 'false'" @click="pickKind('skills')">技能</button>
      <button type="button" :aria-pressed="kind === 'library' ? 'true' : 'false'" @click="pickKind('library')">方案</button>
    </div>
  </teleport>
  <teleport defer to="#chrome-actions">
    <n-input v-model:value="q" class="docs-search" placeholder="搜索名称" clearable />
    <n-button
      v-if="kind === 'skills'"
      :disabled="readonly || !data?.skillsCli.available || updateRunning || updateableCount === 0"
      :loading="updateRunning"
      @click="updateSkills"
    >
      <template #icon><n-icon><CloudDownload :size="16" :stroke-width="1.8" /></n-icon></template>
      <template v-if="updateRunning && progress">检查中 {{ progress.index }}/{{ progress.total }}</template>
      <template v-else>检查更新</template>
    </n-button>
    <n-button type="primary" @click="refresh">
      <template #icon><n-icon><RefreshCw :size="16" :stroke-width="1.8" /></n-icon></template>
      刷新
    </n-button>
  </teleport>

  <div class="card docs is-split">
    <div class="docs-split">
      <div class="docs-list">
        <template v-if="kind === 'skills'">
          <div v-if="legacy?.linkCount" class="legacy-banner">
            <span>检测到 {{ legacy.linkCount }} 个旧联接，迁移后会改为真实目录副本。</span>
            <n-button size="small" type="primary" :disabled="readonly" @click="migrateLegacy">
              <template #icon><n-icon><ArrowRightLeft :size="16" :stroke-width="1.8" /></n-icon></template>
              迁移
            </n-button>
          </div>
          <div v-else-if="legacy?.canClean" class="legacy-banner">
            <span>旧 All-Skills 已无引用，可以清理已核验的重复副本。</span>
            <n-button size="small" :disabled="readonly" @click="cleanLegacy">
              <template #icon><n-icon><Eraser :size="16" :stroke-width="1.8" /></n-icon></template>
              清理旧仓
            </n-button>
          </div>
          <p v-if="data && !data.skillsRootExists" class="docs-empty">技能目录不存在</p>
          <template v-else-if="skills.length">
            <p v-if="data?.skillsHint" class="hint">{{ data.skillsHint }}</p>
            <p v-if="conflictSkills.length" class="hint">{{ conflictSkills.length }} 个 Skill 存在冲突，程序不会自动覆盖</p>
            <div v-if="progress && (progress.running || progress.detail)" class="legacy-banner">
              <span v-if="progress.running">
                {{ progress.currentName ? `正在更新 ${progress.currentName}` : '正在对照远端' }}
                （{{ progress.index }}/{{ progress.total }}，已更新 {{ progress.ok }}<template v-if="progress.skipped">，跳过 {{ progress.skipped }}</template><template v-if="progress.failed">，失败 {{ progress.failed }}</template>）
                <template v-if="progress.detail"> · {{ progress.detail }}</template>
              </span>
              <span v-else>
                {{ progress.detail || `上次更新 ${progress.ok}/${progress.total}` }}
              </span>
            </div>
            <section class="doc-sec">
              <h3>
                全部 <span class="n">{{ skills.length }}</span>
                <span class="doc-on-note">使用中 {{ onSkills.length }}</span>
                <span v-if="updateRunning && progress" class="doc-on-note">检查中 {{ progress.index }}/{{ progress.total }}</span>
              </h3>
              <div class="docs-grid">
                <div v-for="s in sortedSkills" :key="s.path" class="doc-cell">
                  <button
                    type="button"
                    class="doc-card"
                    :class="{
                      'is-off': !s.enabled,
                      'is-on': picked && picked.path === s.path,
                      'is-updating': updateRunning && progress?.currentName === s.name,
                    }"
                    @click="openPreview(s)"
                  >
                      <span class="doc-card-top">
                        <i class="doc-pip" />
                        <b>{{ s.displayName || s.name }}</b>
                        <span v-if="skillCardTag(s.state)" class="doc-tag">{{ skillCardTag(s.state) }}</span>
                      </span>
                    <p>{{ s.description || ' ' }}</p>
                    <time>{{ formatMonthDay(s.modifiedUtc) }}</time>
                  </button>
                  <n-switch
                    class="doc-switch"
                    size="small"
                    :value="['enabled', 'modified', 'legacyLink'].includes(s.state)"
                    :disabled="readonly || toggling.has(s.relPath) || (!s.canEnable && !s.canDisable)"
                    :aria-label="(s.canDisable ? '停用 ' : '启用 ') + s.name"
                    @update:value="(on: boolean) => toggleSkill(s, on)"
                  />
                </div>
              </div>
            </section>
          </template>
          <p v-else class="docs-empty">没有技能</p>
        </template>
        <template v-else>
          <p v-if="data && !data.libraryRootExists" class="docs-empty">资料目录不存在</p>
          <template v-else-if="library.length">
            <p v-if="data?.libraryHint" class="hint">{{ data.libraryHint }}</p>
            <table class="docs-table">
              <colgroup>
                <col class="docs-col-name" /><col class="docs-col-when" />
              </colgroup>
              <thead><tr><th>名称</th><th>改过</th></tr></thead>
              <tbody v-for="g in groups" :key="g.name" :class="{ 'is-fold': folded.has(g.name) }">
                <tr class="doc-ghead">
                  <td colspan="2">
                    <button type="button" class="doc-gbtn" @click="toggleFold(g.name)">
                      <ChevronDown class="ico" :size="14" :stroke-width="1.8" />
                      {{ g.name }} <span class="n">{{ g.items.length }}</span>
                    </button>
                  </td>
                </tr>
                <tr
                  v-for="item in g.items"
                  :key="item.path"
                  data-plan
                  :class="{ 'is-on': picked && picked.path === item.path }"
                  @click="openPreview(item)"
                >
                  <td>
                    <span class="docs-name">
                      <AgentMark v-if="item.agentId" :id="item.agentId" />
                      <b>{{ item.name }}</b>
                    </span>
                  </td>
                  <td class="docs-when">{{ formatMonthDay(item.modifiedUtc) }}</td>
                </tr>
              </tbody>
            </table>
          </template>
          <p v-else class="docs-empty">没有方案</p>
        </template>
      </div>
      <div class="docs-preview">
        <template v-if="picked && preview">
          <h3>{{ picked.name }}</h3>
          <div class="docs-meta">
            <span v-if="picked.kind === 'skill'">
              用户级 Skill · {{ skillStateText((picked as SkillItem).state) }}
            </span>
            <span v-else class="docs-name">
              <AgentMark v-if="(picked as LibItem).agentId" :id="(picked as LibItem).agentId!" />
              {{ (picked as LibItem).agentId ? agentName((picked as LibItem).agentId!) : '方案' }}
              · {{ (picked as LibItem).project }}
            </span>
            <span>{{ picked.path }}</span>
            <span>{{ formatWhen(picked.modifiedUtc) }}</span>
            <span v-if="picked.kind === 'skill' && (picked as SkillItem).description">{{ (picked as SkillItem).description }}</span>
            <span v-if="picked.kind === 'skill' && (picked as SkillItem).conflict">目录冲突，程序未修改任何一侧</span>
          </div>
          <div v-if="picked.kind === 'skill' && !readonly" class="skill-actions">
            <n-button v-if="(picked as SkillItem).canManage" type="primary" @click="manageSkill(picked as SkillItem)">
              <template #icon><n-icon><Archive :size="16" :stroke-width="1.8" /></n-icon></template>
              收进仓库
            </n-button>
            <n-button
              v-if="(picked as SkillItem).canUpdate"
              type="primary"
              :disabled="updateRunning"
              :loading="updateRunning && progress?.currentName === (picked as SkillItem).name"
              @click="updateOne(picked as SkillItem)"
            >
              <template #icon><n-icon><CloudDownload :size="16" :stroke-width="1.8" /></n-icon></template>
              {{ updateRunning && progress?.currentName === (picked as SkillItem).name ? '检查中' : '检查更新' }}
            </n-button>
            <template v-if="(picked as SkillItem).state === 'modified'">
              <n-button @click="resolveSkill(picked as SkillItem, 'keepLocalAsStore')">保留本地版本</n-button>
              <n-button @click="resolveSkill(picked as SkillItem, 'restoreFromStore')">恢复仓库版本</n-button>
            </template>
          </div>
          <n-button v-if="!readonly" class="docs-open" @click="openFile">
            <template #icon><n-icon><ExternalLink :size="16" :stroke-width="1.8" /></n-icon></template>
            打开
          </n-button>
          <pre class="docs-body">{{ preview.content }}</pre>
        </template>
        <p v-else class="docs-empty">{{ kind === 'skills' ? '点一张看正文' : '点一行看正文' }}</p>
      </div>
    </div>
    <div v-if="data" class="docs-roots">
      <button type="button" :disabled="readonly" :title="data.skillsRoot" @click="openRoot('active')"><FolderOpen :size="14" />{{ data.skillsRoot }}</button>
      <button type="button" :disabled="readonly" :title="data.skillsStore" @click="openRoot('store')"><FolderOpen :size="14" />{{ data.skillsStore }}</button>
      <span>{{ data.skillsCli.message }}</span>
    </div>
  </div>
  <AhConfirm
    :show="confirmShow"
    :text="confirmText"
    @update:show="confirmShow = $event"
    @confirm="runConfirmed"
  />
</template>

<style scoped>
.docs-search { width: 200px; }
.legacy-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-3);
  margin-bottom: var(--sp-4);
  padding: var(--sp-3);
  border: 1px solid var(--accent-line);
  border-radius: var(--r-in);
  background: var(--accent-soft);
  color: var(--dim);
  font-size: var(--fs-small);
}
.docs.is-split {
  overflow: hidden;
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.docs-split {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(280px, 36%);
  flex: 1;
  min-height: 0;
}
.docs-list {
  min-width: 0;
  min-height: 0;
  overflow: auto;
  padding: var(--sp-4);
  background: var(--bg-sunken);
  border-right: 1px solid var(--stroke);
  border-radius: var(--r-card) 0 0 var(--r-card);
}
.docs-preview {
  min-width: 0;
  min-height: 0;
  overflow: auto;
  padding: var(--sp-4);
  display: flex;
  flex-direction: column;
  gap: var(--sp-3);
}
.docs-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: var(--sp-3);
}
.doc-card {
  width: 100%;
  text-align: left;
  border: 1px solid var(--stroke);
  border-radius: var(--r-card);
  background: var(--surface);
  padding: var(--sp-3) var(--sp-4);
  cursor: pointer;
  min-height: 96px;
  display: flex; flex-direction: column; gap: 6px;
  color: inherit; font: inherit;
}
.doc-card:hover { border-color: var(--stroke-strong); }
.doc-card.is-off { background: var(--surface-hi); }
.doc-card.is-on {
  border-color: var(--accent-line);
  background: var(--accent-soft);
}
.doc-card.is-updating { border-color: var(--accent-line); }
.doc-card-top { display: flex; align-items: center; gap: 8px; min-width: 0; padding-right: 44px; line-height: var(--h-control); }
.doc-tag { font-size: var(--fs-caption); color: var(--warn); flex: none; line-height: 1; }
.doc-cell { position: relative; min-width: 0; }
.doc-switch {
  position: absolute;
  top: 10px;
  right: 10px;
  z-index: 1;
}
.doc-on-note { margin-left: 8px; font-weight: 400; color: var(--faint); }
.docs-open { align-self: flex-start; }
.skill-actions { display: flex; flex-wrap: wrap; gap: var(--sp-2); }
.docs-roots {
  display: flex;
  align-items: center;
  gap: var(--sp-3);
  min-height: 34px;
  padding: 0 var(--sp-4);
  border-top: 1px solid var(--stroke);
  color: var(--faint);
  font-size: var(--fs-caption);
}
.docs-roots button {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-1);
  min-width: 0;
  max-width: 38%;
  padding: 0;
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: pointer;
}
.docs-roots button:disabled { cursor: default; }
.doc-pip {
  width: 8px; height: 8px; border-radius: 50%; flex: none;
  background: var(--accent-solid);
  box-shadow: 0 0 0 1px var(--dot-ring);
}
.doc-card.is-off .doc-pip { background: var(--idle); }
.doc-card b {
  font-size: var(--fs-body); font-weight: 500;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.doc-card p {
  margin: 0; font-size: var(--fs-caption); color: var(--dim);
  display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden;
}
.doc-card time { font-size: var(--fs-caption); color: var(--faint); margin-top: auto; }
.doc-sec { margin: 0 0 var(--sp-5); }
.doc-sec h3 { font-size: var(--fs-caption); font-weight: 500; color: var(--dim); margin: 0 0 var(--sp-3); }
.doc-sec h3 .n { font-variant-numeric: tabular-nums; }
.docs-table { width: 100%; table-layout: fixed; border-collapse: collapse; font-size: var(--fs-small); }
.docs-table col.docs-col-when { width: 56px; }
.docs-table th {
  text-align: left; font-weight: 400; color: var(--faint); font-size: var(--fs-caption);
  padding: 0 var(--sp-2); height: var(--h-row); border-bottom: 1px solid var(--stroke);
}
.docs-table td {
  padding: 0 var(--sp-2); height: var(--h-row);
  border-bottom: 1px solid var(--stroke);
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.docs-table tr[data-plan] { cursor: pointer; }
.docs-table tr[data-plan]:hover td { background: var(--wash); }
.docs-table tr.is-on td { background: var(--surface-hi); }
.docs-name { display: flex; align-items: center; gap: 8px; min-width: 0; }
.docs-name b { overflow: hidden; text-overflow: ellipsis; font-weight: 500; }
.doc-ghead td { padding: var(--sp-3) 0 0; border-bottom: 0; background: transparent; }
.doc-ghead:first-child td { padding-top: 0; }
.docs-table tbody.is-fold > tr:not(.doc-ghead) { display: none; }
.doc-gbtn {
  display: inline-flex; align-items: center; gap: 6px;
  height: var(--h-row); padding: 0; border: 0;
  background: transparent; color: var(--faint);
  font-size: var(--fs-caption); cursor: pointer;
}
.doc-gbtn:hover { color: var(--text); }
.docs-table tbody.is-fold .doc-gbtn .ico { transform: rotate(-90deg); }
.docs-preview h3 { font-size: var(--fs-card); font-weight: 600; margin: 0; }
.docs-meta { font-size: var(--fs-caption); color: var(--faint); display: flex; flex-direction: column; gap: 2px; }
.docs-body {
  font-size: var(--fs-small); color: var(--dim); line-height: 1.65;
  flex: 1; min-height: 0; overflow: auto; margin: 0;
  white-space: pre-wrap; font-family: var(--mono);
}
.docs-empty { color: var(--empty-fg); font-size: var(--fs-small); padding: var(--sp-6) 0; }
.hint { font-size: var(--fs-caption); color: var(--faint); margin: 0 0 var(--sp-3); }
@media (max-width: 1279px) {
  .docs-split { grid-template-columns: 1fr; }
  .docs-list { border-right: 0; border-radius: var(--r-card) var(--r-card) 0 0; }
  .docs-roots { flex-wrap: wrap; padding-block: var(--sp-2); }
  .docs-roots button { max-width: 100%; width: 100%; }
}
</style>
