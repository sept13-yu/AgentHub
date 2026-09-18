<script setup lang="ts">
import { computed, inject, onMounted, reactive, ref, type Ref } from 'vue'
import { NButton, NIcon, NInput, NRadio, NRadioGroup, NSwitch, useMessage } from 'naive-ui'
import { Archive, Plus, Trash2 } from 'lucide-vue-next'
import AhConfirm from '../components/AhConfirm.vue'
import { del, get, post, put, WRITABLE } from '../api'
import { usePageHotkeys } from '../hotkeys'

const message = useMessage()
const pageLoading = inject<Ref<boolean>>('page-loading')
const readonly = !WRITABLE

interface CodexStatus {
  providerId: string
  configPath: string
  configExists: boolean
  configBroken: boolean
  liveProvider: string | null
  liveProviderMatches: boolean
  liveModel: string | null
  live: {
    tableExists: boolean
    baseUrl: string | null
    wireApi: string | null
    requiresOpenaiAuth: boolean
    supportsWebSockets: boolean | null
    userAgent: string | null
    originator: string | null
    hasAuthCommand: boolean
    authCommand: string | null
    foreignKeys: string[]
    isHybridForm: boolean
    providerMatches: boolean
  } | null
  externalChanged: boolean
  authType: string
  codexRunning: boolean
  activeConnectionId: string | null
  credentialExePath: string
  providerBuckets: Record<string, number>
}

interface CodexConnectionView {
  id: string
  name: string
  kind: 'official' | 'relay'
  baseUrl: string
  defaultModel: string
  supportsWebSockets: boolean
  userAgent: string
  originator: string
  keySet: boolean
  usageBaseUrl: string
  active: boolean
}

interface AuthProfileView {
  id: string
  name: string
  email: string
  plan: string
  accountId: string
  createdAt: string
  updatedAt: string
  active: boolean
}

interface AuthProfileLive {
  authType: string
  email: string
  plan: string
  importable: boolean
}

/** 列表行：连接 / 官方档案 / 未归档登录 / 官方空白位 */
type RowKind = 'official-conn' | 'relay' | 'official-account' | 'live-unarchived' | 'official-blank'

interface ConfigRow {
  key: string
  rowKind: RowKind
  title: string
  subtitle: string
  isCurrent: boolean
  canDelete: boolean
  connectionId?: string
  profileId?: string
}

const status = ref<CodexStatus | null>(null)
const connections = ref<CodexConnectionView[]>([])
const profiles = ref<AuthProfileView[]>([])
const profileLive = ref<AuthProfileLive | null>(null)
const loadError = ref('')
const profilesError = ref('')
const busy = ref(false)

const createOpen = ref(false)
const createType = ref<'official' | 'relay'>('official')
const createAdvanced = ref(false)
const createForm = reactive({
  name: '',
  baseUrl: '',
  defaultModel: '',
  supportsWebSockets: false,
  userAgent: '',
  originator: '',
  apiKey: '',
  usageBaseUrl: '',
})

const editOpen = ref(false)
const editId = ref('')
const editAdvanced = ref(false)
const editForm = reactive({
  name: '',
  baseUrl: '',
  defaultModel: '',
  supportsWebSockets: false,
  userAgent: '',
  originator: '',
  apiKey: '',
  usageBaseUrl: '',
  keySet: false,
})

const deleteShow = ref(false)
const pendingDelete = ref<ConfigRow | null>(null)
const applyShow = ref(false)
const pendingApply = ref<ConfigRow | null>(null)
const archiveShow = ref(false)
const archiveName = ref('')

const officialActive = computed(() =>
  connections.value.some((c) => c.kind === 'official' && c.active))

const liveUnarchived = computed(() =>
  !!profileLive.value?.importable && !profiles.value.some((p) => p.active))

const authLoginText = computed(() => {
  const t = status.value?.authType
  const email = profileLive.value?.email
  if (t === 'chatgpt') return email ? `ChatGPT ${email}` : 'ChatGPT 已登录'
  if (t === 'apikey') return 'API Key'
  if (t === 'none') return '未登录'
  return '未知'
})

