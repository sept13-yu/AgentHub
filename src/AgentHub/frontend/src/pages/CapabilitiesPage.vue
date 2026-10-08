<script setup lang="ts">
import { ref } from 'vue'
import DocsPage from './DocsPage.vue'
import McpPanel from './McpPanel.vue'

type CapTab = 'skills' | 'mcp'
const tab = ref<CapTab>('skills')
const docsPage = ref<InstanceType<typeof DocsPage> | null>(null)
const mcpPanel = ref<InstanceType<typeof McpPanel> | null>(null)
const changingTab = ref(false)

async function pickTab(next: CapTab) {
  if (next === tab.value || changingTab.value) return
  changingTab.value = true
  try {
    if (tab.value === 'skills' && docsPage.value && !(await docsPage.value.canLeave())) return
    if (tab.value === 'mcp' && mcpPanel.value && !(await mcpPanel.value.canLeave())) return
    tab.value = next
  } finally {
    changingTab.value = false
  }
}
</script>

<template>
  <teleport defer to="#chrome-tabs">
    <div class="ah-tabs" role="group" aria-label="能力">
      <button type="button" :disabled="changingTab" :aria-pressed="tab === 'skills' ? 'true' : 'false'" @click="pickTab('skills')">Skills</button>
      <button type="button" :disabled="changingTab" :aria-pressed="tab === 'mcp' ? 'true' : 'false'" @click="pickTab('mcp')">MCP</button>
    </div>
  </teleport>
  <DocsPage v-if="tab === 'skills'" ref="docsPage" panel="skills" />
  <McpPanel v-else ref="mcpPanel" />
</template>
