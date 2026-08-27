export interface ActivityClock {
  set(callback: () => void, delay: number): number
  clear(timer: number): void
}

/** Coalesces wheel bursts while keeping held pointer gestures active. */
export function createCanvasActivity(
  onChange: (active: boolean) => void,
  clock: ActivityClock = {
    set: (callback, delay) => window.setTimeout(callback, delay),
    clear: timer => window.clearTimeout(timer)
  },
  settleDelay = 150
) {
  let active = false
  let held = false
  let disposed = false
  let timer: number | undefined

  function clearTimer() {
    if (timer !== undefined) clock.clear(timer)
    timer = undefined
  }

  function change(next: boolean) {
    if (active === next || disposed) return
    active = next
    onChange(next)
  }

  function settle() {
    clearTimer()
    if (!active || held) return
    timer = clock.set(() => {
      timer = undefined
      if (!held) change(false)
    }, settleDelay)
  }

  return {
    hold(next: boolean) {
      if (disposed || held === next) return
      held = next
      clearTimer()
      if (held) change(true)
      else settle()
    },
    pulse() {
      if (disposed) return
      change(true)
      settle()
    },
    reset() {
      clearTimer()
      held = false
      change(false)
    },
    dispose() {
      clearTimer()
      disposed = true
    }
  }
}
