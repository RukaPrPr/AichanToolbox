import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { setImmediate, setTimeout } from 'node:timers/promises'
import { test } from 'node:test'
import { buildProductionHtml } from '../build/html.mjs'
import { waitForStartupPaint } from '../src/startup.ts'

test('production uses the same light-first shell, telemetry and background as development', () => {
  const source = readFileSync(new URL('../index.html', import.meta.url), 'utf8')
  const output = buildProductionHtml(source)
  assert.match(output, /name="color-scheme" content="light"/)
  assert.doesNotMatch(output, /color-scheme" content="dark"/)
  assert.match(output, /background:#eef1f4/)
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
