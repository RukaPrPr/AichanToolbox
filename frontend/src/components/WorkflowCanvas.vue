<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { callHost } from '../bridge'
import { canConnect, outputPorts } from '../defaults'
import { WebGlConnectionRenderer } from '../renderers/WebGlConnectionRenderer'
import { useAppStore } from '../store'
import type { PortDefinition, WorkflowConnection, WorkflowNode } from '../types'
import NodeCard from './NodeCard.vue'

const store = useAppStore()
const surface = ref<HTMLElement>()
const gridLayer = ref<HTMLElement>()
const pane = ref<HTMLElement>()
const connectionCanvas = ref<HTMLCanvasElement>()
const zoomText = ref('100%')

type Point = { x: number; y: number }
type CachedPort = { element: HTMLElement; nodeId: string; portId: string; direction: 'input' | 'output'; kind: string; point: Point }
type Curve = { connectionId: string | null; fromNodeId: string; toNodeId: string; start: Point; end: Point; color: string; active: boolean; dimmed: boolean }

let drawFrame = 0
let interactionFrame = 0
let pendingPointer: { pointerId: number; clientX: number; clientY: number } | null = null
let resizeObserver: ResizeObserver | undefined
let viewport = { ...store.workflow.viewport }
let lastSurfaceSize: { width: number; height: number } | null = null
let lastGridZoom = -1
let portCacheDirty = true
let connectionRenderer: WebGlConnectionRenderer | null = null
const portCache = new Map<string, CachedPort>()
let pan: { pointerId: number; clientX: number; clientY: number; x: number; y: number } | null = null
let drag: { node: WorkflowNode; element: HTMLElement; pointerId: number; clientX: number; clientY: number; x: number; y: number; nextX: number; nextY: number } | null = null
let resizeDrag: { node: WorkflowNode; element: HTMLElement; pointerId: number; clientX: number; clientY: number; width: number; height: number; nextWidth: number; nextHeight: number } | null = null
let linking: { node: WorkflowNode; port: PortDefinition; pointerId: number; x: number; y: number; snap: HTMLElement | null } | null = null
const snapRadius = 46

function applyViewport(commit = false) {
  if (!pane.value || !surface.value) return
  pane.value.style.transform = `translate3d(${viewport.x}px, ${viewport.y}px, 0) scale(${viewport.zoom})`
  if (gridLayer.value) {
    const period = 140 * viewport.zoom
    const offsetX = ((viewport.x % period) + period) % period
    const offsetY = ((viewport.y % period) + period) % period
    gridLayer.value.style.transform = `translate3d(${300 + offsetX - period}px, ${300 + offsetY - period}px, 0)`
    if (lastGridZoom !== viewport.zoom) {
      lastGridZoom = viewport.zoom
      gridLayer.value.style.setProperty('--grid-size', `${28 * viewport.zoom}px`)
      gridLayer.value.style.setProperty('--grid-period', `${period}px`)
    }
  }
  zoomText.value = `${Math.round(viewport.zoom * 100)}%`
  if (commit) store.workflow.viewport = { ...viewport }
}

function requestDraw() {
  if (drawFrame) return
  drawFrame = requestAnimationFrame(drawConnections)
}

function drawConnectionsImmediately() {
  if (drawFrame) cancelAnimationFrame(drawFrame)
  drawFrame = 0
  drawConnections()
}

function invalidatePortLayout() {
  portCacheDirty = true
  requestDraw()
}

function surfaceResized(entries: ResizeObserverEntry[]) {
  const bounds = entries[0]?.contentRect
  if (!bounds) return
  const nextSize = { width: bounds.width, height: bounds.height }
  if (lastSurfaceSize && (Math.abs(nextSize.width - lastSurfaceSize.width) > .5 || Math.abs(nextSize.height - lastSurfaceSize.height) > .5)) {
    viewport.x += (nextSize.width - lastSurfaceSize.width) / 2
    viewport.y += (nextSize.height - lastSurfaceSize.height) / 2
    applyViewport(true)
  }
  lastSurfaceSize = nextSize
  requestDraw()
}

