<script setup lang="ts">
import { onMounted, onUnmounted, provide, ref } from 'vue'
import { useRoute } from 'vue-router'
import { bindAppHotkeys, pageHotkeysKey, type PageHotkeys } from './hotkeys'
import { FileText, KeyRound, LayoutDashboard, Menu, MessageSquare, Moon, ScrollText, Settings, Sun, Zap } from 'lucide-vue-next'
import { get } from './api'
import appIcon from './assets/agenthub.png'
import { setTheme, theme } from './theme'
import { setTokenUnit } from './tokenUnit'

const route = useRoute()
const drawerOpen = ref(false)
const pageLoading = ref(false)
const pageHotkeys = ref<PageHotkeys | null>(null)
provide('page-loading', pageLoading)
provide(pageHotkeysKey, pageHotkeys)

let stopHotkeys: (() => void) | null = null
onMounted(() => {
  stopHotkeys = bindAppHotkeys(pageHotkeys)
  void get<{ dashboard?: { tokenUnit?: string } }>('/api/settings')
    .then((s) => {
      const u = s.dashboard?.tokenUnit
      if (u === 'en' || u === 'zh') setTokenUnit(u)
    })
    .catch(() => { /* 首屏用本地缓存单位 */ })
})
onUnmounted(() => { stopHotkeys?.() })

const nav = [
  { to: '/dashboard', label: '仪表盘', icon: LayoutDashboard },
  { to: '/sessions', label: '会话', icon: MessageSquare },
  { to: '/capabilities', label: '能力', icon: Zap },
  { to: '/docs', label: '资料', icon: FileText },
  { to: '/rules', label: '规则', icon: ScrollText },
  { to: '/codex-config', label: 'Codex', icon: KeyRound },
]
</script>

<template>
  <div class="app">
    <div class="scrim" :class="{ 'is-on': drawerOpen }" @click="drawerOpen = false" />

    <aside class="rail" :class="{ 'is-open': drawerOpen }">
      <div class="brand">
        <span class="brand-mark" aria-hidden="true">
          <img class="brand-img" :src="appIcon" alt="" />
        </span>
        <span class="brand-name">AgentHub</span>
      </div>
      <nav class="nav" aria-label="主导航">
        <router-link
          v-for="item in nav"
          :key="item.to"
          :to="item.to"
          class="nav-item"
          :title="item.label"
          @click="drawerOpen = false"
        >
          <component :is="item.icon" :size="16" :stroke-width="1.8" /><span class="nav-label">{{ item.label }}</span>
        </router-link>
      </nav>
      <div class="rail-foot">
        <router-link
          to="/settings"
          class="nav-item settings-foot"
          title="设置"
          @click="drawerOpen = false"
        >
          <Settings :size="16" :stroke-width="1.8" /><span class="nav-label">设置</span>
        </router-link>
      </div>
    </aside>

    <main class="main">
      <div class="page-load" v-show="pageLoading"><i /></div>
      <div class="chrome">
        <button
          class="menu-btn"
          type="button"
          aria-label="打开导航"
          aria-controls="rail"
          :aria-expanded="drawerOpen ? 'true' : 'false'"
          @click="drawerOpen = !drawerOpen"
        >
          <Menu :size="16" :stroke-width="1.8" />
        </button>
        <h1>{{ route.meta.title }}</h1>
        <span class="spacer" />
        <div class="chrome-end">
          <div id="chrome-tabs" />
          <div id="chrome-extra" />
          <div id="chrome-actions" />
          <button
            type="button"
            class="theme-btn chrome-theme"
            :title="theme === 'dark' ? '切换到浅色' : '切换到深色'"
            :aria-label="theme === 'dark' ? '切换到浅色' : '切换到深色'"
            @click="setTheme(theme === 'dark' ? 'light' : 'dark')"
          >
            <Sun v-if="theme === 'dark'" :size="16" :stroke-width="1.8" />
            <Moon v-else :size="16" :stroke-width="1.8" />
          </button>
        </div>
      </div>
      <div class="stage">
        <router-view />
      </div>
    </main>
  </div>
</template>

<style scoped>
.app {
  display: grid;
  grid-template-columns: 208px minmax(0, 1fr);
  height: 100%;
  height: 100dvh;
  overflow: hidden;
}
.scrim {
  display: none;
  position: fixed;
  inset: 0;
  background: var(--overlay);
  z-index: 30;
}