const currentLabel = computed(() => {
  const active = connections.value.find((c) => c.active)
  if (!active) return '—'
  if (active.kind === 'official') {
    const p = profiles.value.find((x) => x.active)
    if (p) return p.name || p.email || '官方'
    if (liveUnarchived.value) return profileLive.value?.email || '当前登录（未归档）'
    return '官方 · 未登录'
  }
  return active.name || active.baseUrl || '中转'
})

const statusLine = computed(() => {
  if (!status.value) return ''
  const run = status.value.codexRunning ? '运行中' : '未运行'
  return `Codex：${run} · 登录：${authLoginText.value} · 当前：${currentLabel.value}`
})

const rows = computed<ConfigRow[]>(() => {
  const out: ConfigRow[] = []
  const official = connections.value.find((c) => c.kind === 'official')
  const relays = connections.value.filter((c) => c.kind === 'relay')

  // 官方空白位：始终保留一条「官方连接」入口（未登录或可新建登录）
  if (official) {
    const blankCurrent = official.active
      && status.value?.authType === 'none'
      && !profiles.value.some((p) => p.active)
    out.push({
      key: 'official-blank',
      rowKind: 'official-blank',
      title: official.name || '官方',
      subtitle: blankCurrent
        ? '未登录 · 点应用后打开 Codex 登录'
        : '空白登录位 · 应用后清空登录并走 ChatGPT',
      isCurrent: blankCurrent,
      canDelete: false,
      connectionId: official.id,
    })
  }

  if (liveUnarchived.value) {
    const email = profileLive.value?.email || 'ChatGPT'
    const plan = profileLive.value?.plan
    out.push({
      key: 'live-unarchived',
      rowKind: 'live-unarchived',
      title: '当前登录（未归档）',
      subtitle: plan ? `${email} · ${plan}` : email,
      isCurrent: !!official?.active,
      canDelete: false,
      connectionId: official?.id,
    })
  }

  for (const p of profiles.value) {
    const bits = [p.email, p.plan].filter((x) => x && x.trim())
    out.push({
      key: 'profile:' + p.id,
      rowKind: 'official-account',
      title: p.name || p.email || '官方账号',
      subtitle: bits.length ? bits.join(' · ') : '已归档的 ChatGPT 登录',
      isCurrent: !!p.active && !!official?.active,
      canDelete: true,
      profileId: p.id,
      connectionId: official?.id,
    })
  }

  for (const c of relays) {
    out.push({
      key: 'relay:' + c.id,
      rowKind: 'relay',
      title: c.name || '（未命名中转）',
      subtitle: c.baseUrl || '—',
      isCurrent: c.active,
      canDelete: !c.active,
      connectionId: c.id,
    })
  }

  return out
})

const showArchiveSecondary = computed(() => !!profileLive.value?.importable)

async function load() {
  loadError.value = ''
  if (pageLoading) pageLoading.value = true
  try {
    const [s, c] = await Promise.all([
      get<CodexStatus>('/api/codex-config/status'),
      get<{ connections: CodexConnectionView[] }>('/api/codex-config/connections'),
    ])
    status.value = s
    connections.value = c.connections
    await loadProfiles()
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    if (pageLoading) pageLoading.value = false
  }
}

async function loadProfiles() {
  try {
    const r = await get<{ profiles: AuthProfileView[]; live: AuthProfileLive }>(
      '/api/codex-config/auth-profiles')
    profiles.value = r.profiles
    profileLive.value = r.live
    profilesError.value = ''
  } catch (e) {
    profilesError.value = e instanceof Error ? e.message : String(e)
  }
}

function openCreate() {
  if (readonly) return
  createType.value = 'official'
  createAdvanced.value = false
  createForm.name = ''
  createForm.baseUrl = ''
  createForm.defaultModel = ''
  createForm.supportsWebSockets = false
  createForm.userAgent = ''
  createForm.originator = ''
  createForm.apiKey = ''
  createForm.usageBaseUrl = ''
  createOpen.value = true
}

