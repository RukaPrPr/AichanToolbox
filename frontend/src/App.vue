<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { callHost, importDroppedFiles, onHostEvent } from './bridge'
import { actionDialog, requestAction, requestNotice, resolveAction } from './confirm'
import { nodeMeta } from './defaults'
import { useAppStore } from './store'
import type { ArchiveJob, FileJob, NodeType, ReplacedArchiveConfirmation, ReplacedSourceConfirmation, StartupSnapshot, WorkflowDocument } from './types'
import GlassSelect from './components/GlassSelect.vue'
import NodeGlyph from './components/NodeGlyph.vue'
import WorkflowCanvas from './components/WorkflowCanvas.vue'

const props = defineProps<{
  startup: StartupSnapshot | null
  startupError: string
  startupRequestMs: number
  scriptStartedAt: number
}>()
const store = useAppStore()
const toast = ref<{ text: string; error: boolean } | null>(null)
const fileDragActive = ref(false)
const profiles = ref<string[]>(props.startup?.profiles ?? [])
const selectedProfile = ref(props.startup?.selectedProfile ?? '')
const profileDialogOpen = ref(false)
const profileDialogMode = ref<'save' | 'rename'>('save')
const profileDraftName = ref('')
const profileNameInput = ref<HTMLInputElement | null>(null)
const isMaximized = ref(Boolean(props.startup?.maximized))
const sidebarDrawerOpen = ref(false)
const stackedHeader = ref(window.innerWidth < 1180)
const brandElement = ref<HTMLElement | null>(null)
const primaryToolbarGroup = ref<HTMLElement | null>(null)
const secondaryToolbarGroup = ref<HTMLElement | null>(null)
const sidebarHostElement = ref<HTMLElement | null>(null)
const sidebarToggleElement = ref<HTMLElement | null>(null)
const sidebarPanelElement = ref<HTMLElement | null>(null)
const sidebarResponsiveMorphing = ref(false)
const profileStorageKey = 'aichan:last-workflow-profile'
let toastTimer = 0
let fileDragDepth = 0
let responsiveFrame = 0
let responsiveSequence = 0
let activeSidebarGhost: HTMLElement | null = null
let activeSidebarAnimation: Animation | null = null

const nodeGroups: { label: string; nodes: NodeType[] }[] = [
  { label: '预处理', nodes: ['ZipExtract'] },
  { label: '输入与分支', nodes: ['Import', 'FormatFilter', 'SizeFilter', 'ResolutionFilter'] },
  { label: '图片处理', nodes: ['ConvertJpg', 'Resize', 'Descreen', 'Quality', 'TargetSize'] },
  { label: '输出', nodes: ['Output'] },
  { label: '后处理', nodes: ['ZipPack', 'DeleteExtracted'] }
]

const progressPercent = computed(() => store.progressTotal ? Math.round(store.progress / store.progressTotal * 100) : 0)
const canStart = computed(() => store.jobs.length > 0 || store.archives.length > 0)
const workStageLabel = computed(() => store.workStage === 'preprocess' ? '解压' : store.workStage === 'postprocess' ? '打包' : store.workStage === 'cleanup' ? '清理' : store.workMode === 'estimate' ? '预估' : '处理')
const profileOptions = computed<{ label: string; value: string | number }[]>(() => [
  { label: '工作流配置', value: '' },
  ...profiles.value.map(name => ({ label: name, value: name }))
])
const parallelOptions: { label: string; value: string | number }[] = Array.from({ length: 16 }, (_, index) => ({ label: String(index + 1), value: index + 1 }))
const workflowStructureSignature = computed(() => JSON.stringify({
  autoGrayscale: store.workflow.autoGrayscale,
  nodes: store.workflow.nodes.map(node => ({ id: node.id, type: node.type, title: node.title, x: node.x, y: node.y, width: node.width, height: node.height, data: node.data })),
  connections: store.workflow.connections
}))

watch(workflowStructureSignature, (value, previous) => {
  if (previous !== undefined && value !== previous) store.invalidateRoutes()
})

function notify(text: string, error = false) {
  toast.value = { text, error }
  window.clearTimeout(toastTimer)
  toastTimer = window.setTimeout(() => { toast.value = null }, 3600)
}

