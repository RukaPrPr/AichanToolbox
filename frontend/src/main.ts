import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { callHost } from './bridge'
import { useAppStore } from './store'
import type { StartupSnapshot } from './types'
import './styles.css'

const scriptStartedAt = performance.now()
const htmlStartedAt = Number((window as Window & {
  __AICHAN_BOOT__?: { htmlStartedAt?: number }
}).__AICHAN_BOOT__?.htmlStartedAt ?? 0)
const profileStorageKey = 'aichan:last-workflow-profile'
const pinia = createPinia()
let startup: StartupSnapshot | null = null
let startupError = ''
const requestStartedAt = performance.now()

try {
  startup = await callHost<StartupSnapshot>('app.startup', {
    rememberedProfile: localStorage.getItem(profileStorageKey) ?? ''
  })
  const store = useAppStore(pinia)
  store.version = startup.version
  store.processorCount = startup.processorCount
  store.setJobs(startup.jobs ?? [])
  store.setArchives(startup.archives ?? [])
  if (startup.workflow) store.replaceWorkflow(startup.workflow)
} catch (error) {
  startupError = error instanceof Error ? error.message : String(error)
}

const startupRequestMs = performance.now() - requestStartedAt
const app = createApp(App, { startup, startupError, startupRequestMs, scriptStartedAt, htmlStartedAt })
app.use(pinia)
app.mount('#app')