function openEditRelay(id: string) {
  if (readonly) return
  const c = connections.value.find((x) => x.id === id)
  if (!c || c.kind !== 'relay') return
  editId.value = id
  editAdvanced.value = false
  editForm.name = c.name
  editForm.baseUrl = c.baseUrl
  editForm.defaultModel = c.defaultModel
  editForm.supportsWebSockets = c.supportsWebSockets
  editForm.userAgent = c.userAgent
  editForm.originator = c.originator
  editForm.usageBaseUrl = c.usageBaseUrl
  editForm.apiKey = ''
  editForm.keySet = c.keySet
  editOpen.value = true
}

function relayPayload(f: typeof createForm) {
  return {
    name: f.name.trim(),
    baseUrl: f.baseUrl.trim(),
    defaultModel: f.defaultModel.trim(),
    supportsWebSockets: f.supportsWebSockets,
    userAgent: f.userAgent.trim(),
    originator: f.originator.trim(),
    usageBaseUrl: f.usageBaseUrl.trim(),
    ...(f.apiKey.trim() ? { apiKey: f.apiKey.trim() } : {}),
  }
}

async function saveCreate() {
  if (readonly || busy.value) return
  busy.value = true
  if (pageLoading) pageLoading.value = true
  try {
    if (createType.value === 'official') {
      const r = await post<{ ok: boolean; restartRequired?: boolean; note?: string; error?: string }>(
        '/api/codex-config/official/prepare-blank', {})
      if (!r.ok) {
        message.error(r.error || '准备官方空白失败')
        return
      }
      message.success(r.note || '请打开 Codex 登录；登录后点「归档当前登录」')
      if (r.restartRequired) message.warning('检测到 Codex 在运行，请彻底退出后重启再登录')
      createOpen.value = false
      await load()
      return
    }
    if (!createForm.name.trim()) {
      message.error('请填写名称')
      return
    }
    if (!createForm.baseUrl.trim()) {
      message.error('请填写 baseUrl')
      return
    }
    if (!createForm.apiKey.trim()) {
      message.error('请填写 API Key')
      return
    }
    const r = await post<{ id: string }>('/api/codex-config/connections', relayPayload(createForm))
    message.success('中转已保存')
    createOpen.value = false
    await load()
    // 新建中转后不自动应用，用户点「应用」
    void r
  } catch (e) {
    message.error('保存失败：' + (e instanceof Error ? e.message : String(e)))
  } finally {
    busy.value = false
    if (pageLoading) pageLoading.value = false
  }
}

async function saveEdit() {
  if (readonly || busy.value || !editId.value) return
  busy.value = true
  if (pageLoading) pageLoading.value = true
  try {
    if (!editForm.name.trim() || !editForm.baseUrl.trim()) {
      message.error('名称与 baseUrl 不能为空')
      return
    }
    await put(`/api/codex-config/connections/${editId.value}`, relayPayload(editForm))
    message.success('已保存（尚未应用）')
    editOpen.value = false
    await load()
  } catch (e) {
    message.error('保存失败：' + (e instanceof Error ? e.message : String(e)))
  } finally {
    busy.value = false
    if (pageLoading) pageLoading.value = false
  }
}

function askApply(row: ConfigRow) {
  if (readonly || busy.value) return
  pendingApply.value = row
  applyShow.value = true
}

const applyConfirmText = computed(() => {
  const row = pendingApply.value
  if (!row) return ''
  if (row.rowKind === 'relay') {
    return `将「${row.title}」写入 Codex 并设为当前中转。若 Codex 正在运行，需彻底退出后重启生效。`
  }
  if (row.rowKind === 'official-account') {
    const lines = [`将档案「${row.title}」写回登录，并切到官方连接。`]
    if (liveUnarchived.value) lines.push('当前登录尚未归档，切换后该登录会丢失；建议先「归档当前登录」。')
    lines.push('若 Codex 正在运行，请彻底退出后重启。')
    return lines.join('\n')
  }
  if (row.rowKind === 'official-blank') {
    return '将清空当前登录并切换到官方连接。打开 Codex 后按提示用 ChatGPT 登录新账号；登好后可点「归档当前登录」。不会删除已有档案。'
  }
  if (row.rowKind === 'live-unarchived') {
    return '当前登录尚未归档。点确定会确保官方连接生效；建议随后点「归档当前登录」。'
  }
  return '确定应用该配置？'
})

