<script setup lang="ts">
import { computed, inject, nextTick, onMounted, onUnmounted, reactive, ref, watch, type Ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { NButton, NIcon, NInput, NInputNumber, NModal, NProgress, NSwitch, useMessage } from 'naive-ui'
import { FileCog, Lock } from 'lucide-vue-next'
import AhConfirm from '../components/AhConfirm.vue'
import { get, post, put, WRITABLE } from '../api'
import { usePageHotkeys } from '../hotkeys'
import { invalidateDashCache } from '../dashCache'
import { normalizeTokenUnit, setTokenUnit, tokenUnit, type TokenUnit } from '../tokenUnit'

const message = useMessage()
const pageLoading = inject<Ref<boolean>>('page-loading')
const readonly = !WRITABLE

interface PriceSyncInfo {
  source?: string
  lastFetchOk?: boolean | null
  lastFetchAt?: string | null
  lastFetchError?: string | null
  hasDiskCache?: boolean
  cachePath?: string
  userCatalogPath?: string
  userCatalogExists?: boolean
  localOverrideActive?: boolean
  catalogError?: string | null
  usingLastGoodCatalog?: boolean
  catalogUpdatedAt?: string
  modelCount?: number
}

interface SettingsPayload {
  app: {
    autostart?: boolean
  }
  dashboard: {
    costEstimate: boolean
    priceSync?: PriceSyncInfo
    tokenUnit?: string
    scanIntervalMinutes: number
  }
  credentials: {
    relayPanelBaseUrl: string
    deepseekKeySet: boolean
    relayKeySet: boolean
    workbuddySessionSet: boolean
    cursorCloudApiKeySet?: boolean
  }
  autostartActual: boolean
  autostartSupported?: boolean
  updateSupported?: boolean
  secretsSupported?: boolean
  configPath: string
  appVersion?: string
  updateInstalled?: boolean
}

interface AppUpdateStatus {
  installed?: boolean
  busy?: boolean
  canApply?: boolean
  needsInstaller?: boolean
  current?: string
  latest?: string
  releaseUrl?: string
  error?: string
  message?: string
}

const loaded = ref(false)
const loadError = ref('')
const saving = ref(false)
const activeSection = ref('general')
const snapshot = ref('')
const configPath = ref('')
const appVersion = ref('')
const updateInstalled = ref(false)
const updateLatest = ref('')
const updateReleaseUrl = ref('')
const updateHint = ref('')
const updateBusy = ref(false)
const updateCanApply = ref(false)
const applyShow = ref(false)
const readyShow = ref(false)
const downloadShow = ref(false)
const progressShow = ref(false)
const progressPercent = ref(0)
const progressText = ref('')
const progressPhase = ref('')
const updateReady = ref(false)
const cancelBusy = ref(false)
const cancelRequested = ref(false)
let progressTimer: number | null = null
let progressPolling = false
const priceSync = ref<PriceSyncInfo | null>(null)
const LATEST_RELEASE_URL = 'https://github.com/sept13-yu/AgentHub/releases/latest'
const autostartSupported = ref(true)
const updateSupported = ref(true)
const secretsSupported = ref(true)

const f = reactive({
  relayPanelBaseUrl: '',
  autostart: false,
  costEstimate: false,
  tokenUnit: 'zh' as TokenUnit,
  scanIntervalMinutes: 15,
  deepseekKey: '',
  relayKey: '',
  workbuddySession: '',
  cursorCloudApiKey: '',
  deepseekKeySet: false,
  relayKeySet: false,
  workbuddySessionSet: false,
  cursorCloudApiKeySet: false,
})

const sectionNav = [
  { id: 'general', label: '常规外观' },
  { id: 'credentials', label: '凭据与外部服务' },
  { id: 'usage', label: '用量额度' },
]

const secretKeys = ['deepseekKey', 'relayKey', 'workbuddySession', 'cursorCloudApiKey'] as const
const clearCursorCloudKey = ref(false)

function clearCursorCloudApiKey() {
  if (readonly) return
  f.cursorCloudApiKey = ''
  clearCursorCloudKey.value = true
  f.cursorCloudApiKeySet = false
}

watch(() => f.cursorCloudApiKey, (v) => {
  if (v.trim().length > 0) clearCursorCloudKey.value = false
})

function pickTokenUnit(next: TokenUnit) {
  f.tokenUnit = next
  setTokenUnit(next)
}

function snapOf(): string {
  const copy = JSON.parse(JSON.stringify(f)) as Record<string, unknown>
  for (const key of secretKeys) copy[key] = ''
  return JSON.stringify(copy)
}

const dirty = computed(() => {
  if (!loaded.value) return false
  // 密钥不进快照串（snapOf 两侧都置空），单独判断：输入框非空即视为有改动，
  // 否则只改 Key 时永远不会出现保存按钮
  if (secretKeys.some((key) => f[key].trim().length > 0)) return true
  if (clearCursorCloudKey.value) return true
  return snapOf() !== snapshot.value
})

function applyLoaded(s: SettingsPayload) {
  f.relayPanelBaseUrl = s.credentials.relayPanelBaseUrl
  f.autostart = s.autostartActual
  autostartSupported.value = s.autostartSupported !== false
  updateSupported.value = s.updateSupported !== false
  secretsSupported.value = s.secretsSupported !== false
  const d = s.dashboard
  f.costEstimate = !!d.costEstimate
  if (d.tokenUnit === 'en' || d.tokenUnit === 'zh') {
    f.tokenUnit = d.tokenUnit
    setTokenUnit(d.tokenUnit)
  } else {
    f.tokenUnit = normalizeTokenUnit(tokenUnit.value)
  }
  f.scanIntervalMinutes = Math.max(0, Math.min(1440, Number(d.scanIntervalMinutes) || 0))
  f.deepseekKey = ''
  f.relayKey = ''
  f.workbuddySession = ''
  f.cursorCloudApiKey = ''
  f.deepseekKeySet = s.credentials.deepseekKeySet
  f.relayKeySet = s.credentials.relayKeySet
  f.workbuddySessionSet = !!s.credentials.workbuddySessionSet
  f.cursorCloudApiKeySet = !!s.credentials.cursorCloudApiKeySet
  configPath.value = s.configPath
  appVersion.value = s.appVersion || ''
  updateInstalled.value = !!s.updateInstalled
  priceSync.value = s.dashboard.priceSync ?? null
  snapshot.value = snapOf()
}

function priceSyncHint(): string {
  const s = priceSync.value
  if (!s) return '模型目录将在启动或刷新用量时同步'
  if (s.catalogError) {
    const why = s.catalogError.length > 60 ? s.catalogError.slice(0, 60) + '…' : s.catalogError
    return s.usingLastGoodCatalog
      ? `本地补丁未生效，仍用上次有效目录 · ${why}`
      : `本地补丁已忽略 · ${why}`
  }
  const src = (s.source || '').toLowerCase()
  const suffix = s.localOverrideActive ? ' · 本地补丁生效' : ''
  if (src === 'github' || src === 'gitee' || src === 'remote') {
    const where = src === 'gitee' ? 'Gitee' : 'GitHub'
    return (s.lastFetchAt ? `已从 ${where} 拉取 · ${s.lastFetchAt}` : `已从 ${where} 拉取`) + suffix
  }
  if (src === 'cache')
    return (s.lastFetchAt
      ? `用上次拉到的本地副本 · 上次拉取 ${s.lastFetchAt}`
      : '用上次拉到的本地副本') + suffix
  if (s.lastFetchOk === false)
    return '使用内置模型目录 · 同步失败' + suffix
  return '用内置模型目录 · 尚未拉到远程（启动后会自动拉）' + suffix
}

async function load() {
  loadError.value = ''
  if (pageLoading) pageLoading.value = true
  try {
    const settings = await get<SettingsPayload>('/api/settings')
    applyLoaded(settings)
    loaded.value = true
    await nextTick()
    bindSectionObserver()
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    if (pageLoading) pageLoading.value = false
  }
}

let sectionObserver: IntersectionObserver | undefined

function bindSectionObserver() {
  sectionObserver?.disconnect()
  sectionObserver = new IntersectionObserver((entries) => {
    const visible = entries
      .filter((e) => e.isIntersecting)
      .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0]
    const id = visible?.target.id
    if (id?.startsWith('settings-')) activeSection.value = id.slice('settings-'.length)
  }, { rootMargin: '-15% 0px -65% 0px', threshold: 0 })
  document.querySelectorAll('[id^="settings-"]').forEach((el) => sectionObserver!.observe(el))
}

