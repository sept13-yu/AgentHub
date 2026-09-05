import { createApp } from 'vue'
import { createRouter, createWebHashHistory } from 'vue-router'
import App from './App.vue'
import DashboardPage from './pages/DashboardPage.vue'
import SessionsPage from './pages/SessionsPage.vue'
import DocsPage from './pages/DocsPage.vue'
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
    { path: '/sessions', component: SessionsPage, meta: { title: '会话管理' } },
    { path: '/docs', component: DocsPage, meta: { title: '资料中心' } },
    { path: '/rules', component: RulesPage, meta: { title: '共用规则' } },
    { path: '/codex-config', component: CodexConfigPage, meta: { title: 'Codex 配置' } },
    { path: '/settings', component: SettingsPage, meta: { title: '设置' } },
  ],
})

createApp(App).use(router).mount('#app')
