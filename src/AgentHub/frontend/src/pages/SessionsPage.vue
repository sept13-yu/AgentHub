<script setup lang="ts">
import { computed, inject, onMounted, ref, watch, type Ref } from 'vue'
import { NButton, NCheckbox, NIcon, NInput, useMessage } from 'naive-ui'
import { ChevronDown, Copy, Eraser, ExternalLink, FileMinus, Lock, RefreshCw, Trash2, Unlock, X } from 'lucide-vue-next'
import { get, post, WRITABLE } from '../api'
import { agentName } from '../agentMeta'
import AgentMark from '../components/AgentMark.vue'
import AhConfirm from '../components/AhConfirm.vue'
import { formatBytes, formatWhen } from '../format'

const message = useMessage()
const pageLoading = inject<Ref<boolean>>('page-loading')
const readonly = !WRITABLE

type RangeKey = 'week' | 'before' | 'all'
interface Source { id: string; name: string }
interface SessionRow {
  id: string
  agent: string
  title: string
  project: string | null
  messageCount: number
  sizeBytes: number
  lastActivity: string
  locked: boolean
  orphanSub: boolean
}
interface SessionPage {
  total: number
  offset: number
  limit: number
  lockedCount?: number
  weekStart: string
  cursorAvailable: boolean
  cursorMissingReason: string | null
  cursorRunning: boolean
  sources: Source[]
  items: SessionRow[]
}
interface Detail {
  id: string
  agent: string
  title: string
  project: string | null
  messageCount: number
  sizeBytes: number
  lastActivity: string
  locked: boolean
  orphanSub: boolean
  canRename: boolean
  canOpen: boolean
  note: string | null
  messages: { role: string; timestamp: string | null; text: string }[]
}
interface DeleteResult {
  agentId: string
  id: string
  ok: boolean
  error?: string | null
  warning?: string | null
  note?: string | null
}

const ranges: { id: RangeKey; label: string }[] = [
  { id: 'week', label: '本周' },
  { id: 'before', label: '本周之前' },
  { id: 'all', label: '全部' },
]

const range = ref<RangeKey>('week')
const agent = ref('all')
const q = ref('')
const page = ref<SessionPage | null>(null)
const selected = ref<Map<string, SessionRow>>(new Map())
const current = ref<SessionRow | null>(null)
const folded = ref<Set<string>>(new Set())
const detail = ref<Detail | null>(null)
const previewErr = ref('')
const previewBusy = ref(false)
const titleEdit = ref('')
let previewAc: AbortController | null = null
const vacuum = ref(false)
const confirmShow = ref(false)
const confirmKind = ref<'delete' | 'shell' | 'orphan'>('delete')
const pendingRows = ref<SessionRow[]>([])

const items = computed(() => page.value?.items ?? [])
const sources = computed(() => page.value?.sources ?? [])
const groups = computed(() => {
  const map = new Map<string, { key: string; name: string; path: string; items: SessionRow[] }>()
  for (const row of items.value) {
    const key = projectKey(row.project)
    const g = map.get(key)
    if (g) g.items.push(row)
    else map.set(key, { key, name: projectName(row.project), path: row.project || '', items: [row] })
  }
  const known = [...map.values()].filter((g) => g.key)
  const unknown = map.get('')
  const byWhen = (rows: SessionRow[]) => rows.reduce((m, r) => (r.lastActivity > m ? r.lastActivity : m), '')
  for (const g of known) g.items.sort((a, b) => b.lastActivity.localeCompare(a.lastActivity))
  if (unknown) unknown.items.sort((a, b) => b.lastActivity.localeCompare(a.lastActivity))
  known.sort((a, b) => {
    const t = byWhen(b.items).localeCompare(byWhen(a.items))
    return t || a.name.localeCompare(b.name, 'zh')
  })
  return unknown ? [...known, unknown] : known
})
const cursorOk = computed(() => !!page.value?.cursorAvailable)
const cursorRunning = computed(() => !!page.value?.cursorRunning)