async function save() {
  if (readonly || saving.value) return
  saving.value = true
  if (pageLoading) pageLoading.value = true
  try {
    await put<{ ok: boolean }>('/api/settings', {
      app: {
        ...(autostartSupported.value ? { autostart: !!f.autostart } : {}),
      },
      dashboard: {
        costEstimate: !!f.costEstimate,
        tokenUnit: f.tokenUnit,
        scanIntervalMinutes: Math.max(0, Math.min(1440, Number(f.scanIntervalMinutes) || 0)),
      },
      credentials: secretsSupported.value
        ? {
            relayPanelBaseUrl: f.relayPanelBaseUrl,
            ...(f.deepseekKey.trim() ? { deepseekKey: f.deepseekKey.trim() } : {}),
            ...(f.relayKey.trim() ? { relayKey: f.relayKey.trim() } : {}),
            ...(f.workbuddySession.trim() ? { workbuddySession: f.workbuddySession.trim() } : {}),
            ...(f.cursorCloudApiKey.trim()
              ? { cursorCloudApiKey: f.cursorCloudApiKey.trim() }
              : clearCursorCloudKey.value
                ? { cursorCloudApiKey: null }
                : {}),
          }
        : { relayPanelBaseUrl: f.relayPanelBaseUrl },
    })
    setTokenUnit(f.tokenUnit)
    clearCursorCloudKey.value = false
    invalidateDashCache()
    window.dispatchEvent(new CustomEvent('agenthub-refresh'))
    await load()
    message.success('已保存并应用')
  } catch (e) {
    message.error('保存失败：' + (e instanceof Error ? e.message : String(e)))
  } finally {
    saving.value = false
    if (pageLoading) pageLoading.value = false
  }
}

