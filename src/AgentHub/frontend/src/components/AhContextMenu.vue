<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, provide, ref } from 'vue'
import { ahMenuKey, isEditableTarget, type AhMenuItem } from '../ahMenu'

const open = ref(false)
const x = ref(0)
const y = ref(0)
const items = ref<AhMenuItem[]>([])
const active = ref(-1)
const menuEl = ref<HTMLElement | null>(null)
let restore: HTMLElement | null = null

const api = {
  open(e: MouseEvent, next: AhMenuItem[]) {
    e.preventDefault()
    e.stopPropagation()
    showAt(e.clientX, e.clientY, next, document.activeElement instanceof HTMLElement ? document.activeElement : null)
  },
  close,
}

provide(ahMenuKey, api)

function showAt(cx: number, cy: number, next: AhMenuItem[], focusBack: HTMLElement | null) {
  restore = focusBack
  items.value = next
  x.value = cx
  y.value = cy
  open.value = true
  active.value = firstEnabled()
  void nextTick(() => {
    place()
    menuEl.value?.focus()
  })
}

function close() {
  if (!open.value) return
  open.value = false
  items.value = []
  const back = restore
  restore = null
  if (back && document.contains(back)) back.focus()
}

function firstEnabled(): number {
  return items.value.findIndex((it) => !it.separator && !it.disabled)
}

function place() {
  const el = menuEl.value
  if (!el) return
  const pad = 8
  const w = el.offsetWidth
  const h = el.offsetHeight
  let nx = x.value
  let ny = y.value
  if (nx + w > window.innerWidth - pad) nx = Math.max(pad, window.innerWidth - w - pad)
  if (ny + h > window.innerHeight - pad) ny = Math.max(pad, y.value - h)
  x.value = nx
  y.value = ny
}

function moveActive(dir: 1 | -1) {
  if (!items.value.length) return
  let i = active.value
  for (let n = 0; n < items.value.length; n++) {
    i = (i + dir + items.value.length) % items.value.length
    const it = items.value[i]
    if (!it.separator && !it.disabled) {
      active.value = i
      return
    }
  }
}

async function run(it: AhMenuItem) {
  if (it.separator || it.disabled || !it.handler) return
  close()
  await it.handler()
}

function hoverItem(i: number, it: AhMenuItem) {
  if (!it.disabled) active.value = i
}

function editItems(el: HTMLElement): AhMenuItem[] {
  return [
    { key: 'copy', label: '复制', shortcut: 'Ctrl+C', handler: () => doEdit('copy', el) },
    { key: 'paste', label: '粘贴', shortcut: 'Ctrl+V', handler: () => doEdit('paste', el) },
    { key: 'all', label: '全选', shortcut: 'Ctrl+A', handler: () => doEdit('selectAll', el) },
  ]
}

async function doEdit(act: 'copy' | 'paste' | 'selectAll', el: HTMLElement) {
  el.focus()
  if (act === 'selectAll') {
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) el.select()
    else document.execCommand('selectAll')
    return
  }
  if (act === 'copy') {
    document.execCommand('copy')
    return
  }
  if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
    try {
      const text = await navigator.clipboard.readText()
      const start = el.selectionStart ?? el.value.length
      const end = el.selectionEnd ?? start
      const next = el.value.slice(0, start) + text + el.value.slice(end)
      const proto = Object.getOwnPropertyDescriptor(el.constructor.prototype, 'value')
      proto?.set?.call(el, next)
      el.setSelectionRange(start + text.length, start + text.length)
      el.dispatchEvent(new Event('input', { bubbles: true }))
    } catch {
      document.execCommand('paste')
    }
    return
  }
  document.execCommand('paste')
}

function onDocMenu(e: MouseEvent) {
  const t = e.target
  if (t instanceof Element && t.closest('.ah-menu')) {
    e.preventDefault()
    return
  }
  if (!isEditableTarget(t)) return
  e.preventDefault()
  e.stopPropagation()
  showAt(e.clientX, e.clientY, editItems(t), t)
}

function onDocPointer(e: MouseEvent) {
  if (!open.value) return
  const t = e.target
  if (t instanceof Element && t.closest('.ah-menu')) return
  close()
}

function onKey(e: KeyboardEvent) {
  if (!open.value) return
  if (e.key === 'Escape') {
    e.preventDefault()
    e.stopPropagation()
    e.stopImmediatePropagation()
    close()
    return
  }
  if (e.key === 'ArrowDown') {
    e.preventDefault()
    e.stopImmediatePropagation()
    moveActive(1)
    return
  }
  if (e.key === 'ArrowUp') {
    e.preventDefault()
    e.stopImmediatePropagation()
    moveActive(-1)
    return
  }
  if (e.key === 'Enter' || e.key === ' ') {
    e.preventDefault()
    e.stopImmediatePropagation()
    const it = items.value[active.value]
    if (it) void run(it)
  }
}

onMounted(() => {
  window.addEventListener('contextmenu', onDocMenu, true)
  window.addEventListener('mousedown', onDocPointer, true)
  window.addEventListener('keydown', onKey, true)
  window.addEventListener('resize', close)
  window.addEventListener('blur', close)
})
onUnmounted(() => {
  window.removeEventListener('contextmenu', onDocMenu, true)
  window.removeEventListener('mousedown', onDocPointer, true)
  window.removeEventListener('keydown', onKey, true)
  window.removeEventListener('resize', close)
  window.removeEventListener('blur', close)
})

</script>

<template>
  <slot />
  <Teleport to="body">
    <div
      v-if="open"
      ref="menuEl"
      class="ah-menu"
      role="menu"
      tabindex="-1"
      :style="{ left: x + 'px', top: y + 'px' }"
    >
      <template v-for="(it, i) in items" :key="it.key || 'sep-' + i">
        <div v-if="it.separator" class="ah-menu-sep" role="separator" />
        <button
          v-else
          type="button"
          role="menuitem"
          class="ah-menu-item"
          :class="{ 'is-danger': it.danger, 'is-on': i === active }"
          :disabled="it.disabled"
          :aria-disabled="it.disabled ? 'true' : 'false'"
          @mouseenter="hoverItem(i, it)"
          @click="run(it)"
        >
          <span>{{ it.label }}</span>
          <kbd v-if="it.shortcut">{{ it.shortcut }}</kbd>
        </button>
      </template>
    </div>
  </Teleport>
</template>

<style scoped>
.ah-menu {
  position: fixed;
  z-index: 80;
  user-select: none;
  min-width: 196px;
  max-width: min(280px, calc(100vw - 16px));
  padding: 4px;
  background: var(--surface);
  border: 1px solid var(--stroke);
  border-radius: var(--r-in);
  outline: none;
}
.ah-menu-sep {
  height: 1px;
  margin: 4px 8px;
  background: var(--stroke);
}
.ah-menu-item {
  display: flex;
  align-items: center;
  gap: var(--sp-3);
  width: 100%;
  min-height: var(--h-control);
  padding: 0 10px;
  border: 0;
  border-radius: var(--r-in);
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: var(--fs-body);
  text-align: left;
  cursor: pointer;
}
.ah-menu-item span { min-width: 0; flex: 1; }
.ah-menu-item kbd {
  flex: none;
  font-family: var(--mono);
  font-size: var(--fs-caption);
  color: var(--faint);
  font-weight: 400;
}
.ah-menu-item.is-on { background: var(--wash); }
.ah-menu-item.is-danger { color: var(--danger); }
.ah-menu-item:disabled {
  color: var(--disabled-fg);
  cursor: default;
  background: transparent;
}
</style>
