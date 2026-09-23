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
  'gemini-cli': 'M12 3l2.2 5.4L20 9l-4.2 3.6L17 19l-5-3.2L7 19l1.2-6.4L4 9l5.8-.6L12 3z',
  kiro: 'M4 7h16v3H8v2h8v3H8v2h12v3H4V7z',
  copilot: 'M12 4a8 8 0 100 16 8 8 0 000-16zm-1.2 4.2h2.4V12H16v2.4h-2.8V18h-2.4v-3.6H8V12h2.8V8.2z',
  'kimi-code': 'M6 4h4.2L16 12l-5.8 8H6l5.8-8L6 4z',
  codebuddy: 'M5 5h6v6H5V5zm8 0h6v6h-6V5zM5 13h6v6H5v-6zm8 0h6v6h-6v-6z',
  hermes: 'M12 3c2.4 2.6 3.6 4.8 3.6 7.2 0 2-.8 3.6-2.2 4.6V19H10.6v-4.2C9.2 13.8 8.4 12.2 8.4 10.2 8.4 7.8 9.6 5.6 12 3z',
  openclaw: 'M4 14c2-6 6-8 8-8s6 2 8 8c-2 1-4 2-8 2s-6-1-8-2zm4 1c.6 2.6 1.8 4.4 4 4.4s3.4-1.8 4-4.4',
  'every-code': 'M8 6L4 12l4 6M16 6l4 6-4 6M13 5l-2 14',
  astudio: 'M12 3L4 21h3.2l1.6-3.6h6.4L16.8 21H20L12 3zm0 7.2l2.2 5.2H9.8L12 10.2z',
  'oh-my-pi': 'M7 7h11v2.2H7V7zM9 7v12h2.2V7H9zM13.2 7v8.2h2.2V7h-2.2z',
  omo: 'M12 4a8 8 0 100 16 8 8 0 000-16zm0 3.2a4.8 4.8 0 110 9.6 4.8 4.8 0 010-9.6z',
  pi: 'M8 6h10v2.2H8V6zM10 6v12h2.2V6H10zM14.2 6v8h2.2V6h-2.2z',
  dots: 'M5 10.2h3.2v3.2H5v-3.2zm5.4 0h3.2v3.2h-3.2v-3.2zM15.8 10.2H19v3.2h-3.2v-3.2z',
  'prime-agent': 'M6 4h9a5 5 0 010 10H9.2V20H6V4zm3.2 2.6v4.8H15a2.4 2.4 0 000-4.8H9.2z',
  'craft-agents': 'M5 8h6V5h2v3h6v2h-6v9h-2V10H5V8z',
  'kilo-cli': 'M6 4h3.2l4.8 10.2V4H17v16h-3.2L9 9.8V20H6V4z',
  'kilo-code': 'M4 6h7v2.2H7.2V11H10v2.2H7.2V18H4V6zm9 0h7v2.2h-4.2V11H19v2.2h-3.2V18H13V6z',
  'roo-code': 'M4 11L12 4l8 7v9H4v-9zm3 2v4h4v-4H7zm6 0v4h4v-4h-4z',
  'zed-agent': 'M4 5h16v3H8.5l8.2 11H20v3H4v-3h10.8L6.6 8H4V5z',
  goose: 'M4 14c2-5 5-8 8-8 2 0 3.2 1 4 2.4 1.6-.6 3.2.2 4 1.6-1.4.2-2.6.8-3.4 1.6 1 .8 1.6 2 1.4 3.4-2 .2-4-.4-5.4-1.6C11 15.2 8 16 4 14z',
  droid: 'M8 8h8v2h2v8H6V10h2V8zm2 4h1.4v1.4H10V12zm2.6 0H14v1.4h-1.4V12zM9 16.2h6v1.4H9v-1.4z',
  anythingllm: 'M5 19L12 4l7 15h-2.4l-1.4-3.2H8.8L7.4 19H5zm4.6-5h4.8L12 9.2 9.6 14z',
  'claude-science': 'M9 3h6v2.2H13V8c2.8.4 5 2.6 5 5.6V19H6v-5.4C6 10.6 8.2 8.4 11 8V5.2H9V3z',
  'lm-studio': 'M6 5h3.2v10.2L16.8 5H20v14h-3.2V8.8L9.2 19H6V5z',
  'unsloth-studio': 'M7 5h3.2l1.8 9.2L13.8 5H17l-3.2 14h-3.6L7 5z',
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