async function openConfig() {
  if (readonly) return
  try {
    await post('/api/settings/open-config')
  } catch (e) {
    message.error(e instanceof Error ? e.message : '打开配置失败')
  }
}

function applyUpdateStatus(r: AppUpdateStatus) {
  if (r.current) appVersion.value = r.current
  if (r.installed != null) updateInstalled.value = r.installed
  updateLatest.value = r.latest || ''
  if (r.releaseUrl != null) updateReleaseUrl.value = r.releaseUrl
  updateCanApply.value = !!r.canApply
  const text = r.error || r.message || ''
  updateHint.value = r.error ? text : [appVersion.value && `当前 ${appVersion.value}`, text].filter(Boolean).join(' · ')
  return text
}

function clearProgressTimer() {
  if (progressTimer != null) {
    window.clearInterval(progressTimer)
    progressTimer = null
  }
}

async function pollUpdateProgress() {
  if (progressPolling) return
  progressPolling = true
  try {
    const p = await get<{ running?: boolean; percent?: number; phase?: string; message?: string }>(
      '/api/update/progress',
    )
    if (typeof p.percent === 'number') progressPercent.value = p.percent
    if (p.phase) progressPhase.value = p.phase
    progressText.value = cancelRequested.value ? '正在取消下载…' : p.message || {
      checking: '正在检查更新…',
      downloading: '正在下载安装包…',
      verifying: '正在校验安装包…',
      ready: '安装包已准备好',
      launching: '正在打开安装向导…',
      error: '更新失败',
    }[p.phase || ''] || '正在准备更新…'
  } catch {
    /* 轮询失败不打断主请求 */
  } finally {
    progressPolling = false
  }
}

async function checkUpdate() {
  if (updateBusy.value) return
  updateBusy.value = true
  updateHint.value = '正在检查…'
  updateReady.value = false
  updateReleaseUrl.value = ''
  try {
    const r = await get<AppUpdateStatus>('/api/update')
    const text = applyUpdateStatus(r)
    if (r.error) {
      message.error(text || '检查更新失败')
      return
    }
    if (r.needsInstaller && r.latest) {
      downloadShow.value = true
      return
    }
    if (r.canApply && r.latest) {
      message.success(`发现新版本 ${r.latest}`)
      applyShow.value = true
      return
    }
    if (r.latest) message.success(text || '已是最新版本')
  } catch (e) {
    const text = e instanceof Error ? e.message : '检查更新失败'
    updateHint.value = text
    message.error(text)
  } finally {
    updateBusy.value = false
  }
}

async function openReleasePage() {
  downloadShow.value = false
  const url = updateReleaseUrl.value || LATEST_RELEASE_URL
  try {
    if (WRITABLE) await post('/api/settings/open-release', { url })
    else window.open(url, '_blank', 'noopener')
  } catch {
    window.open(url, '_blank', 'noopener')
  }
}

