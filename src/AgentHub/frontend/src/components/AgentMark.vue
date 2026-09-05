<script setup lang="ts">
import { computed } from 'vue'
import { agentIcon } from '../agentMeta'

/** Cursor / ZCode 单色标跟字色；WorkBuddy / Codex / 彩标用自带底板的原图。 */
const INK: Record<string, string> = {
  cursor: 'M11.503.131 1.891 5.678a.84.84 0 0 0-.42.726v11.188c0 .3.162.575.42.724l9.609 5.55a1 1 0 0 0 .998 0l9.61-5.55a.84.84 0 0 0 .42-.724V6.404a.84.84 0 0 0-.42-.726L12.497.131a1.01 1.01 0 0 0-.996 0M2.657 6.338h18.55c.263 0 .43.287.297.515L12.23 22.918c-.062.107-.229.064-.229-.06V12.335a.59.59 0 0 0-.295-.51l-9.11-5.257c-.109-.063-.064-.23.061-.23',
  zcode: 'M5 4h14v3.2L10.2 16H19V20H5v-3.2L13.8 8H5V4z',
}

const props = defineProps<{ id: string | null | undefined }>()

const ink = computed(() => (props.id ? INK[props.id] : undefined))
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
