import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { finalQuality, formatFinalQuality } from '../src/fileQuality.ts'

function job(quality, status = '已完成') {
  return { finalQuality: quality, status }
}

test('final quality shows the encoder quality selected by the completed workflow', () => {
  assert.equal(finalQuality(job(90)), 90)
  assert.equal(formatFinalQuality(job(73)), '73')
  assert.equal(formatFinalQuality(job(100)), '100')
})

test('unprocessed files show quality 100 while unfinished or failed work stays unknown', () => {
  assert.equal(formatFinalQuality(job(null, '不处理')), '100')
  assert.equal(formatFinalQuality(job(null, '待处理')), '—')
  assert.equal(formatFinalQuality(job(90, '失败 · 无法保存')), '—')
  assert.equal(formatFinalQuality(job(90, '已取消')), '—')
})

test('ZIP list owns wheel input and separates its border shell from its scroll viewport', () => {
  const component = readFileSync(new URL('../src/components/NodeCard.vue', import.meta.url), 'utf8')
  const styles = readFileSync(new URL('../src/styles.css', import.meta.url), 'utf8')
  assert.match(component, /class="archive-list-scroll" @wheel\.stop/)
  assert.match(styles, /\.archive-list\{display:block;padding:1px 3px 1px 1px;overflow:hidden\}/)
  assert.match(styles, /\.archive-list-scroll\{[^}]*overflow-y:auto;[^}]*overscroll-behavior:contain/)
})

test('bulk source replacement has a dedicated progress stage', () => {
  const store = readFileSync(new URL('../src/store.ts', import.meta.url), 'utf8')
  const app = readFileSync(new URL('../src/App.vue', import.meta.url), 'utf8')
  assert.match(store, /workStage:[^\n]*'replace'/)
  assert.match(store, /value\.stage === 'replace' \? '正在批量替换原文件…'/)
  assert.match(app, /store\.workStage === 'replace' \? '替换'/)
})