async function applyUpdate() {
  if (readonly || updateBusy.value) return
  applyShow.value = false
  updateBusy.value = true
  updateReady.value = false
  cancelRequested.value = false
  updateHint.value = '正在下载安装包…'
  progressShow.value = true
  progressPercent.value = 0
  progressPhase.value = 'checking'
  progressText.value = '正在检查更新…'
  clearProgressTimer()
  progressTimer = window.setInterval(() => { void pollUpdateProgress() }, 500)
  try {
    const r = await post<AppUpdateStatus>('/api/settings/apply-update')
    clearProgressTimer()
    const text = applyUpdateStatus(r)
    if (r.needsInstaller && r.latest) {
      progressShow.value = false
      downloadShow.value = true
      return
    }
    if (r.error) {
      progressPhase.value = 'error'
      progressText.value = text || '下载安装包失败'
      return
    }
    if (!r.canApply) {
      progressShow.value = false
      message.info(text || '当前没有可安装的更新')
      return
    }
    updateReady.value = true
    progressPhase.value = 'ready'
    progressText.value = text || '安装包已准备好'
    progressShow.value = false
    readyShow.value = true
  } catch (e) {
    clearProgressTimer()
    const text = e instanceof Error ? e.message : '下载安装包失败'
    progressPhase.value = 'error'
    progressText.value = text
    updateHint.value = text
  } finally {
    clearProgressTimer()
    updateBusy.value = false
    cancelRequested.value = false
  }
}

async function cancelUpdate() {
  if (!updateBusy.value || cancelBusy.value || cancelRequested.value || progressPhase.value !== 'downloading') return
  cancelBusy.value = true
  try {
    await post('/api/settings/cancel-update')
    cancelRequested.value = true
    progressText.value = '正在取消下载…'
  } catch (e) {
    message.error(e instanceof Error ? e.message : '取消下载失败')
  } finally {
    cancelBusy.value = false
  }
}

async function launchUpdate() {
  if (readonly || updateBusy.value || !updateReady.value) return
  readyShow.value = false
  updateBusy.value = true
  progressShow.value = true
  progressPhase.value = 'launching'
  progressText.value = '正在打开安装向导…'
  try {
    const r = await post<AppUpdateStatus>('/api/settings/launch-update')
    if (r.error) throw new Error(r.error)
    updateReady.value = false
    progressShow.value = false
    updateHint.value = r.message || '安装向导已启动'
    message.success(updateHint.value)
  } catch (e) {
    const text = e instanceof Error ? e.message : '无法打开安装向导'
    updateReady.value = false
    progressPhase.value = 'error'
    progressText.value = text
    updateHint.value = text
  } finally {
    updateBusy.value = false
  }
}

function discard() {
  if (!snapshot.value) return
  Object.assign(f, JSON.parse(snapshot.value) as typeof f)
  f.deepseekKey = ''
  f.relayKey = ''
  f.workbuddySession = ''
  f.cursorCloudApiKey = ''
  clearCursorCloudKey.value = false
  setTokenUnit(f.tokenUnit)
}

function scrollToSection(id: string) {
  activeSection.value = id
  document.getElementById('settings-' + id)?.scrollIntoView({
    behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
    block: 'start',
  })
}

const leaveShow = ref(false)
let leaveResolve: ((ok: boolean) => void) | null = null

onBeforeRouteLeave(() => {
  if (!dirty.value) return true
  leaveShow.value = true
  return new Promise<boolean>((resolve) => { leaveResolve = resolve })
})

function leaveOk() {
  leaveShow.value = false
  leaveResolve?.(true)
  leaveResolve = null
}

function leaveCancel() {
  leaveShow.value = false
  leaveResolve?.(false)
  leaveResolve = null
}

function onBeforeUnload(e: BeforeUnloadEvent) {
  if (readonly || !dirty.value) return
  e.preventDefault()
  e.returnValue = ''
}

usePageHotkeys({
  refresh: () => {
    if (dirty.value) {
      message.warning('有未保存的修改，先保存或放弃')
      return
    }
    void load()
  },
})

onMounted(async () => {
  await load()
  window.addEventListener('beforeunload', onBeforeUnload)
})
onUnmounted(() => {
  window.removeEventListener('beforeunload', onBeforeUnload)
  sectionObserver?.disconnect()
  clearProgressTimer()
})
</script>