// Cursor agentKv 内容库占用 → 大时引导用户跑官方 GC 命令（AgentHub 不代删共享库）
interface CursorStorage { mainDbBytes: number; agentKvBytes: number; agentKvCount: number }
const GC_HINT_KEY = 'ah-cursor-gc-hint-dismissed'
const GC_HINT_THRESHOLD = 500 * 1024 * 1024
const GC_COMMAND = 'Developer: GC Agent KV Blobs'
const cursorStorage = ref<CursorStorage | null>(null)
const gcHintOff = ref(localStorage.getItem(GC_HINT_KEY) === '1')
const gcHintVisible = computed(() =>
  cursorOk.value && !gcHintOff.value && !!cursorStorage.value
  && cursorStorage.value.agentKvBytes >= GC_HINT_THRESHOLD)

interface CursorOrphans {
  fateRows: number
  fateBytes: number
  heightRows: number
  heightBytes: number
  inlineDiffRows: number
  inlineDiffBytes: number
  composerIds: number
  totalRows: number
  totalBytes: number
}
const cursorOrphans = ref<CursorOrphans | null>(null)
const orphanVisible = computed(() => cursorOk.value && (cursorOrphans.value?.totalRows ?? 0) > 0)

async function loadCursorStorage() {
  if (!cursorOk.value) return
  try {
    cursorStorage.value = await get<CursorStorage>('/api/sessions/cursor/storage')
  } catch { /* 拿不到就不提示，不影响会话页 */ }
}

async function loadCursorOrphans() {
  if (!cursorOk.value) return
  try {
    cursorOrphans.value = await get<CursorOrphans>('/api/sessions/cursor/orphans')
  } catch {
    cursorOrphans.value = null
  }
}

function dismissGcHint() {
  gcHintOff.value = true
  localStorage.setItem(GC_HINT_KEY, '1')
}

async function copyGcCommand() {
  try {
    await navigator.clipboard.writeText(GC_COMMAND)
    message.success('命令已复制，去 Cursor 按 Ctrl+Shift+P 粘贴运行')
  } catch {
    message.error('复制失败，请手动输入：' + GC_COMMAND)
  }
}
const filterHasCursor = computed(() =>
  agent.value === 'cursor' || (agent.value === 'all' && sources.value.some((s) => s.id === 'cursor')))
const lockedInFilter = computed(() => page.value?.lockedCount ?? 0)
const confirmSkip = computed(() => {
  if (confirmKind.value !== 'delete') return 0
  if (pendingRows.value.length === 1) return 0
  return lockedInFilter.value
})
const confirmText = computed(() => {
  if (confirmKind.value === 'shell') return '清理 Cursor 空壳。'
  if (confirmKind.value === 'orphan') {
    const o = cursorOrphans.value
    if (!o) return '回收已无会话的缓存。不碰列表里的会话，也不碰 agentKv。'
    return `回收已无会话的缓存：行高 ${o.heightRows} · 部分 diff ${o.fateRows} · 内联 diff ${o.inlineDiffRows}（约 ${formatBytes(o.totalBytes)}）。不碰列表里的会话，也不碰 agentKv。`
  }
  const hostHint = pendingRows.value.some((r) => r.agent === 'zcode' || r.agent === 'workbuddy')
    ? ' ZCode / WorkBuddy 需先退出，否则侧栏标题还在。'
    : ''
  return `删除 ${pendingRows.value.length} 条，已跳过 ${confirmSkip.value} 条锁定。${hostHint}`
})
const confirmShowVacuum = computed(() =>
  !readonly && (confirmKind.value === 'shell' || confirmKind.value === 'orphan' || filterHasCursor.value))
const canDelete = computed(() => {
  if (readonly) return false
  if (selected.value.size) return true
  return !!current.value && !current.value.locked
})

function keyOf(r: SessionRow) { return `${r.agent}:${r.id}` }

function projectKey(path: string | null): string {
  if (!path) return ''
  return path.replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase()
}

function projectName(path: string | null): string {
  if (!path) return '未知'
  const trimmed = path.replace(/[\\/]+$/, '')
  const name = trimmed.split(/[\\/]/).pop()
  return name || path
}