.rail {
  min-height: 0;
  overflow-x: hidden;
  overflow-y: auto;
  background: var(--bg-sunken);
  border-right: 1px solid var(--stroke);
  padding: var(--sp-4) var(--sp-2);
  display: flex;
  flex-direction: column;
  gap: var(--sp-4);
}
.brand {
  display: flex;
  align-items: center;
  gap: var(--sp-2);
  padding: var(--sp-2) var(--sp-3);
  margin-bottom: var(--sp-1);
}
.brand-mark {
  width: var(--h-icon-btn);
  height: var(--h-icon-btn);
  border-radius: var(--r-in);
  overflow: hidden;
  flex: none;
}
.brand-img {
  width: 100%;
  height: 100%;
  display: block;
  object-fit: cover;
}
.brand-name {
  font-size: var(--fs-card);
  font-weight: 600;
  letter-spacing: -0.005em;
}
.nav {
  display: flex;
  flex-direction: column;
  gap: 1px;
}
.nav-item {
  display: flex;
  align-items: center;
  gap: var(--sp-2);
  padding: 0 var(--sp-3);
  height: var(--h-control);
  max-width: 100%;
  min-width: 0;
  box-sizing: border-box;
  border-radius: var(--r-in);
  color: var(--dim);
  text-decoration: none;
  font-size: var(--fs-body);
  transition:
    background var(--dur) linear,
    color var(--dur) linear;
}
.nav-item:hover {
  background: var(--wash);
  color: var(--text);
}
.nav-item:active {
  background: var(--seg-on-bg);
}
.nav-item[aria-current='page'] {
  background: var(--accent-soft);
  color: var(--text);
  font-weight: 600;
  box-shadow: inset 2px 0 0 var(--accent-solid);
}
.rail-foot {
  margin-top: auto;
  padding: var(--sp-2) 0;
  overflow: hidden;
  min-width: 0;
}
.settings-foot {
  width: 100%;
  max-width: 100%;
}
.theme-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: var(--h-icon-btn);
  height: var(--h-icon-btn);
  padding: 0;
  border: 0;
  border-radius: var(--r-in);
  background: transparent;
  color: var(--dim);
  cursor: pointer;
  flex: none;
  transition:
    background var(--dur) linear,
    color var(--dur) linear;
}
.theme-btn:hover {
  background: var(--wash);
  color: var(--text);
}
.theme-btn:active {
  background: var(--seg-on-bg);
}
.chrome-theme {
  margin-left: var(--sp-2);
}

.main {
  padding: var(--sp-6) var(--sp-7);
  display: flex;
  flex-direction: column;
  gap: var(--sp-5);
  box-sizing: border-box;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}
.stage {
  flex: 1;
  min-height: 0;
  overflow: auto;
  scrollbar-width: none;
  display: flex;
  flex-direction: column;
  gap: var(--sp-5);
}
.stage::-webkit-scrollbar { width: 0; height: 0; }
.stage > :deep(*) {
  flex-shrink: 0;
}
.stage > :deep(.rules),
.stage > :deep(.page) {
  flex: 1 1 auto;
  min-height: 0;
}
.page-load {
  height: 2px;
  margin: calc(var(--sp-6) * -1) calc(var(--sp-7) * -1) 0;
  background: var(--loading-track);
  overflow: hidden;
}
.page-load i {
  display: block;
  width: 38%;
  height: 100%;
  background: var(--loading-bar);
  animation: page-load-slide 1.1s linear infinite;
}
@keyframes page-load-slide {
  from { transform: translateX(-100%); }
  to { transform: translateX(280%); }
}
@media (prefers-reduced-motion: reduce) {
  .page-load i { animation: none; }
}
.chrome {
  display: flex;
  align-items: center;
  gap: var(--sp-3);
  flex-wrap: wrap;
  flex: none;
  z-index: 8;
  background: var(--bg);
  padding: 0;
}
.chrome h1 {
  font-size: var(--fs-page);
  font-weight: 650;
  letter-spacing: -0.01em;
  margin: 0;
}
.chrome-end {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-4);
}
.menu-btn {
  display: none;
  width: var(--h-icon-btn);
  height: var(--h-icon-btn);
  padding: 0;
  border: 1px solid var(--stroke);
  background: var(--surface);
  color: var(--text);
  border-radius: var(--r-in);
  cursor: pointer;
  align-items: center;
  justify-content: center;
  transition:
    background var(--dur) linear,
    border-color var(--dur) linear;
}
.menu-btn:hover {
  background: var(--surface-hi);
  border-color: var(--stroke-strong);
}
.menu-btn:active {
  background: var(--wash);
}
#chrome-tabs,
#chrome-extra,
#chrome-actions {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-2);
}
#chrome-extra:not(:empty) {
  margin-left: var(--sp-2);
}
#chrome-actions:not(:empty) {
  padding-left: var(--sp-4);
  border-left: 1px solid var(--stroke);
}

/* 中宽：侧栏收成 56px 图标栏，文字隐藏，链接靠 title 提示 */
@media (max-width: 1279px) {
  .app {
    grid-template-columns: 56px minmax(0, 1fr);
  }
  .brand {
    justify-content: center;
    padding-inline: 0;
  }
  .nav-item {
    justify-content: center;
    padding-inline: 0;
  }
  .nav-label,
  .brand-name {
    display: none;
  }
}

/* 窄窗：图标栏也放不下，改抽屉 + 菜单按钮 */
@media (max-width: 899px) {
  .app {
    grid-template-columns: 1fr;
  }
  .rail {
    position: fixed;
    inset: 0 auto 0 0;
    width: 208px;
    z-index: 40;
    transform: translateX(-100%);
    transition: transform var(--dur) linear;
  }
  .rail.is-open {
    transform: translateX(0);
  }
  .scrim.is-on {
    display: block;
  }
  .menu-btn {
    display: inline-flex;
  }
  .brand {
    justify-content: flex-start;
    padding: var(--sp-2) var(--sp-3);
  }
  .nav-item {
    justify-content: flex-start;
    padding-inline: var(--sp-3);
  }
  .nav-label,
  .brand-name {
    display: inline;
  }
  .main {
    padding: var(--sp-4);
  }
  .page-load {
    margin: calc(var(--sp-4) * -1) calc(var(--sp-4) * -1) 0;
  }
}

/* 矮屏：内容区滚动优先，避免主栏裁切 */
@media (max-height: 700px) {
  .main {
    padding-top: var(--sp-3);
    padding-bottom: var(--sp-3);
  }
  .stage {
    overflow: auto;
  }
}
</style>
