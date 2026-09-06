<script setup lang="ts">
import { computed, inject, nextTick, onMounted, onUnmounted, reactive, ref, type Ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { NButton, NIcon, NInput, NInputNumber, NSwitch, useMessage } from 'naive-ui'
import { ChevronDown, ChevronUp, FileCog, Lock } from 'lucide-vue-next'
import AhConfirm from '../components/AhConfirm.vue'
import { get, post, put, WRITABLE } from '../api'
import AgentMark from '../components/AgentMark.vue'
import {
  normalizeAgentOrder,
  SET_AGENTS,
  SET_PAYGO,
  type AgentId,
  type AgentShowKey,
  type PaygoShowKey,
} from '../settingsModel'
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
}

interface SettingsPayload {
  app: {
    petEnabled: boolean
    petSize: string
  }
  dashboard: {
    costEstimate: boolean
    priceSync?: PriceSyncInfo
    tokenUnit?: string
    scanIntervalMinutes: number
    showQuotaDeepSeek: boolean
    showQuotaCursor: boolean
    showQuotaRelay: boolean
    showQuotaWorkBuddy: boolean
    showQuotaTrae: boolean
    showQuotaZcode: boolean
    showQuotaCodex: boolean
    showAgentDsh?: boolean
    agentOrder?: string[]
  }
  credentials: {
    relayPanelBaseUrl: string
    deepseekKeySet: boolean
    relayKeySet: boolean
    workbuddySessionSet: boolean
  }
  autostartActual: boolean
  configPath: string
  canUninstall?: boolean
}

const loaded = ref(false)
const loadError = ref('')
const saving = ref(false)
const activeSection = ref('general')
const snapshot = ref('')
const configPath = ref('')
const canUninstall = ref(false)
const priceSync = ref<PriceSyncInfo | null>(null)
const uninstallShow = ref(false)
const uninstalling = ref(false)

const f = reactive({
  relayPanelBaseUrl: '',
  autostart: false,
  petEnabled: false,
  petSize: 'medium' as 'small' | 'medium' | 'large',
  costEstimate: false,
  tokenUnit: 'zh' as TokenUnit,
  scanIntervalMinutes: 15,
  showQuotaDeepSeek: true,
  showQuotaCursor: true,
  showQuotaRelay: true,
  showQuotaWorkBuddy: true,
  showQuotaTrae: true,
  showQuotaZcode: true,
  showQuotaCodex: true,
  showAgentDsh: true,
  agentOrder: normalizeAgentOrder([]),
  deepseekKey: '',
  relayKey: '',
  workbuddySession: '',
  deepseekKeySet: false,
  relayKeySet: false,
  workbuddySessionSet: false,
})

const sectionNav = [
  { id: 'general', label: '常规外观' },
  { id: 'credentials', label: '凭据与外部服务' },
  { id: 'usage', label: '用量额度' },
]

const secretKeys = ['deepseekKey', 'relayKey', 'workbuddySession'] as const

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
  return snapOf() !== snapshot.value
})

