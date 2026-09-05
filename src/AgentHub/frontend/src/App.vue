<script setup lang="ts">
import { computed } from 'vue'
import { NConfigProvider, NMessageProvider, darkTheme, lightTheme, type GlobalThemeOverrides } from 'naive-ui'
import { naiveOverrides } from './tokens'
import { theme } from './theme'
import AppShell from './AppShell.vue'

const overrides = computed(() => naiveOverrides(theme.value) as GlobalThemeOverrides)
const naiveTheme = computed(() => (theme.value === 'dark' ? darkTheme : lightTheme))
</script>

<template>
  <n-config-provider class="ah-root" :theme="naiveTheme" :theme-overrides="overrides">
    <n-message-provider>
      <app-shell />
    </n-message-provider>
  </n-config-provider>
</template>

<style>
.ah-root {
  height: 100%;
}
</style>
