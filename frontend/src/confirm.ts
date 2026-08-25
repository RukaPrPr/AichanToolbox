import { ref } from 'vue'

export interface GlassConfirmation {
  title: string
  message: string
  confirmText: string
  cancelText: string | null
  danger: boolean
}

export const actionDialog = ref<GlassConfirmation | null>(null)

let pendingResolve: ((value: boolean) => void) | null = null

export function requestAction(title: string, message: string, confirmText: string, danger = false, cancelText: string | null = '取消') {
  pendingResolve?.(false)
  actionDialog.value = { title, message, confirmText, cancelText, danger }
  return new Promise<boolean>(resolve => { pendingResolve = resolve })
}

export function requestNotice(title: string, message: string, confirmText = '知道了', danger = false) {
  return requestAction(title, message, confirmText, danger, null)
}

export function resolveAction(value: boolean) {
  const resolve = pendingResolve
  pendingResolve = null
  actionDialog.value = null
  resolve?.(value)
}