function applyLoaded(s: SettingsPayload) {
  f.relayPanelBaseUrl = s.credentials.relayPanelBaseUrl
  f.autostart = s.autostartActual
  f.petEnabled = !!s.app.petEnabled
  f.petSize = s.app.petSize === 'small' || s.app.petSize === 'large' ? s.app.petSize : 'medium'
  const d = s.dashboard
  f.costEstimate = !!d.costEstimate
  if (d.tokenUnit === 'en' || d.tokenUnit === 'zh') {
    f.tokenUnit = d.tokenUnit
    setTokenUnit(d.tokenUnit)
  } else {
    f.tokenUnit = normalizeTokenUnit(tokenUnit.value)
  }
  f.scanIntervalMinutes = Math.max(0, Math.min(1440, Number(d.scanIntervalMinutes) || 0))
  f.showQuotaDeepSeek = d.showQuotaDeepSeek !== false
  f.showQuotaCursor = d.showQuotaCursor !== false
  f.showQuotaRelay = d.showQuotaRelay !== false
  f.showQuotaWorkBuddy = d.showQuotaWorkBuddy !== false
  f.showQuotaTrae = d.showQuotaTrae !== false
  f.showQuotaZcode = d.showQuotaZcode !== false
  f.showQuotaCodex = d.showQuotaCodex !== false
  f.showAgentDsh = d.showAgentDsh !== false
  f.agentOrder = normalizeAgentOrder(d.agentOrder)
  f.deepseekKey = ''
  f.relayKey = ''
  f.workbuddySession = ''
  f.deepseekKeySet = s.credentials.deepseekKeySet
  f.relayKeySet = s.credentials.relayKeySet
  f.workbuddySessionSet = !!s.credentials.workbuddySessionSet
  configPath.value = s.configPath
  canUninstall.value = !!s.canUninstall
  priceSync.value = s.dashboard.priceSync ?? null
  snapshot.value = snapOf()
}