function addNode(type: NodeType) {
  const view = store.workflow.viewport
  store.addNode(type, Math.round((360 - view.x) / view.zoom), Math.round((160 - view.y) / view.zoom))
  if (window.innerWidth < 1180) sidebarDrawerOpen.value = false
}

function handleResponsiveResize() {
  if (responsiveFrame) return
  responsiveFrame = window.requestAnimationFrame(() => {
    responsiveFrame = 0
    const nextStacked = window.innerWidth < 1180
    if (nextStacked !== stackedHeader.value) void animateResponsiveHeader(nextStacked)
  })
}

function sidebarShellRadius(stacked: boolean, drawerOpen: boolean) {
  if (!stacked) return '0 17px 17px 0'
  return drawerOpen ? '17px' : '12px'
}

async function animateResponsiveHeader(nextStacked: boolean) {
  const sequence = ++responsiveSequence
  const elements = [brandElement.value, primaryToolbarGroup.value, secondaryToolbarGroup.value].filter((element): element is HTMLElement => Boolean(element))
  const firstRects = new Map(elements.map(element => [element, element.getBoundingClientRect()]))
  const previousStacked = stackedHeader.value
  const previousDrawerOpen = sidebarDrawerOpen.value
  const sourceElement = previousStacked
    ? (previousDrawerOpen ? sidebarPanelElement.value : sidebarToggleElement.value)
    : sidebarPanelElement.value
  const interruptedRect = activeSidebarGhost?.getBoundingClientRect()
  const sourceRect = interruptedRect ?? sourceElement?.getBoundingClientRect()

  activeSidebarAnimation?.cancel()
  activeSidebarAnimation = null
  activeSidebarGhost?.remove()
  activeSidebarGhost = null

  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches
  let ghost: HTMLElement | null = null
  if (!reducedMotion && sourceRect) {
    ghost = document.createElement('div')
    ghost.className = 'sidebar-morph-ghost'
    Object.assign(ghost.style, {
      left: `${sourceRect.left}px`,
      top: `${sourceRect.top}px`,
      width: `${sourceRect.width}px`,
      height: `${sourceRect.height}px`,
      borderRadius: sidebarShellRadius(previousStacked, previousDrawerOpen)
    })
    document.body.appendChild(ghost)
    activeSidebarGhost = ghost
    sidebarResponsiveMorphing.value = true
  }

  sidebarDrawerOpen.value = false
  stackedHeader.value = nextStacked
  await nextTick()

  for (const element of elements) {
    const first = firstRects.get(element)
    if (!first) continue
    const last = element.getBoundingClientRect()
    const deltaX = first.left - last.left
    const deltaY = first.top - last.top
    if (Math.abs(deltaX) < .5 && Math.abs(deltaY) < .5) continue
    element.getAnimations().forEach(animation => animation.cancel())
    element.animate([
      { transform: `translate3d(${deltaX}px, ${deltaY}px, 0)`, opacity: .92 },
      { transform: 'translate3d(0, 0, 0)', opacity: 1 }
    ], { duration: 240, easing: 'cubic-bezier(.22,.78,.24,1)' })
  }

  const targetElement = nextStacked ? sidebarToggleElement.value : sidebarPanelElement.value
  const targetRect = targetElement?.getBoundingClientRect()
  if (reducedMotion || !ghost || !targetRect) {
    if (sequence === responsiveSequence) sidebarResponsiveMorphing.value = false
    ghost?.remove()
    if (activeSidebarGhost === ghost) activeSidebarGhost = null
    return
  }

  const animation = ghost.animate([
    {
      left: `${sourceRect!.left}px`, top: `${sourceRect!.top}px`,
      width: `${sourceRect!.width}px`, height: `${sourceRect!.height}px`,
      borderRadius: sidebarShellRadius(previousStacked, previousDrawerOpen), opacity: .96
    },
    {
      left: `${targetRect.left}px`, top: `${targetRect.top}px`,
      width: `${targetRect.width}px`, height: `${targetRect.height}px`,
      borderRadius: sidebarShellRadius(nextStacked, false), opacity: 1
    }
  ], { duration: 270, easing: 'cubic-bezier(.22,.78,.24,1)', fill: 'forwards' })
  activeSidebarAnimation = animation
  try { await animation.finished } catch { return }
  if (sequence !== responsiveSequence) return

  ghost.remove()
  activeSidebarGhost = null
  activeSidebarAnimation = null
  sidebarResponsiveMorphing.value = false
  await nextTick()

  if (nextStacked) {
    sidebarToggleElement.value?.animate([
      { opacity: 0, transform: 'scale(.78)' },
      { opacity: 1, transform: 'scale(1)' }
    ], { duration: 130, easing: 'cubic-bezier(.2,.8,.2,1)' })
  } else {
    Array.from(sidebarPanelElement.value?.children ?? []).forEach((child, index) => {
      child.animate([
        { opacity: 0, transform: 'translateX(-7px)' },
        { opacity: 1, transform: 'translateX(0)' }
      ], { duration: 150, delay: Math.min(index * 18, 70), easing: 'ease-out', fill: 'both' })
    })
  }
}

function handleResponsiveKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') sidebarDrawerOpen.value = false
}

async function clearNodes() {
  if (!store.workflow.nodes.length) return
  if (!await requestAction('清空工作流', '将清空全部节点、连线、图片与 ZIP 列表。磁盘中的原始文件及已经解压的文件夹不会被删除。', '确认清空', true)) return
  try {
    await callHost('files.clear')
    await callHost('archives.clear', { nodeId: '' })
    store.setJobs([])
    store.clearNodes()
    store.status = '已清空全部节点、连线和文件列表'
  } catch (error) { showError(error) }
}

async function refreshProfiles(loadRemembered = false) {
  const names = await callHost<string[] | null>('profiles.list')
  profiles.value = names ?? []
  if (!loadRemembered) return
  const remembered = localStorage.getItem(profileStorageKey) ?? ''
  if (!remembered || !profiles.value.includes(remembered)) return
  selectedProfile.value = remembered
  await loadSelectedProfile()
}

async function saveProfile(saveAsNew = false) {
  if (!saveAsNew && selectedProfile.value) {
    await persistProfile(selectedProfile.value)
    return
  }
  profileDialogMode.value = 'save'
  profileDraftName.value = saveAsNew && selectedProfile.value ? `${selectedProfile.value} 2` : '我的工作流'
  profileDialogOpen.value = true
  await nextTick()
  profileNameInput.value?.focus()
  profileNameInput.value?.select()
}

async function confirmProfileName() {
  const name = profileDraftName.value.trim()
  if (!name) return
  if (profileDialogMode.value === 'rename') {
    await renameProfile(name)
    return
  }
  profileDialogOpen.value = false
  await persistProfile(name)
}

async function openRenameProfile() {
  if (!selectedProfile.value) return
  profileDialogMode.value = 'rename'
  profileDraftName.value = selectedProfile.value
  profileDialogOpen.value = true
  await nextTick()
  profileNameInput.value?.focus()
  profileNameInput.value?.select()
}

async function renameProfile(newName: string) {
  const oldName = selectedProfile.value
  if (!oldName) return
  if (newName === oldName) {
    profileDialogOpen.value = false
    return
  }
  try {
    const names = await callHost<string[]>('profiles.rename', { oldName, newName })
    profiles.value = names ?? profiles.value
    selectedProfile.value = newName
    localStorage.setItem(profileStorageKey, newName)
    profileDialogOpen.value = false
    store.status = `已将内置配置“${oldName}”改名为“${newName}”`
    notify(`配置已改名为“${newName}”`)
  } catch (error) { showError(error) }
}

async function persistProfile(name: string) {
  try {
    const names = await callHost<string[]>('profiles.save', { name, workflow: store.workflow })
    profiles.value = names ?? profiles.value
    selectedProfile.value = name
    localStorage.setItem(profileStorageKey, name)
    store.status = `已保存内置配置“${name}”`
    notify(`配置“${name}”已保存`)
  } catch (error) { showError(error) }
}

async function loadSelectedProfile() {
  const name = selectedProfile.value
  if (!name) {
    localStorage.removeItem(profileStorageKey)
    return
  }
  try {
    const workflow = await callHost<WorkflowDocument>('profiles.load', { name })
    store.replaceWorkflow(workflow)
    localStorage.setItem(profileStorageKey, name)
    store.status = `已加载内置配置“${name}”`
  } catch (error) { showError(error) }
}

