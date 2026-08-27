/** Wait for actual browser frames; a timer firing is not evidence of a painted page. */
export async function waitForStartupPaint(
  page: Pick<Document, 'fonts' | 'images'> = document,
  requestFrame: (callback: FrameRequestCallback) => number = callback => window.requestAnimationFrame(callback)
) {
  try { await page.fonts.ready } catch { /* System font fallbacks can still render. */ }
  await Promise.all(Array.from(page.images, async image => {
    try { await image.decode() } catch { /* A missing optional image must not block startup. */ }
  }))
  // The first callback runs before paint; the next frame gives the mounted UI a
  // paint opportunity. A hidden/minimized page waits until it can render again.
  await new Promise<void>(resolve => requestFrame(() => requestFrame(() => resolve())))
}
