import { runInNewContext } from 'node:vm'
import { applyTheme, resolveTheme, rememberTheme, themes, graphitePurple, noirGold, themeStorageKey } from '../src/theme.ts'
import { createCanvasActivity } from '../src/canvasActivity.ts'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { setImmediate, setTimeout } from 'node:timers/promises'
import { test } from 'node:test'
import { buildProductionHtml } from '../build/html.mjs'
import { waitForStartupPaint } from '../src/startup.ts'

test('production shares early theme restoration and a light default with development', () => {
  const source = readFileSync(new URL('../index.html', import.meta.url), 'utf8')
  const output = buildProductionHtml(source)
  assert.match(output, /name="color-scheme" content="light dark"/)
  assert.match(output, /data-theme="light"/)
  assert.match(output, /--app-background:#eef1f4/)
  assert.ok(output.indexOf('<style>') < output.indexOf('href="./styles.css"'))
  assert.match(output, /window\.__AICHAN_BOOT__/)
  assert.equal((output.match(/src="\.\/assets\/app\.js"/g) ?? []).length, 1)
  assert.doesNotMatch(output, /src="\/src\/main\.ts"/)
})

test('a missing or duplicated source entry fails the build instead of shipping an empty page', () => {
  assert.throws(() => buildProductionHtml('<head></head>'))
  const entry = '<script type="module" src="/src/main.ts"></script>'
  assert.throws(() => buildProductionHtml(`<head></head>${entry}${entry}`))
})

test('readiness waits for fonts, decoded images and two real frame callbacks', async () => {
  const fonts = Promise.withResolvers()
  const logo = Promise.withResolvers()
  const frames = []
  let ready = false
  const task = waitForStartupPaint({
    fonts: { ready: fonts.promise }, images: [{ decode: () => logo.promise }]
  }, callback => frames.push(callback)).then(() => { ready = true })
  await setImmediate()
  assert.equal(frames.length, 0)
  fonts.resolve()
  await setImmediate()
  assert.equal(frames.length, 0)
  logo.resolve()
  await setImmediate()
  assert.equal(frames.length, 1)
  // Regression: the former 80ms timeout must not report a hidden page as painted.
  await setTimeout(120)
  assert.equal(ready, false)
  frames.shift()(0)
  await setImmediate()
  assert.equal(ready, false)
  assert.equal(frames.length, 1)
  frames.shift()(16)
  await task
  assert.equal(ready, true)
})

test('only the temporary splash uses a transparent HWND; the WebView2 host stays opaque', () => {
  const splash = readFileSync(new URL('../../desktop/StartupWindow.xaml', import.meta.url), 'utf8')
  const main = readFileSync(new URL('../../desktop/MainWindow.xaml', import.meta.url), 'utf8')
  assert.match(splash, /AllowsTransparency="True"/)
  assert.match(splash, /Background="Transparent"/)
  assert.doesNotMatch(main, /AllowsTransparency="True"/)
  assert.match(main, /wv2:WebView2 /)
  assert.match(main, /DefaultBackgroundColor="#EEF1F4"/)
  assert.doesNotMatch(splash, /WindowChrome/)
  assert.match(splash, /x:Name="StartupSurface" Background="#EEF1F4"/)
})

test('splash exit fades its visual content without animating native window opacity', () => {
  const splash = readFileSync(new URL('../../desktop/StartupWindow.xaml.cs', import.meta.url), 'utf8')
  assert.match(splash, /From = StartupSurface\.Opacity/)
  assert.match(splash, /StartupSurface\.BeginAnimation\(UIElement\.OpacityProperty, fade\)/)
  assert.doesNotMatch(splash, /(?<!\.)\bBeginAnimation\(OpacityProperty, fade\)/)
})

test('host readiness is completed only after the splash handoff, and is cancelled on close', () => {
  const host = readFileSync(new URL('../../desktop/MainWindow.xaml.cs', import.meta.url), 'utf8')
  const reveal = host.slice(host.indexOf('private void RevealFrontend()'), host.indexOf('private void MarkFrontendRevealed()'))
  assert.match(reveal, /Dispatcher\.BeginInvoke/)
  assert.match(reveal, /startup\.RevealAndClose\(MarkFrontendRevealed\)/)
  assert.doesNotMatch(reveal, /_frontendReadySignal\.TrySetResult/)
  assert.match(host, /_frontendReadySignal\.TrySetCanceled\(\)/)
})

test('failed optional assets use fallbacks, but cannot bypass the two frame handoff', async () => {
  const frames = []
  let ready = false
  const task = waitForStartupPaint({
    fonts: { ready: Promise.reject(new Error('font unavailable')) },
    images: [{ decode: async () => { throw new Error('image unavailable') } }]
  }, callback => frames.push(callback)).then(() => { ready = true })
  await setImmediate()
  assert.equal(ready, false)
  assert.equal(frames.length, 1)
  frames.shift()(0)
  frames.shift()(16)
  await task
  assert.equal(ready, true)
})

function themePage() {
  const values = new Map()
  return {
    values,
    documentElement: {
      dataset: { theme: 'light' },
      style: {
        colorScheme: 'light',
        setProperty: (key, value) => values.set(key, value),
        removeProperty: key => values.delete(key)
      }
    }
  }
}

function runThemeBootstrap(html, getStorage) {
  const page = themePage()
  const context = { document: page, window: {}, performance: { now: () => 12 } }
  Object.defineProperty(context, 'localStorage', { get: getStorage })
  const script = html.match(/<script>([\s\S]*?)<\/script>/)?.[1]
  assert.ok(script, 'the early bootstrap script must exist')
  runInNewContext(script, context)
  return page
}

test('development and production restore each saved theme before mounting the app', () => {
  const source = readFileSync(new URL('../index.html', import.meta.url), 'utf8')
  for (const html of [source, buildProductionHtml(source)]) {
    for (const theme of themes) {
      const saved = JSON.stringify({ id: theme.id, colorScheme: theme.colorScheme, background: theme.background })
      const page = runThemeBootstrap(html, () => ({
        getItem(key) { assert.equal(key, themeStorageKey); return saved }
      }))
      assert.equal(page.documentElement.dataset.theme, theme.id)
      assert.equal(page.documentElement.style.colorScheme, theme.colorScheme)
      assert.equal(page.values.get('--app-background'), theme.background)
    }
  }
})

test('bad or unavailable theme storage keeps the safe light startup default', () => {
  const html = readFileSync(new URL('../index.html', import.meta.url), 'utf8')
  const invalid = [null, '{', 'null', JSON.stringify({
    id: 'test', colorScheme: 'night', background: '#16171b'
  }), JSON.stringify({
    id: 'test', colorScheme: 'dark', background: 'url(https://invalid.example)'
  }), JSON.stringify({
    id: 'test\n', colorScheme: 'dark', background: '#16171b'
  }), JSON.stringify({
    id: 'test', colorScheme: 'dark', background: ['#16171b']
  }), JSON.stringify({
    id: 'test', colorScheme: 'dark', background: '#16171b\n'
  })]
  for (const saved of invalid) {
    const page = runThemeBootstrap(html, () => ({ getItem: () => saved }))
    assert.equal(page.documentElement.dataset.theme, 'light')
    assert.equal(page.values.size, 0)
  }
  const page = runThemeBootstrap(html, () => { throw new Error('storage blocked') })
  assert.equal(page.documentElement.dataset.theme, 'light')
})

test('switching back to silver-white removes palette overrides without touching layout variables', () => {
  const page = themePage()
  page.values.set('--node-library-width', '218px')
  applyTheme(graphitePurple.id, page)
  assert.equal(page.documentElement.dataset.theme, graphitePurple.id)
  assert.equal(page.documentElement.dataset.themePalette, '')
  assert.equal(page.values.get('--theme-accent'), graphitePurple.tokens['--theme-accent'])
  applyTheme('light', page)
  assert.equal(page.documentElement.style.colorScheme, 'light')
  assert.equal(page.documentElement.dataset.themePalette, undefined)
  assert.equal(page.values.has('--theme-accent'), false)
  assert.equal(page.values.get('--node-library-width'), '218px')
  assert.equal(applyTheme('removed-theme', page).id, 'light')
})

test('another palette can be registered without changing the theme application logic', () => {
  const future = {
    ...graphitePurple, id: 'future-test', name: 'Future',
    tokens: { ...graphitePurple.tokens, '--theme-accent': '#83cdb6' }
  }
  const catalog = [...themes, future]
  const page = themePage()
  assert.equal(resolveTheme(future.id, catalog), future)
  applyTheme(future.id, page, catalog)
  assert.equal(page.documentElement.dataset.theme, future.id)
  assert.equal(page.values.get('--theme-accent'), '#83cdb6')
  applyTheme('light', page, catalog)
  assert.equal(page.values.has('--theme-accent'), false)
})

test('theme persistence stores startup metadata and degrades safely on storage failures', () => {
  let saved
  assert.equal(rememberTheme(graphitePurple, () => ({
    setItem(key, value) { assert.equal(key, themeStorageKey); saved = JSON.parse(value) }
  })), true)
  assert.deepEqual(saved, {
    id: graphitePurple.id, colorScheme: graphitePurple.colorScheme, background: graphitePurple.background
  })
  assert.equal(rememberTheme(graphitePurple, () => { throw new Error('storage blocked') }), false)
  assert.equal(rememberTheme(graphitePurple, () => ({
    setItem() { throw new Error('quota exceeded') }
  })), false)
})

test('optional node-library ornaments and palette-only variables are cleared when leaving a decorated theme', () => {
  const page = themePage()
  const future = { ...noirGold, id: 'future-decorated', libraryOrnament: { cornerMask: './future/corner.svg', opacity: .3 } }
  const defaultOpacity = { ...future, id: 'future-default-opacity', libraryOrnament: { cornerMask: './future/default.svg' } }
  const zeroOpacity = { ...future, id: 'future-zero-opacity', libraryOrnament: { cornerMask: './future/zero.svg', opacity: 0 } }
  const withoutOrnaments = { ...noirGold, id: 'future-plain', libraryOrnament: undefined }
  const catalog = [...themes, future, defaultOpacity, zeroOpacity, withoutOrnaments]
  for (const decorated of [noirGold, future, defaultOpacity, zeroOpacity]) {
    for (const plain of ['light', graphitePurple.id, withoutOrnaments.id]) {
      applyTheme(decorated.id, page, catalog)
      assert.equal(page.documentElement.dataset.themeLibraryOrnament, '')
      assert.equal(page.values.get('--theme-library-corner-mask'), `url("${decorated.libraryOrnament.cornerMask}")`)
      assert.equal(page.values.get('--theme-library-ornament-opacity'), String(decorated.libraryOrnament.opacity ?? .55))
      applyTheme(plain, page, catalog)
      assert.equal(page.documentElement.dataset.themeLibraryOrnament, undefined)
      assert.equal(page.values.has('--theme-library-corner-mask'), false)
      assert.equal(page.values.has('--theme-library-ornament-opacity'), false)
      if (plain !== withoutOrnaments.id) assert.equal(page.values.has('--theme-on-danger'), false)
    }
  }
  let saved
  rememberTheme(noirGold, () => ({ setItem(_key, value) { saved = JSON.parse(value) } }))
  assert.deepEqual(Object.keys(saved).sort(), ['background', 'colorScheme', 'id'])
})

function activityClock() {
  let now = 0
  let nextId = 0
  const pending = new Map()
  return {
    set(callback, delay) { const id = ++nextId; pending.set(id, { callback, at: now + delay }); return id },
    clear(id) { pending.delete(id) },
    advance(duration) {
      now += duration
      for (const [id, entry] of pending) {
        if (entry.at <= now) { pending.delete(id); entry.callback() }
      }
    },
    get pendingCount() { return pending.size }
  }
}

test('canvas wheel bursts dim once and settle only after the last zoom event', () => {
  const clock = activityClock()
  const changes = []
  const activity = createCanvasActivity(value => changes.push(value), clock)
  for (let i = 0; i < 8; i++) { activity.pulse(); clock.advance(100) }
  assert.deepEqual(changes, [true])
  assert.equal(clock.pendingCount, 1)
  clock.advance(49)
  assert.deepEqual(changes, [true])
  clock.advance(1)
  assert.deepEqual(changes, [true, false])
  assert.equal(clock.pendingCount, 0)
})

test('held canvas gestures remain dim through pauses, zooms and rapid gesture reentry', () => {
  const clock = activityClock()
  const changes = []
  const activity = createCanvasActivity(value => changes.push(value), clock)
  activity.hold(true)
  activity.pulse()
  clock.advance(1000)
  assert.deepEqual(changes, [true])
  assert.equal(clock.pendingCount, 0)
  activity.hold(false)
  clock.advance(100)
  activity.hold(true)
  clock.advance(1000)
  assert.deepEqual(changes, [true])
  activity.hold(false)
  clock.advance(150)
  assert.deepEqual(changes, [true, false])
})

test('blur, theme changes and disposal cannot leave a canvas fade timer running', () => {
  const clock = activityClock()
  const changes = []
  const activity = createCanvasActivity(value => changes.push(value), clock)
  activity.hold(true)
  activity.reset()
  assert.deepEqual(changes, [true, false])
  activity.pulse()
  activity.reset()
  clock.advance(1000)
  assert.deepEqual(changes, [true, false, true, false])
  activity.pulse()
  activity.dispose()
  activity.hold(false)
  activity.pulse()
  clock.advance(1000)
  assert.equal(clock.pendingCount, 0)
  assert.deepEqual(changes, [true, false, true, false, true])
})
