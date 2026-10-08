import { computed, ref, type Ref } from 'vue'
import type { MessageApi } from 'naive-ui'
import { get } from '../api'
import type { McpPayload } from './types'

// 列表快照：/api/mcp 只读。三个状态量各管各的（R05）：
// - loadGeneration：响应世代，只有最新世代能写 data/loadError；
// - pendingLoads：本实例在途请求计数，决定 loading（含共用加载条）；
// - disposed：组件是否已卸载，卸载后任何请求都不得再写共用加载状态。
export function useMcpSnapshot(pageLoading: Ref<boolean> | undefined, msg: MessageApi) {
  const data = ref<McpPayload | null>(null)
  const loadError = ref('')
  const loading = ref(false)
  let loadGeneration = 0
  let pendingLoads = 0
  let disposed = false

  const detectedAdapters = computed(() => (data.value?.adapters ?? []).filter((a) => a.detected))
  // 可写端 = 已检测且配置文件读取正常；读取失败的端不能当作同步目标。
  const writableAdapters = computed(() => detectedAdapters.value.filter((a) => !a.error))
  const adapterErrors = computed(() => detectedAdapters.value.filter((a) => !!a.error))

  function setLoading(on: boolean) {
    if (pageLoading) pageLoading.value = on
  }

  function syncLoading() {
    loading.value = pendingLoads > 0
    if (!disposed) setLoading(pendingLoads > 0)
  }

  async function load(): Promise<boolean> {
    if (disposed) return false
    const generation = ++loadGeneration
    pendingLoads++
    syncLoading()
    try {
      const snapshot = await get<McpPayload>('/api/mcp')
      if (generation !== loadGeneration) return false
      data.value = snapshot
      loadError.value = ''
      return true
    } catch (e) {
      if (generation === loadGeneration) {
        loadError.value = e instanceof Error ? e.message : '读取失败'
        msg.error(loadError.value)
      }
      return false
    } finally {
      // 每个请求结束都按剩余计数准确更新 loading：并发下不管谁先返回，
      // 全部结束时共用加载条必须熄灭，最新请求未返回前本地 loading 保持 true。
      pendingLoads = Math.max(0, pendingLoads - 1)
      syncLoading()
    }
  }

  function invalidate() {
    loadGeneration++
    // 卸载：本实例不再有归属请求，旧请求的 finally 只清本地计数，
    // 经 disposed 挡住对下一页共用加载状态的写入（R03）。
    disposed = true
    pendingLoads = 0
    loading.value = false
  }

  return { data, loading, loadError, detectedAdapters, writableAdapters, adapterErrors, load, invalidate }
}

export type McpSnapshotState = ReturnType<typeof useMcpSnapshot>
