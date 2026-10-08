import { computed, ref, type Ref } from 'vue'
import { del, post } from '../api'
import type { ConfigRow, WriteResult } from './types'
import type { CodexSnapshotState } from './useCodexSnapshot'
import { useCodexSessionNotice } from './useCodexSessionNotice'

export function useCodexActions(snapshot: CodexSnapshotState, busy: Ref<boolean>, blocked: () => boolean,
  success: (text: string) => void) {
  const { recordWrite, recordIssue, markApplied } = useCodexSessionNotice()
  const applyShow = ref(false)
  const pendingApply = ref<ConfigRow | null>(null)
  const applyError = ref('')
  const deleteShow = ref(false)
  const pendingDelete = ref<ConfigRow | null>(null)
  const deleteError = ref('')
  const archiveShow = ref(false)
  const archiveName = ref('')
  const archiveError = ref('')
  const prepareShow = ref(false)
  const prepareError = ref('')
  let returnToPrepare = false
  let writtenProfileId = ''
  const modalOpen = computed(() => applyShow.value || deleteShow.value || archiveShow.value || prepareShow.value)
  const canStart = () => !snapshot.writeDisabled.value && !busy.value && !blocked() && !modalOpen.value
  const runningText = computed(() => snapshot.status.value?.codexRunning
    ? '检测到 Codex 正在运行；写入后须彻底退出并重启，再确认连接。'
    : '未检测到 Codex 进程；文件写入后仍需在 Codex 中确认连接。')
  const applyConfirmText = computed(() => {
    const row = pendingApply.value
    if (!row) return ''
    return [`从「${snapshot.currentLabel.value}」切换到「${row.title}」。`,
      row.rowKind === 'relay' ? '将连接写入 Codex 配置文件。' : '将保存的登录副本写回 Codex，并切到官方连接。',
      snapshot.liveUnarchived.value ? '当前登录尚未保存副本，切换可能覆盖该登录；可取消后先保存当前登录。' : '当前登录副本状态见当前配置。',
      runningText.value].join('\n')
  })
  const prepareText = computed(() => [`当前连接：「${snapshot.currentLabel.value}」；登录：${snapshot.authLoginText.value}。`,
    '确认后将清空 Codex 当前登录，并写入官方连接。已保存的登录副本不会删除。',
    snapshot.liveUnarchived.value ? '当前登录尚未保存副本，建议先保存当前登录。' : '',
    runningText.value, '随后在 Codex 登录新账号，登录完成后可保存新登录副本。'].filter(Boolean).join('\n'))
  const deleteText = computed(() => pendingDelete.value?.rowKind === 'official-account'
    ? `删除「${pendingDelete.value.title}」的登录副本？Codex 当前登录文件不会被清空。`
    : `删除中转连接「${pendingDelete.value?.title || ''}」？`)

  function askApply(row: ConfigRow) {
    if (!canStart() || (row.isCurrent && !row.savedUnapplied)) return
    pendingApply.value = row
    writtenProfileId = ''
    applyError.value = ''
    applyShow.value = true
  }
  function askDelete(row: ConfigRow) {
    if (!canStart() || !row.canDelete) return
    pendingDelete.value = row
    deleteError.value = ''
    deleteShow.value = true
  }
  function openPrepare() {
    if (!canStart()) return
    prepareError.value = ''
    prepareShow.value = true
  }
  function openArchive(forPrepare = false) {
    if (snapshot.writeDisabled.value || busy.value || blocked() || !snapshot.profileLive.value?.importable) return
    if (modalOpen.value && !(forPrepare && prepareShow.value)) return
    returnToPrepare = forPrepare
    prepareShow.value = false
    archiveName.value = snapshot.profileLive.value.email || ''
    archiveError.value = ''
    archiveShow.value = true
  }
  function errorText(error: unknown) { return error instanceof Error ? error.message : String(error) }
  function requireOk(result: WriteResult) {
    if (!result.ok) throw new Error(result.error || '写入失败')
  }
  async function doApply() {
    const row = pendingApply.value
    if (!applyShow.value || !row || busy.value || snapshot.writeDisabled.value) return
    busy.value = true
    snapshot.invalidate()
    applyError.value = ''
    try {
      // C04：确认框打开期间手动刷新与轮询都被阻塞，写入前必须重读最新连接与登录
      // 状态；读取失败或写入禁用立即终止，保留确认框、错误与既有部分写入事实。
      const fresh = await snapshot.load()
      if (!fresh) throw new Error('读取连接状态失败，请重试')
      if (snapshot.writeDisabled.value) throw new Error('当前无法写入 Codex 配置，请检查连接或配置状态')
      if (row.rowKind === 'relay' && row.connectionId) {
        const result = await post<WriteResult>(`/api/codex-config/connections/${row.connectionId}/apply`)
        requireOk(result)
        recordWrite(`中转「${row.title}」已写入配置`, !!result.restartRequired)
        markApplied(row.connectionId)
      } else if (row.rowKind === 'official-account' && row.profileId) {
        const needsOfficial = !snapshot.officialActive.value || snapshot.status.value?.externalChanged
          || snapshot.status.value?.live?.isHybridForm || !snapshot.status.value?.liveProviderMatches
        if (needsOfficial && !row.connectionId) throw new Error('未找到官方连接，请刷新后重试')
        const targetLoginVerified = !snapshot.loadError.value
          && snapshot.profiles.value.some(profile => profile.id === row.profileId && profile.active)
        if (writtenProfileId !== row.profileId || !targetLoginVerified) {
          writtenProfileId = ''
          const switched = await post<WriteResult>(`/api/codex-config/auth-profiles/${row.profileId}/switch`)
          requireOk(switched)
          writtenProfileId = row.profileId
          recordWrite(`账号「${row.title}」的登录副本已写回`, !!switched.restartRequired)
        }
        if (needsOfficial && row.connectionId) {
          const applied = await post<WriteResult>(`/api/codex-config/connections/${row.connectionId}/apply`)
          requireOk(applied)
          recordWrite(`账号「${row.title}」的登录副本已写回，官方连接已写入配置`, !!applied.restartRequired)
        }
      } else throw new Error('目标连接无效，请刷新后重试')
      recordIssue('')
      applyShow.value = false
      pendingApply.value = null
      success('写入完成，请在 Codex 中确认连接')
    } catch (error) {
      applyError.value = writtenProfileId
        ? '登录副本已写回，但官方连接写入未完成：' + errorText(error)
        : '写入失败：' + errorText(error)
      if (writtenProfileId) recordIssue('登录副本已写回；官方连接尚未确认写入完成，请核对当前状态后重试切换。')
    } finally {
      await snapshot.load()
      busy.value = false
    }
  }
  async function doPrepare() {
    if (!prepareShow.value || busy.value || snapshot.writeDisabled.value) return
    busy.value = true
    snapshot.invalidate()
    prepareError.value = ''
    try {
      const result = await post<WriteResult>('/api/codex-config/official/prepare-blank')
      requireOk(result)
      recordWrite('当前登录已清空，官方连接已写入', !!result.restartRequired,
        '请在 Codex 登录新账号，完成后回到此页保存当前登录副本。')
      prepareShow.value = false
      success('已准备新登录，请在 Codex 登录')
    } catch (error) {
      prepareError.value = '准备新登录未完成：' + errorText(error)
        + '。此操作先清空登录再写官方连接，当前登录可能已清空，请核对刷新后的状态。'
      recordIssue('准备新登录未完成，当前登录可能已清空；请核对当前登录与连接后重试。')
    }
    finally { await snapshot.load(); busy.value = false }
  }
  async function doArchive() {
    if (!archiveShow.value || busy.value || snapshot.writeDisabled.value) return
    busy.value = true
    snapshot.invalidate()
    archiveError.value = ''
    try {
      const result = await post<{ id: string; updated?: boolean }>('/api/codex-config/auth-profiles/import',
        { name: archiveName.value.trim() })
      success(result.updated ? '已更新登录副本' : '已保存当前登录副本')
      archiveShow.value = false
      await snapshot.load()
      if (returnToPrepare) prepareShow.value = true
      returnToPrepare = false
    } catch (error) { archiveError.value = '保存副本失败：' + errorText(error) }
    finally { busy.value = false }
  }
  async function doDelete() {
    const row = pendingDelete.value
    if (!deleteShow.value || !row || busy.value || snapshot.writeDisabled.value || !row.canDelete) return
    busy.value = true
    snapshot.invalidate()
    deleteError.value = ''
    try {
      if (row.rowKind === 'relay' && row.connectionId) {
        await del(`/api/codex-config/connections/${row.connectionId}`)
        markApplied(row.connectionId)
      } else if (row.profileId) await del(`/api/codex-config/auth-profiles/${row.profileId}`)
      else throw new Error('删除目标无效')
      success(row.rowKind === 'relay' ? '已删除中转连接' : '已删除登录副本')
      deleteShow.value = false
      pendingDelete.value = null
      await snapshot.load()
    } catch (error) { deleteError.value = errorText(error) }
    finally { busy.value = false }
  }
  return { applyShow, pendingApply, applyError, applyConfirmText, deleteShow, deleteText, deleteError,
    archiveShow, archiveName, archiveError, prepareShow, prepareError, prepareText, modalOpen,
    askApply, askDelete, openPrepare, openArchive, doApply, doPrepare, doArchive, doDelete }
}