async function deleteProfile() {
  const name = selectedProfile.value
  if (!name || !await requestAction('删除工作流配置', `确定删除内置配置“${name}”吗？此操作不会删除任何图片。`, '确认删除', true)) return
  try {
    const names = await callHost<string[]>('profiles.delete', { name })
    profiles.value = names ?? []
    selectedProfile.value = ''
    localStorage.removeItem(profileStorageKey)
    store.status = `已删除内置配置“${name}”`
  } catch (error) { showError(error) }
}

async function startWork(mode: 'estimate' | 'run') {
  try {
    const willReplaceArchive = mode === 'run' && store.workflow.nodes.some(node => node.type === 'ZipPack' && node.data.replaceSourceArchive)
    const archiveReplacement = await callHost<ReplacedArchiveConfirmation>('work.confirmReplacedArchives', {
      workflow: store.workflow,
      mode,
      willReplaceAgain: willReplaceArchive
    })
    let useReplacedArchive = false
    if (archiveReplacement.prompted) {
      if (archiveReplacement.unavailable) {
        await requestNotice('输入 ZIP 不存在', archiveReplacement.message ?? '替换后的新 ZIP 已不存在，无法继续处理。', '知道了', true)
        return
      }
      if (!await requestAction('原 ZIP 已替换', archiveReplacement.message ?? '是否使用上次生成的新 ZIP 继续处理？', '使用新 ZIP 继续', true)) return
      const refreshed = await callHost<{ archives: ArchiveJob[]; jobs: FileJob[] }>('work.acceptReplacedArchives', { ids: archiveReplacement.ids ?? [] })
      store.setArchives(refreshed.archives ?? [])
      store.setJobs(refreshed.jobs ?? [])
      useReplacedArchive = true
    } else if (!archiveReplacement.proceed) return

    let preprocessArchives = mode === 'run'
    if (mode === 'estimate') {
      const preflight = await callHost<{ connected: number; archives: number; pending: number; missingNodes: number; required: boolean }>('archives.preflight', { workflow: store.workflow })
      if (preflight.missingNodes > 0) throw new Error('工作流中的 ZIP 解压节点还没有选择压缩包。')
      if (preflight.pending > 0) {
        preprocessArchives = await requestAction(
          '需要 ZIP 解压预处理',
          `检测到 ${preflight.pending} 个 ZIP 尚未解压。精确预估不估算解压过程，需要先生成实际图片文件。`,
          '解压并继续预估'
        )
        if (!preprocessArchives) return
      }
    }
    const willReplaceAgain = mode === 'run' && store.workflow.nodes.some(node => node.type === 'Output' && node.data.replaceOriginal)
    const cleanupPackIds = new Set(store.workflow.connections
      .filter(connection => store.workflow.nodes.some(node => node.id === connection.fromNodeId && node.type === 'ZipPack')
        && store.workflow.nodes.some(node => node.id === connection.toNodeId && node.type === 'DeleteExtracted'))
      .map(connection => connection.fromNodeId))
    const willDeleteExtracted = mode === 'run' && store.workflow.connections.some(connection => cleanupPackIds.has(connection.toNodeId)
      && store.workflow.nodes.some(node => node.id === connection.fromNodeId && node.type === 'Output'))
    const replacement = await callHost<ReplacedSourceConfirmation>('work.confirmReplacedSources', { mode, willReplaceAgain })
    let useReplacedSources = false
    if (replacement.prompted) {
      if (replacement.unavailable) {
        await requestNotice('输入图片不存在', replacement.message ?? '替换后的新图片已不存在，无法继续处理。', '知道了', true)
        return
      }
      if (!await requestAction('原图片已删除', replacement.message ?? '是否使用上次生成的新图片继续处理？', '使用新图片继续', true)) return
      const refreshedJobs = await callHost<FileJob[]>('work.acceptReplacedSources', { ids: replacement.ids ?? [] })
      store.setJobs(refreshedJobs ?? [])
      useReplacedSources = true
    } else if (!replacement.proceed) return
    if (willReplaceAgain && !useReplacedSources) {
      if (!await requestAction('确认替换原图片', '工作流包含“替换原文件”。处理成功后，原图片会移入回收站。', '继续运行', true)) return
    }
    if (willReplaceArchive && !useReplacedArchive && !await requestAction('确认替换原 ZIP', 'ZIP 压缩节点将重新打包并替换原 ZIP。新压缩包会先完整校验，原 ZIP 备份随后移入回收站。', '继续并替换', true)) return
    if (willDeleteExtracted && !await requestAction('确认删除解压文件夹', 'ZIP 成功打包并校验后，将永久删除解压节点创建的对应文件夹及其全部内容。此操作不进入回收站。', '继续并删除', true)) return
    store.invalidateRoutes()
    const summary = await callHost<any>(mode === 'estimate' ? 'work.estimate' : 'work.run', {
      workflow: store.workflow,
      useReplacedSources,
      preprocessArchives,
      archivePasswords: store.archivePasswords
    })
    if (summary?.cancelled) notify(`任务已停止：已完成 ${summary.successes}，失败 ${summary.failures}`)
    else if (summary) notify(`${mode === 'estimate' ? '预估' : '处理'}完成：图片 ${summary.successes}，ZIP ${summary.packedArchives ?? 0}，失败 ${(summary.failures ?? 0) + (summary.archiveFailures ?? 0)}，缓存命中 ${summary.cacheHits}`)
  } catch (error) { showError(error) }
}

