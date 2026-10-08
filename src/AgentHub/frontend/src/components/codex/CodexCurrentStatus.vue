<script setup lang="ts">
import type { CodexStatus } from '../../codex/types'

defineProps<{
  status: CodexStatus | null
  currentLabel: string
  authLoginText: string
  liveUnarchived: boolean
  notice: string
}>()
</script>

<template>
  <section class="card current-status" aria-labelledby="codex-current-title">
    <div class="card-head">
      <h2 id="codex-current-title">当前配置</h2>
      <span v-if="status" class="process-state" :aria-label="`Codex 进程${status.codexRunning ? '运行中' : '未运行'}`">
        {{ status.codexRunning ? '运行中' : '未运行' }}
      </span>
    </div>
    <div class="card-body current-body">
      <template v-if="status">
        <div class="configuration">
          <div class="configuration-main">
            <p class="current-label">{{ currentLabel }}</p>
            <p v-if="status.live?.baseUrl" class="address">{{ status.live.baseUrl }}</p>
            <p v-if="authLoginText && authLoginText !== currentLabel" class="login-text">{{ authLoginText }}</p>
            <p v-if="liveUnarchived" class="unarchived">登录副本未保存</p>
          </div>
        </div>
        <dl v-if="status.liveModel" class="facts">
          <div v-if="status.liveModel"><dt>配置模型</dt><dd>{{ status.liveModel }}</dd></div>
        </dl>
        <div v-if="status.configBroken" class="notice danger" role="alert">
          配置无法解析，写入已暂停。修复配置后请刷新。
          <span class="path">{{ status.configPath }}</span>
        </div>
        <div v-if="status.externalChanged" class="notice warning" role="status">
          当前文件与保存的连接不一致，切换会重新写入。
        </div>
        <div v-if="status.live?.isHybridForm" class="notice warning" role="status">
          当前配置包含混合认证字段，请核对后再切换。
        </div>
      </template>
      <p v-else class="process-hint">尚未读取到当前配置。</p>
      <div v-if="notice" class="notice session-notice" role="status">{{ notice }}</div>
    </div>
  </section>
</template>

<style scoped>
h2, p, dl, dd { margin: 0; }
h2 { font-size: var(--fs-card); font-weight: 600; }
.process-state { margin-left: auto; font-size: var(--fs-small); color: var(--dim); font-weight: 400; }
.current-body { padding: var(--sp-4) var(--sp-5); }
.configuration { display: flex; align-items: flex-start; gap: var(--sp-4); justify-content: space-between; }
.configuration-main { min-width: 0; }
.current-label { font-size: var(--fs-title); font-weight: 600; overflow-wrap: anywhere; }
.address, .path { font-family: var(--mono); overflow-wrap: anywhere; }
.address { color: var(--dim); font-size: var(--fs-small); margin-top: var(--sp-1); }
.login-text { margin-top: var(--sp-2); overflow-wrap: anywhere; }
.unarchived { margin-top: var(--sp-1); font-size: var(--fs-small); color: var(--warn); }
.facts { display: flex; flex-wrap: wrap; gap: var(--sp-2) var(--sp-6); margin-top: var(--sp-2); font-size: var(--fs-small); }
.facts > div { display: flex; gap: var(--sp-2); }
dt { color: var(--dim); }
dd { overflow-wrap: anywhere; }
.process-hint { color: var(--dim); font-size: var(--fs-small); margin-top: var(--sp-2); }
.notice { margin-top: var(--sp-3); padding: var(--sp-3); border-radius: var(--r-in); font-size: var(--fs-small); overflow-wrap: anywhere; }
.danger { background: var(--danger-soft); color: var(--danger); }
.warning { background: var(--warn-soft); color: var(--text); }
.session-notice { background: var(--accent-soft); color: var(--text); }
.path { display: block; margin-top: var(--sp-1); }
@media (max-width: 640px) {
  .configuration { flex-direction: column; gap: var(--sp-3); }
  .facts { flex-direction: column; gap: var(--sp-2); }
}
</style>
