import { createApp } from 'vue'
import { createRouter, createWebHashHistory } from 'vue-router'
import App from './App.vue'
import DashboardPage from './pages/DashboardPage.vue'
import SessionsPage from './pages/SessionsPage.vue'
import DocsPage from './pages/DocsPage.vue'
import CapabilitiesPage from './pages/CapabilitiesPage.vue'
import CodexConfigPage from './pages/CodexConfigPage.vue'
import RulesPage from './pages/RulesPage.vue'
import SettingsPage from './pages/SettingsPage.vue'
import { applyCssVars } from './tokens'
import { theme } from './theme'
import './styles.css'

applyCssVars(document.documentElement, theme.value)

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/', redirect: '/dashboard' },
    { path: '/dashboard', component: DashboardPage, meta: { title: '仪表盘' } },
    { path: '/sessions', component: SessionsPage, meta: { title: '会话' } },
    { path: '/capabilities', component: CapabilitiesPage, meta: { title: '能力' } },
    { path: '/docs', component: DocsPage, props: { panel: 'library' }, meta: { title: '资料' } },
    { path: '/rules', component: RulesPage, meta: { title: '规则' } },
    { path: '/codex-config', component: CodexConfigPage, meta: { title: 'Codex' } },
    { path: '/settings', component: SettingsPage, meta: { title: '设置' } },
  ],
})

createApp(App).use(router).mount('#app')
