/// <reference types="vite/client" />

interface Window {
  __AGENTHUB_TOKEN__?: string
  __AGENTHUB_SHELL__?: boolean
  __AGENTHUB_THEME__?: string
  /** 'wpf' | 'tauri'；浏览器直连为 undefined */
  __AGENTHUB_HOST__?: 'wpf' | 'tauri'
  __TAURI__?: { core: { invoke: <T>(cmd: string, args?: Record<string, unknown>) => Promise<T> } }
  chrome?: { webview?: { postMessage: (msg: string) => void } }
}

declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<object, object, unknown>
  export default component
}
