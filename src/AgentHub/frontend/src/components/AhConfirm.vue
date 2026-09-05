<script setup lang="ts">
import { NButton, NModal } from 'naive-ui'

defineProps<{
  show: boolean
  text: string
  okText?: string
  altText?: string
  hideCancel?: boolean
}>()

const emit = defineEmits<{
  'update:show': [boolean]
  confirm: []
  alt: []
}>()
</script>

<template>
  <n-modal :show="show" :mask-closable="true" @update:show="emit('update:show', $event)">
    <div class="ah-confirm" role="dialog" aria-modal="true" :aria-label="text">
      <p>{{ text }}</p>
      <slot />
      <div class="ah-confirm-acts">
        <n-button v-if="altText" @click="emit('alt')">{{ altText }}</n-button>
        <n-button v-if="!hideCancel" @click="emit('update:show', false)">取消</n-button>
        <n-button type="primary" @click="emit('confirm')">{{ okText || '确定' }}</n-button>
      </div>
    </div>
  </n-modal>
</template>

<style scoped>
.ah-confirm {
  width: min(400px, calc(100vw - 48px));
  padding: var(--sp-5);
  background: var(--surface);
  border: 1px solid var(--stroke);
  border-radius: var(--r-card);
}
.ah-confirm p {
  margin: 0 0 var(--sp-4);
  font-size: var(--fs-body);
  color: var(--text);
  line-height: 1.55;
  white-space: pre-wrap;
}
.ah-confirm-acts {
  display: flex;
  justify-content: flex-end;
  gap: var(--sp-2);
  margin-top: var(--sp-4);
}
</style>