<template>
  <teleport defer to="#chrome-tabs">
    <div class="tabs" role="group" aria-label="设置分区">
      <button
        v-for="t in sectionNav"
        :key="t.id"
        type="button"
        :aria-pressed="activeSection === t.id ? 'true' : 'false'"
        @click="scrollToSection(t.id)"
      >{{ t.label }}</button>
    </div>
  </teleport>
  <teleport defer to="#chrome-actions">
    <template v-if="dirty">
      <n-button :disabled="readonly || saving" @click="discard">放弃修改</n-button>
      <n-button type="primary" :disabled="readonly" :loading="saving" @click="save">保存并应用</n-button>
    </template>
  </teleport>

  <p v-if="readonly" class="banner">浏览器直连为只读，改设置请在 AgentHub 窗口内操作。</p>
  <p v-if="loadError" class="usage-error">读取设置失败：{{ loadError }}</p>

  <form v-else-if="loaded" class="settings" @submit.prevent="save">
    <section id="settings-general" class="card set-card">
      <div class="card-head">常规外观</div>
      <div class="card-body general-grid">
        <div v-if="autostartSupported" class="row">
          <div class="meta">
            <label class="lbl" for="s-autostart">开机自启</label>
            <span class="hint">登录系统时启动 AgentHub</span>
          </div>
          <div class="ctrl"><n-switch id="s-autostart" :disabled="readonly" v-model:value="f.autostart" /></div>
        </div>
        <div class="row">
          <div class="meta">
            <span class="lbl">程序配置</span>
            <span class="hint">修改后重启生效</span>
          </div>
          <div class="ctrl">
            <n-button type="button" :disabled="readonly" @click="openConfig">
              <template #icon><n-icon><FileCog :size="16" :stroke-width="1.8" /></n-icon></template>
              打开配置文件
            </n-button>
          </div>
        </div>
        <div class="row">
          <div class="meta">
            <span class="lbl">版本与更新</span>
            <span class="hint">{{ updateHint || (appVersion ? `当前 ${appVersion}` : '检查最新版本') }}</span>
          </div>
          <div class="ctrl ctrl--actions">
            <template v-if="updateSupported">
            <n-button type="button" :disabled="updateBusy" :loading="updateBusy" @click="checkUpdate">
              检查更新
            </n-button>
            </template>
            <n-button v-if="updateReady" type="primary" :disabled="updateBusy" @click="readyShow = true">
              打开安装向导
            </n-button>
            <n-button type="button" @click="openReleasePage">
              手动下载
            </n-button>
          </div>
        </div>
      </div>
    </section>

    <section id="settings-credentials" class="card set-card">
      <div class="card-head">
        凭据与外部服务
        <span class="spacer" />
        <span class="hint">凭据留空保留</span>
      </div>
      <div class="card-body credentials-grid">
        <p v-if="!secretsSupported" class="usage-error">当前平台尚未支持凭据加密存储</p>
        <section class="service-group service-group--relay" aria-labelledby="relay-title">
          <div class="service-heading">
            <h3 id="relay-title">Sub2API</h3>
            <span class="hint">查询中转服务余额</span>
          </div>
          <div class="service-fields relay-fields">
            <div class="credential-field">
              <label class="field-label" for="s-relay-base" title="自动拼接 /v1/usage 查询余额">服务地址</label>
              <n-input id="s-relay-base" class="url-input" :disabled="readonly" :spellcheck="false" v-model:value="f.relayPanelBaseUrl" placeholder="输入服务地址" />
            </div>
            <div class="credential-field">
              <div class="field-heading">
                <label class="field-label" for="s-relay">API Key</label>
                <span v-if="f.relayKeySet" class="lock" role="img" title="凭据已加密存储" aria-label="凭据已加密存储"><n-icon :size="14"><Lock :stroke-width="1.8" /></n-icon></span>
              </div>
              <n-input
                id="s-relay"
                type="password"
                show-password-on="click"
                :disabled="readonly || !secretsSupported"
                autocomplete="off"
                :placeholder="f.relayKeySet ? '已配置，留空保留' : '粘贴 API Key'"
                v-model:value="f.relayKey"
              />
            </div>
          </div>
        </section>
        <section class="service-group" aria-labelledby="deepseek-title">
          <div class="service-heading">
            <h3 id="deepseek-title">DeepSeek</h3>
            <span class="hint">查询账户余额</span>
          </div>
          <div class="credential-field">
            <div class="field-heading">
              <label class="field-label" for="s-dsk">API Key</label>
              <span v-if="f.deepseekKeySet" class="lock" role="img" title="凭据已加密存储" aria-label="凭据已加密存储"><n-icon :size="14"><Lock :stroke-width="1.8" /></n-icon></span>
            </div>
            <n-input
              id="s-dsk"
              type="password"
              show-password-on="click"
              :disabled="readonly || !secretsSupported"
              autocomplete="off"
              :placeholder="f.deepseekKeySet ? '已配置，留空保留' : '粘贴 API Key'"
              v-model:value="f.deepseekKey"
            />
          </div>
        </section>
        <section class="service-group" aria-labelledby="workbuddy-title">
          <div class="service-heading">
            <h3 id="workbuddy-title">WorkBuddy</h3>
            <span class="hint">查询积分与清理云端会话</span>
          </div>
          <div class="credential-field">
            <div class="field-heading">
              <label class="field-label" for="s-wb">Cookie · session</label>
              <span v-if="f.workbuddySessionSet" class="lock" role="img" title="凭据已加密存储" aria-label="凭据已加密存储"><n-icon :size="14"><Lock :stroke-width="1.8" /></n-icon></span>
            </div>
            <n-input
              id="s-wb"
              type="password"
              show-password-on="click"
              :disabled="readonly || !secretsSupported"
              autocomplete="off"
              :placeholder="f.workbuddySessionSet ? '已配置，留空保留' : '粘贴 session Cookie'"
              v-model:value="f.workbuddySession"
            />
          </div>
        </section>
        <section class="service-group service-group--cursor" aria-labelledby="cursor-title">
          <div class="service-heading">
            <h3 id="cursor-title">Cursor Cloud</h3>
            <span class="hint">在会话页查看云端 Agent</span>
          </div>
          <div class="credential-field">
            <div class="field-heading">
              <label class="field-label" for="s-ccloud" title="在 Cursor Dashboard 的 API Keys 页面获取">API Key</label>
              <span v-if="f.cursorCloudApiKeySet && !clearCursorCloudKey" class="lock" role="img" title="凭据已加密存储" aria-label="凭据已加密存储"><n-icon :size="14"><Lock :stroke-width="1.8" /></n-icon></span>
              <n-button
                v-if="(f.cursorCloudApiKeySet || clearCursorCloudKey) && !readonly"
                text
                size="tiny"
                :disabled="!secretsSupported || clearCursorCloudKey"
                @click="clearCursorCloudApiKey"
              >{{ clearCursorCloudKey ? '保存后清除' : '清除' }}</n-button>
            </div>
            <n-input
              id="s-ccloud"
              type="password"
              show-password-on="click"
              :disabled="readonly || !secretsSupported"
              autocomplete="off"
              :placeholder="f.cursorCloudApiKeySet && !clearCursorCloudKey ? '已配置，留空保留' : '粘贴 API Key'"
              v-model:value="f.cursorCloudApiKey"
            />
          </div>
        </section>
      </div>
    </section>

    <section id="settings-usage" class="card set-card">
      <div class="card-head">用量额度</div>
      <div class="card-body">
        <div class="row">
          <div class="meta">
            <label class="lbl" for="s-cost">成本估算</label>
            <span class="hint">按模型价格估算用量成本，供参考。</span>
            <span class="hint price-status">{{ priceSyncHint() }}</span>
            <details class="cost-details">
              <summary>查看计算规则</summary>
              <p>净输入 × 输入价 + 缓存读取 × 缓存读取价 + 缓存写入 × 缓存写入价 +（输出 + 推理）× 输出价。缺少缓存价格时使用输入价；Grok 优先采用厂商返回的美元成本。</p>
              <p>启动和手动刷新用量时同步价格表，依次尝试 GitHub、Gitee；失败时使用本地副本或内置价格表。</p>
              <p v-if="priceSync?.lastFetchOk === false && priceSync.lastFetchError">同步失败：{{ priceSync.lastFetchError }}</p>
            </details>
          </div>
          <div class="ctrl"><n-switch id="s-cost" :disabled="readonly" v-model:value="f.costEstimate" /></div>
        </div>
        <div class="row">
          <div class="meta">
            <span class="lbl">Token 单位</span>
            <span class="hint">中文按万 / 百万 / 千万 / 亿；英文按 K / M / B</span>
          </div>
          <div class="ctrl">
            <div class="segs" role="radiogroup" aria-label="Token 单位">
              <button type="button" role="radio" :aria-checked="f.tokenUnit === 'zh'" :disabled="readonly" @click="pickTokenUnit('zh')">中文</button>
              <button type="button" role="radio" :aria-checked="f.tokenUnit === 'en'" :disabled="readonly" @click="pickTokenUnit('en')">英文</button>
            </div>
          </div>
        </div>
        <div class="row">
          <div class="meta">
            <label class="lbl" for="s-scan">扫描间隔</label>
            <span class="hint">分钟 · 0 只保留启动扫和手动刷新</span>
          </div>
          <div class="ctrl">
            <n-input-number
              id="s-scan"
              class="field-num num"
              :disabled="readonly"
              :min="0"
              :max="1440"
              :show-button="false"
              placeholder=" "
              v-model:value="f.scanIntervalMinutes"
            />
          </div>
        </div>
      </div>
    </section>
  </form>

  <AhConfirm
    :show="leaveShow"
    text="设置页有未保存的修改。确定离开吗？未保存的修改将丢失。"
    ok-text="离开"
    @update:show="(on: boolean) => { if (!on) leaveCancel(); else leaveShow = true }"
    @confirm="leaveOk"
  />
  <AhConfirm
    :show="applyShow"
    :text="updateLatest ? `下载并校验 ${updateLatest} 的完整安装包？` : '下载并校验完整安装包？'"
    ok-text="开始下载"
    @update:show="(on: boolean) => { applyShow = on }"
    @confirm="applyUpdate"
  />
  <AhConfirm
    :show="readyShow"
    text="安装包已校验。打开安装向导后，AgentHub 将退出；请在向导中完成安装。"
    ok-text="打开安装向导"
    @update:show="(on: boolean) => { readyShow = on }"
    @confirm="launchUpdate"
  />
  <AhConfirm
    :show="downloadShow"
    :text="updateLatest
      ? `发现新版本 ${updateLatest}。请前往发布页手动下载并安装。`
      : '请前往发布页手动下载。'"
    ok-text="打开版本页"
    @update:show="(on: boolean) => { downloadShow = on }"
    @confirm="openReleasePage"
  />
  <n-modal :show="progressShow" :mask-closable="false" :close-on-esc="false">
    <div class="update-progress" role="dialog" aria-modal="true" aria-label="更新进度">
      <p class="update-progress__title">{{ progressPhase === 'error' ? '更新未完成' : progressPhase === 'launching' ? '正在打开安装向导' : '准备安装包' }}</p>
      <p class="update-progress__text" :class="{ 'update-progress__text--error': progressPhase === 'error' }">{{ progressText || '正在准备更新…' }}</p>
      <n-progress
        v-if="progressPhase === 'downloading' || progressPhase === 'verifying'"
        type="line"
        :percentage="progressPercent"
        indicator-placement="inside"
        :processing="progressPhase === 'downloading'"
      />
      <div v-if="progressPhase === 'error'" class="update-progress__actions">
        <n-button type="button" @click="progressShow = false">关闭</n-button>
        <n-button type="button" :disabled="updateBusy" @click="updateReady ? launchUpdate() : applyUpdate()">
          {{ updateReady ? '重试打开' : '重新下载' }}
        </n-button>
      </div>
      <div v-else-if="progressPhase === 'downloading' && !cancelRequested" class="update-progress__actions">
        <n-button type="button" :loading="cancelBusy" :disabled="cancelBusy" @click="cancelUpdate">取消下载</n-button>
      </div>
    </div>
  </n-modal>
