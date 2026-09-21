import { ref } from 'vue'
import { applyCssVars, type ThemeName } from './tokens'
import { shellSetTheme } from './hostBridge'

/** 首帧来源是壳层初始化脚本写下的 data-theme；浏览器直读缓存（UI_RULES §7.2）。 */
function initialTheme(): ThemeName {
  const fromDom = document.documentElement.dataset.theme
  if (fromDom === 'light' || fromDom === 'dark') return fromDom
  try {
    if (localStorage.getItem('agenthub-theme') === 'light') return 'light'
  } catch {
    /* 隐私模式 */
  }
  return 'dark'
}

export const theme = ref<ThemeName>(initialTheme())

/** 用户切主题：页面套变量、写缓存，并通知壳层。 */
export function setTheme(next: ThemeName): void {
  theme.value = next
  applyCssVars(document.documentElement, next)
  try {
    localStorage.setItem('agenthub-theme', next)
  } catch {
    /* 隐私模式 */
  }
  void shellSetTheme(next)
}
