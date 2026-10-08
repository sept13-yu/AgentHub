<script setup lang="ts">
import { computed, nextTick, ref, useId } from 'vue'
import { NButton, NModal } from 'naive-ui'

const props = withDefaults(defineProps<{
  show: boolean
  text: string
  title?: string
  okText?: string
  altText?: string
  hideCancel?: boolean
  tone?: 'primary' | 'danger'
  loading?: boolean
  disabled?: boolean
  error?: string
  maskClosable?: boolean
}>(), {
  tone: 'primary',
  loading: false,
  disabled: false,
  maskClosable: true,
})

const emit = defineEmits<{
  'update:show': [boolean]
  confirm: []
  alt: []
}>()

const id = useId()
const dispatching = ref(false)
const pending = computed(() => props.loading || dispatching.value)

function updateShow(show: boolean) {
  if (!show && pending.value) return
  emit('update:show', show)
}

async function runAction(action: 'confirm' | 'alt') {
  if (pending.value || props.disabled) return
  dispatching.value = true
  try {
    if (action === 'confirm') emit('confirm')
    else emit('alt')
    // emit 不等待异步处理器；跨请求的防重由调用页 loading 承担。
    await nextTick()
  } finally {
    dispatching.value = false
  }
}
</script>

<template>
  <n-modal :show="show" :mask-closable="maskClosable && !pending" :close-on-esc="!pending" @update:show="updateShow">
    <div class="ah-confirm" role="dialog" aria-modal="true" :aria-labelledby="title ? id + '-title' : undefined" :aria-label="title ? undefined : text" :aria-describedby="id + '-text'" :aria-busy="pending">
      <h2 v-if="title" :id="id + '-title'">{{ title }}</h2>
      <p :id="id + '-text'">{{ text }}</p>
      <slot />
      <p v-if="error" class="ah-confirm-error" role="alert">{{ error }}</p>
      <div class="ah-confirm-acts">
        <n-button v-if="altText" :disabled="pending || disabled" @click="runAction('alt')">{{ altText }}</n-button>
        <n-button v-if="!hideCancel" :disabled="pending" @click="updateShow(false)">取消</n-button>
        <n-button :type="tone === 'danger' ? 'error' : 'primary'" :loading="loading" :disabled="pending || disabled" @click="runAction('confirm')">{{ okText || '确定' }}</n-button>
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
.ah-confirm h2 {
  margin: 0 0 var(--sp-3);
  font-size: var(--fs-title);
  font-weight: 600;
}
.ah-confirm p {
  margin: 0 0 var(--sp-4);
  font-size: var(--fs-body);
  color: var(--text);
  line-height: 1.55;
  white-space: pre-wrap;
}
.ah-confirm .ah-confirm-error {
  padding: var(--sp-3);
  color: var(--error-fg);
  background: var(--error-soft);
  border-radius: var(--r-in);
}
.ah-confirm-acts {
  display: flex;
  justify-content: flex-end;
  gap: var(--sp-2);
  margin-top: var(--sp-4);
}
</style>
