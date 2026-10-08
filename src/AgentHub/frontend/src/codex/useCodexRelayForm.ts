import { computed, ref, type Ref } from 'vue'
import { post, put } from '../api'
import type { RelayFieldErrors, RelayForm } from './types'
import type { CodexSnapshotState } from './useCodexSnapshot'
import { useCodexSessionNotice } from './useCodexSessionNotice'

function emptyForm(): RelayForm {
  return { name: '', baseUrl: '', defaultModel: '', supportsWebSockets: false,
    userAgent: '', originator: '', apiKey: '', usageBaseUrl: '', keySet: false }
}

export function useCodexRelayForm(snapshot: CodexSnapshotState, busy: Ref<boolean>, blocked: () => boolean,
  success: (text: string) => void) {
  const show = ref(false)
  const editId = ref('')
  const editing = computed(() => !!editId.value)
  const form = ref<RelayForm>(emptyForm())
  const errors = ref<RelayFieldErrors>({})
  const error = ref('')
  const discardShow = ref(false)
  let baseline = ''
  let discardPromise: Promise<boolean> | null = null
  let discardResolve: ((ok: boolean) => void) | null = null
  const dirty = computed(() => show.value && JSON.stringify(form.value) !== baseline)
  const { markSaved } = useCodexSessionNotice()

  function open(id = '') {
    if (snapshot.writeDisabled.value || busy.value || blocked() || show.value) return
    const connection = id ? snapshot.connections.value.find(c => c.id === id && c.kind === 'relay') : null
    if (id && !connection) return
    editId.value = id
    form.value = connection ? { ...emptyForm(), name: connection.name, baseUrl: connection.baseUrl,
      defaultModel: connection.defaultModel, supportsWebSockets: connection.supportsWebSockets,
      userAgent: connection.userAgent, originator: connection.originator,
      usageBaseUrl: connection.usageBaseUrl, keySet: connection.keySet } : emptyForm()
    errors.value = {}
    error.value = ''
    baseline = JSON.stringify(form.value)
    show.value = true
  }
  function updateForm(value: RelayForm) {
    if (busy.value || snapshot.writeDisabled.value) return
    form.value = { ...value }
    errors.value = {}
    error.value = ''
  }
  function confirmDiscard(): Promise<boolean> {
    if (busy.value) return Promise.resolve(false)
    if (!dirty.value) return Promise.resolve(true)
    if (discardPromise) return discardPromise
    discardShow.value = true
    discardPromise = new Promise(resolve => { discardResolve = resolve })
    return discardPromise
  }
  function resolveDiscard(ok: boolean) {
    discardShow.value = false
    const resolve = discardResolve
    discardResolve = null
    discardPromise = null
    resolve?.(ok)
  }
  async function close() {
    if (await confirmDiscard()) show.value = false
  }
  function validUrl(value: string) {
    try { return ['http:', 'https:'].includes(new URL(value).protocol) }
    catch { return false }
  }
  function validate() {
    const f = form.value
    const next: RelayFieldErrors = {}
    if (!f.name.trim()) next.name = '请填写连接名称'
    if (!f.baseUrl.trim()) next.baseUrl = '请填写服务地址'
    else if (!validUrl(f.baseUrl.trim())) next.baseUrl = '请填写完整的 HTTP 或 HTTPS 地址'
    if (!f.keySet && !f.apiKey.trim()) next.apiKey = '请填写 API Key'
    if (f.usageBaseUrl.trim() && !validUrl(f.usageBaseUrl.trim())) next.usageBaseUrl = '请填写完整的 HTTP 或 HTTPS 地址'
    errors.value = next
    return Object.keys(next).length === 0
  }
  async function save() {
    if (!show.value || snapshot.writeDisabled.value || busy.value || !validate()) return
    busy.value = true
    snapshot.invalidate()
    error.value = ''
    const f = form.value
    const payload = { name: f.name.trim(), baseUrl: f.baseUrl.trim(), defaultModel: f.defaultModel.trim(),
      supportsWebSockets: f.supportsWebSockets, userAgent: f.userAgent.trim(), originator: f.originator.trim(),
      usageBaseUrl: f.usageBaseUrl.trim(), ...(f.apiKey.trim() ? { apiKey: f.apiKey.trim() } : {}) }
    try {
      let id = editId.value
      if (id) await put(`/api/codex-config/connections/${id}`, payload)
      else id = (await post<{ id: string }>('/api/codex-config/connections', payload)).id
      markSaved(id)
      success('连接已保存，尚未写入 Codex；可在列表中切换或写入更新')
      baseline = JSON.stringify(form.value)
      show.value = false
      form.value.apiKey = ''
      await snapshot.load()
    } catch (failure) { error.value = '保存失败：' + (failure instanceof Error ? failure.message : String(failure)) }
    finally { busy.value = false }
  }
  function onBeforeUnload(event: BeforeUnloadEvent) {
    if (!dirty.value && !busy.value) return
    event.preventDefault()
    event.returnValue = ''
  }
  return { show, editing, form, errors, error, dirty, discardShow, open, updateForm,
    confirmDiscard, resolveDiscard, close, save, onBeforeUnload }
}
