import { post, put } from './api'

export type HostKind = 'wpf' | 'tauri' | 'browser'

export function host(): HostKind {
  if (window.__AGENTHUB_HOST__ === 'tauri') return 'tauri'
  if (window.__AGENTHUB_HOST__ === 'wpf' || window.chrome?.webview) return 'wpf'
  return 'browser'
}

/** 桌面额度沿用同一份消息合同，由各平台壳负责窗口和持久化。 */
export async function sendDesktopQuotaMessage(msg: string): Promise<void> {
  switch (host()) {
    case 'wpf': {
      const bridge = window.chrome?.webview
      if (!bridge) throw new Error('WebView2 bridge unavailable')
      bridge.postMessage(msg)
      return
    }
    case 'tauri': {
      const bridge = window.__TAURI__?.core
      if (!bridge) throw new Error('Tauri bridge unavailable')
      await bridge.invoke('desktop_quota_message', { msg })
      return
    }
    default:
      return
  }
}

export const IS_MAC =
  /Mac|iPhone|iPad/.test(navigator.platform) || navigator.userAgent.includes('Macintosh')

/** 快捷键前缀文案：Mac 用 ⌘，其余 Ctrl */
export const MOD = IS_MAC ? '⌘' : 'Ctrl'

/** 通知壳层应用主题。WPF 由壳持久化；Tauri 由页面先 PUT 再调壳只改窗口外观；浏览器无操作。 */
export async function shellSetTheme(theme: 'light' | 'dark'): Promise<void> {
  switch (host()) {
    case 'wpf':
      window.chrome?.webview?.postMessage('theme:' + theme)
      return
    case 'tauri':
      await put('/api/settings', { app: { theme } })
      await window.__TAURI__?.core.invoke('set_theme', { theme })
      return
    default:
      return
  }
}

/** 目录选择。返回 null 表示取消或不支持。 */
export async function pickFolder(initialPath: string): Promise<string | null> {
  switch (host()) {
    case 'wpf': {
      const r = await post<{ path?: string; cancelled?: boolean }>('/api/settings/browse-folder', {
        initialPath,
      })
      return r.path ?? null
    }
    case 'tauri':
      return (
        (await window.__TAURI__?.core.invoke<string | null>('pick_folder', { initialPath })) ?? null
      )
    default:
      return null
  }
}

export const CAN_PICK_FOLDER = host() !== 'browser'