function toggleFold(key: string) {
  const next = new Set(folded.value)
  if (next.has(key)) next.delete(key)
  else next.add(key)
  folded.value = next
}

function setLoading(on: boolean) {
  if (pageLoading) pageLoading.value = on
}

function listParams(offset: number, limit: number) {
  const params = new URLSearchParams({
    range: range.value,
    offset: String(offset),
    limit: String(limit),
  })
  if (agent.value !== 'all') params.set('agent', agent.value)
  if (q.value.trim()) params.set('q', q.value.trim())
  return params
}

async function loadList() {
  const acc: SessionRow[] = []
  let off = 0
  const lim = 200
  let last: SessionPage | null = null
  while (true) {
    const data = await get<SessionPage>(`/api/sessions?${listParams(off, lim)}`)
    last = data
    acc.push(...data.items)
    off += data.items.length
    if (off >= data.total || data.items.length === 0 || data.items.length < lim) break
  }
  page.value = last ? { ...last, items: acc, offset: 0, limit: acc.length } : null
  const keys = new Set(acc.map(keyOf))
  if (current.value && !keys.has(keyOf(current.value))) {
    current.value = null
    detail.value = null
    previewErr.value = ''
  }
}

async function refresh() {
  if (readonly) return
  setLoading(true)
  try {
    await post('/api/sessions/index/refresh')
    await loadList()
    await Promise.all([loadCursorStorage(), loadCursorOrphans()])
    message.success('已刷新')
  } catch (e) {
    message.error(e instanceof Error ? e.message : '刷新失败')
  } finally {
    setLoading(false)
  }
}

async function load() {
  setLoading(true)
  try {
    await loadList()
    await Promise.all([loadCursorStorage(), loadCursorOrphans()])
  } catch (e) {
    message.error(e instanceof Error ? e.message : '读取失败')
  } finally {
    setLoading(false)
  }
}

async function openRow(row: SessionRow) {
  current.value = row
  titleEdit.value = row.title
  previewErr.value = ''
  previewBusy.value = true
  if (detail.value && keyOf(detail.value) !== keyOf(row)) detail.value = null
  previewAc?.abort()
  const ac = new AbortController()
  previewAc = ac
  try {
    const next = await get<Detail>(
      `/api/sessions/detail?agent=${encodeURIComponent(row.agent)}&id=${encodeURIComponent(row.id)}`,
      { signal: ac.signal },
    )
    if (previewAc !== ac) return
    detail.value = next
    titleEdit.value = next.title
  } catch (e) {
    if (ac.signal.aborted) return
    previewErr.value = e instanceof Error ? e.message : '预览失败'
    message.error(previewErr.value)
  } finally {
    if (previewAc === ac) previewBusy.value = false
  }
}

function toggleOne(row: SessionRow, on: boolean) {
  if (row.locked || readonly) return
  const next = new Map(selected.value)
  if (on) next.set(keyOf(row), row)
  else next.delete(keyOf(row))
  selected.value = next
}

const pageUnlocked = computed(() => items.value.filter((r) => !r.locked))
const pageAllOn = computed(() =>
  pageUnlocked.value.length > 0 && pageUnlocked.value.every((r) => selected.value.has(keyOf(r))))

function toggleAll(on: boolean) {
  if (readonly) return
  if (!on) {
    selected.value = new Map()
    return
  }
  const picked = new Map<string, SessionRow>()
  for (const row of items.value) {
    if (!row.locked) picked.set(keyOf(row), row)
  }
  selected.value = picked
}

async function toggleLock(row: SessionRow) {
  if (readonly) return
  try {
    await post('/api/sessions/lock', { agent: row.agent, id: row.id, locked: !row.locked })
    row.locked = !row.locked
    if (row.locked) {
      const next = new Map(selected.value)
      next.delete(keyOf(row))
      selected.value = next
    }
    if (detail.value && detail.value.id === row.id) detail.value.locked = row.locked
  } catch (e) {
    message.error(e instanceof Error ? e.message : '锁定失败')
  }
}

