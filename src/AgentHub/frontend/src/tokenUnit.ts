import { ref } from 'vue'

export type TokenUnit = 'zh' | 'en'

function readStored(): TokenUnit {
  try {
    if (localStorage.getItem('agenthub-token-unit') === 'en') return 'en'
  } catch {
    /* 隐私模式 */
  }
  return 'zh'
}

export const tokenUnit = ref<TokenUnit>(readStored())

export function normalizeTokenUnit(raw: unknown): TokenUnit {
  return raw === 'en' ? 'en' : 'zh'
}

export function setTokenUnit(next: TokenUnit): void {
  tokenUnit.value = normalizeTokenUnit(next)
  try {
    localStorage.setItem('agenthub-token-unit', tokenUnit.value)
  } catch {
    /* 隐私模式 */
  }
}