async function cancelWork() {
  try { await callHost('work.cancel') } catch (error) { showError(error) }
}

async function openOutput() {
  try { await callHost('output.open') } catch (error) { showError(error) }
}

function showError(error: unknown) {
  const message = error instanceof Error ? error.message : String(error)
  store.status = message
  notify(message, true)
}

async function windowCommand(command: string) {
  try {
    const result = await callHost<{ maximized?: boolean } | null>(`window.${command}`)
    if (command === 'maximize' && typeof result?.maximized === 'boolean') isMaximized.value = result.maximized
  } catch (error) { showError(error) }
}

function beginWindowDrag(event: PointerEvent) {
  if (event.button !== 0 || (event.target as HTMLElement).closest('button, select, input, label')) return
  callHost('window.drag').catch(showError)
}

function beginWindowResize(edge: string, event: PointerEvent) {
  if (event.button !== 0) return
  callHost('window.resize', { edge }).catch(showError)
}

function containsFiles(event: DragEvent) {
  return Array.from(event.dataTransfer?.types ?? []).includes('Files')
}

function fileDragEnter(event: DragEvent) {
  if (!containsFiles(event)) return
  fileDragDepth += 1
  fileDragActive.value = true
}

function fileDragLeave() {
  if (!fileDragActive.value) return
  fileDragDepth = Math.max(0, fileDragDepth - 1)
  if (!fileDragDepth) fileDragActive.value = false
}

async function fileDrop(event: DragEvent) {
  fileDragDepth = 0
  fileDragActive.value = false
  const files = Array.from(event.dataTransfer?.files ?? [])
  if (!files.length) return
  try {
    const zipFiles = files.filter(file => file.name.toLowerCase().endsWith('.zip'))
    const imageFiles = files.filter(file => !file.name.toLowerCase().endsWith('.zip'))
    if (zipFiles.length) {
      const extractNodes = store.workflow.nodes.filter(node => node.type === 'ZipExtract')
      const selectedExtract = extractNodes.find(node => store.selectedNodeIds.includes(node.id)) ?? extractNodes[0]
      if (!selectedExtract) throw new Error('请先在画布中添加“ZIP 解压”节点。')
      const archives = await importDroppedFiles<ArchiveJob[]>(zipFiles, 'archives.drop', { nodeId: selectedExtract.id })
      store.setArchives(archives ?? [])
    }
    if (imageFiles.length) {
      const jobs = await importDroppedFiles<FileJob[]>(imageFiles)
      store.setJobs(jobs)
    }
    store.status = `已拖入 ${files.length} 个文件`
  } catch (error) { showError(error) }
}

