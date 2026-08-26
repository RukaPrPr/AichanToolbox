type EventHandler = (data: any) => void

interface PendingCall {
  resolve: (value: any) => void
  reject: (reason: Error) => void
}

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: unknown): void
        postMessageWithAdditionalObjects?(message: unknown, additionalObjects: unknown[]): void
        addEventListener(name: 'message', handler: (event: MessageEvent) => void): void
      }
    }
  }
}

const pending = new Map<string, PendingCall>()
const handlers = new Map<string, Set<EventHandler>>()
const mockProfiles = new Map<string, unknown>()
const webview = window.chrome?.webview

if (webview) {
  webview.addEventListener('message', (message) => {
    const value = message.data
    if (value?.id && pending.has(value.id)) {
      const call = pending.get(value.id)!
      pending.delete(value.id)
      value.ok ? call.resolve(value.data) : call.reject(new Error(value.error || '操作失败'))
      return
    }
    if (value?.event) handlers.get(value.event)?.forEach(handler => handler(value.data))
  })
}

export function callHost<T = unknown>(command: string, payload: unknown = {}): Promise<T> {
  if (!webview) return mockCall(command, payload) as Promise<T>
  const id = crypto.randomUUID()
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve, reject })
    webview.postMessage({ id, command, payload })
  })
}

export function importDroppedFiles<T = unknown>(files: File[], command = 'files.drop', payload: unknown = {}): Promise<T> {
  if (!files.length) return Promise.resolve([] as T)
  if (!webview?.postMessageWithAdditionalObjects)
    return Promise.reject(new Error('当前预览环境不支持系统文件拖入，请使用“选择图片”。'))
  const id = crypto.randomUUID()
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve, reject })
    webview.postMessageWithAdditionalObjects!({ id, command, payload }, files)
  })
}

export function onHostEvent(eventName: string, handler: EventHandler) {
  if (!handlers.has(eventName)) handlers.set(eventName, new Set())
  handlers.get(eventName)!.add(handler)
  return () => handlers.get(eventName)?.delete(handler)
}

async function mockCall(command: string, payload: unknown) {
  if (command === 'app.startup') return {
    version: '8.0.0-dev', processorCount: 16, jobs: [], archives: [], maximized: false,
    profiles: Array.from(mockProfiles.keys()).sort((a, b) => a.localeCompare(b, 'zh-CN')),
    selectedProfile: '', workflow: null
  }
  if (command === 'app.ready') return { version: '8.0.0-dev', processorCount: 16, jobs: [], archives: [], maximized: false }
  if (command === 'app.frontendReady') return null
  if (command === 'window.maximize') return { maximized: false }
  if (command.startsWith('window.')) return null
  if (command === 'workflow.load') return null
  if (command === 'profiles.list') return Array.from(mockProfiles.keys()).sort((a, b) => a.localeCompare(b, 'zh-CN'))
  if (command === 'profiles.save') {
    const value = payload as { name: string; workflow: unknown }
    mockProfiles.set(value.name, JSON.parse(JSON.stringify(value.workflow)))
    return Array.from(mockProfiles.keys()).sort((a, b) => a.localeCompare(b, 'zh-CN'))
  }
  if (command === 'profiles.load') {
    const name = (payload as { name: string }).name
    return JSON.parse(JSON.stringify(mockProfiles.get(name)))
  }
  if (command === 'profiles.rename') {
    const value = payload as { oldName: string; newName: string }
    if (!mockProfiles.has(value.oldName)) throw new Error(`找不到工作流配置“${value.oldName}”。`)
    if (value.oldName !== value.newName && mockProfiles.has(value.newName)) throw new Error(`工作流配置“${value.newName}”已经存在。`)
    const workflow = mockProfiles.get(value.oldName)
    mockProfiles.delete(value.oldName)
    mockProfiles.set(value.newName, workflow)
    return Array.from(mockProfiles.keys()).sort((a, b) => a.localeCompare(b, 'zh-CN'))
  }
  if (command === 'profiles.delete') {
    mockProfiles.delete((payload as { name: string }).name)
    return Array.from(mockProfiles.keys()).sort((a, b) => a.localeCompare(b, 'zh-CN'))
  }
  if (command === 'work.confirmReplacedSources') return { proceed: true, prompted: false, count: 0, ids: [] }
  if (command === 'work.acceptReplacedSources') return []
  if (command === 'work.confirmReplacedArchives') return { proceed: true, prompted: false, count: 0, ids: [] }
  if (command === 'work.acceptReplacedArchives') return { archives: [], jobs: [] }
  if (command === 'archives.preflight') return { connected: 0, archives: 0, pending: 0, missingNodes: 0, required: false }
  if (command === 'archives.preprocess') return { archives: [], jobs: [] }
  if (command.startsWith('archives.')) return []
  if (command === 'dialog.outputDirectory') return 'D:\\图片输出'
  return null
}