async function doApply() {
  applyShow.value = false
  const row = pendingApply.value
  pendingApply.value = null
  if (!row || readonly) return
  busy.value = true
  if (pageLoading) pageLoading.value = true
  try {
    if (row.rowKind === 'relay' && row.connectionId) {
      const r = await post<{ ok: boolean; restartRequired?: boolean; error?: string }>(
        `/api/codex-config/connections/${row.connectionId}/apply`)
      if (!r.ok) message.error('应用失败：' + (r.error ?? '未知错误'))
      else if (r.restartRequired) message.warning('已写入。检测到 Codex 在运行，请彻底退出并重启后生效')
      else message.success('已应用中转')
    } else if (row.rowKind === 'official-account' && row.profileId) {
      const sw = await post<{ ok: boolean; restartRequired?: boolean; error?: string }>(
        `/api/codex-config/auth-profiles/${row.profileId}/switch`)
      if (!sw.ok) {
        message.error('切换失败：' + (sw.error ?? '未知错误'))
        return
      }
      if (!officialActive.value && row.connectionId) {
        const ap = await post<{ ok: boolean; restartRequired?: boolean; error?: string }>(
          `/api/codex-config/connections/${row.connectionId}/apply`)
        if (!ap.ok) {
          message.error('已写回登录，但应用官方连接失败：' + (ap.error ?? '未知错误'))
          return
        }
        if (sw.restartRequired || ap.restartRequired) {
          message.warning('已切换官方账号。检测到 Codex 在运行，请彻底退出并重启后生效')
        } else {
          message.success('已切换到该官方账号')
        }
      } else if (sw.restartRequired) {
        message.warning('已写回登录。检测到 Codex 在运行，请彻底退出并重启后生效')
      } else {
        message.success('已切换到该官方账号')
      }
    } else if (row.rowKind === 'official-blank' || row.rowKind === 'live-unarchived') {
      if (row.rowKind === 'official-blank') {
        const r = await post<{ ok: boolean; restartRequired?: boolean; note?: string; error?: string }>(
          '/api/codex-config/official/prepare-blank', {})
        if (!r.ok) message.error(r.error || '准备失败')
        else {
          message.success(r.note || '请打开 Codex 登录；登录后点「归档当前登录」')
          if (r.restartRequired) message.warning('检测到 Codex 在运行，请彻底退出后重启再登录')
        }
      } else if (row.connectionId) {
        const r = await post<{ ok: boolean; restartRequired?: boolean; error?: string }>(
          `/api/codex-config/connections/${row.connectionId}/apply`)
        if (!r.ok) message.error('应用失败：' + (r.error ?? '未知错误'))
        else if (r.restartRequired) message.warning('已切到官方。请重启 Codex；建议归档当前登录')
        else message.success('已切到官方；建议点「归档当前登录」')
      }
    }
    await load()
  } catch (e) {
    message.error('应用失败：' + (e instanceof Error ? e.message : String(e)))
  } finally {
    busy.value = false
    if (pageLoading) pageLoading.value = false
  }
}

function askDelete(row: ConfigRow) {
  if (readonly || !row.canDelete) return
  pendingDelete.value = row
  deleteShow.value = true
}

async function doDelete() {
  deleteShow.value = false
  const row = pendingDelete.value
  pendingDelete.value = null
  if (!row) return
  try {
    if (row.rowKind === 'relay' && row.connectionId) {
      await del(`/api/codex-config/connections/${row.connectionId}`)
      message.success('已删除中转')
    } else if (row.rowKind === 'official-account' && row.profileId) {
      await del(`/api/codex-config/auth-profiles/${row.profileId}`)
      message.success('已删除档案（不影响 Codex 当前登录）')
    }
    await load()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '删除失败')
  }
}

