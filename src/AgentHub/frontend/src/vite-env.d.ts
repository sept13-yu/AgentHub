/// <reference types="vite/client" />

interface Window {
  __AGENTHUB_TOKEN__?: string
  __AGENTHUB_SHELL__?: boolean
  __AGENTHUB_THEME__?: string
  /** 'wpf' | 'tauri'；浏览器直连为 undefined */
  __AGENTHUB_HOST__?: 'wpf' | 'tauri'
  __TAURI__?: { core: { invoke: <T>(cmd: string, args?: Record<string, unknown>) => Promise<T> } }
  chrome?: {
    webview?: {
      postMessage: (msg: string) => void
      addEventListener?: (type: 'message', listener: (ev: { data?: unknown }) => void) => void
    }
  }
  /** 桌面额度悬浮窗注入。主窗口没有这个标记。 */
  __AGENTHUB_DESKTOP_QUOTA__?: boolean
  __AGENTHUB_DQ_STATE__?: { expanded?: boolean; windowIds?: string[] | null; theme?: string }
}

declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<object, object, unknown>
  export default component
}
