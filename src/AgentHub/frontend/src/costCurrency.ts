import { ref } from 'vue'

export type CostCurrency = 'CNY' | 'USD'

const KEY = 'agenthub-cost-currency'

function readStored(): CostCurrency {
  try {
    if (localStorage.getItem(KEY) === 'USD') return 'USD'
  } catch {
    /* 隐私模式 */
  }
  return 'CNY'
}

export const costCurrency = ref<CostCurrency>(readStored())

export function setCostCurrency(next: CostCurrency): void {
  costCurrency.value = next === 'USD' ? 'USD' : 'CNY'
  try {
    localStorage.setItem(KEY, costCurrency.value)
  } catch {
    /* 隐私模式 */
  }
}

export function toggleCostCurrency(): void {
  setCostCurrency(costCurrency.value === 'CNY' ? 'USD' : 'CNY')
}
