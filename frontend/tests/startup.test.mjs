import { runInNewContext } from 'node:vm'
import { applyTheme, resolveTheme, rememberTheme, themes, graphitePurple, noirGold, themeStorageKey } from '../src/theme.ts'
import { createCanvasActivity } from '../src/canvasActivity.ts'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { setImmediate, setTimeout } from 'node:timers/promises'
import { test } from 'node:test'
import { buildProductionHtml } from '../build/html.mjs'
import { waitForStartupPaint } from '../src/startup.ts'
import { createPinia, setActivePinia } from 'pinia'
import { nextTick, watch } from 'vue'
import * as vue from 'vue'
import { compileScript, parse } from '@vue/compiler-sfc'
import ts from 'typescript'
import * as defaults from '../src/defaults.ts'
import { useAppStore, workflowExecutionSignature } from '../src/store.ts'

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

function completedRoute(t) {
  setActivePinia(createPinia())
  const store = useAppStore()
  const node = store.workflow.nodes.find(value => value.type === 'Import')
  const job = {
    id: 'completed-route', checked: true, status: '已完成',
    routeNodeIds: store.workflow.nodes.map(value => value.id),
    routeConnectionIds: store.workflow.connections.map(value => value.id)
  }
  store.setJobs([job])
  store.setWorkState({ busy: false, mode: 'run', summary: { total: 1, successes: 1, failures: 0, cacheHits: 0, cancelled: false } })
  store.showJobRoute(job.id)
  const stop = watch(() => workflowExecutionSignature(store.workflow), () => store.invalidateRoutes())
  t.after(() => { stop(); store.$dispose() })
  return { store, node, job }
}

test('node layout, canvas navigation and display ordering preserve a completed route', async t => {
  const { store, node, job } = completedRoute(t)
  const signature = workflowExecutionSignature(store.workflow)
  store.updateNodePosition(node.id, node.x + 40, node.y + 20)
  store.updateNodeSize(node.id, 2400, 500)
  store.workflow.viewport = { x: 240, y: -80, zoom: .5 }
  node.title = '文件列表'
  store.workflow.nodes.reverse()
  store.workflow.connections.reverse()
  await nextTick()
  assert.equal(workflowExecutionSignature(store.workflow), signature)
  assert.equal(store.routesValid, true)
  assert.equal(store.highlightedJobId, job.id)
  assert.deepEqual(store.highlightedConnectionIds, job.routeConnectionIds)
})

test('processing settings invalidate the completed route while leaving its record available for explanation', async t => {
  const { store, job } = completedRoute(t)
  store.workflow.nodes.find(node => node.type === 'Quality').data.qualityPercent = 60
  await nextTick()
  assert.equal(store.routesValid, false)
  assert.equal(store.highlightedJobId, null)
  assert.ok(job.routeNodeIds.length)
  assert.match(store.routeUnavailableReason(job), /已失效/)
  store.showJobRoute(job.id)
  assert.equal(store.highlightedJobId, null)
})

test('recreating an otherwise identical connection invalidates old route identifiers', async t => {
  const { store } = completedRoute(t)
  store.workflow.connections[0].id = 'replacement-connection'
  await nextTick()
  assert.equal(store.routesValid, false)
  assert.equal(store.highlightedJobId, null)
})

test('route availability explains missing records and running work and prevents stale activation', t => {
  const { store, job } = completedRoute(t)
  assert.equal(store.routeUnavailableReason(job), '')
  assert.match(store.routeUnavailableReason({ ...job, routeNodeIds: [] }), /暂无路径记录/)
  store.setWorkState({ busy: true, mode: 'run', total: 1 })
  assert.match(store.routeUnavailableReason(job), /执行中/)
  store.showJobRoute(job.id)
  assert.equal(store.highlightedJobId, null)
})

test('a file-list node wider than the old limit survives saving and reloading without accepting invalid dimensions', t => {
  const { store, node } = completedRoute(t)
  store.updateNodeSize(node.id, 3200, 500)
  store.updateNodeSize(node.id, Number.POSITIVE_INFINITY, 500)
  store.updateNodeSize(node.id, 100, Number.NaN)
  assert.equal(node.width, 3200)
  const saved = JSON.parse(JSON.stringify(store.workflow))
  store.replaceWorkflow(saved)
  const restored = store.workflow.nodes.find(value => value.id === node.id)
  assert.equal(restored.width, 3200)
  assert.equal(restored.height, 500)
  assert.equal(store.routesValid, false)
})