function clientPoint(clientX: number, clientY: number) {
  if (!surface.value) return null
  const bounds = surface.value.getBoundingClientRect()
  return {
    x: (clientX - bounds.left - viewport.x) / viewport.zoom,
    y: (clientY - bounds.top - viewport.y) / viewport.zoom
  }
}

function portKey(nodeId: string, portId: string, direction: 'input' | 'output') {
  return `${nodeId}:${portId}:${direction}`
}

function rebuildPortCache() {
  if (!surface.value) return null
  const surfaceBounds = surface.value.getBoundingClientRect()
  portCache.clear()
  for (const element of Array.from(surface.value.querySelectorAll<HTMLElement>('[data-port-id]'))) {
    const nodeId = element.dataset.node ?? ''
    const portId = element.dataset.port ?? ''
    const direction = element.dataset.direction === 'input' ? 'input' : 'output'
    if (!nodeId || !portId) continue
    const bounds = element.getBoundingClientRect()
    portCache.set(portKey(nodeId, portId, direction), {
      element,
      nodeId,
      portId,
      direction,
      kind: element.dataset.kind ?? '',
      point: {
        x: (bounds.left + bounds.width / 2 - surfaceBounds.left - viewport.x) / viewport.zoom,
        y: (bounds.top + bounds.height / 2 - surfaceBounds.top - viewport.y) / viewport.zoom
      }
    })
  }
  portCacheDirty = false
}

function ensurePortCache() {
  if (portCacheDirty) rebuildPortCache()
}

function portPoint(nodeId: string, portId: string, direction: 'input' | 'output') {
  ensurePortCache()
  const cached = portCache.get(portKey(nodeId, portId, direction))
  if (!cached) return null
  let x = cached.point.x
  let y = cached.point.y
  if (drag?.node.id === nodeId) {
    x += drag.nextX - drag.x
    y += drag.nextY - drag.y
  }
  if (resizeDrag?.node.id === nodeId && direction === 'output')
    x += resizeDrag.nextWidth - resizeDrag.width
  return { x, y }
}

function worldToClient(point: Point) {
  if (!surface.value) return null
  const bounds = surface.value.getBoundingClientRect()
  return {
    x: bounds.left + viewport.x + point.x * viewport.zoom,
    y: bounds.top + viewport.y + point.y * viewport.zoom
  }
}

function nearestInput(clientX: number, clientY: number, excludeNodeId: string, kind?: string) {
  ensurePortCache()
  let nearest: { element: HTMLElement; x: number; y: number; distance: number } | null = null
  for (const cached of portCache.values()) {
    if (cached.direction !== 'input' || cached.nodeId === excludeNodeId) continue
    if (kind && cached.kind !== kind) continue
    const center = worldToClient(cached.point)
    if (!center) continue
    const distance = Math.hypot(clientX - center.x, clientY - center.y)
    if (distance <= snapRadius && (!nearest || distance < nearest.distance)) {
      nearest = { element: cached.element, x: cached.point.x, y: cached.point.y, distance }
    }
  }
  return nearest
}

function updateSnap(next: HTMLElement | null) {
  if (!linking || linking.snap === next) return
  linking.snap?.classList.remove('snap-target')
  linking.snap = next
  linking.snap?.classList.add('snap-target')
}

function connectionColor(connection: WorkflowConnection) {
  const node = store.workflow.nodes.find(value => value.id === connection.fromNodeId)
  return outputPorts(node?.type ?? 'Import').find(port => port.id === connection.fromPort)?.color ?? '#70a7ff'
}