</template>

<style scoped>
.update-progress {
  width: min(400px, calc(100vw - 48px));
  padding: var(--sp-5);
  background: var(--surface);
  border: 1px solid var(--stroke);
  border-radius: var(--r-card);
}
.update-progress__title {
  margin: 0 0 var(--sp-2);
  font-size: var(--fs-body);
  font-weight: 600;
  color: var(--text);
}
.update-progress__text {
  margin: 0 0 var(--sp-4);
  font-size: var(--fs-small);
  color: var(--dim);
  line-height: 1.5;
}
.update-progress__text--error { color: var(--danger); }
.update-progress__actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--sp-2);
  margin-top: var(--sp-4);
}
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
  font-size: var(--fs-body);
}
.settings {
  display: flex;
  flex-direction: column;
  gap: var(--sp-5);
  width: 100%;
  min-width: 0;
  container: settings / inline-size;
}
.set-card { scroll-margin-top: var(--sp-3); }
.set-card:hover { border-color: var(--stroke); }
.general-grid,
.credentials-grid { display: grid; gap: var(--sp-5) var(--sp-6); }
.credentials-grid > .usage-error { grid-column: 1 / -1; }
.service-group { min-width: 0; }
.service-group + .service-group { padding-top: var(--sp-5); border-top: 1px solid var(--stroke); }
.service-heading { display: flex; flex-direction: column; gap: var(--sp-1); margin-bottom: var(--sp-4); }
.service-heading h3 { margin: 0; font-size: var(--fs-body); font-weight: 600; line-height: 1.5; }
.service-fields { display: grid; gap: var(--sp-4) var(--sp-6); }
.credential-field { display: flex; flex-direction: column; gap: var(--sp-2); min-width: 0; }
.field-heading { display: flex; align-items: center; gap: var(--sp-2); min-height: 20px; }
.field-heading :deep(.n-button) { margin-left: auto; font-size: var(--fs-caption); }
.field-label { font-size: var(--fs-caption); color: var(--dim); min-height: 20px; }
.credential-field :deep(.n-input) { width: 100%; }
.general-grid .row { padding-block: 0; box-shadow: none; }
.price-status { overflow-wrap: anywhere; max-width: 78ch; }
.cost-details {
  margin-top: var(--sp-2);
  max-width: 78ch;
  font-size: var(--fs-caption);
  color: var(--dim);
}
.cost-details summary { width: fit-content; cursor: pointer; }
.cost-details summary:hover { color: var(--text); }
.cost-details summary:focus-visible { outline: 2px solid var(--accent-solid); outline-offset: 3px; }
.cost-details p { margin: var(--sp-2) 0 0; line-height: 1.7; }
@container settings (min-width: 680px) {
  .credentials-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .service-group--relay, .service-group--cursor { grid-column: 1 / -1; }
  .relay-fields { grid-template-columns: repeat(2, minmax(0, 1fr)); }
}
@container settings (min-width: 760px) {
  .general-grid { grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); }
  .general-grid .row { grid-template-columns: 1fr; align-content: start; gap: var(--sp-3); }
  .general-grid .row + .row { padding-left: var(--sp-6); border-left: 1px solid var(--stroke); }
  .general-grid .meta { min-height: 42px; }
  .general-grid .ctrl { justify-content: flex-start; min-height: var(--h-control); }
  .general-grid .ctrl :deep(.n-button:first-child) { padding-left: 0; }
}
@container settings (min-width: 900px) {
  .credentials-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .service-group--cursor { grid-column: auto; }
}

