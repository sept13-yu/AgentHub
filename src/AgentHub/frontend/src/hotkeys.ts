import { inject, onMounted, onUnmounted, type InjectionKey, type Ref } from 'vue'
import { hasOpenModal, isEditableTarget } from './ahMenu'

export interface PageHotkeys {
  refresh?: () => void | Promise<void>
  remove?: () => void
  selectAll?: () => void
  escape?: () => void
}

export const pageHotkeysKey: InjectionKey<Ref<PageHotkeys | null>> = Symbol('ah-hotkeys')

export function usePageHotkeys(handlers: PageHotkeys): void {
  const slot = inject(pageHotkeysKey)
  if (!slot) return
  onMounted(() => { slot.value = handlers })
  onUnmounted(() => {
    if (slot.value === handlers) slot.value = null
  })
}

export function isRefreshKey(e: KeyboardEvent): boolean {
  if (e.key === 'F5') return true
  return e.key.toLowerCase() === 'r' && (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey
}

export function isSelectAllKey(e: KeyboardEvent): boolean {
  return e.key.toLowerCase() === 'a' && (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey
}

export function bindAppHotkeys(slot: Ref<PageHotkeys | null>): () => void {
  const onKey = (e: KeyboardEvent) => {
    if (e.defaultPrevented) return
    if (isRefreshKey(e)) {
      e.preventDefault()
      e.stopPropagation()
      if (!document.querySelector('.ah-menu')) void slot.value?.refresh?.()
      return
    }
    if (document.querySelector('.ah-menu')) return
    if (isEditableTarget(e.target)) return
    if (hasOpenModal()) return
    if (e.key === 'Escape') {
      slot.value?.escape?.()
      return
    }
    if (e.key === 'Delete') {
      e.preventDefault()
      slot.value?.remove?.()
      return
    }
    if (isSelectAllKey(e)) {
      e.preventDefault()
      slot.value?.selectAll?.()
    }
  }
  window.addEventListener('keydown', onKey, true)
  return () => window.removeEventListener('keydown', onKey, true)
}
