<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { callHost } from '../bridge'
import { requestAction } from '../confirm'
import { hasInput, inputKind, nodeMeta, outputPorts } from '../defaults'
import { useAppStore } from '../store'
import type { ArchiveJob, FileJob, PortDefinition, WorkflowNode } from '../types'
import FileTable from './FileTable.vue'
import GlassSelect from './GlassSelect.vue'
import NodeGlyph from './NodeGlyph.vue'
import NumberField from './NumberField.vue'

const props = defineProps<{ node: WorkflowNode; selected: boolean; routeHighlighted: boolean; jobs: FileJob[]; busy: boolean }>()
const emit = defineEmits<{
  (event: 'headerDown', node: WorkflowNode, pointer: PointerEvent): void
  (event: 'select', node: WorkflowNode, pointer: PointerEvent): void
  (event: 'remove', id: string): void
  (event: 'duplicate', node: WorkflowNode): void
  (event: 'outputDown', node: WorkflowNode, port: PortDefinition, pointer: PointerEvent): void
  (event: 'resizeDown', node: WorkflowNode, pointer: PointerEvent): void
  (event: 'size', id: string, width: number, height: number): void
  (event: 'redraw'): void
}>()

const store = useAppStore()
const root = ref<HTMLElement>()
const meta = computed(() => nodeMeta[props.node.type])
const ports = computed(() => outputPorts(props.node.type))
const targetMaximumQuality = computed({
  get: () => Math.min(100, Number(props.node.data.targetStartQuality) + Number(props.node.data.targetQualitySpan)),
  set: value => {
    const start = Math.min(99, Math.max(20, Math.round(Number(props.node.data.targetStartQuality) || 90)))
    props.node.data.targetQualitySpan = Math.max(1, Math.min(100 - start, Math.round(Number(value)) - start))
  }
})
const percentMarks = [20, 40, 60, 80, 100]
const descreenMarks = [
  { value: 1, label: '轻微' },
  { value: 2, label: '中度' },
  { value: 3, label: '强力' }
]
const comparisonOptions = ['>=', '>', '<=', '<'].map(value => ({ label: value, value }))
const resolutionJoinOptions = [{ label: '并且', value: 'AND' }, { label: '或者', value: 'OR' }]
const archiveEncodingOptions = [
  { label: '自动识别', value: 'auto' },
  { label: 'UTF-8', value: 'utf8' },
  { label: '简体中文', value: 'gb18030' },
  { label: '日文 CP932', value: 'cp932' }
]
const nodeArchives = computed(() => store.archives.filter(archive => archive.nodeId === props.node.id))
const archivePassword = computed({
  get: () => store.archivePasswords[props.node.id] ?? '',
  set: value => store.setArchivePassword(props.node.id, value)
})
let resizeObserver: ResizeObserver | undefined

onMounted(() => {
  if (!root.value) return
  resizeObserver = new ResizeObserver(() => {
    if (!root.value) return
    if (root.value.classList.contains('is-resizing')) return
    const width = root.value.offsetWidth
    const height = root.value.offsetHeight
    if (Math.abs(width - props.node.width) < 1 && Math.abs(height - props.node.height) < 1) return
    emit('size', props.node.id, width, height)
    emit('redraw')
  })
  resizeObserver.observe(root.value)
})
onBeforeUnmount(() => resizeObserver?.disconnect())

async function selectFiles() {
  const jobs = await callHost<FileJob[]>('files.select')
  if (jobs) store.setJobs(jobs)
}

async function clearFiles() {
  if (props.jobs.length && !await requestAction('清空文件列表', '清空当前文件列表？磁盘中的原始图片不会被删除。', '确认清空', true)) return
  const jobs = await callHost<FileJob[]>('files.clear')
  store.setJobs(jobs ?? [])
}

async function removeChecked() {
  const ids = props.jobs.filter(job => job.checked).map(job => job.id)
  if (!ids.length) return
  const jobs = await callHost<FileJob[]>('files.remove', { ids })
  store.setJobs(jobs ?? [])
}

async function chooseOutput() {
  const directory = await callHost<string | null>('dialog.outputDirectory', { current: props.node.data.outputDirectory })
  if (directory) props.node.data.outputDirectory = directory
}

async function selectArchives() {
  const archives = await callHost<ArchiveJob[]>('archives.select', { nodeId: props.node.id })
  store.setArchives(archives ?? [])
}

async function clearArchives() {
  if (nodeArchives.value.length && !await requestAction('清空 ZIP 列表', '清空这个节点的 ZIP 列表？已经解压到磁盘的文件夹不会被删除。', '确认清空', true)) return
  const archives = await callHost<ArchiveJob[]>('archives.clear', { nodeId: props.node.id })
  store.setArchives(archives ?? [])
}