async function saveTitle() {
  if (readonly || !detail.value || !detail.value.canRename) return
  const title = titleEdit.value.trim()
  if (!title || title.length > 200) return
  try {
    await post('/api/sessions/rename', { agent: detail.value.agent, id: detail.value.id, title })
    detail.value.title = title
    if (current.value) current.value.title = title
    message.success('已改标题')
  } catch (e) {
    message.error(e instanceof Error ? e.message : '改标题失败')
  }
}

async function openFile() {
  if (readonly || !detail.value?.canOpen) return
  try {
    await post('/api/sessions/open', { agent: detail.value.agent, id: detail.value.id })
  } catch (e) {
    message.error(e instanceof Error ? e.message : '打开失败')
  }
}

function askRemove() {
  if (!canDelete.value) return
  pendingRows.value = selected.value.size
    ? [...selected.value.values()]
    : current.value ? [current.value] : []
  if (!pendingRows.value.length) return
  confirmKind.value = 'delete'
  vacuum.value = false
  confirmShow.value = true
}

function askCleanShells() {
  if (readonly || !cursorOk.value) return
  pendingRows.value = []
  confirmKind.value = 'shell'
  vacuum.value = false
  confirmShow.value = true
}

function askCleanOrphans() {
  if (readonly || !orphanVisible.value) return
  pendingRows.value = []
  confirmKind.value = 'orphan'
  vacuum.value = false
  confirmShow.value = true
}

async function runDelete(rows: SessionRow[]) {
  setLoading(true)
  try {
    const r = await post<{ ok: boolean; skipped: number; results?: DeleteResult[]; vacuum?: { ok?: boolean; error?: string } }>(
      '/api/sessions/delete',
      {
        items: rows.map((x) => ({ agent: x.agent, id: x.id })),
        vacuum: filterHasCursor.value && vacuum.value,
      },
    )
    // 后端是逐条结果：失败的必须亮出来，不能一律报"已删除"
    const vacuumErr = r.vacuum && r.vacuum.ok === false ? r.vacuum.error : null
    const failed = (r.results ?? []).filter((x) => !x.ok)
    if (vacuumErr) message.error(vacuumErr)
    if (failed.length) {
      const first = failed[0].error || '未知原因'
      message.error(failed.length === 1 ? `删除失败：${first}` : `${failed.length} 条删除失败：${first}`)
    } else if (!vacuumErr) {
      const warn = (r.results ?? []).map((x) => x.warning || x.note).find((x) => !!x)
      if (warn) message.warning(warn)
      else message.success(r.skipped ? `已删除，跳过 ${r.skipped} 条锁定` : '已删除')
    }
    const gone = new Set(
      (r.results ?? []).filter((x) => x.ok).map((x) => `${x.agentId}:${x.id}`),
    )
    if (current.value && gone.has(keyOf(current.value))) {
      current.value = null
      detail.value = null
      previewErr.value = ''
    }
    // 失败的保留勾选，方便直接重试
    const next = new Map(selected.value)
    for (const row of rows) if (gone.has(keyOf(row))) next.delete(keyOf(row))
    selected.value = next
    await loadList()
    await loadCursorOrphans()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '删除失败')
    await loadList()
  } finally {
    setLoading(false)
  }
}

async function runCleanShells() {
  setLoading(true)
  try {
    const r = await post<{ ok: boolean; found: number; vacuum?: { ok?: boolean; error?: string } }>(
      '/api/sessions/cursor/shell-clean',
      { vacuum: vacuum.value },
    )
    if (r.vacuum && r.vacuum.ok === false && r.vacuum.error)
      message.error(r.vacuum.error)
    else
      message.success(`已清理 ${r.found} 个空壳`)
    await loadList()
    await loadCursorOrphans()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '清理失败')
  } finally {
    setLoading(false)
  }
}

