<script setup lang="ts">
import { computed } from 'vue'
import { agentIcon } from '../agentMeta'

/** Cursor / ZCode 单色标跟字色；WorkBuddy / Codex / 彩标用自带底板的原图。 */
const INK: Record<string, string> = {
  cursor: 'M11.503.131 1.891 5.678a.84.84 0 0 0-.42.726v11.188c0 .3.162.575.42.724l9.609 5.55a1 1 0 0 0 .998 0l9.61-5.55a.84.84 0 0 0 .42-.724V6.404a.84.84 0 0 0-.42-.726L12.497.131a1.01 1.01 0 0 0-.996 0M2.657 6.338h18.55c.263 0 .43.287.297.515L12.23 22.918c-.062.107-.229.064-.229-.06V12.335a.59.59 0 0 0-.295-.51l-9.11-5.257c-.109-.063-.064-.23.061-.23',
  zcode: 'M5 4h14v3.2L10.2 16H19V20H5v-3.2L13.8 8H5V4z',
  minimax: 'M4 19V5h2.4l5.6 9.1L17.6 5H20v14h-2.4V9.6L12.4 19h-.8L6.4 9.6V19H4z',
  antigravity: 'M12 3l8 14H4L12 3zm0 4.6L7.6 15h8.8L12 7.6z',
  reasonix: 'M12 2l7 6.2v7.6L12 22l-7-6.2V8.2L12 2z',
  devin: 'M8 3h8l5 5v8l-5 5H8l-5-5V8l5-5z',
  opencode: 'M4 6h7v2H6v8h5v2H4V6zm9 0h7v12h-7v-2h5V8h-5V6z',
  'claude-code': 'M12 2.2l1.7 5.1 5.1 1.7-5.1 1.7L12 15.8l-1.7-5.1L5.2 9l5.1-1.7L12 2.2z',
}

const props = defineProps<{ id: string | null | undefined }>()

const ink = computed(() => {
  const id = props.id === 'cursor-cloud' ? 'cursor' : props.id
  return id ? INK[id] : undefined
})
const src = computed(() => agentIcon(props.id))
</script>

<template>
  <svg v-if="ink" class="src-ico" viewBox="0 0 24 24" aria-hidden="true">
    <path :d="ink" />
  </svg>
  <img v-else-if="src" class="src-ico" :src="src" alt="" />
</template>

<style scoped>
.src-ico {
  width: var(--icon);
  height: var(--icon);
  border-radius: 3px;
  flex: none;
  color: var(--text);
}
svg.src-ico {
  display: block;
  fill: currentColor;
}
img.src-ico {
  display: block;
  object-fit: contain;
}
</style>