async function removeArchive(id: string) {
  const archives = await callHost<ArchiveJob[]>('archives.remove', { ids: [id] })
  store.setArchives(archives ?? [])
}

async function preprocessArchives() {
  const result = await callHost<{ archives: ArchiveJob[]; jobs: FileJob[] }>('archives.preprocess', {
    workflow: store.workflow,
    nodeId: props.node.id,
    archivePasswords: store.archivePasswords
  })
  if (result?.archives) store.setArchives(result.archives)
  if (result?.jobs) store.setJobs(result.jobs)
}

function bytes(value: number) {
  if (value < 1024) return `${value} B`
  if (value < 1024 ** 2) return `${(value / 1024).toFixed(1)} KB`
  return `${(value / 1024 ** 2).toFixed(2)} MB`
}

function outputStyle(index: number) {
  const top = props.node.type === 'Import'
    ? 72
    : props.node.type === 'FormatFilter' ? 78 + index * 34 : 78 + index * 40
  return { top: `${top}px`, '--port-color': ports.value[index].color }
}

function inputStyle() {
  return { top: `${props.node.type === 'Import' ? 72 : 78}px` }
}

function rangeStyle(value: number) {
  return { '--range-value': `${Math.max(0, Math.min(100, (value - 20) / 0.8))}%` }
}

function setPercent(key: 'scalePercent' | 'qualityPercent', value: number) {
  props.node.data[key] = value
}

function descreenRangeStyle(value: number) {
  return { '--range-value': `${Math.max(0, Math.min(100, (value - 1) * 50))}%` }
}
</script>