async function runCleanOrphans() {
  setLoading(true)
  try {
    const r = await post<{
      ok: boolean
      deletedRows: number
      deletedBytes: number
      vacuum?: { ok?: boolean; error?: string }
    }>('/api/sessions/cursor/orphan-clean', { vacuum: vacuum.value })
    if (r.vacuum && r.vacuum.ok === false && r.vacuum.error)
      message.error(r.vacuum.error)
    else
      message.success(`已回收 ${r.deletedRows} 行（约 ${formatBytes(r.deletedBytes)}）`)
    await loadCursorOrphans()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '回收失败')
  } finally {
    setLoading(false)
  }
}

async function confirmOk() {
  confirmShow.value = false
  if (confirmKind.value === 'shell') await runCleanShells()
  else if (confirmKind.value === 'orphan') await runCleanOrphans()
  else await runDelete(pendingRows.value)
}

function pickRange(id: RangeKey) {
  range.value = id
  selected.value = new Map()
}
function pickAgent(id: string) {
  agent.value = id
  selected.value = new Map()
}

watch([range, agent], () => { void load() })
let qTimer = 0
watch(q, () => {
  window.clearTimeout(qTimer)
  qTimer = window.setTimeout(() => { void load() }, 280)
})

onMounted(() => { void load() })
</script>