onMounted(async () => {
  const mountedAt = performance.now()
  window.addEventListener('resize', handleResponsiveResize)
  window.addEventListener('keydown', handleResponsiveKeydown)
  onHostEvent('jobsChanged', (jobs: FileJob[]) => store.setJobs(jobs))
  onHostEvent('archivesChanged', (archives: ArchiveJob[]) => store.setArchives(archives))
  onHostEvent('jobUpdated', (job: FileJob) => store.updateJob(job))
  onHostEvent('workProgress', (value: any) => store.setProgress(value))
  onHostEvent('workState', (value: any) => store.setWorkState(value))
  onHostEvent('windowStateChanged', (value: { maximized: boolean }) => { isMaximized.value = Boolean(value?.maximized) })
  if (props.startup?.selectedProfile) {
    localStorage.setItem(profileStorageKey, props.startup.selectedProfile)
    store.status = `已加载内置配置“${props.startup.selectedProfile}”`
  } else if (localStorage.getItem(profileStorageKey)) {
    localStorage.removeItem(profileStorageKey)
  }
  if (props.startupError) showError(new Error(props.startupError))
  await nextTick()
  try { await document.fonts.ready } catch { /* The UI can still render with system fallbacks. */ }
  const waitForPaint = () => Promise.race<void>([
    new Promise<void>(resolve => window.requestAnimationFrame(() => resolve())),
    new Promise<void>(resolve => window.setTimeout(resolve, 80))
  ])
  await waitForPaint()
  await waitForPaint()
  try {
    await callHost('app.frontendReady', {
      scriptToMountedMs: mountedAt - props.scriptStartedAt,
      scriptToReadyMs: performance.now() - props.scriptStartedAt,
      startupRequestMs: props.startupRequestMs
    })
  } catch { /* Browser preview has no native startup layer. */ }
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', handleResponsiveResize)
  window.removeEventListener('keydown', handleResponsiveKeydown)
  if (responsiveFrame) window.cancelAnimationFrame(responsiveFrame)
  responsiveSequence++
  activeSidebarAnimation?.cancel()
  activeSidebarGhost?.remove()
})
</script>