function buildCurves() {
  const curves: Curve[] = []
  const routeActive = store.routesValid && Boolean(store.highlightedJobId)
  const highlightedConnections = new Set(store.highlightedConnectionIds)
  for (const connection of store.workflow.connections) {
    const start = portPoint(connection.fromNodeId, connection.fromPort, 'output')
    const end = portPoint(connection.toNodeId, connection.toPort, 'input')
    const active = routeActive && highlightedConnections.has(connection.id)
    if (start && end) curves.push({ connectionId: connection.id, fromNodeId: connection.fromNodeId, toNodeId: connection.toNodeId, start, end, color: connectionColor(connection), active, dimmed: routeActive && !active })
  }
  if (linking) {
    const start = portPoint(linking.node.id, linking.port.id, 'output')
    if (start) curves.push({ connectionId: null, fromNodeId: linking.node.id, toNodeId: '', start, end: { x: linking.x, y: linking.y }, color: linking.port.color, active: true, dimmed: false })
  }
  return curves
}

function drawConnections() {
  drawFrame = 0
  if (!connectionRenderer || !surface.value) return
  connectionRenderer.resize(surface.value.clientWidth, surface.value.clientHeight)
  const curves = buildCurves()
  connectionRenderer.render(curves, viewport)
}

function surfaceDown(event: PointerEvent) {
  const target = event.target as HTMLElement
  if (target.closest('.workflow-node') || event.button !== 0 && event.button !== 1) return
  event.preventDefault()
  store.selectedNodeIds = []
  pan = { pointerId: event.pointerId, clientX: event.clientX, clientY: event.clientY, x: viewport.x, y: viewport.y }
  surface.value?.setPointerCapture(event.pointerId)
  surface.value?.classList.add('is-panning')
}

function headerDown(node: WorkflowNode, event: PointerEvent) {
  if (store.busy || event.button !== 0) return
  event.preventDefault()
  store.selectNode(node.id, event.ctrlKey || event.shiftKey)
  const element = surface.value?.querySelector(`[data-node-id="${node.id}"]`) as HTMLElement | null
  if (!element) return
  ensurePortCache()
  drag = {
    node,
    element,
    pointerId: event.pointerId,
    clientX: event.clientX,
    clientY: event.clientY,
    x: node.x,
    y: node.y,
    nextX: node.x,
    nextY: node.y
  }
  element.classList.add('is-dragging')
  document.body.classList.add('node-dragging')
  requestDraw()
}

function resizeDown(node: WorkflowNode, event: PointerEvent) {
  if (store.busy || event.button !== 0) return
  const element = surface.value?.querySelector(`[data-node-id="${node.id}"]`) as HTMLElement | null
  if (!element) return
  store.selectNode(node.id)
  ensurePortCache()
  resizeDrag = {
    node,
    element,
    pointerId: event.pointerId,
    clientX: event.clientX,
    clientY: event.clientY,
    width: element.offsetWidth,
    height: element.offsetHeight,
    nextWidth: element.offsetWidth,
    nextHeight: element.offsetHeight
  }
  element.classList.add('is-resizing')
  requestDraw()
}

function pointerMove(event: PointerEvent) {
  const activePointer = pan?.pointerId ?? drag?.pointerId ?? resizeDrag?.pointerId ?? linking?.pointerId ?? -1
  if (event.pointerId !== activePointer) return
  event.preventDefault()
  pendingPointer = { pointerId: event.pointerId, clientX: event.clientX, clientY: event.clientY }
  if (!interactionFrame) interactionFrame = requestAnimationFrame(flushPointerMove)
}

