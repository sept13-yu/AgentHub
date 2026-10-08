import { computed, ref, type Ref } from 'vue'
import { get, WRITABLE } from '../api'
import { useCodexSessionNotice } from './useCodexSessionNotice'
import type { AuthProfileLive, AuthProfileView, CodexConnectionView, CodexSnapshot, CodexStatus, ConfigRow } from './types'

export function useCodexSnapshot(pageLoading?: Ref<boolean>) {
  const status = ref<CodexStatus | null>(null)
  const connections = ref<CodexConnectionView[]>([])
  const profiles = ref<AuthProfileView[]>([])
  const profileLive = ref<AuthProfileLive | null>(null)
  const loadError = ref('')
  const loading = ref(false)
  const lastReadAt = ref('')
  const { savedUnapplied } = useCodexSessionNotice()
  // generation：响应世代，只有最新请求能写数据；activeReads：本实例在途读取数；
  // disposed：组件已卸载，旧请求不得再触碰共享加载状态（C01/C02）。
  let generation = 0
  let loadingOwner = 0
  let activeReads = 0
  let disposed = false
  let timer = 0
  const writeDisabled = computed(() => !WRITABLE || !status.value || status.value.configBroken)
  const official = computed(() => connections.value.find(c => c.kind === 'official'))
  const officialActive = computed(() => !!official.value?.active)
  const liveUnarchived = computed(() => !!profileLive.value?.importable && !profiles.value.some(p => p.active))
  const archiveLabel = computed(() => profiles.value.some(p => p.active) ? '更新登录副本' : '保存当前登录')
  const authLoginText = computed(() => {
    if (status.value?.authType === 'chatgpt') return profileLive.value?.email || 'ChatGPT 已登录'
    if (status.value?.authType === 'apikey') return 'API Key'
    return status.value?.authType === 'none' ? '未登录' : '未知'
  })
  const currentLabel = computed(() => {
    const active = connections.value.find(c => c.active)
    if (!active) return status.value?.liveProvider || '尚未配置'
    if (active.kind === 'relay') return active.name || active.baseUrl || '中转连接'
    const profile = profiles.value.find(p => p.active)
    return profile?.name || profile?.email || profileLive.value?.email || '官方连接'
  })
  const officialRows = computed<ConfigRow[]>(() => profiles.value.map(p => ({
    key: 'profile:' + p.id, rowKind: 'official-account', title: p.name || p.email || '官方账号',
    subtitle: [p.email, p.plan].filter(Boolean).join(' · ') || '已保存的 ChatGPT 登录副本',
    isCurrent: p.active && officialActive.value, canDelete: true, profileId: p.id, connectionId: official.value?.id,
  })))
  const relayRows = computed<ConfigRow[]>(() => connections.value.filter(c => c.kind === 'relay').map(c => ({
    key: 'relay:' + c.id, rowKind: 'relay', title: c.name || '未命名中转', subtitle: c.baseUrl || '地址未设置',
    isCurrent: c.active, canDelete: !c.active, connectionId: c.id, keySet: c.keySet,
    savedUnapplied: savedUnapplied.value.has(c.id),
  })))
  function invalidate() { generation += 1 }
  async function load(options: { silent?: boolean } = {}) {
    if (disposed) return false
    const gen = ++generation
    activeReads++
    const ownsLoading = !options.silent
    if (ownsLoading) {
      loadingOwner = gen
      loading.value = true
      if (pageLoading) pageLoading.value = true
    }
    try {
      const snapshot = await get<CodexSnapshot>('/api/codex-config/snapshot')
      if (gen !== generation) return false
      status.value = snapshot.status
      connections.value = snapshot.connections
      profiles.value = snapshot.profiles.profiles
      profileLive.value = snapshot.profiles.live
      loadError.value = ''
      lastReadAt.value = new Date().toLocaleTimeString()
      return true
    } catch (error) {
      if (gen === generation) loadError.value = error instanceof Error ? error.message : String(error)
      return false
    } finally {
      activeReads--
      // 加载归属仍是本请求才清（手动刷新并发时让位新请求）；卸载后的旧请求
      // 不得清除其他页面刚点亮的共享加载条（C01）。
      if (ownsLoading && loadingOwner === gen) {
        loading.value = false
        if (!disposed && pageLoading) pageLoading.value = false
      }
    }
  }
  function startPoll(blocked: () => boolean) {
    // C02：在途读取（含 silent）未结束前不启动下一轮，慢轮询不再把上一轮作废成永不过期；
    // 手动刷新/写后刷新启动更新的 generation，仍会作废并覆盖在途 silent 结果。
    const refresh = () => {
      if (!blocked() && !loading.value && activeReads === 0 && document.visibilityState !== 'hidden') void load({ silent: true })
    }
    timer = window.setInterval(refresh, 5000)
    window.addEventListener('focus', refresh)
    document.addEventListener('visibilitychange', refresh)
    return () => {
      window.clearInterval(timer)
      window.removeEventListener('focus', refresh)
      document.removeEventListener('visibilitychange', refresh)
      // C01：卸载时若本实例仍拥有共享加载条，先同步释放它（切到不接管加载状态的
      // 页面时不能常亮），再清加载归属；disposed 保证旧请求的 finally 不再触碰
      // 共享状态——下一页后来置 true 的加载条不会被旧响应清掉。
      if (loadingOwner !== 0 && pageLoading) pageLoading.value = false
      disposed = true
      generation += 1
      loadingOwner = 0
      loading.value = false
    }
  }
  return { status, connections, profiles, profileLive, loadError, lastReadAt, loading, writeDisabled, official,
    officialActive, liveUnarchived, archiveLabel, authLoginText, currentLabel, officialRows, relayRows, load, invalidate, startPoll }
}

export type CodexSnapshotState = ReturnType<typeof useCodexSnapshot>