<template>
  <div class="app-shell" :class="{ 'header-stacked': stackedHeader, 'sidebar-responsive-morphing': sidebarResponsiveMorphing }" @dragenter.prevent="fileDragEnter" @dragover.prevent @dragleave.prevent="fileDragLeave" @drop.prevent="fileDrop">
    <div class="ambient ambient-one" /><div class="ambient ambient-two" /><div class="ambient ambient-three" />
    <template v-if="!isMaximized">
      <i v-for="edge in ['top', 'right', 'bottom', 'left', 'topLeft', 'topRight', 'bottomRight', 'bottomLeft']" :key="edge" class="window-resize-zone" :class="`resize-${edge}`" @pointerdown.stop.prevent="beginWindowResize(edge, $event)" />
    </template>

    <header class="topbar glass-panel" @pointerdown="beginWindowDrag">
      <div ref="brandElement" class="brand">
        <div class="brand-mark"><img :src="'./aichan-program.png'" alt="" /><i /></div>
        <div><h1>艾酱图片工具箱</h1><p>Visual Workflow Studio <b>{{ store.version }}</b></p></div>
      </div>
      <nav class="toolbar">
        <div ref="primaryToolbarGroup" class="toolbar-group toolbar-primary">
          <div class="profile-tools">
            <GlassSelect v-model="selectedProfile" class="profile-select" label="选择内置工作流配置" :options="profileOptions" :disabled="store.busy" @change="loadSelectedProfile" />
            <button class="toolbar-button profile-save" :disabled="store.busy" title="保存或覆盖当前配置" @click="saveProfile(false)">保存</button>
            <button class="toolbar-button icon-only" :disabled="store.busy" title="另存为新配置" @click="saveProfile(true)">＋</button>
            <button class="toolbar-button icon-only profile-rename" :disabled="store.busy || !selectedProfile" title="重命名当前配置" @click="openRenameProfile">✎</button>
            <button class="toolbar-button icon-only profile-delete" :disabled="store.busy || !selectedProfile" title="删除当前配置" @click="deleteProfile">×</button>
          </div>
        </div>
        <div ref="secondaryToolbarGroup" class="toolbar-group toolbar-secondary">
          <span class="toolbar-divider" />
          <div class="compact-field"><span>并行</span>
            <GlassSelect v-model="store.workflow.parallelism" class="parallel-select" label="并行数量" :options="parallelOptions" :disabled="store.busy" />
          </div>
          <label class="cache-toggle grayscale-toggle" title="仅在明确识别为黑白图时，以单通道灰度编码减小 JPG 体积">
            <input v-model="store.workflow.autoGrayscale" type="checkbox" :disabled="store.busy" /><span />黑白优化
          </label>
          <label class="cache-toggle" title="预估完成后保留中间结果，运行时直接复用">
            <input v-model="store.workflow.cacheEstimates" type="checkbox" :disabled="store.busy" /><span />缓存预估
          </label>
          <button class="toolbar-button estimate" :disabled="store.busy || !canStart" @click="startWork('estimate')">
            <svg class="toolbar-glyph" viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="3" width="14" height="18" rx="2.5" /><path d="M8 7h8M8.5 12h.01M12 12h.01M15.5 12h.01M8.5 16h.01M12 16h.01M15.5 16h.01" /></svg>精确预估
          </button>
          <button v-if="!store.busy" class="run-button" :disabled="!canStart" @click="startWork('run')"><i>▶</i>运行工作流</button>
          <button v-else class="run-button cancel" @click="cancelWork"><i>■</i>停止任务</button>
          <button class="toolbar-button icon-only" title="打开输出目录" @click="openOutput">↗</button>
        </div>
      </nav>
      <div class="window-controls">
        <button aria-label="最小化" @click="windowCommand('minimize')"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 8h10" /></svg></button>
        <button :aria-label="isMaximized ? '还原窗口' : '最大化'" :title="isMaximized ? '还原窗口' : '最大化'" @click="windowCommand('maximize')">
          <svg v-if="!isMaximized" viewBox="0 0 16 16" aria-hidden="true"><rect x="3.5" y="3.5" width="9" height="9" rx=".5" /></svg>
          <svg v-else viewBox="0 0 16 16" aria-hidden="true"><rect x="2.5" y="5" width="8.5" height="8.5" rx=".7" /><path d="M5 5V2.5h8.5V11H11" /></svg>
        </button>
        <button class="close-window" aria-label="关闭" @click="windowCommand('close')"><svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3.5 3.5l9 9m0-9l-9 9" /></svg></button>
      </div>
    </header>

    <div class="sidebar-drawer-scrim" :class="{ visible: sidebarDrawerOpen }" @pointerdown="sidebarDrawerOpen = false" />

    <div ref="sidebarHostElement" class="sidebar-host" :class="{ 'drawer-open': sidebarDrawerOpen }">
      <button ref="sidebarToggleElement" class="sidebar-toggle glass-panel" :class="{ active: sidebarDrawerOpen }" type="button" :title="sidebarDrawerOpen ? '收起节点库' : '打开节点库'" :aria-label="sidebarDrawerOpen ? '收起节点库' : '打开节点库'" @click="sidebarDrawerOpen = !sidebarDrawerOpen">
        <svg class="sidebar-toggle-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3 4.5 7.2 12 11.4l7.5-4.2L12 3Z" /><path d="m4.5 7.2 7.5 4.2 7.5-4.2M4.5 7.2v9.5L12 21m7.5-13.8v9.5L12 21m0-9.6V21" /></svg>
        <span>节点库</span>
        <svg class="sidebar-toggle-chevron" viewBox="0 0 12 8" aria-hidden="true"><path d="m1 1.5 5 5 5-5" /></svg>
      </button>
      <aside ref="sidebarPanelElement" class="sidebar glass-panel" :class="{ 'drawer-open': sidebarDrawerOpen }">
        <div class="sidebar-heading">
          <div><span>NODE LIBRARY</span><h2>节点库</h2></div>
          <div class="sidebar-heading-actions">
            <button class="clear-button" title="清空节点" :disabled="store.busy" @click="clearNodes">清空</button>
            <button class="sidebar-close" type="button" title="关闭节点库" aria-label="关闭节点库" @click="sidebarDrawerOpen = false">×</button>
          </div>
        </div>

        <section v-for="group in nodeGroups" :key="group.label" class="node-group">
          <h3>{{ group.label }}</h3>
          <button v-for="type in group.nodes" :key="type" class="library-node" :disabled="store.busy" @click="addNode(type)">
            <i :style="{ '--accent': nodeMeta[type].accent }"><NodeGlyph :type="type" /></i>
            <span><strong>{{ nodeMeta[type].title }}</strong><small>{{ type }}</small></span>
            <b>＋</b>
          </button>
        </section>

        <div class="sidebar-help">
          <span>操作提示</span>
          <p>拖动空白区域移动画布，滚轮围绕指针缩放。拖动节点标题移动；从彩色端口拉出连接线。</p>
        </div>
      </aside>
    </div>

    <WorkflowCanvas />

    <footer class="statusbar glass-panel">
      <div class="status-message"><i :class="{ busy: store.busy }" />{{ store.status }}</div>
      <div v-if="store.busy" class="work-progress">
        <span>{{ workStageLabel }} {{ store.progress }} / {{ store.progressTotal }}</span>
        <div><i :style="{ width: `${progressPercent}%` }" /></div>
        <b>{{ progressPercent }}%</b>
      </div>
      <div class="status-summary">
        <span>{{ store.selectedCount }} / {{ store.jobs.length }} 张图片</span>
        <span v-if="store.archiveCount">{{ store.archiveCount }} 个 ZIP</span>
        <span>{{ store.workflow.nodes.length }} 节点</span>
        <span>{{ store.workflow.connections.length }} 连线</span>
        <span v-if="store.processorCount">CPU {{ store.processorCount }} 线程</span>
      </div>
    </footer>

    <Transition name="toast">
      <div v-if="toast" class="toast glass-panel" :class="{ error: toast.error }"><i>{{ toast.error ? '!' : '✓' }}</i>{{ toast.text }}</div>
    </Transition>
    <Transition name="dialog-fade">
      <div v-if="actionDialog" class="profile-dialog-backdrop" @pointerdown.self="resolveAction(false)">
        <div class="profile-dialog action-dialog glass-panel">
          <div class="profile-dialog-icon" :class="{ danger: actionDialog.danger }">{{ actionDialog.danger ? '!' : '⇣' }}</div>
          <div class="profile-dialog-copy">
            <strong>{{ actionDialog.title }}</strong>
            <span>{{ actionDialog.message }}</span>
          </div>
          <div class="profile-dialog-actions">
            <button v-if="actionDialog.cancelText" type="button" class="soft-button" @click="resolveAction(false)">{{ actionDialog.cancelText }}</button>
            <button type="button" class="soft-button primary" :class="{ danger: actionDialog.danger }" @click="resolveAction(true)">{{ actionDialog.confirmText }}</button>
          </div>
        </div>
      </div>
    </Transition>
    <Transition name="dialog-fade">
      <div v-if="profileDialogOpen" class="profile-dialog-backdrop" @pointerdown.self="profileDialogOpen = false">
        <form class="profile-dialog glass-panel" @submit.prevent="confirmProfileName">
          <div class="profile-dialog-icon">⌁</div>
          <div class="profile-dialog-copy">
            <strong>{{ profileDialogMode === 'rename' ? '重命名工作流配置' : '保存工作流配置' }}</strong>
            <span>{{ profileDialogMode === 'rename' ? '只修改配置名称，已保存的工作流内容保持不变。' : '配置保存在软件内部，可从顶部下拉框直接切换。' }}</span>
          </div>
          <label for="profile-name">{{ profileDialogMode === 'rename' ? '新配置名称' : '配置名称' }}</label>
          <input id="profile-name" ref="profileNameInput" v-model="profileDraftName" maxlength="40" autocomplete="off" @keydown.esc="profileDialogOpen = false" />
          <small>{{ profileDialogMode === 'rename' ? '新名称不能与其他配置重复。' : '若名称已经存在，将更新该配置。' }}</small>
          <div class="profile-dialog-actions">
            <button type="button" class="soft-button" @click="profileDialogOpen = false">取消</button>
            <button type="submit" class="soft-button primary" :disabled="!profileDraftName.trim()">{{ profileDialogMode === 'rename' ? '确认改名' : '保存配置' }}</button>
          </div>
        </form>
      </div>
    </Transition>
    <Transition name="drop-overlay">
      <div v-if="fileDragActive" class="file-drop-overlay">
        <div class="file-drop-card glass-panel"><i>＋</i><strong>释放以导入图片或 ZIP</strong><span>ZIP 会加入选中的解压节点；图片直接进入文件列表</span></div>
      </div>
    </Transition>
  </div>
</template>