// Exercise the real canvas handlers and renderer inputs with measured DOM geometry.
// Rebuilding the port cache between pointer frames reproduces a layout notification
// arriving while a node is being resized, without depending on browser timing.
const canvasScript = compileScript(parse(readFileSync(new URL('../src/components/WorkflowCanvas.vue', import.meta.url), 'utf8')).descriptor, { id: 'canvas-regression' })
const canvasModule = ts.transpileModule(canvasScript.content, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS }
}).outputText

async function canvasGeometryFixture(t, zoom) {
  setActivePinia(createPinia())
  const store = useAppStore()
  const node = defaults.makeNode('Import', 20, 30)
  const output = defaults.makeNode('Output', 1300, 430)
  node.width = 1000
  const view = { x: 40, y: 25, zoom }
  store.replaceWorkflow({ ...store.workflow, nodes: [node, output], viewport: view, connections: [
    { id: 'geometry-edge', fromNodeId: node.id, fromPort: 'out', toNodeId: output.id, toPort: 'in' }
  ] })
  const imported = store.workflow.nodes[0]
  const classList = () => {
    const values = new Set()
    return { add: value => values.add(value), remove: value => values.delete(value), contains: value => values.has(value) }
  }
  const style = () => ({ setProperty(key, value) { this[key] = value } })
  const bounds = (left, top, width, height) => ({ left, top, width, height, right: left + width, bottom: top + height })
  const surfaceBounds = bounds(218, 66, 1280, 720)
  const elements = new Map(store.workflow.nodes.map(value => {
    const element = vue.markRaw({
      classList: classList(), style: style(),
      get offsetWidth() { return Math.round(Number.parseFloat(this.style.width) || value.width) },
      get offsetHeight() { return Math.round(Number.parseFloat(this.style.height) || value.height) },
      getBoundingClientRect() {
        const x = Number.parseFloat(this.style['--node-x'] ?? value.x) + Number.parseFloat(this.style['--node-drag-x'] ?? '0')
        const y = Number.parseFloat(this.style['--node-y'] ?? value.y) + Number.parseFloat(this.style['--node-drag-y'] ?? '0')
        return bounds(surfaceBounds.left + view.x + x * zoom, surfaceBounds.top + view.y + y * zoom, this.offsetWidth * zoom, this.offsetHeight * zoom)
      }
    })
    return [value.id, element]
  }))
  const ports = store.workflow.nodes.flatMap(value => ['input', 'output'].map(direction => vue.markRaw({
    dataset: { node: value.id, port: direction === 'input' ? 'in' : 'out', direction, kind: 'image' },
    getBoundingClientRect() {
      const rect = elements.get(value.id).getBoundingClientRect()
      const x = direction === 'input' ? rect.left + zoom : rect.right - zoom
      return bounds(x - 14 * zoom, rect.top + 73 * zoom, 28 * zoom, 28 * zoom)
    }
  })))
  const frames = new Map()
  const mounted = []
  const cleanup = []
  let nextFrame = 0
  let rendered = []
  const module = { exports: {} }
  runInNewContext(canvasModule, {
    module, exports: module.exports,
    require(name) {
      if (name === 'vue') return { ...vue, onMounted: callback => mounted.push(callback), onBeforeUnmount: callback => cleanup.push(callback), watch: (...args) => { const stop = vue.watch(...args); cleanup.push(stop); return stop } }
      if (name === '../store') return { useAppStore: () => store }
      if (name === '../defaults') return defaults
      if (name === '../canvasActivity') return { createCanvasActivity }
      if (name === '../bridge') return { callHost: async () => null }
      if (name === '../renderers/WebGlConnectionRenderer') return { WebGlConnectionRenderer: class {
        resize() {}
        render(curves) { rendered = curves }
        dispose() {}
      } }
      if (name.endsWith('.vue')) return {}
      throw new Error(`Unexpected canvas import: ${name}`)
    },
    window: { addEventListener() {}, removeEventListener() {} },
    document: { body: { classList: classList() } },
    ResizeObserver: class { observe() {} disconnect() {} },
    requestAnimationFrame: callback => { frames.set(++nextFrame, callback); return nextFrame },
    cancelAnimationFrame: id => frames.delete(id)
  })
  const canvas = module.exports.default.setup({}, { expose() {} })
  canvas.surface.value = vue.markRaw({
    clientWidth: surfaceBounds.width, clientHeight: surfaceBounds.height, classList: classList(),
    getBoundingClientRect: () => surfaceBounds,
    querySelectorAll: () => ports,
    querySelector: selector => elements.get(selector.match(/data-node-id="([^"]+)"/)?.[1])
  })
  canvas.pane.value = vue.markRaw({ style: style() })
  canvas.connectionCanvas.value = vue.markRaw({})
  for (const callback of mounted) await callback()
  const flush = () => {
    while (frames.size) {
      const [id, callback] = frames.entries().next().value
      frames.delete(id)
      callback()
    }
  }
  const checkEndpoints = () => {
    const curve = rendered.find(value => value.connectionId === 'geometry-edge')
    for (const [point, id, direction] of [[curve.start, imported.id, 'output'], [curve.end, output.id, 'input']]) {
      const rect = ports.find(port => port.dataset.node === id && port.dataset.direction === direction).getBoundingClientRect()
      const expectedX = (rect.left + rect.width / 2 - surfaceBounds.left - view.x) / zoom
      const expectedY = (rect.top + rect.height / 2 - surfaceBounds.top - view.y) / zoom
      assert.ok(Math.abs(point.x - expectedX) < 1e-6, `${direction} port detached horizontally: ${point.x} vs ${expectedX} at zoom ${zoom}`)
      assert.ok(Math.abs(point.y - expectedY) < 1e-6, `${direction} port detached vertically: ${point.y} vs ${expectedY} at zoom ${zoom}`)
    }
  }
  flush()
  checkEndpoints()
  t.after(() => { for (const callback of cleanup) callback(); store.$dispose() })
  const pointer = delta => ({ pointerId: 1, button: 0, clientX: 500 + delta, clientY: 300, preventDefault() {} })
  return { canvas, node: imported, flush, checkEndpoints, pointer }
}