<template>
  <teleport defer to="#chrome-tabs">
    <div class="ah-tabs" role="group" aria-label="日期档">
      <button
        v-for="t in ranges"
        :key="t.id"
        type="button"
        :aria-pressed="range === t.id ? 'true' : 'false'"
        @click="pickRange(t.id)"
      >{{ t.label }}</button>
    </div>
  </teleport>
  <teleport defer to="#chrome-actions">
    <n-button v-if="selected.size || current" type="error" :disabled="!canDelete" @click="askRemove">
      <template #icon><n-icon><Trash2 :size="16" :stroke-width="1.8" /></n-icon></template>
      删除
    </n-button>
    <n-button type="primary" :disabled="readonly" @click="refresh">
      <template #icon><n-icon><RefreshCw :size="16" :stroke-width="1.8" /></n-icon></template>
      刷新
    </n-button>
  </teleport>

  <div class="card sess is-split">
    <div class="card-body">
      <div class="sess-split" :class="{ 'has-preview': !!current }">
        <div class="sess-list">
          <div class="sess-filters">
            <div class="sess-src" role="group" aria-label="来源">
              <button type="button" :aria-pressed="agent === 'all' ? 'true' : 'false'" @click="pickAgent('all')">全部</button>
              <button
                v-for="s in sources"
                :key="s.id"
                type="button"
                :aria-pressed="agent === s.id ? 'true' : 'false'"
                @click="pickAgent(s.id)"
              >
                <AgentMark :id="s.id" />{{ s.name }}
              </button>
            </div>
            <n-input v-model:value="q" class="sess-search" placeholder="搜索标题 / 路径" clearable />
            <n-button
              v-if="cursorOk"
              quaternary
              :disabled="readonly"
              @click="askCleanShells"
            >
              <template #icon><n-icon><Eraser :size="16" :stroke-width="1.8" /></n-icon></template>
              清理空壳
            </n-button>
            <n-button
              v-if="orphanVisible"
              quaternary
              :disabled="readonly"
              @click="askCleanOrphans"
            >
              <template #icon><n-icon><FileMinus :size="16" :stroke-width="1.8" /></n-icon></template>
              回收孤儿 {{ cursorOrphans?.totalRows }}
            </n-button>
          </div>
          <div v-if="gcHintVisible" class="gc-hint">
            <span class="gc-text">
              Cursor 内容共享库（agentKv，{{ formatBytes(cursorStorage!.agentKvBytes) }} / 库共
              {{ formatBytes(cursorStorage!.mainDbBytes) }}）会随使用持续增长。AgentHub
              删除会话不触碰它；孤儿数据可在 Cursor 中按
              <b>Ctrl+Shift+P</b> 运行 <code class="mono">{{ GC_COMMAND }}</code>
              回收（官方命令，不删聊天，需数分钟与约 2 倍库大小的空闲磁盘）。
            </span>
            <span class="gc-ops">
              <n-button quaternary :disabled="readonly" @click="copyGcCommand">
                <template #icon><n-icon :size="16"><Copy :stroke-width="1.8" /></n-icon></template>
                复制命令
              </n-button>
              <n-button quaternary aria-label="关闭提示" @click="dismissGcHint">
                <template #icon><n-icon :size="16"><X :stroke-width="1.8" /></n-icon></template>
              </n-button>
            </span>
          </div>
          <table v-if="groups.length" class="sess-table">
            <colgroup>
              <col class="c-check" />
              <col class="c-title" />
              <col class="c-when" />
              <col class="c-lock" />
            </colgroup>
            <thead>
              <tr>
                <th>
                  <n-checkbox
                    :checked="pageAllOn"
                    :disabled="readonly || pageUnlocked.length === 0"
                    @update:checked="toggleAll"
                  />
                </th>
                <th>标题</th>
                <th class="num">最后活动</th>
                <th />
              </tr>
            </thead>
            <tbody
              v-for="g in groups"
              :key="g.key || 'unknown'"
              :class="{ 'is-fold': folded.has(g.key) }"
            >
              <tr class="sess-ghead">
                <td colspan="4">
                  <button type="button" class="sess-gbtn" :title="g.path" @click="toggleFold(g.key)">
                    <ChevronDown class="ico" :size="14" :stroke-width="1.8" />
                    {{ g.name }} <span class="n">{{ g.items.length }}</span>
                  </button>
                </td>
              </tr>
              <tr
                v-for="row in g.items"
                :key="keyOf(row)"
                :class="{ 'is-on': current && keyOf(current) === keyOf(row) }"
                @click="openRow(row)"
              >
                <td @click.stop>
                  <n-checkbox
                    :checked="selected.has(keyOf(row))"
                    :disabled="readonly || row.locked"
                    @update:checked="(on: boolean) => toggleOne(row, on)"
                  />
                </td>
                <td :title="row.title || '(无标题)'">
                  <span class="sess-title">
                    <AgentMark :id="row.agent" />
                    <b>{{ row.title || '(无标题)' }}</b>
                    <span v-if="row.orphanSub" class="sess-tag">子会话</span>
                    <span v-if="row.locked" class="sess-tag is-locked">已锁</span>
                  </span>
                </td>
                <td class="num">{{ formatWhen(row.lastActivity) }}</td>
                <td class="sess-lock" @click.stop>
                  <button
                    type="button"
                    class="icon-quiet"
                    :class="{ 'is-locked': row.locked }"
                    :aria-label="row.locked ? '解锁' : '锁定'"
                    :aria-pressed="row.locked ? 'true' : 'false'"
                    :disabled="readonly"
                    @click="toggleLock(row)"
                  >
                    <!-- 图标显示当前状态（已锁=闭锁），点击动作由 aria-label 表达 -->
                    <Lock v-if="row.locked" :size="16" :stroke-width="1.8" />
                    <Unlock v-else :size="16" :stroke-width="1.8" />
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
          <p v-else class="sess-empty">当前筛选没有会话</p>
        </div>
        <div class="sess-preview">
          <template v-if="current">
            <h3>{{ detail?.title || current.title || '(无标题)' }}</h3>
            <div class="sess-meta">
              <span>{{ agentName(detail?.agent || current.agent) }} · {{ (detail?.project ?? current.project) || '未知项目' }}</span>
              <span>{{ detail?.messageCount ?? current.messageCount }} 条 · {{ formatBytes(detail?.sizeBytes ?? current.sizeBytes) }} · {{ formatWhen(detail?.lastActivity || current.lastActivity) }}</span>
            </div>
            <div v-if="!readonly && detail" class="sess-acts">
              <n-input v-if="detail.canRename" v-model:value="titleEdit" maxlength="200" placeholder="标题" @blur="saveTitle" />
              <n-button v-if="detail.canOpen" @click="openFile">
                <template #icon><n-icon><ExternalLink :size="16" :stroke-width="1.8" /></n-icon></template>
                打开
              </n-button>
            </div>
            <p v-if="detail?.note" class="hint">{{ detail.note }}</p>
            <p v-if="previewErr" class="sess-empty">{{ previewErr }}</p>
            <div v-else-if="detail?.messages.length" class="sess-msgs">
              <div v-for="(m, i) in detail.messages" :key="i" class="sess-msg">
                <span class="role">{{ m.role }}</span>
                <span class="when">{{ formatWhen(m.timestamp) }}</span>
                <p>{{ m.text }}</p>
              </div>
            </div>
            <p v-else-if="previewBusy" class="sess-empty">正在读取预览</p>
            <p v-else class="sess-empty">这条没有消息正文</p>
          </template>
          <p v-else class="sess-empty">点一行看预览</p>
        </div>
      </div>
    </div>
  </div>

  <AhConfirm v-model:show="confirmShow" :text="confirmText" @confirm="confirmOk">
    <label v-if="confirmShowVacuum" class="vac">
      <n-checkbox v-model:checked="vacuum" :disabled="cursorRunning" />
      同时回收磁盘
      <span v-if="cursorRunning" class="hint">请先退出 Cursor</span>
    </label>
  </AhConfirm>