<template>
  <article
    ref="root"
    class="workflow-node"
    :class="[`node-${node.type.toLowerCase()}`, { selected, 'route-highlighted': routeHighlighted, resizable: node.type === 'Import' }]"
    :style="{
      width: `${node.width || meta.width}px`,
      height: `${node.height || meta.height}px`,
      '--node-x': `${node.x}px`,
      '--node-y': `${node.y}px`,
      '--node-accent': meta.accent
    }"
    :data-node-id="node.id"
    @pointerdown="emit('select', node, $event)"
  >
    <header class="node-header" @pointerdown.stop="emit('headerDown', node, $event)">
      <span class="node-icon"><NodeGlyph :type="node.type" /></span>
      <div class="node-title">
        <strong>{{ node.title }}</strong>
        <small>{{ node.type }}</small>
      </div>
      <div class="node-actions nodrag">
        <button class="node-copy" title="复制节点" :disabled="busy || node.type === 'Import'" @pointerdown.stop @click.stop="emit('duplicate', node)">
          <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="7" y="7" width="12" height="12" rx="2"/><path d="M5 16H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v1"/></svg>
        </button>
        <button class="node-close" title="删除节点" :disabled="busy" @pointerdown.stop @click.stop="emit('remove', node.id)">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 5l14 14M19 5L5 19"/></svg>
        </button>
      </div>
    </header>

    <div v-if="hasInput(node.type)" class="port-input-row port-row" :style="inputStyle()">
      <button
        class="port-dot input-port"
        :data-port-id="`${node.id}:in:input`"
        :data-node="node.id"
        data-port="in"
        data-direction="input"
        :data-kind="inputKind(node.type) ?? undefined"
        aria-label="输入端口"
      />
    </div>

    <div
      v-for="(port, index) in ports"
      :key="port.id"
      class="port-output-row port-row"
      :style="outputStyle(index)"
    >
      <span v-if="port.kind !== 'batch' && port.label !== '图片'">{{ port.label }}</span>
      <button
        class="port-dot output-port"
        :style="{ '--port-color': port.color }"
        :data-port-id="`${node.id}:${port.id}:output`"
        :data-node="node.id"
        :data-port="port.id"
        data-direction="output"
        :data-kind="port.kind"
        :aria-label="`${port.label}输出端口`"
        @pointerdown.stop.prevent="emit('outputDown', node, port, $event)"
        @contextmenu.stop.prevent="store.clearOutput(node.id, port.id); emit('redraw')"
      />
    </div>

    <div class="node-body nodrag" @pointerdown.stop>
      <template v-if="node.type === 'Import'">
        <div class="import-actions">
          <button class="soft-button primary" :disabled="busy" @click="selectFiles">＋ 选择图片</button>
          <button class="soft-button" :disabled="busy || !jobs.length" @click="removeChecked">移除勾选</button>
          <button class="soft-button danger" :disabled="busy || !jobs.length" @click="clearFiles">清空列表</button>
          <span class="file-count">{{ store.selectedCount }} / {{ jobs.length }} 张</span>
        </div>
        <FileTable :jobs="jobs" :busy="busy" />
      </template>

      <template v-else-if="node.type === 'ZipExtract'">
        <div class="archive-actions">
          <button class="soft-button primary" :disabled="busy" @click="selectArchives">＋ 选择 ZIP</button>
          <button class="soft-button danger" :disabled="busy || !nodeArchives.length" @click="clearArchives">清空</button>
          <span>{{ nodeArchives.length }} 个包</span>
        </div>
        <div class="archive-list">
          <div v-if="!nodeArchives.length" class="archive-empty">批量选择 ZIP，每个压缩包会建立独立同名文件夹</div>
          <div v-for="archive in nodeArchives" :key="archive.id" class="archive-row" :title="archive.sourcePath">
            <div><strong>{{ archive.name }}</strong><small>{{ bytes(archive.size) }} · {{ archive.status }}</small></div>
            <i><span :style="{ width: `${archive.progress}%` }" /></i>
            <button :disabled="busy" title="移除 ZIP" @click="removeArchive(archive.id)">×</button>
          </div>
        </div>
        <label class="archive-field"><span>文件名编码</span><GlassSelect v-model="node.data.archiveEncoding" class="node-select archive-select" label="ZIP 文件名编码" :options="archiveEncodingOptions" :disabled="busy" /></label>
        <label class="archive-field"><span>解压密码</span><input v-model="archivePassword" class="archive-password" type="password" autocomplete="off" placeholder="无密码请留空" :disabled="busy" /></label>
        <button class="soft-button archive-preprocess" :disabled="busy || !nodeArchives.length" @click="preprocessArchives">执行解压预处理</button>
        <p class="node-hint">默认解压到 ZIP 同目录的同名文件夹；若已存在则创建编号目录。</p>
      </template>

      <template v-else-if="node.type === 'FormatFilter'">
        <div class="node-description">按照当前图片格式分流<br><small>JPG / JPEG 使用同一出口</small></div>
      </template>

      <template v-else-if="node.type === 'ConvertJpg'">
        <div class="feature-symbol pink">JPG</div>
        <p class="node-description centered">Jpegli 高保真 JPEG · 4:4:4<br><small>延迟到工作流末端编码，透明区域使用白色</small></p>
      </template>

      <template v-else-if="node.type === 'SizeFilter'">
        <label class="field-row"><span>文件大小</span><GlassSelect v-model="node.data.sizeOperator" class="node-select operator-select" label="文件大小比较方式" :options="comparisonOptions" :disabled="busy" /><NumberField v-model="node.data.sizeMb" :min="0" :step="0.1" :disabled="busy" label="文件大小 MB" /><em>MB</em></label>
      </template>

      <template v-else-if="node.type === 'ResolutionFilter'">
        <div class="resolution-line">
          <input v-model="node.data.widthEnabled" type="checkbox" /><b>宽</b><GlassSelect v-model="node.data.widthOperator" class="node-select operator-select" label="宽度比较方式" :options="comparisonOptions" :disabled="busy" /><NumberField v-model="node.data.widthValue" :min="1" :disabled="busy" label="宽度像素" /><em>px</em>
        </div>
        <div class="join-line"><span></span><GlassSelect v-model="node.data.resolutionJoin" class="node-select join-select" label="宽高条件关系" :options="resolutionJoinOptions" :disabled="busy" /><span></span></div>
        <div class="resolution-line">
          <input v-model="node.data.heightEnabled" type="checkbox" /><b>高</b><GlassSelect v-model="node.data.heightOperator" class="node-select operator-select" label="高度比较方式" :options="comparisonOptions" :disabled="busy" /><NumberField v-model="node.data.heightValue" :min="1" :disabled="busy" label="高度像素" /><em>px</em>
        </div>
      </template>

      <template v-else-if="node.type === 'Resize'">
        <div class="range-title"><span>等比缩放</span><strong>{{ node.data.scalePercent }}%</strong></div>
        <input v-model.number="node.data.scalePercent" class="glass-range blue" :style="rangeStyle(node.data.scalePercent)" type="range" min="20" max="100" />
        <div class="range-marks">
          <button v-for="value in percentMarks" :key="value" :style="{ left: `${(value - 20) / 0.8}%` }" @click="setPercent('scalePercent', value)">{{ value }}%</button>
        </div>
        <p class="node-hint">宽高同时按比例调整，使用 Lanczos 重采样。</p>
      </template>

      <template v-else-if="node.type === 'Quality'">
        <div class="range-title"><span>JPG 输出画质</span><strong>{{ node.data.qualityPercent }}%</strong></div>
        <input v-model.number="node.data.qualityPercent" class="glass-range purple" :style="rangeStyle(node.data.qualityPercent)" type="range" min="20" max="100" />
        <div class="range-marks">
          <button v-for="value in percentMarks" :key="value" :style="{ left: `${(value - 20) / 0.8}%` }" @click="setPercent('qualityPercent', value)">{{ value }}%</button>
        </div>
        <p class="node-hint">Jpegli 高画质编码，4:4:4 色度；默认 100%。</p>
      </template>

      <template v-else-if="node.type === 'Descreen'">
        <div class="range-title"><span>逆网点化强度</span><strong>{{ descreenMarks.find(item => item.value === node.data.descreenLevel)?.label ?? '中度' }}</strong></div>
        <input v-model.number="node.data.descreenLevel" class="glass-range descreen-range" :style="descreenRangeStyle(node.data.descreenLevel)" type="range" min="1" max="3" step="1" />
        <div class="range-marks descreen-marks">
          <button v-for="(item, index) in descreenMarks" :key="item.value" :style="{ left: `${index * 50}%` }" @click="node.data.descreenLevel = item.value">{{ item.label }}</button>
        </div>
        <p class="node-hint">灰度化并平滑规则网点；强度越高，网点越弱，细节变化越明显。</p>
      </template>

      <template v-else-if="node.type === 'TargetSize'">
        <div class="target-size-line">
          <span>目标体积</span>
          <NumberField v-model="node.data.targetSizeMb" :min="0.01" :max="1024" :step="0.1" :disabled="busy" label="目标体积 MB" />
          <em>MB</em>
        </div>
        <div class="target-quality-grid">
          <label><span>起始画质</span><NumberField v-model="node.data.targetStartQuality" :min="20" :max="99" :disabled="busy" label="起始画质" /></label>
          <label><span>画质上限</span><NumberField v-model="targetMaximumQuality" :min="Math.min(100, node.data.targetStartQuality + 1)" :max="100" :disabled="busy" label="画质上限" /></label>
          <label><span>画质下限</span><NumberField v-model="node.data.targetMinimumQuality" :min="20" :max="Math.max(20, node.data.targetStartQuality)" :disabled="busy" label="画质下限" /></label>
        </div>
        <div class="target-derived"><span>动态预测</span><i />最多 5 次真实编码</div>
        <label class="switch-line target-result-toggle"><input v-model="node.data.targetKeepSmallestOnUnmet" type="checkbox" /><span>未达标时输出最小结果</span></label>
        <p class="node-hint">达标出口输出 JPG；关闭开关时，未达标出口会丢弃尝试结果并跳过本节点。</p>
      </template>

      <template v-else-if="node.type === 'Output'">
        <label class="switch-line"><input v-model="node.data.sameFolder" type="checkbox" /><span>保存到原图所在文件夹</span></label>
        <div v-if="!node.data.sameFolder" class="directory-picker">
          <input v-model="node.data.outputDirectory" readonly placeholder="请选择输出目录" />
          <button class="soft-button" @click="chooseOutput">浏览</button>
        </div>
        <label class="switch-line warning"><input v-model="node.data.replaceOriginal" type="checkbox" /><span>替换原文件</span></label>
        <p class="node-hint">未发生格式转换、缩放或画质处理时自动跳过；替换时原文件会移入回收站。</p>
        <div class="output-summary"><i />工作流终点</div>
      </template>

      <template v-else-if="node.type === 'ZipPack'">
        <div class="feature-symbol zip-store">STORE</div>
        <p class="node-description centered">按来源 ZIP 分别重新打包<br><small>仅存储模式 · 不重复压缩图片数据</small></p>
        <label class="switch-line"><input v-model="node.data.preserveNonImageFiles" type="checkbox" /><span>保留非图片文件和目录结构</span></label>
        <label class="switch-line warning"><input v-model="node.data.replaceSourceArchive" type="checkbox" /><span>替换原 ZIP</span></label>
        <p class="node-hint">替换前会完整校验新 ZIP；若图片处理失败，则对应压缩包不会被替换。</p>
        <div class="output-summary archive-summary"><i />ZIP 后处理终点</div>
      </template>

      <template v-else-if="node.type === 'DeleteExtracted'">
        <div class="feature-symbol cleanup-symbol">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M9 3h6l1 4H8zM7 7l1 14h8l1-14" /><path d="M10 11v6m4-6v6" /></svg>
        </div>
        <p class="node-description centered">删除解压节点创建的对应文件夹<br><small>仅在 ZIP 完整打包并校验成功后执行</small></p>
        <p class="node-hint cleanup-warning">永久删除文件夹及内部图片，不进入回收站；处理或打包失败时不会删除。</p>
        <div class="output-summary cleanup-summary"><i />目录清理终点</div>
      </template>
    </div>
    <button v-if="node.type === 'Import'" class="resize-corner" aria-label="调整文件列表节点大小" @pointerdown.stop.prevent="emit('resizeDown', node, $event)" />
  </article>
</template>
