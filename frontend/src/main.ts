import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { callHost } from './bridge'
import { useAppStore } from './store'
import { applyTheme, rememberTheme } from './theme'
import type { StartupSnapshot } from './types'
import './styles.css'
import './themes.css'

const scriptStartedAt = performance.now()
const htmlStartedAt = Number((window as Window & {
  __AICHAN_BOOT__?: { htmlStartedAt?: number }
}).__AICHAN_BOOT__?.htmlStartedAt ?? 0)
const profileStorageKey = 'aichan:last-workflow-profile'
applyTheme(document.documentElement.dataset.theme)
let rememberedProfile = ''
try { rememberedProfile = localStorage.getItem(profileStorageKey) ?? '' } catch { /* Optional preferences may be unavailable. */ }
const pinia = createPinia()
let startup: StartupSnapshot | null = null
let startupError = ''
const requestStartedAt = performance.now()

try {
  startup = await callHost<StartupSnapshot>('app.startup', {
    rememberedProfile
  })
  const store = useAppStore(pinia)
  store.version = startup.version
  store.processorCount = startup.processorCount
  store.setJobs(startup.jobs ?? [])
  store.setArchives(startup.archives ?? [])
  if (startup.workflow) store.replaceWorkflow(startup.workflow)
  // The native preference colors the splash and survives cleared WebView2 data.
  if (typeof startup.theme === 'string') {
    const theme = applyTheme(startup.theme)
    rememberTheme(theme)
    await callHost('app.setTheme', {
      id: theme.id, colorScheme: theme.colorScheme, background: theme.background
    })
  }
} catch (error) {
  startupError = error instanceof Error ? error.message : String(error)
}

const startupRequestMs = performance.now() - requestStartedAt
const app = createApp(App, { startup, startupError, startupRequestMs, scriptStartedAt, htmlStartedAt })
app.use(pinia)
app.mount('#app')