function openArchive() {
  if (readonly || !profileLive.value?.importable) return
  archiveName.value = profileLive.value.email || ''
  archiveShow.value = true
}

async function doArchive() {
  archiveShow.value = false
  if (readonly) return
  busy.value = true
  if (pageLoading) pageLoading.value = true
  try {
    const r = await post<{ id: string; updated?: boolean }>(
      '/api/codex-config/auth-profiles/import',
      { name: archiveName.value.trim() })
    message.success(r.updated ? '已更新该账号的归档' : '已归档当前登录')
    await load()
  } catch (e) {
    message.error('归档失败：' + (e instanceof Error ? e.message : String(e)))
  } finally {
    busy.value = false
    if (pageLoading) pageLoading.value = false
  }
}

function typeBadge(row: ConfigRow) {
  if (row.rowKind === 'relay') return '中转'
  return '官方'
}

function applyLabel(row: ConfigRow) {
  if (row.isCurrent && row.rowKind !== 'official-blank') return '当前'
  if (row.rowKind === 'official-account' || row.rowKind === 'relay') return '应用'
  return '应用'
}

usePageHotkeys({
  refresh: () => { void load() },
})

onMounted(() => load())
</script>

<template>
  <p v-if="readonly" class="banner">浏览器直连为只读，切换 Codex 配置请在 AgentHub 窗口内操作。</p>
  <p v-if="loadError" class="usage-error">读取失败：{{ loadError }}</p>

  <div v-if="status" class="page">
    <section class="card status-card">
      <div class="card-body status-line">{{ statusLine }}</div>
    </section>

    <p v-if="status.configBroken" class="usage-error">config.toml 语法损坏，已阻止一切写入。请先在 Codex 中修复该文件。</p>
    <p v-else-if="status.externalChanged" class="banner">受管字段与当前生效连接不一致。点「应用」会重写这些字段。</p>
    <p v-else-if="status.live?.isHybridForm" class="banner">当前 live 是混合形态；应用任一配置后会重塑为标准形态。</p>
    <p v-if="profilesError" class="usage-error">读取档案失败：{{ profilesError }}</p>

    <section class="card list-card">
      <div class="card-head">
        配置
        <span class="spacer" />
        <n-button
          v-if="showArchiveSecondary"
          quaternary
          :disabled="readonly || busy"
          @click="openArchive"
        >
          <template #icon><n-icon :size="16"><Archive :stroke-width="1.8" /></n-icon></template>
          归档当前登录
        </n-button>
        <n-button type="primary" :disabled="readonly || busy" @click="openCreate">
          <template #icon><n-icon :size="16"><Plus :stroke-width="1.8" /></n-icon></template>
          新建
        </n-button>
      </div>
      <div class="card-body">
        <div v-if="rows.length === 0" class="empty">还没有配置。点「新建」添加官方登录位或中转。</div>
        <div v-else class="row-list">
          <div
            v-for="row in rows"
            :key="row.key"
            class="cfg-row"
            :class="{ 'is-current': row.isCurrent }"
          >
            <span class="kind-badge" :class="row.rowKind === 'relay' ? 'is-relay' : 'is-official'">
              {{ typeBadge(row) }}
            </span>
            <div
              class="cfg-main"
              :class="{ 'is-editable': row.rowKind === 'relay' }"
              @click="row.rowKind === 'relay' && row.connectionId && openEditRelay(row.connectionId)"
            >
              <span class="cfg-title">{{ row.title }}</span>
              <span class="cfg-sub">{{ row.subtitle }}</span>
            </div>
            <span v-if="row.isCurrent" class="cur-badge">当前</span>
            <div class="cfg-acts">
              <n-button
                size="small"
                type="primary"
                :disabled="readonly || busy || (row.isCurrent && row.rowKind !== 'official-blank')"
                @click="askApply(row)"
              >
                {{ applyLabel(row) }}
              </n-button>
              <n-button
                v-if="row.canDelete"
                size="small"
                quaternary
                type="error"
                :disabled="readonly || busy"
                @click="askDelete(row)"
              >
                <template #icon><n-icon :size="14"><Trash2 :stroke-width="1.8" /></n-icon></template>
                删除
              </n-button>
            </div>
          </div>
        </div>
      </div>
    </section>

    <!-- 新建侧栏 -->
    <div v-if="createOpen" class="drawer-mask" @click.self="createOpen = false">
      <aside class="drawer" role="dialog" aria-label="新建配置">
        <div class="drawer-head">新建</div>
        <div class="drawer-body">
          <div class="field">
            <span class="lbl">类型</span>
            <n-radio-group v-model:value="createType" :disabled="readonly" name="create-type">
              <n-radio value="official">官方</n-radio>
              <n-radio value="relay">中转</n-radio>
            </n-radio-group>
          </div>
          <div v-if="createType === 'official'" class="hint-block">
            保存后会清空当前登录，并切到官方连接。打开 Codex 用 ChatGPT 登新号；登好后可再「归档当前登录」。不会删除已有档案。
          </div>
          <template v-else>
            <div class="field">
              <label class="lbl" for="cx-new-name">名称</label>
              <n-input id="cx-new-name" :disabled="readonly" :spellcheck="false" v-model:value="createForm.name" placeholder="如 Sub2API 中转" />
            </div>
            <div class="field">
              <label class="lbl" for="cx-new-url">baseUrl</label>
              <n-input id="cx-new-url" :disabled="readonly" :spellcheck="false" v-model:value="createForm.baseUrl" placeholder="https://…" />
            </div>
            <div class="field">
              <label class="lbl" for="cx-new-key">API Key</label>
              <n-input id="cx-new-key" type="password" show-password-on="click" :disabled="readonly" autocomplete="off" v-model:value="createForm.apiKey" placeholder=" " />
            </div>
            <div class="field">
              <label class="lbl" for="cx-new-model">默认模型（可选）</label>
              <n-input id="cx-new-model" :disabled="readonly" :spellcheck="false" v-model:value="createForm.defaultModel" placeholder="留空则不改模型" />
            </div>
            <button type="button" class="adv-toggle" @click="createAdvanced = !createAdvanced">
              {{ createAdvanced ? '收起高级' : '高级' }}
            </button>
            <template v-if="createAdvanced">
              <div class="field row-inline">
                <span class="lbl">WebSocket</span>
                <n-switch :disabled="readonly" v-model:value="createForm.supportsWebSockets" />
              </div>
              <div class="field">
                <label class="lbl" for="cx-new-ua">User-Agent</label>
                <n-input id="cx-new-ua" :disabled="readonly" :spellcheck="false" v-model:value="createForm.userAgent" />
              </div>
              <div class="field">
                <label class="lbl" for="cx-new-org">Originator</label>
                <n-input id="cx-new-org" :disabled="readonly" :spellcheck="false" v-model:value="createForm.originator" />
              </div>
              <div class="field">
                <label class="lbl" for="cx-new-usage">余额查询地址</label>
                <n-input id="cx-new-usage" :disabled="readonly" :spellcheck="false" v-model:value="createForm.usageBaseUrl" placeholder="可选，不写入 Codex" />
              </div>
              <p class="hint">Provider ID 固定为 OpenAI；路径由本机 Codex 决定。</p>
            </template>
          </template>
        </div>
        <div class="drawer-foot">
          <n-button @click="createOpen = false">取消</n-button>
          <n-button type="primary" :loading="busy" :disabled="readonly" @click="saveCreate">保存</n-button>
        </div>
      </aside>
    </div>

    <!-- 编辑中转 -->
    <div v-if="editOpen" class="drawer-mask" @click.self="editOpen = false">
      <aside class="drawer" role="dialog" aria-label="编辑中转">
        <div class="drawer-head">编辑中转</div>
        <div class="drawer-body">
          <div class="field">
            <label class="lbl" for="cx-edit-name">名称</label>
            <n-input id="cx-edit-name" :disabled="readonly" :spellcheck="false" v-model:value="editForm.name" />
          </div>
          <div class="field">
            <label class="lbl" for="cx-edit-url">baseUrl</label>
            <n-input id="cx-edit-url" :disabled="readonly" :spellcheck="false" v-model:value="editForm.baseUrl" />
          </div>
          <div class="field">
            <label class="lbl" for="cx-edit-key">API Key</label>
            <n-input
              id="cx-edit-key"
              type="password"
              show-password-on="click"
              :disabled="readonly"
              autocomplete="off"
              :placeholder="editForm.keySet ? '已配置（留空不修改）' : ' '"
              v-model:value="editForm.apiKey"
            />
          </div>
          <div class="field">
            <label class="lbl" for="cx-edit-model">默认模型（可选）</label>
            <n-input id="cx-edit-model" :disabled="readonly" :spellcheck="false" v-model:value="editForm.defaultModel" />
          </div>
          <button type="button" class="adv-toggle" @click="editAdvanced = !editAdvanced">
            {{ editAdvanced ? '收起高级' : '高级' }}
          </button>
          <template v-if="editAdvanced">
            <div class="field row-inline">
              <span class="lbl">WebSocket</span>
              <n-switch :disabled="readonly" v-model:value="editForm.supportsWebSockets" />
            </div>
            <div class="field">
              <label class="lbl" for="cx-edit-ua">User-Agent</label>
              <n-input id="cx-edit-ua" :disabled="readonly" :spellcheck="false" v-model:value="editForm.userAgent" />
            </div>
            <div class="field">
              <label class="lbl" for="cx-edit-org">Originator</label>
              <n-input id="cx-edit-org" :disabled="readonly" :spellcheck="false" v-model:value="editForm.originator" />
            </div>
            <div class="field">
              <label class="lbl" for="cx-edit-usage">余额查询地址</label>
              <n-input id="cx-edit-usage" :disabled="readonly" :spellcheck="false" v-model:value="editForm.usageBaseUrl" />
            </div>
          </template>
        </div>
        <div class="drawer-foot">
          <n-button @click="editOpen = false">取消</n-button>
          <n-button type="primary" :loading="busy" :disabled="readonly" @click="saveEdit">保存</n-button>
        </div>
      </aside>
    </div>
  </div>

  <AhConfirm
    :show="applyShow"
    :text="applyConfirmText"
    ok-text="应用"
    @update:show="applyShow = $event"
    @confirm="doApply"
  />
  <AhConfirm
    :show="deleteShow"
    :text="pendingDelete?.rowKind === 'relay'
      ? `确定删除中转「${pendingDelete?.title}」吗？只删 AgentHub 记录，不影响 Codex 当前配置。`
      : `确定删除档案「${pendingDelete?.title}」吗？只删归档，不影响 Codex 当前登录。`"
    ok-text="删除"
    @update:show="deleteShow = $event"
    @confirm="doDelete"
  />
  <AhConfirm
    :show="archiveShow"
    text="将当前 ChatGPT 登录归档到本机。同一账号再次归档会覆盖该档案。"
    ok-text="归档"
    @update:show="archiveShow = $event"
    @confirm="doArchive"
  >
    <div class="archive-name">
      <label class="lbl" for="cx-archive-name">名称（可选）</label>
      <n-input id="cx-archive-name" :spellcheck="false" v-model:value="archiveName" placeholder="留空则使用邮箱" />
    </div>
  </AhConfirm>