test('rebuilding port measurements during a resize does not double the width delta, including on release', async t => {
  for (const zoom of [.3, .65, 1, 1.25, 2]) {
    const { canvas, node, flush, checkEndpoints, pointer } = await canvasGeometryFixture(t, zoom)
    canvas.resizeDown(node, pointer(0))
    canvas.pointerMove(pointer(240 * zoom))
    flush()
    checkEndpoints()
    canvas.invalidatePortLayout()
    flush()
    checkEndpoints()
    canvas.pointerUp(pointer(240 * zoom))
    await nextTick()
    flush()
    checkEndpoints()
  }
})

test('resizing cancellation restores the original port after a mid-drag layout refresh', async t => {
  const { canvas, node, flush, checkEndpoints, pointer } = await canvasGeometryFixture(t, .5)
  canvas.resizeDown(node, pointer(0))
  canvas.pointerMove(pointer(160))
  flush()
  canvas.invalidatePortLayout()
  flush()
  canvas.pointerCancel(pointer(160))
  flush()
  assert.equal(node.width, 1000)
  checkEndpoints()
})

test('repeated wide and narrow resizes use the same rounded geometry for the node and connections', async t => {
  const { canvas, node, flush, checkEndpoints, pointer } = await canvasGeometryFixture(t, .65)
  for (const delta of [160.2, 180.4, -85.1, 120.3, -57.6]) {
    canvas.resizeDown(node, pointer(0))
    canvas.pointerMove(pointer(delta))
    flush()
    checkEndpoints()
    canvas.pointerUp(pointer(delta))
    await nextTick()
    flush()
    checkEndpoints()
  }
  assert.ok(node.width > 1120)
})

test('refreshing port measurements during a node move also preserves release and cancellation coordinates', async t => {
  const { canvas, node, flush, checkEndpoints, pointer } = await canvasGeometryFixture(t, .5)
  const movedPointer = { ...pointer(60), clientY: 330 }
  canvas.headerDown(node, pointer(0))
  canvas.pointerMove(movedPointer)
  flush()
  canvas.invalidatePortLayout()
  flush()
  checkEndpoints()
  canvas.pointerCancel(movedPointer)
  flush()
  checkEndpoints()
  canvas.headerDown(node, pointer(0))
  canvas.pointerMove(movedPointer)
  canvas.pointerUp(movedPointer)
  await nextTick()
  flush()
  assert.equal(node.x, 140)
  assert.equal(node.y, 90)
  checkEndpoints()
})