function flushPointerMove() {
  interactionFrame = 0
  const event = pendingPointer
  pendingPointer = null
  if (!event) return
  let geometryChanged = false
  if (pan && event.pointerId === pan.pointerId) {
    viewport.x = pan.x + event.clientX - pan.clientX
    viewport.y = pan.y + event.clientY - pan.clientY
    applyViewport(false)
    geometryChanged = true
  }
  if (drag && event.pointerId === drag.pointerId) {
    drag.nextX = drag.x + (event.clientX - drag.clientX) / viewport.zoom
    drag.nextY = drag.y + (event.clientY - drag.clientY) / viewport.zoom
    drag.element.style.transform = `translate3d(${drag.nextX - drag.x}px, ${drag.nextY - drag.y}px, 0)`
    geometryChanged = true
  }
  if (resizeDrag && event.pointerId === resizeDrag.pointerId) {
    resizeDrag.nextWidth = Math.min(1120, Math.max(570, resizeDrag.width + (event.clientX - resizeDrag.clientX) / viewport.zoom))
    resizeDrag.nextHeight = Math.min(700, Math.max(290, resizeDrag.height + (event.clientY - resizeDrag.clientY) / viewport.zoom))
    resizeDrag.element.style.width = `${Math.round(resizeDrag.nextWidth)}px`
    resizeDrag.element.style.height = `${Math.round(resizeDrag.nextHeight)}px`
    geometryChanged = true
  }
  if (linking && surface.value) {
    const snap = nearestInput(event.clientX, event.clientY, linking.node.id, linking.port.kind)
    updateSnap(snap?.element ?? null)
    const point = clientPoint(event.clientX, event.clientY)
    linking.x = snap?.x ?? point?.x ?? linking.x
    linking.y = snap?.y ?? point?.y ?? linking.y
    geometryChanged = true
  }
  if (geometryChanged) drawConnectionsImmediately()
}

function flushPendingPointer(pointerId: number) {
  if (!pendingPointer || pendingPointer.pointerId !== pointerId) return
  if (interactionFrame) cancelAnimationFrame(interactionFrame)
  interactionFrame = 0
  flushPointerMove()
}

function shiftNodePorts(nodeId: string, deltaX: number, deltaY: number, outputOnly = false) {
  for (const cached of portCache.values()) {
    if (cached.nodeId !== nodeId || (outputOnly && cached.direction !== 'output')) continue
    cached.point = { x: cached.point.x + deltaX, y: cached.point.y + deltaY }
  }
}

function pointerUp(event: PointerEvent) {
  flushPendingPointer(event.pointerId)
  if (pan && event.pointerId === pan.pointerId) {
    pan = null
    surface.value?.classList.remove('is-panning')
    applyViewport(true)
  }
  if (drag && event.pointerId === drag.pointerId) {
    const completed = drag
    const deltaX = completed.nextX - completed.x
    const deltaY = completed.nextY - completed.y
    shiftNodePorts(completed.node.id, deltaX, deltaY)
    completed.element.classList.remove('is-dragging')
    document.body.classList.remove('node-dragging')
    drag = null
    completed.element.style.transform = ''
    completed.element.style.left = `${Math.round(completed.nextX)}px`
    completed.element.style.top = `${Math.round(completed.nextY)}px`
    store.updateNodePosition(completed.node.id, Math.round(completed.nextX), Math.round(completed.nextY))
    requestDraw()
  }
  if (resizeDrag && event.pointerId === resizeDrag.pointerId) {
    const completed = resizeDrag
    shiftNodePorts(completed.node.id, completed.nextWidth - completed.width, 0, true)
    resizeDrag = null
    completed.element.classList.remove('is-resizing')
    store.updateNodeSize(completed.node.id, Math.round(completed.nextWidth), Math.round(completed.nextHeight))
    requestDraw()
  }
  if (linking && event.pointerId === linking.pointerId) {
    const target = document.elementFromPoint(event.clientX, event.clientY) as HTMLElement | null
    const directInput = target?.closest('[data-direction="input"]') as HTMLElement | null
    const input = linking.snap ?? (directInput?.dataset.kind === linking.port.kind ? directInput : null) ?? nearestInput(event.clientX, event.clientY, linking.node.id, linking.port.kind)?.element ?? null
    if (input && input.dataset.node && input.dataset.node !== linking.node.id) {
      const targetNode = store.workflow.nodes.find(node => node.id === input.dataset.node)
      if (targetNode && canConnect(linking.port, targetNode.type)) {
        store.connect({
          id: crypto.randomUUID().replaceAll('-', ''),
          fromNodeId: linking.node.id,
          fromPort: linking.port.id,
          toNodeId: input.dataset.node,
          toPort: input.dataset.port ?? 'in'
        })
        store.status = `已连接 ${linking.node.title} · ${linking.port.label}`
      } else store.status = '端口类型不匹配：批次端口与图片端口不能直接连接'
    }
    linking.snap?.classList.remove('snap-target')
    linking = null
    document.body.classList.remove('linking')
    requestDraw()
  }
}

