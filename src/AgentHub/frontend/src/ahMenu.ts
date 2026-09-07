import type { InjectionKey } from 'vue'

export interface AhMenuItem {
  key: string
  label?: string
  shortcut?: string
  danger?: boolean
  disabled?: boolean
  separator?: boolean
  handler?: () => void | Promise<void>
}

export interface AhMenuApi {
  open: (e: MouseEvent, items: AhMenuItem[]) => void
  close: () => void
}

export const ahMenuKey: InjectionKey<AhMenuApi> = Symbol('ah-menu')

export function isEditableTarget(target: EventTarget | null): target is HTMLElement {
  if (!(target instanceof Element)) return false
  const node = target.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"]')
  if (!node) return false
  if (node instanceof HTMLInputElement) {
    if (node.disabled || node.readOnly) return false
    const type = node.type
    if (type === 'button' || type === 'submit' || type === 'reset'
      || type === 'checkbox' || type === 'radio' || type === 'file'
      || type === 'hidden' || type === 'range' || type === 'color')
      return false
  }
  if (node instanceof HTMLTextAreaElement && (node.disabled || node.readOnly)) return false
  if (node instanceof HTMLSelectElement && node.disabled) return false
  return true
}

export function hasOpenModal(): boolean {
  return !!document.querySelector('[role="dialog"][aria-modal="true"]')
}

export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text)
    return true
  } catch {
    return false
  }
}
