/// <reference types="vite/client" />

interface Window {
  __AGENTHUB_TOKEN__?: string
  __AGENTHUB_SHELL__?: boolean
  __AGENTHUB_THEME__?: string
}

declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<object, object, unknown>
  export default component
}