</template>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  gap: var(--sp-5);
  width: 100%;
  min-width: 0;
  flex: 1 1 auto;
  min-height: 0;
}
.status-line {
  font-size: var(--fs-body);
  color: var(--text);
  line-height: 1.5;
}
.list-card {
  display: flex;
  flex-direction: column;
  min-height: 0;
  flex: 1 1 auto;
}
.list-card .card-body {
  flex: 1 1 auto;
  min-height: 0;
  overflow: auto;
}
.empty {
  font-size: var(--fs-small);
  color: var(--faint);
}
.row-list {
  display: flex;
  flex-direction: column;
  gap: var(--sp-1);
}
.cfg-row {
  display: flex;
  align-items: center;
  gap: var(--sp-3);
  padding: var(--sp-3);
  border-radius: var(--r-in);
  box-shadow: var(--rule-hi);
}
.cfg-row:last-child { box-shadow: none; }
.cfg-row.is-current {
  background: var(--accent-soft);
}
.kind-badge {
  flex: 0 0 auto;
  font-size: var(--fs-caption);
  padding: 2px 8px;
  border-radius: 999px;
  border: 1px solid var(--stroke);
  color: var(--dim);
}
.kind-badge.is-official {
  color: var(--accent-solid);
  border-color: color-mix(in srgb, var(--accent-solid) 35%, var(--stroke));
}
.kind-badge.is-relay {
  color: var(--dim);
}
.cfg-main {
  flex: 1 1 auto;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  cursor: default;
}
.cfg-main.is-editable { cursor: pointer; }
.cfg-title {
  font-size: var(--fs-body);
  font-weight: 500;
  color: var(--text);
}
.cfg-row.is-current .cfg-title { color: var(--accent-solid); }
.cfg-sub {
  font-size: var(--fs-caption);
  color: var(--faint);
  font-family: var(--mono);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.cur-badge {
  flex: 0 0 auto;
  font-size: var(--fs-caption);
  color: var(--ok);
}
.cfg-acts {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-2);
  flex: 0 0 auto;
}
.spacer { flex: 1; }
.banner {
  margin: 0 0 var(--sp-4);
  padding: var(--sp-3) var(--sp-4);
  color: var(--dim);
  background: var(--wash);
  border-radius: var(--r-in);
  font-size: var(--fs-small);
}
.usage-error {
  margin: 0 0 var(--sp-4);
  padding: var(--sp-3) var(--sp-4);
  color: var(--error-fg);
  background: var(--error-soft);
  border-radius: var(--r-in);
  font-size: var(--fs-body);
}
.drawer-mask {
  position: fixed;
  inset: 0;
  z-index: 40;
  background: rgba(0, 0, 0, 0.28);
  display: flex;
  justify-content: flex-end;
}
.drawer {
  width: min(420px, 100vw);
  height: 100%;
  background: var(--surface);
  border-left: 1px solid var(--stroke);
  display: flex;
  flex-direction: column;
  box-shadow: var(--shadow-float, 0 8px 32px rgba(0, 0, 0, 0.18));
}
.drawer-head {
  padding: var(--sp-4) var(--sp-5);
  font-weight: 600;
  font-size: var(--fs-body);
  border-bottom: 1px solid var(--stroke);
}
.drawer-body {
  flex: 1 1 auto;
  overflow: auto;
  padding: var(--sp-4) var(--sp-5);
  display: flex;
  flex-direction: column;
  gap: var(--sp-4);
}
.drawer-foot {
  display: flex;
  justify-content: flex-end;
  gap: var(--sp-2);
  padding: var(--sp-3) var(--sp-5);
  border-top: 1px solid var(--stroke);
}
.field {
  display: flex;
  flex-direction: column;
  gap: var(--sp-2);
}
.field.row-inline {
  flex-direction: row;
  align-items: center;
  justify-content: space-between;
}
.lbl {
  color: var(--text);
  font-size: var(--fs-body);
  font-weight: 500;
}
.hint {
  font-size: var(--fs-caption);
  color: var(--faint);
}
.hint-block {
  font-size: var(--fs-small);
  color: var(--dim);
  line-height: 1.55;
  padding: var(--sp-3);
  background: var(--wash);
  border-radius: var(--r-in);
}
.adv-toggle {
  align-self: flex-start;
  border: none;
  background: transparent;
  color: var(--accent-solid);
  font: inherit;
  font-size: var(--fs-caption);
  cursor: pointer;
  padding: 0;
}
.archive-name {
  display: flex;
  flex-direction: column;
  gap: var(--sp-2);
  margin: 0 0 var(--sp-3);
}
@media (max-width: 720px) {
  .cfg-row { flex-wrap: wrap; }
  .drawer { width: 100vw; }
}
</style>