function priceSyncHint(): string {
  const s = priceSync.value
  if (!s) return '启动后会拉 GitHub 仓库根的价格表；失败则用内置表'
  if (s.source === 'remote')
    return s.lastFetchAt ? `已从 GitHub 拉取 · ${s.lastFetchAt}` : '已从 GitHub 拉取'
  if (s.source === 'cache')
    return s.lastFetchAt
      ? `用上次拉到的本地副本 · 上次拉取 ${s.lastFetchAt}`
      : '用上次拉到的本地副本'
  if (s.lastFetchOk === false)
    return s.lastFetchError ? `用内置表 · 拉取失败：${s.lastFetchError}` : '用内置表 · 拉取失败'
  return '用内置表 · 尚未拉到 GitHub'
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

function paygoOn(key: PaygoShowKey): boolean {
  return f[key]
}

function togglePaygo(key: PaygoShowKey) {
  f[key] = !f[key]
}

function agentMeta(id: AgentId) {
  return SET_AGENTS.find((a) => a.id === id)!
}

function agentOn(id: AgentId): boolean {
  return f[agentMeta(id).show]
}

function setAgentOn(id: AgentId, on: boolean) {
  f[agentMeta(id).show as AgentShowKey] = on
}

function moveAgent(index: number, dir: -1 | 1) {
  const next = index + dir
  if (next < 0 || next >= f.agentOrder.length) return
  const copy = f.agentOrder.slice()
  const tmp = copy[index]
  copy[index] = copy[next]
  copy[next] = tmp
  f.agentOrder = copy
}

async function save() {
  if (readonly || saving.value) return
  saving.value = true
  if (pageLoading) pageLoading.value = true
  try {
    await put<{ ok: boolean }>('/api/settings', {
      app: {
        autostart: !!f.autostart,
        petEnabled: !!f.petEnabled,
        petSize: f.petSize,
      },
      dashboard: {
        costEstimate: !!f.costEstimate,
        tokenUnit: f.tokenUnit,
        scanIntervalMinutes: Math.max(0, Math.min(1440, Number(f.scanIntervalMinutes) || 0)),
        showQuotaDeepSeek: !!f.showQuotaDeepSeek,
        showQuotaCursor: !!f.showQuotaCursor,
        showQuotaRelay: !!f.showQuotaRelay,
        showQuotaWorkBuddy: !!f.showQuotaWorkBuddy,
        showQuotaTrae: !!f.showQuotaTrae,
        showQuotaZcode: !!f.showQuotaZcode,
        showQuotaCodex: !!f.showQuotaCodex,
        showAgentDsh: !!f.showAgentDsh,
        agentOrder: f.agentOrder,
      },
      credentials: {
        relayPanelBaseUrl: f.relayPanelBaseUrl,
        ...(f.deepseekKey.trim() ? { deepseekKey: f.deepseekKey.trim() } : {}),
        ...(f.relayKey.trim() ? { relayKey: f.relayKey.trim() } : {}),
        ...(f.workbuddySession.trim() ? { workbuddySession: f.workbuddySession.trim() } : {}),
      },
    })
    setTokenUnit(f.tokenUnit)
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

async function uninstallApp() {
  if (readonly || uninstalling.value) return
  uninstallShow.value = false
  uninstalling.value = true
  try {
    await post('/api/settings/uninstall')
  } catch (e) {
    uninstalling.value = false
    message.error(e instanceof Error ? e.message : '卸载失败')
  }
}

function discard() {
  if (!snapshot.value) return
  Object.assign(f, JSON.parse(snapshot.value) as typeof f)
  f.deepseekKey = ''
  f.relayKey = ''
  f.workbuddySession = ''
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

onMounted(async () => {
  await load()
  window.addEventListener('beforeunload', onBeforeUnload)
})
onUnmounted(() => {
  window.removeEventListener('beforeunload', onBeforeUnload)
  sectionObserver?.disconnect()
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
      <div class="card-body">
        <div class="row">
          <div class="meta"><label class="lbl" for="s-autostart">开机自启</label></div>
          <div class="ctrl"><n-switch id="s-autostart" :disabled="readonly" v-model:value="f.autostart" /></div>
        </div>
        <div class="row">
          <div class="meta"><label class="lbl" for="s-pet">显示宠物</label></div>
          <div class="ctrl"><n-switch id="s-pet" :disabled="readonly" v-model:value="f.petEnabled" /></div>
        </div>
        <div class="row">
          <div class="meta"><span class="lbl">宠物尺寸</span></div>
          <div class="ctrl">
            <div class="segs" role="radiogroup" aria-label="宠物尺寸">
              <button type="button" role="radio" :aria-checked="f.petSize === 'small'" :disabled="readonly || !f.petEnabled" @click="f.petSize = 'small'">小</button>
              <button type="button" role="radio" :aria-checked="f.petSize === 'medium'" :disabled="readonly || !f.petEnabled" @click="f.petSize = 'medium'">中</button>
              <button type="button" role="radio" :aria-checked="f.petSize === 'large'" :disabled="readonly || !f.petEnabled" @click="f.petSize = 'large'">大</button>
            </div>
          </div>
        </div>
        <div class="row">
          <div class="meta">
            <span class="lbl">程序配置</span>
            <span class="hint">高级选项可直接编辑，修改后重启 AgentHub 生效</span>
            <span class="hint path-text" :title="configPath">{{ configPath }}</span>
          </div>
          <div class="ctrl">
            <n-button type="button" :disabled="readonly" @click="openConfig">
              <template #icon><n-icon><FileCog :size="16" :stroke-width="1.8" /></n-icon></template>
              打开配置文件
            </n-button>
          </div>
        </div>
        <div v-if="canUninstall" class="row">
          <div class="meta">
            <span class="lbl">卸载</span>
            <span class="hint">删除程序文件；配置和缓存仍留在本机用户目录</span>
          </div>
          <div class="ctrl">
            <n-button type="button" :disabled="readonly || uninstalling" @click="uninstallShow = true">
              卸载 AgentHub
            </n-button>
          </div>
        </div>
      </div>
    </section>

    <section id="settings-credentials" class="card set-card">
      <div class="card-head">
        凭据与外部服务
        <span class="spacer" />
        <span class="hint">Key 留空不修改</span>
      </div>
      <div class="card-body">
        <div class="row row--fill">
          <div class="meta">
            <label class="lbl" for="s-relay-base">Sub2API 地址</label>
            <span class="hint">用于查询余额，自动拼接 /v1/usage</span>
          </div>
          <div class="ctrl ctrl--field">
            <n-input id="s-relay-base" class="num" :disabled="readonly" :spellcheck="false" v-model:value="f.relayPanelBaseUrl" placeholder=" " />
          </div>
        </div>
        <div class="row row--fill">
          <div class="meta"><label class="lbl" for="s-dsk">DeepSeek API Key</label></div>
          <div class="ctrl ctrl--secret">
            <n-input
              id="s-dsk"
              type="password"
              show-password-on="click"
              :disabled="readonly"
              autocomplete="off"
              :placeholder="f.deepseekKeySet ? '已配置（留空不修改）' : ' '"
              v-model:value="f.deepseekKey"
            />
            <span v-if="f.deepseekKeySet" class="lock"><n-icon :size="16"><Lock :stroke-width="1.8" /></n-icon> DPAPI</span>
          </div>
        </div>
        <div class="row row--fill">
          <div class="meta">
            <label class="lbl" for="s-relay">Sub2API API Key</label>
            <span class="hint">查余额走 GET /v1/usage</span>
          </div>
          <div class="ctrl ctrl--secret">
            <n-input
              id="s-relay"
              type="password"
              show-password-on="click"
              :disabled="readonly"
              autocomplete="off"
              :placeholder="f.relayKeySet ? '已配置（留空不修改）' : ' '"
              v-model:value="f.relayKey"
            />
            <span v-if="f.relayKeySet" class="lock"><n-icon :size="16"><Lock :stroke-width="1.8" /></n-icon> DPAPI</span>
          </div>
        </div>
        <div class="row row--fill">
          <div class="meta">
            <label class="lbl" for="s-wb">WorkBuddy Cookie</label>
            <span class="hint">查积分、清云端会话，Cookie 名 session</span>
          </div>
          <div class="ctrl ctrl--secret">
            <n-input
              id="s-wb"
              type="password"
              show-password-on="click"
              :disabled="readonly"
              autocomplete="off"
              :placeholder="f.workbuddySessionSet ? '已配置（留空不修改）' : ' '"
              v-model:value="f.workbuddySession"
            />
            <span v-if="f.workbuddySessionSet" class="lock"><n-icon :size="16"><Lock :stroke-width="1.8" /></n-icon> DPAPI</span>
          </div>
        </div>
      </div>
    </section>

    <section id="settings-usage" class="card set-card">
      <div class="card-head">用量额度</div>
      <div class="card-body">
        <div class="row">
          <div class="meta">
            <label class="lbl" for="s-cost">成本估算</label>
            <span class="hint">只算输入/输出，不算缓存。{{ priceSyncHint() }}</span>
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
        <div class="block">
          <div class="meta">
            <span class="lbl">额度</span>
            <span class="hint">只控制首页余额砖。点一下开/关</span>
          </div>
          <div class="paygo" role="group" aria-label="额度">
            <button
              v-for="p in SET_PAYGO"
              :key="p.id"
              type="button"
              :aria-pressed="paygoOn(p.show) ? 'true' : 'false'"
              :disabled="readonly"
              @click="togglePaygo(p.show)"
            >
              <AgentMark :id="p.id" />
              {{ p.name }}
            </button>
          </div>
        </div>
        <div class="block">
          <div class="meta">
            <span class="lbl">Agent</span>
            <span class="hint">一家一个开关：额度、用量、会话一起关</span>
          </div>
          <div class="order-grid order-head" aria-hidden="true">
            <span>Agent</span><span>窗口</span><span>显示</span><span class="order-ops">顺序</span>
          </div>
          <ol class="order">
            <li
              v-for="(id, i) in f.agentOrder"
              :key="id"
              class="order-grid"
              :class="{ 'is-off': !agentOn(id) }"
            >
              <span class="order-who">
                <AgentMark :id="id" />
                <span class="order-name">{{ agentMeta(id).name }}</span>
              </span>
              <span class="order-parts">{{ agentMeta(id).windows }}</span>
              <n-switch
                :disabled="readonly"
                :value="agentOn(id)"
                :aria-label="agentMeta(id).name + ' 显示'"
                @update:value="(on: boolean) => setAgentOn(id, on)"
              />
              <span class="order-ops">
                <n-button quaternary :disabled="readonly || i === 0" aria-label="上移" @click="moveAgent(i, -1)">
                  <template #icon><n-icon :size="16"><ChevronUp :stroke-width="1.8" /></n-icon></template>
                </n-button>
                <n-button quaternary :disabled="readonly || i === f.agentOrder.length - 1" aria-label="下移" @click="moveAgent(i, 1)">
                  <template #icon><n-icon :size="16"><ChevronDown :stroke-width="1.8" /></n-icon></template>
                </n-button>
              </span>
            </li>
          </ol>
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
    :show="uninstallShow"
    text="确定卸载 AgentHub？程序文件会删掉，配置和缓存仍留在本机用户目录。"
    ok-text="卸载"
    @update:show="(on: boolean) => { uninstallShow = on }"
    @confirm="uninstallApp"
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
  font-size: var(--fs-body);
}
.settings {
  display: flex;
  flex-direction: column;
  gap: var(--sp-5);
  width: 100%;
  min-width: 0;
}
.set-card { scroll-margin-top: var(--sp-3); }
.set-card:hover { border-color: var(--stroke); }

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
.row--fill {
  grid-template-columns: minmax(0, 1fr);
  gap: var(--sp-2);
}
.row--fill .ctrl {
  justify-content: stretch;
  width: 100%;
}
.block { padding: var(--sp-4) 0 0; }
.block .meta { margin-bottom: var(--sp-3); }
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
.row--fill :deep(.n-input) { width: 100%; }
.path-text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--mono);
  font-size: var(--fs-caption);
  color: var(--faint);
}
.field-num { width: 120px; }
:deep(.field-num.n-input-number) { width: 120px; }
.lock {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-1);
  white-space: nowrap;
  font-size: var(--fs-caption);
  color: var(--ok);
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

.paygo {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-2);
}
.paygo button {
  height: var(--h-control);
  padding: 0 12px;
  border: 1px solid var(--stroke);
  border-radius: 999px;
  background: var(--surface);
  color: var(--dim);
  font: inherit;
  font-size: var(--fs-small);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.paygo button:hover:not(:disabled) { color: var(--text); border-color: var(--stroke-strong); }
.paygo button[aria-pressed='true'] {
  color: var(--text);
  font-weight: 500;
  background: var(--accent-soft);
  border-color: transparent;
}
.paygo button:disabled { cursor: not-allowed; color: var(--disabled-fg); }
.order-grid {
  display: grid;
  grid-template-columns: 148px minmax(120px, 1fr) 56px 72px;
  gap: var(--sp-3);
  align-items: center;
}
.order-head {
  padding: 0 0 var(--sp-2);
  font-size: var(--fs-caption);
  color: var(--faint);
}
.order-head .order-ops { justify-self: end; }
.order {
  list-style: none;
  margin: 0;
  padding: 0;
  width: 100%;
}
.order li {
  min-height: var(--h-row);
  padding: var(--sp-2) 0;
  box-shadow: var(--rule-hi);
}
.order li:last-child { box-shadow: none; }
.order-who {
  display: flex;
  align-items: center;
  gap: var(--sp-2);
  min-width: 0;
}
.order-name { font-size: var(--fs-body); font-weight: 500; }
.order li.is-off .order-name { color: var(--faint); font-weight: 400; }
.order-parts {
  font-size: var(--fs-caption);
  color: var(--faint);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.order-ops {
  display: flex;
  justify-content: flex-end;
  gap: 0;
}

@media (max-width: 1279px) {
  .row { grid-template-columns: 1fr; }
  .ctrl { justify-content: flex-start; }
  .ctrl--secret { grid-template-columns: minmax(0, 1fr); }
  .lock { justify-self: start; }
  .order-grid { grid-template-columns: minmax(96px, 1fr) minmax(80px, 1.2fr) 44px 64px; }
}
</style>
