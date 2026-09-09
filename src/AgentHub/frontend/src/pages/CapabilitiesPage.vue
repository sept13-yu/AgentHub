<script setup lang="ts">
import { ref } from 'vue'
import DocsPage from './DocsPage.vue'
import McpPanel from './McpPanel.vue'

type CapTab = 'skills' | 'mcp'
const tab = ref<CapTab>('skills')
</script>

<template>
  <teleport defer to="#chrome-tabs">
    <div class="ah-tabs" role="group" aria-label="能力">
      <button type="button" :aria-pressed="tab === 'skills' ? 'true' : 'false'" @click="tab = 'skills'">Skills</button>
      <button type="button" :aria-pressed="tab === 'mcp' ? 'true' : 'false'" @click="tab = 'mcp'">MCP</button>
    </div>
  </teleport>
  <DocsPage v-if="tab === 'skills'" panel="skills" />
  <McpPanel v-else />
</template>