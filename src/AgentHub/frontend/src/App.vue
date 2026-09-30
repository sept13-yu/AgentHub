<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { NConfigProvider, NMessageProvider, darkTheme, lightTheme, type GlobalThemeOverrides } from 'naive-ui'
import { naiveOverrides } from './tokens'
import { theme } from './theme'
import AhContextMenu from './components/AhContextMenu.vue'
import AppShell from './AppShell.vue'

const route = useRoute()
const bare = computed(() => route.name === 'desktop-quota')
const overrides = computed(() => naiveOverrides(theme.value) as GlobalThemeOverrides)
const naiveTheme = computed(() => (theme.value === 'dark' ? darkTheme : lightTheme))
</script>

<template>
  <router-view v-if="bare" />
  <n-config-provider v-else class="ah-root" :theme="naiveTheme" :theme-overrides="overrides">
    <n-message-provider>
      <ah-context-menu>
        <app-shell />
      </ah-context-menu>
    </n-message-provider>
  </n-config-provider>
</template>

<style>
.ah-root {
  height: 100%;
}
</style>
