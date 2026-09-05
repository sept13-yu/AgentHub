<script setup lang="ts">
import { computed, inject, onMounted, reactive, ref, type Ref } from 'vue'
import { NButton, NIcon, NInput, NSwitch, useMessage } from 'naive-ui'
import { CloudDownload, Lock, Plus, Save, Trash2 } from 'lucide-vue-next'
import AhConfirm from '../components/AhConfirm.vue'
import { api, del, get, post, put, WRITABLE } from '../api'

const message = useMessage()
const pageLoading = inject<Ref<boolean>>('page-loading')
const readonly = !WRITABLE

interface CodexLiveInfo {
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
}
interface CodexStatus {
  providerId: string
  configPath: string
  configExists: boolean
  configBroken: boolean
  liveProvider: string | null
  liveProviderMatches: boolean
  liveModel: string | null
  live: CodexLiveInfo | null
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
interface DiffRow {
  field: string
  live: string | null
  candidate: string | null
  change: 'keep' | 'set' | 'clear' | 'change'
}

const status = ref<CodexStatus | null>(null)
const connections = ref<CodexConnectionView[]>([])
const selectedId = ref('')
const loadError = ref('')
const saving = ref(false)
const applying = ref(false)
const importing = ref(false)
const deleteShow = ref(false)
const diffRows = ref<DiffRow[] | null>(null)

const f = reactive({
  name: '',
  baseUrl: '',
  defaultModel: '',
  supportsWebSockets: false,
  userAgent: '',
  originator: '',
  apiKey: '',
  usageBaseUrl: '',
})

const selected = computed(() => connections.value.find((c) => c.id === selectedId.value) ?? null)
const isOfficial = computed(() => selected.value?.kind === 'official')

const authTypeText = computed(() => {
  const t = status.value?.authType
  return t === 'chatgpt' ? 'ChatGPT 登录'
    : t === 'apikey' ? 'API Key'
    : t === 'none' ? '未登录'
    : '未知'
})

async function load(keepSelection = true) {
  loadError.value = ''
  if (pageLoading) pageLoading.value = true
  try {
    const [s, c] = await Promise.all([
      get<CodexStatus>('/api/codex-config/status'),
      get<{ connections: CodexConnectionView[] }>('/api/codex-config/connections'),
    ])
    status.value = s
    connections.value = c.connections
    const wanted = keepSelection ? selectedId.value : ''
    const active = c.connections.find((x) => x.id === wanted)
      ?? c.connections.find((x) => x.active)
      ?? c.connections[0]
    select(active?.id ?? '')
    diffRows.value = null
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    if (pageLoading) pageLoading.value = false
  }
}

function select(id: string) {
  selectedId.value = id
  diffRows.value = null
  // 清掉未保存的占位项，避免切走后列表残留
  if (!id.startsWith('new:')) connections.value = connections.value.filter((c) => !c.id.startsWith('new:'))
  const conn = connections.value.find((c) => c.id === id)
  f.name = conn?.name ?? ''
  f.baseUrl = conn?.baseUrl ?? ''
  f.defaultModel = conn?.defaultModel ?? ''
  f.supportsWebSockets = conn?.supportsWebSockets ?? false
  f.userAgent = conn?.userAgent ?? ''
  f.originator = conn?.originator ?? ''
  f.usageBaseUrl = conn?.usageBaseUrl ?? ''
  f.apiKey = ''
}

function bodyPayload() {
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

async function save() {
  if (readonly || saving.value || !selected.value) return
  saving.value = true
  if (pageLoading) pageLoading.value = true
  try {
    if (selected.value.id.startsWith('new:')) {
      const r = await post<{ id: string }>('/api/codex-config/connections', bodyPayload())
      message.success('连接已保存')
      await load()
      selectedId.value = r.id
      select(r.id)
    } else {
      await put(`/api/codex-config/connections/${selected.value.id}`, bodyPayload())
      message.success('连接已保存（尚未写入 Codex）')
      await load()
    }
  } catch (e) {
    message.error('保存失败：' + (e instanceof Error ? e.message : String(e)))
  } finally {
    saving.value = false
    if (pageLoading) pageLoading.value = false
  }
}

function newConnection() {
  if (readonly) return
  const placeholder: CodexConnectionView = {
    id: 'new:' + Date.now(), name: '', kind: 'relay', baseUrl: '', defaultModel: '',
    supportsWebSockets: false, userAgent: '', originator: '', keySet: false,
    usageBaseUrl: '', active: false,
  }
  connections.value = [...connections.value, placeholder]
  selectedId.value = placeholder.id
  select(placeholder.id)
  diffRows.value = null
}

async function applyConnection() {
  if (readonly || applying.value || !selected.value || selected.value.id.startsWith('new:')) return
  applying.value = true
  if (pageLoading) pageLoading.value = true
  try {
    const r = await post<{ ok: boolean; restartRequired: boolean; backupPath?: string; error?: string }>(
      `/api/codex-config/connections/${selected.value.id}/apply`)
    if (!r.ok) {
      message.error('应用失败：' + (r.error ?? '未知错误'))
    } else if (r.restartRequired) {
      message.warning('已写入 Codex 配置。检测到 Codex 正在运行，请彻底退出并重启 Codex 后生效')
    } else {
      message.success('已写入 Codex 配置并生效')
    }
    await load()
  } catch (e) {
    message.error('应用失败：' + (e instanceof Error ? e.message : String(e)))
  } finally {
    applying.value = false
    if (pageLoading) pageLoading.value = false
  }
}

async function importCurrent() {
  if (readonly || importing.value) return
  importing.value = true
  if (pageLoading) pageLoading.value = true
  try {
    const r = await post<{ id: string }>('/api/codex-config/import-current')
    message.success('已从当前 Codex 配置导入连接')
    await load()
    selectedId.value = r.id
    select(r.id)
  } catch (e) {
    message.error('导入失败：' + (e instanceof Error ? e.message : String(e)))
  } finally {
    importing.value = false
    if (pageLoading) pageLoading.value = false
  }
}

async function loadDiff() {
  if (!selected.value || selected.value.id.startsWith('new:')) return
  try {
    const r = await get<{ rows: DiffRow[] }>(`/api/codex-config/diff/${selected.value.id}`)
    diffRows.value = r.rows
  } catch (e) {
    message.error(e instanceof Error ? e.message : '读取变更预览失败')
  }
}

async function removeConnection() {
  deleteShow.value = false
  if (!selected.value) return
  try {
    await del(`/api/codex-config/connections/${selected.value.id}`)
    message.success('连接已删除')
    await load()
  } catch (e) {
    message.error(e instanceof Error ? e.message : '删除失败')
  }
}

const changeText: Record<DiffRow['change'], string> = {
  keep: '不变', set: '新增', clear: '移除', change: '修改',
}

onMounted(() => load())
</script>

<template>
  <p v-if="readonly" class="banner">浏览器直连为只读，切换 Codex 连接请在 AgentHub 窗口内操作。</p>
  <p v-if="loadError" class="usage-error">读取失败：{{ loadError }}</p>

  <div v-if="status" class="page">
    <section class="card status-card">
      <div class="card-body status-grid">
        <span class="lbl">固定 Provider ID</span><span class="mono">{{ status.providerId }}</span>
        <span class="lbl">live model_provider</span>
        <span class="mono" :class="{ 'is-warn': !status.liveProviderMatches }">
          {{ status.liveProvider ?? '（文件不存在）' }}<template v-if="!status.liveProviderMatches"> · 会导致部分历史在 Codex Desktop 中不可见，应用任一连接后恢复</template>
        </span>
        <span class="lbl">Codex 登录</span><span>{{ authTypeText }}</span>
        <span class="lbl">Codex 进程</span>
        <span>{{ status.codexRunning ? '运行中（切换后需重启 Codex 生效）' : '未运行' }}</span>
        <span class="lbl">配置路径</span><span class="mono path" :title="status.configPath">{{ status.configPath }}</span>
      </div>
    </section>

    <p v-if="status.configBroken" class="usage-error">config.toml 语法损坏，已阻止一切写入。请先在 Codex 中修复该文件。</p>
    <p v-else-if="status.externalChanged" class="banner">检测到 config.toml 在 AgentHub 之外被修改过（如 CC Switch 或手工编辑）。下次应用连接时会基于最新文件重写受管字段。</p>
    <p v-else-if="status.live?.isHybridForm" class="banner">当前 live 是「中转地址 + requires_openai_auth」的混合形态；应用 AgentHub 连接后会重塑为标准形态（中转走命令式认证，官方走登录态）。</p>

    <div class="columns">
      <section class="card list-card">
        <div class="card-head">
          连接
          <span class="spacer" />
          <n-button quaternary :disabled="readonly" aria-label="新建中转连接" @click="newConnection">
            <template #icon><n-icon :size="16"><Plus :stroke-width="1.8" /></n-icon></template>
          </n-button>
        </div>
        <div class="card-body conn-list">
          <button
            v-for="c in connections"
            :key="c.id"
            type="button"
            class="conn-item"
            :class="{ 'is-active': c.id === selectedId, 'is-current': c.active }"
            @click="select(c.id)"
          >
            <span class="conn-name">{{ c.name || '（未命名）' }}</span>
            <span class="conn-sub">{{ c.kind === 'official' ? '官方订阅' : c.baseUrl }}</span>
            <span v-if="c.active" class="conn-badge">当前</span>
          </button>
          <div class="import-row">
            <n-button :loading="importing" :disabled="readonly" @click="importCurrent">
              <template #icon><n-icon :size="16"><CloudDownload :stroke-width="1.8" /></n-icon></template>
              从当前配置导入
            </n-button>
          </div>
        </div>
      </section>

      <section v-if="selected" class="card editor-card">
        <div class="card-head">
          {{ selected.id.startsWith('new:') ? '新建中转连接' : selected.kind === 'official' ? '官方订阅' : '中转连接' }}
          <span v-if="selected.active" class="hint"> · 当前生效</span>
        </div>
        <div class="card-body">
          <template v-if="isOfficial">
            <p class="hint explain">
              官方订阅使用 Codex 自己的 ChatGPT 登录（auth.json），AgentHub 不读写任何凭据。
              应用后会移除中转 base_url、静态请求头与命令式认证，Codex 将直连官方。
            </p>
          </template>
          <template v-else>
            <div class="row row--fill">
              <div class="meta"><label class="lbl" for="cx-name">名称</label></div>
              <div class="ctrl ctrl--field"><n-input id="cx-name" :disabled="readonly" :spellcheck="false" v-model:value="f.name" placeholder="如 Sub2API 中转" /></div>
            </div>
            <div class="row row--fill">
              <div class="meta"><label class="lbl" for="cx-url">Responses 地址</label></div>
              <div class="ctrl ctrl--field"><n-input id="cx-url" :disabled="readonly" :spellcheck="false" v-model:value="f.baseUrl" placeholder="https://…" /></div>
            </div>
            <div class="row row--fill">
              <div class="meta">
                <label class="lbl" for="cx-model">默认模型</label>
                <span class="hint">留空保持 Codex 当前模型不变</span>
              </div>
              <div class="ctrl ctrl--field"><n-input id="cx-model" :disabled="readonly" :spellcheck="false" v-model:value="f.defaultModel" placeholder="gpt-5.2 等" /></div>
            </div>
            <div class="row">
              <div class="meta">
                <span class="lbl">WebSocket</span>
                <span class="hint">中转支持 Responses WebSocket 时开启</span>
              </div>
              <div class="ctrl"><n-switch :disabled="readonly" v-model:value="f.supportsWebSockets" /></div>
            </div>
            <div class="row row--fill">
              <div class="meta"><label class="lbl" for="cx-ua">User-Agent</label><span class="hint">写入静态 http_headers；留空删除该头</span></div>
              <div class="ctrl ctrl--field"><n-input id="cx-ua" :disabled="readonly" :spellcheck="false" v-model:value="f.userAgent" placeholder=" " /></div>
            </div>
            <div class="row row--fill">
              <div class="meta"><label class="lbl" for="cx-org">Originator</label></div>
              <div class="ctrl ctrl--field"><n-input id="cx-org" :disabled="readonly" :spellcheck="false" v-model:value="f.originator" placeholder=" " /></div>
            </div>
            <div class="row row--fill">
              <div class="meta">
                <label class="lbl" for="cx-key">API Key</label>
                <span class="hint">DPAPI 加密保存；经 codex-credential 命令提供给 Codex</span>
              </div>
              <div class="ctrl ctrl--secret">
                <n-input
                  id="cx-key"
                  type="password"
                  show-password-on="click"
                  :disabled="readonly"
                  autocomplete="off"
                  :placeholder="selected.keySet ? '已配置（留空不修改）' : ' '"
                  v-model:value="f.apiKey"
                />
                <span v-if="selected.keySet" class="lock"><n-icon :size="16"><Lock :stroke-width="1.8" /></n-icon> DPAPI</span>
              </div>
            </div>
            <div class="row row--fill">
              <div class="meta">
                <label class="lbl" for="cx-usage">余额查询地址</label>
                <span class="hint">可选，仅查询用，不写入 Codex</span>
              </div>
              <div class="ctrl ctrl--field"><n-input id="cx-usage" :disabled="readonly" :spellcheck="false" v-model:value="f.usageBaseUrl" placeholder=" " /></div>
            </div>
          </template>

          <div class="actions">
            <n-button v-if="!isOfficial" type="primary" :loading="saving" :disabled="readonly" @click="save">
              <template #icon><n-icon :size="16"><Save :stroke-width="1.8" /></n-icon></template>
              保存
            </n-button>
            <n-button type="primary" :loading="applying" :disabled="readonly || selected.id.startsWith('new:')" @click="applyConnection">
              应用并切换
            </n-button>
            <n-button :disabled="readonly || isOfficial || selected.id.startsWith('new:')" @click="loadDiff">查看变更</n-button>
            <n-button v-if="!isOfficial" quaternary type="error" :disabled="readonly || selected.active || selected.id.startsWith('new:')" @click="deleteShow = true">
              <template #icon><n-icon :size="16"><Trash2 :stroke-width="1.8" /></n-icon></template>
              删除
            </n-button>
          </div>
          <p class="hint explain">「保存」只更新 AgentHub 内的连接记录；「应用并切换」才写入 Codex 的 config.toml。</p>

          <div v-if="diffRows" class="diff-block">
            <div class="meta"><span class="lbl">变更预览（应用时写入的受管字段）</span></div>
            <table class="diff-table">
              <thead><tr><th>字段</th><th>当前 live</th><th>应用后</th><th>动作</th></tr></thead>
              <tbody>
                <tr v-for="r in diffRows" :key="r.field" :class="'is-' + r.change">
                  <td class="mono">{{ r.field }}</td>
                  <td class="mono cell">{{ r.live ?? '—' }}</td>
                  <td class="mono cell">{{ r.candidate ?? '—' }}</td>
                  <td>{{ changeText[r.change] }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </section>
    </div>
  </div>

  <AhConfirm
    :show="deleteShow"
    text="确定删除该中转连接吗？只删除 AgentHub 内的记录，不影响 Codex 当前配置。"
    ok-text="删除"
    @update:show="deleteShow = $event"
    @confirm="removeConnection"
  />
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
.status-grid {
  display: grid;
  grid-template-columns: 160px minmax(0, 1fr);
  gap: var(--sp-2) var(--sp-5);
  align-items: baseline;
}
.status-grid .lbl { color: var(--faint); font-size: var(--fs-caption); }
.mono { font-family: var(--mono); font-size: var(--fs-caption); }
.mono.is-warn { color: var(--warn); }
.mono.path {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  display: block;
}
.columns {
  display: grid;
  grid-template-columns: 300px minmax(0, 1fr);
  gap: var(--sp-5);
  align-items: stretch;
  flex: 1 1 auto;
  min-height: 0;
}
.list-card,
.editor-card {
  display: flex;
  flex-direction: column;
  min-height: 0;
}
.list-card .card-body,
.editor-card .card-body {
  flex: 1 1 auto;
  min-height: 0;
  overflow: auto;
}
.conn-list {
  display: flex;
  flex-direction: column;
  gap: var(--sp-1);
}
.conn-item {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 2px;
  text-align: left;
  border: 1px solid transparent;
  background: transparent;
  border-radius: var(--r-in);
  padding: var(--sp-2) var(--sp-3);
  font: inherit;
  cursor: pointer;
  color: var(--text);
}
.conn-item:hover { background: var(--wash); }
.conn-item.is-active { background: var(--accent-soft); }
.conn-name { font-size: var(--fs-body); font-weight: 500; }
.conn-item.is-current .conn-name { color: var(--accent-solid); }
.conn-sub {
  font-size: var(--fs-caption);
  color: var(--faint);
  font-family: var(--mono);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.conn-badge {
  position: absolute;
  right: var(--sp-3);
  top: var(--sp-2);
  font-size: var(--fs-caption);
  color: var(--ok);
}
.import-row { padding-top: var(--sp-3); }
.explain { margin: 0 0 var(--sp-3); }
.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--sp-3);
  padding-top: var(--sp-3);
}
.diff-block { padding-top: var(--sp-4); display: flex; flex-direction: column; gap: var(--sp-2); }
.diff-table {
  width: 100%;
  border-collapse: collapse;
  font-size: var(--fs-caption);
}
.diff-table th,
.diff-table td {
  text-align: left;
  padding: var(--sp-1) var(--sp-2);
  border-bottom: 1px solid var(--stroke);
  vertical-align: top;
}
.diff-table th { color: var(--faint); font-weight: 400; }
.diff-table .cell { max-width: 320px; overflow-wrap: anywhere; white-space: pre-wrap; }
.diff-table tr.is-change td:nth-child(3),
.diff-table tr.is-set td:nth-child(3) { color: var(--accent-solid); }
.diff-table tr.is-clear td:nth-child(3) { color: var(--error-fg); }
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
.lock {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-1);
  white-space: nowrap;
  font-size: var(--fs-caption);
  color: var(--ok);
}
.meta { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.lbl { color: var(--text); font-size: var(--fs-body); font-weight: 500; }
.hint { font-size: var(--fs-caption); color: var(--faint); font-weight: 400; }
.ctrl { display: flex; align-items: center; justify-content: flex-end; min-width: 0; }
.ctrl--field,
.ctrl--secret {
  display: grid;
  width: 100%;
  min-width: 0;
  gap: var(--sp-2);
  align-items: center;
}
.ctrl--field { grid-template-columns: minmax(0, 1fr); }
.ctrl--secret { grid-template-columns: minmax(0, 1fr) auto; }
.row {
  display: grid;
  grid-template-columns: minmax(168px, 240px) minmax(0, 1fr);
  gap: var(--sp-3) var(--sp-6);
  align-items: center;
  min-height: var(--h-row);
  padding: var(--sp-3) 0;
  box-shadow: var(--rule-hi);
}
.row:last-of-type { box-shadow: none; }
.row--fill {
  grid-template-columns: minmax(0, 1fr);
  gap: var(--sp-2);
}
.row--fill .ctrl {
  justify-content: stretch;
  width: 100%;
}
.row--fill :deep(.n-input) { width: 100%; }
.spacer { flex: 1; }

@media (max-width: 1279px) {
  .columns { grid-template-columns: 1fr; }
  .row { grid-template-columns: 1fr; }
  .status-grid { grid-template-columns: 1fr; gap: 2px; }
}
</style>