function outputDown(node: WorkflowNode, port: PortDefinition, event: PointerEvent) {
  if (store.busy || event.button !== 0 || !surface.value) return
  ensurePortCache()
  const point = clientPoint(event.clientX, event.clientY)
  if (!point) return
  linking = { node, port, pointerId: event.pointerId, x: point.x, y: point.y, snap: null }
  document.body.classList.add('linking')
  requestDraw()
}

function wheel(event: WheelEvent) {
  if (!surface.value) return
  event.preventDefault()
  const bounds = surface.value.getBoundingClientRect()
  const cursorX = event.clientX - bounds.left
  const cursorY = event.clientY - bounds.top
  const worldX = (cursorX - viewport.x) / viewport.zoom
  const worldY = (cursorY - viewport.y) / viewport.zoom
  const step = event.deltaY < 0 ? 0.1 : -0.1
  const zoom = Math.min(2, Math.max(0.3, Math.round((viewport.zoom + step) * 10) / 10))
  viewport.x = cursorX - worldX * zoom
  viewport.y = cursorY - worldY * zoom
  viewport.zoom = zoom
  applyViewport(true)
  requestDraw()
}

function setZoom(next: number) {
  if (!surface.value) return
  const cx = surface.value.clientWidth / 2
  const cy = surface.value.clientHeight / 2
  const worldX = (cx - viewport.x) / viewport.zoom
  const worldY = (cy - viewport.y) / viewport.zoom
  viewport.zoom = Math.min(2, Math.max(0.3, next))
  viewport.x = cx - worldX * viewport.zoom
  viewport.y = cy - worldY * viewport.zoom
  applyViewport(true)
  requestDraw()
}

function fitNodes() {
  if (!surface.value || !store.workflow.nodes.length) return
  const left = Math.min(...store.workflow.nodes.map(node => node.x))
  const top = Math.min(...store.workflow.nodes.map(node => node.y))
  const right = Math.max(...store.workflow.nodes.map(node => node.x + node.width))
  const bottom = Math.max(...store.workflow.nodes.map(node => node.y + node.height))
  const zoom = Math.min(1, Math.max(0.3, Math.min((surface.value.clientWidth - 120) / (right - left), (surface.value.clientHeight - 120) / (bottom - top))))
  viewport = {
    zoom,
    x: (surface.value.clientWidth - (right - left) * zoom) / 2 - left * zoom,
    y: (surface.value.clientHeight - (bottom - top) * zoom) / 2 - top * zoom
  }
  applyViewport(true)
  requestDraw()
}

function resetView() {
  setZoom(1)
}

async function removeNode(id: string) {
  if (store.busy) return
  const node = store.workflow.nodes.find(value => value.id === id)
  if (node?.type === 'ZipExtract') {
    const archives = await callHost<any[]>('archives.clear', { nodeId: id })
    store.setArchives(archives ?? [])
  }
  store.removeNode(id)
  nextTick(requestDraw)
}

function duplicateNode(node: WorkflowNode) {
  if (store.busy) return
  store.duplicateNode(node)
  nextTick(requestDraw)
}

function selectNode(node: WorkflowNode, event: PointerEvent) {
  store.selectNode(node.id, event.ctrlKey || event.shiftKey)
}

