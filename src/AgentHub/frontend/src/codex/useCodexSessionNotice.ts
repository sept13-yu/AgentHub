import { computed, ref } from 'vue'

// 仅记录本前端会话的文件写入事实；读取及进程状态不证明 Codex 已加载。
const writtenFact = ref('')
const restartPending = ref(false)
const writeDetail = ref('')
const issue = ref('')
const savedUnapplied = ref<Set<string>>(new Set())
const notice = computed(() => [writtenFact.value ? `${writtenFact.value}。${restartPending.value
  ? '本会话写入时曾检测到 Codex 在运行，请彻底退出后重启；连接是否已加载仍需在 Codex 中确认。'
  : '文件已写入；打开或重启 Codex 后，请在 Codex 中确认连接。'}` : '', writeDetail.value, issue.value].filter(Boolean).join('\n'))

export function useCodexSessionNotice() {
  function recordWrite(fact: string, restartRequired: boolean, detail = '') {
    writtenFact.value = fact
    restartPending.value ||= restartRequired
    writeDetail.value = detail
    issue.value = ''
  }
  function markSaved(id: string) {
    savedUnapplied.value = new Set([...savedUnapplied.value, id])
  }
  function markApplied(id: string) {
    const next = new Set(savedUnapplied.value)
    next.delete(id)
    savedUnapplied.value = next
  }
  function recordIssue(detail: string) {
    issue.value = detail
  }
  return { notice, savedUnapplied, recordWrite, recordIssue, markSaved, markApplied }
}