.tabs {
  display: inline-flex;
  align-items: stretch;
  gap: var(--sp-4);
  height: var(--h-control);
}
.tabs button {
  border: 0;
  background: transparent;
  color: var(--dim);
  font: inherit;
  font-size: var(--fs-small);
  padding: 0;
  height: var(--h-control);
  cursor: pointer;
  box-shadow: inset 0 -2px 0 transparent;
  transition:
    color var(--dur) linear,
    box-shadow var(--dur) linear;
}
.tabs button:hover,
.tabs button:active {
  color: var(--text);
}
.tabs button[aria-pressed='true'] {
  color: var(--text);
  font-weight: 500;
  box-shadow: inset 0 -2px 0 var(--accent-solid);
}

.row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: var(--sp-3) var(--sp-6);
  align-items: center;
  min-height: var(--h-row);
  padding: var(--sp-3) 0;
  box-shadow: var(--rule-hi);
}
.row:last-child { box-shadow: none; }
.meta {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.lbl {
  color: var(--text);
  font-size: var(--fs-body);
  font-weight: 500;
}
.hint {
  font-size: var(--fs-caption);
  color: var(--faint);
  font-weight: 400;
}
.ctrl {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  min-width: 0;
}
.ctrl--actions { gap: var(--sp-2); flex-wrap: wrap; }
.field-num { width: 120px; }
:deep(.field-num.n-input-number) { width: 120px; }
/* 地址按路径文本展示，数字仍使用正文的等宽数字。 */
.url-input { font-family: var(--mono); }
.lock {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-1);
  white-space: nowrap;
  font-size: var(--fs-caption);
  color: var(--faint);
}
.segs {
  display: inline-flex;
  align-items: stretch;
  gap: var(--sp-4);
  height: var(--h-control);
}
.segs button {
  border: 0;
  background: transparent;
  color: var(--dim);
  font: inherit;
  font-size: var(--fs-small);
  padding: 0;
  height: var(--h-control);
  cursor: pointer;
  box-shadow: inset 0 -2px 0 transparent;
  transition:
    color var(--dur) linear,
    box-shadow var(--dur) linear;
}
.segs button:hover:not(:disabled),
.segs button:active:not(:disabled) {
  color: var(--text);
}
.segs button[aria-checked='true'] {
  color: var(--text);
  font-weight: 500;
  box-shadow: inset 0 -2px 0 var(--accent-solid);
}
.segs button:disabled { cursor: not-allowed; }
.segs button:disabled:not([aria-checked='true']) {
  color: var(--disabled-fg);
  box-shadow: none;
}
.segs button[aria-checked='true']:disabled {
  color: var(--dim);
  box-shadow: inset 0 -2px 0 var(--accent-line);
}

@container settings (max-width: 640px) {
  .row { grid-template-columns: 1fr; }
  .ctrl { justify-content: flex-start; }
  .general-grid .row { grid-template-columns: minmax(0, 1fr) auto; gap: var(--sp-3); }
  .general-grid .ctrl { justify-content: flex-end; max-width: 148px; }
  .general-grid .ctrl--actions { flex-direction: column; align-items: flex-end; gap: 0; }
  .general-grid .ctrl :deep(.n-button) { padding-inline: 0; max-width: 100%; }
  .lock { justify-self: start; }
}
</style>