function keyDown(event: KeyboardEvent) {
  const target = event.target as HTMLElement
  if (target.matches('input, textarea, select') || store.busy) return
  if (event.key === 'Delete' || event.key === 'Backspace') {
    const ids = [...store.selectedNodeIds]
    ids.forEach(id => {
      const node = store.workflow.nodes.find(value => value.id === id)
      if (node?.type === 'ZipExtract') callHost<any[]>('archives.clear', { nodeId: id }).then(archives => store.setArchives(archives ?? []))
      store.removeNode(id)
    })
    requestDraw()
  }
  if (event.key === '0') resetView()
  if (event.key.toLowerCase() === 'f') fitNodes()
}

watch(() => store.workflow, async value => {
  viewport = { ...value.viewport }
  await nextTick()
  portCacheDirty = true
  applyViewport(false)
  requestDraw()
}, { deep: false })
watch(() => store.workflow.connections, requestDraw, { deep: true })
watch(() => store.workflow.nodes.length, () => nextTick(invalidatePortLayout))
watch(() => store.highlightedJobId, requestDraw)

onMounted(async () => {
  window.addEventListener('pointermove', pointerMove)
  window.addEventListener('pointerup', pointerUp)
  window.addEventListener('keydown', keyDown)
  if (surface.value) lastSurfaceSize = { width: surface.value.clientWidth, height: surface.value.clientHeight }
  resizeObserver = new ResizeObserver(surfaceResized)
  if (surface.value) resizeObserver.observe(surface.value)
  if (connectionCanvas.value) connectionRenderer = new WebGlConnectionRenderer(connectionCanvas.value)
  viewport = { ...store.workflow.viewport }
  await nextTick()
  portCacheDirty = true
  applyViewport(false)
  requestDraw()
})

onBeforeUnmount(() => {
  window.removeEventListener('pointermove', pointerMove)
  window.removeEventListener('pointerup', pointerUp)
  window.removeEventListener('keydown', keyDown)
  resizeObserver?.disconnect()
  lastSurfaceSize = null
  if (drawFrame) cancelAnimationFrame(drawFrame)
  if (interactionFrame) cancelAnimationFrame(interactionFrame)
  connectionRenderer?.dispose()
  connectionRenderer = null
})
</script>

<template>
  <main ref="surface" class="workflow-surface" @pointerdown="surfaceDown" @wheel="wheel">
    <div ref="gridLayer" class="workflow-grid-layer" />
    <canvas ref="connectionCanvas" class="connection-webgl-canvas" aria-hidden="true" />
    <div ref="pane" class="transform-pane">
      <NodeCard
        v-for="node in store.workflow.nodes"
        :key="node.id"
        :node="node"
        :jobs="store.jobs"
        :busy="store.busy"
        :selected="store.selectedNodeIds.includes(node.id)"
        :route-highlighted="store.routesValid && store.highlightedNodeIds.includes(node.id)"
        @header-down="headerDown"
        @select="selectNode"
        @remove="removeNode"
        @duplicate="duplicateNode"
        @output-down="outputDown"
        @resize-down="resizeDown"
        @size="store.updateNodeSize"
        @redraw="invalidatePortLayout"
      />
    </div>

    <div class="canvas-badge glass-panel"><i class="live-dot" />自由工作流画布</div>
    <div class="canvas-controls glass-panel" @pointerdown.stop>
      <button title="缩小" @click="setZoom(Math.round((viewport.zoom - .1) * 10) / 10)">−</button>
      <button class="zoom-value" @click="resetView">{{ zoomText }}</button>
      <button title="放大" @click="setZoom(Math.round((viewport.zoom + .1) * 10) / 10)">＋</button>
      <span />
      <button @click="fitNodes">适应</button>
      <button @click="resetView">重置</button>
    </div>
    <div v-if="!store.workflow.nodes.length" class="empty-canvas">
      <div class="empty-orbit"><i /><i /><i /></div>
      <h2>从左侧添加第一个节点</h2>
      <p>节点可以自由组合、拖动、缩放与分支</p>
    </div>
  </main>
</template>