</template>

<style scoped>
.sess-filters {
  display: flex; flex-wrap: wrap; align-items: center; gap: var(--sp-2) var(--sp-3);
  margin-bottom: var(--sp-4);
}
.sess-src {
  display: inline-flex; flex-wrap: wrap; align-items: center; gap: var(--sp-2);
}
.sess-src button {
  height: var(--h-control); padding: 0 12px;
  border: 1px solid var(--stroke); border-radius: 999px;
  background: var(--surface); color: var(--dim);
  font-size: var(--fs-small); cursor: pointer;
  display: inline-flex; align-items: center; gap: 6px;
}
.sess-src button:hover { color: var(--text); border-color: var(--stroke-strong); }
.sess-src button[aria-pressed='true'] {
  color: var(--text); font-weight: 500;
  background: var(--accent-soft); border-color: transparent;
}
.sess-search { width: 220px; }
.vac { display: inline-flex; align-items: center; gap: 6px; font-size: var(--fs-caption); color: var(--dim); }
.hint { font-size: var(--fs-caption); color: var(--faint); }
.sess.is-split {
  overflow: hidden;
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.sess.is-split > .card-body {
  flex: 1;
  box-sizing: border-box;
  min-height: 0;
}
.sess-split {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  height: 100%;
  min-height: 0;
}
.sess-split.has-preview {
  grid-template-columns: minmax(0, 1fr) minmax(280px, 360px);
}
.sess-list {
  min-width: 0;
  min-height: 0;
  overflow: auto;
}
.sess-split.has-preview .sess-list {
  padding-right: var(--sp-4);
  border-right: 1px solid var(--stroke);
}
.sess-preview {
  display: none;
  min-width: 0;
  min-height: 0;
  overflow: auto;
  padding-left: var(--sp-4);
  flex-direction: column;
  gap: var(--sp-3);
}
.sess-split.has-preview .sess-preview {
  display: flex;
}
.sess-table {
  width: 100%;
  table-layout: fixed;
  border-collapse: collapse;
  font-size: var(--fs-small);
}
.sess-table col.c-check { width: 36px; }
.sess-table col.c-lock { width: 40px; }
.sess-table col.c-when { width: 148px; }
.sess-table th {
  text-align: left; font-weight: 400; color: var(--faint); font-size: var(--fs-caption);
  padding: 0 var(--sp-2); height: var(--h-row);
  border-bottom: 1px solid var(--stroke);
}
.sess-table td {
  padding: 0 var(--sp-2); height: var(--h-row);
  border-bottom: 1px solid var(--stroke);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  vertical-align: middle;
}
.sess-table th.num,
.sess-table td.num { text-align: right; }
.sess-table tr:not(.sess-ghead) { cursor: pointer; }
.sess-table tr:not(.sess-ghead):hover td { background: var(--wash); }
.sess-table tr.is-on td { background: var(--surface-hi); }
.sess-title { display: flex; align-items: center; gap: 6px; min-width: 0; }
.sess-title b {
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 500; min-width: 0;
}
.sess-tag { font-size: var(--fs-caption); color: var(--faint); flex: none; }
.icon-quiet {
  width: var(--h-icon-btn); height: var(--h-icon-btn); padding: 0;
  border: 0; background: transparent; color: var(--dim);
  border-radius: var(--r-in); cursor: pointer;
  display: inline-grid; place-items: center;
}
.icon-quiet:hover:not(:disabled) { color: var(--text); background: var(--wash); }
.icon-quiet:disabled { color: var(--disabled-fg); cursor: not-allowed; }
.icon-quiet[aria-pressed='true'] { color: var(--faint); }
/* 锁定态用警示色一眼可辨：图标与「已锁」标签同色，悬停不回落成普通色 */
.icon-quiet.is-locked,
.icon-quiet.is-locked[aria-pressed='true'] { color: var(--warn); }
.icon-quiet.is-locked:hover:not(:disabled) { color: var(--warn); background: var(--wash); }
.sess-tag.is-locked { color: var(--warn); }
.sess-lock { text-align: center; overflow: visible; }
.gc-hint {
  display: flex;
  align-items: flex-start;
  gap: var(--sp-3);
  margin: 0 0 var(--sp-3);
  padding: var(--sp-3) var(--sp-4);
  border: 1px solid var(--stroke);
  border-left: 3px solid var(--warn);
  border-radius: var(--r-in);
  background: var(--wash);
  font-size: var(--fs-caption);
  color: var(--dim);
  line-height: 1.6;
}
.gc-text { min-width: 0; }
.gc-text code {
  padding: 1px 6px;
  border-radius: var(--r-in);
  background: var(--surface);
  color: var(--text);
}
.gc-ops { display: inline-flex; align-items: center; gap: var(--sp-1); flex: none; }
.sess-ghead td { padding: 0; border-bottom: 0; background: transparent; }
.sess-table tbody + tbody .sess-ghead td { padding-top: var(--sp-3); }
.sess-table tbody.is-fold > tr:not(.sess-ghead) { display: none; }
.sess-gbtn {
  display: inline-flex; align-items: center; gap: 6px;
  height: var(--h-row); padding: 0; border: 0;
  background: transparent; color: var(--faint);
  font-size: var(--fs-caption); cursor: pointer;
}
.sess-gbtn:hover { color: var(--text); }
.sess-gbtn .n { font-variant-numeric: tabular-nums; }
.sess-table tbody.is-fold .sess-gbtn .ico { transform: rotate(-90deg); }
.sess-empty { color: var(--empty-fg); font-size: var(--fs-small); padding: var(--sp-6) 0; }
.sess-preview h3 { font-size: var(--fs-card); font-weight: 600; margin: 0; }
.sess-meta { font-size: var(--fs-caption); color: var(--faint); display: flex; flex-direction: column; gap: 2px; }
.sess-acts { display: flex; flex-wrap: wrap; gap: var(--sp-2); }
.sess-msgs {
  display: flex; flex-direction: column; gap: var(--sp-3);
  flex: 1; min-height: 0; overflow: auto; padding-right: var(--sp-2);
}
.sess-msg { font-size: var(--fs-small); color: var(--dim); }
.sess-msg .role { color: var(--text); font-weight: 500; margin-right: 6px; }
.sess-msg .when { color: var(--faint); font-size: var(--fs-caption); }
.sess-msg p { margin: 4px 0 0; white-space: pre-wrap; }
@media (max-width: 1279px) {
  .sess-split.has-preview { grid-template-columns: 1fr; }
  .sess-split.has-preview .sess-list {
    padding-right: 0; border-right: 0; padding-bottom: var(--sp-4);
    border-bottom: 1px solid var(--stroke);
  }
  .sess-split.has-preview .sess-preview { padding-left: 0; padding-top: var(--sp-4); }
}
</style>
