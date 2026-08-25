<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { callHost } from '../bridge'
import { useAppStore } from '../store'
import type { FileJob } from '../types'

const props = defineProps<{ jobs: FileJob[]; busy: boolean }>()
const store = useAppStore()
const scrollTop = ref(0)
const viewportHeight = ref(220)
const scrollHost = ref<HTMLElement>()
const headerGrid = ref<HTMLElement>()
const sortKey = ref<keyof FileJob>('name')
const ascending = ref(true)
const rowHeight = 34
const columns = [
  { key: 'checked', label: '', width: 36, min: 32, max: 52, sortable: false },
  { key: 'name', label: '文件名', width: 180, min: 110, max: 420, sortable: true },
  { key: 'format', label: '原格式', width: 74, min: 62, max: 130, sortable: true },
  { key: 'targetFormat', label: '目标格式', width: 84, min: 72, max: 140, sortable: true },
  { key: 'originalSize', label: '原始大小', width: 94, min: 78, max: 150, sortable: true },
  { key: 'originalWidth', label: '原始分辨率', width: 118, min: 100, max: 180, sortable: true },
  { key: 'targetWidth', label: '目标分辨率', width: 118, min: 100, max: 180, sortable: true },
  { key: 'estimatedSize', label: '预估大小', width: 96, min: 80, max: 160, sortable: true },
  { key: 'status', label: '状态', width: 150, min: 110, max: 280, sortable: true }
] as const
function savedColumnWidths() {
  try {
    const saved = JSON.parse(localStorage.getItem('aichan.fileColumnWidths') ?? '[]') as number[]
    if (saved.length === columns.length) return saved.map((width, index) => Math.min(columns[index].max, Math.max(columns[index].min, Number(width) || columns[index].width)))
  } catch { /* 使用默认列宽 */ }
  return columns.map(column => column.width)
}
const columnWidths = ref(savedColumnWidths())
const gridTemplate = computed(() => columnWidths.value.map(width => `${width}px`).join(' '))
const tableWidth = computed(() => columnWidths.value.reduce((total, width) => total + width, 0))
let columnDrag: { index: number; clientX: number; width: number; scale: number } | null = null
let resizeObserver: ResizeObserver | undefined

onMounted(() => {
  if (!scrollHost.value) return
  resizeObserver = new ResizeObserver(() => {
    if (scrollHost.value) viewportHeight.value = scrollHost.value.clientHeight
  })
  resizeObserver.observe(scrollHost.value)
  window.addEventListener('pointermove', resizeColumn)
  window.addEventListener('pointerup', finishColumnResize)
})
onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  window.removeEventListener('pointermove', resizeColumn)
  window.removeEventListener('pointerup', finishColumnResize)
})

const sorted = computed(() => [...props.jobs].sort((a, b) => {
  const av = a[sortKey.value] ?? ''
  const bv = b[sortKey.value] ?? ''
  const result = typeof av === 'number' && typeof bv === 'number'
    ? av - bv
    : String(av).localeCompare(String(bv), 'zh-CN', { numeric: true })
  return ascending.value ? result : -result
}))

const start = computed(() => Math.max(0, Math.floor(scrollTop.value / rowHeight) - 3))
const count = computed(() => Math.ceil(viewportHeight.value / rowHeight) + 7)
const visible = computed(() => sorted.value.slice(start.value, start.value + count.value))

function setSort(key: keyof FileJob) {
  if (sortKey.value === key) ascending.value = !ascending.value
  else { sortKey.value = key; ascending.value = true }
}

function sortMark(key: keyof FileJob) {
  return sortKey.value === key ? (ascending.value ? ' ↑' : ' ↓') : ''
}

async function toggle(job: FileJob, checked: boolean) {
  job.checked = checked
  await callHost('files.check', { id: job.id, checked })
}

async function toggleAll(checked: boolean) {
  store.jobs.forEach(job => { job.checked = checked })
  await callHost('files.checkAll', { checked })
}

function onScroll(event: Event) {
  const target = event.currentTarget as HTMLElement
  scrollTop.value = target.scrollTop
  viewportHeight.value = target.clientHeight
  if (headerGrid.value) headerGrid.value.style.transform = `translateX(${-target.scrollLeft}px)`
}

function beginColumnResize(index: number, event: PointerEvent) {
  const table = (event.currentTarget as HTMLElement).closest('.file-table') as HTMLElement | null
  const scale = table && table.offsetWidth ? table.getBoundingClientRect().width / table.offsetWidth : 1
  columnDrag = { index, clientX: event.clientX, width: columnWidths.value[index], scale: scale || 1 }
  document.body.classList.add('column-resizing')
}

function resizeColumn(event: PointerEvent) {
  if (!columnDrag) return
  const column = columns[columnDrag.index]
  const next = columnDrag.width + (event.clientX - columnDrag.clientX) / columnDrag.scale
  columnWidths.value[columnDrag.index] = Math.round(Math.min(column.max, Math.max(column.min, next)))
}

function finishColumnResize() {
  if (columnDrag) localStorage.setItem('aichan.fileColumnWidths', JSON.stringify(columnWidths.value))
  columnDrag = null
  document.body.classList.remove('column-resizing')
}

function bytes(value: number | null | undefined) {
  if (value == null || value < 0) return '—'
  if (value < 1024) return `${value} B`
  if (value < 1024 ** 2) return `${(value / 1024).toFixed(1)} KB`
  return `${(value / 1024 ** 2).toFixed(2)} MB`
}

function dimensions(width: number, height: number) {
  return width > 0 && height > 0 ? `${width} × ${height}` : '未知'
}
</script>

<template>
  <div class="file-table" @pointerdown.stop>
    <div class="file-table-head-viewport">
      <div ref="headerGrid" class="file-table-head file-grid" :style="{ gridTemplateColumns: gridTemplate, width: `${tableWidth}px` }">
        <div v-for="(column, index) in columns" :key="column.key" class="file-head-cell">
          <label v-if="column.key === 'checked'" class="check-cell" title="全选">
            <input type="checkbox" :checked="store.allChecked" :disabled="busy || !jobs.length" @change="toggleAll(($event.target as HTMLInputElement).checked)" />
          </label>
          <button v-else @click="setSort(column.key as keyof FileJob)">{{ column.label }}{{ sortMark(column.key as keyof FileJob) }}</button>
          <span class="column-resizer" title="拖动调整列宽" @pointerdown.stop.prevent="beginColumnResize(index, $event)" />
        </div>
      </div>
    </div>
    <div ref="scrollHost" class="file-table-scroll" @scroll="onScroll" @wheel.stop>
      <div v-if="!jobs.length" class="file-empty">
        <span class="file-empty-icon">＋</span>
        <strong>还没有图片</strong>
        <small>点击“选择图片”导入 PNG、JPG、WebP、AVIF、HEIC 或其他格式</small>
      </div>
      <div v-else class="file-spacer" :style="{ height: `${sorted.length * rowHeight}px`, minWidth: `${tableWidth}px` }">
        <div
          v-for="(job, index) in visible"
          :key="job.id"
          class="file-row file-grid"
          :class="{ unchecked: !job.checked, failed: job.status.startsWith('失败'), 'route-active': store.highlightedJobId === job.id }"
          :style="{ transform: `translateY(${(start + index) * rowHeight}px)`, gridTemplateColumns: gridTemplate }"
          :title="job.sourcePath"
        >
          <label class="check-cell">
            <input type="checkbox" :checked="job.checked" :disabled="busy" @change="toggle(job, ($event.target as HTMLInputElement).checked)" />
          </label>
          <span class="file-name">{{ job.name }}</span>
          <span><i class="format-pill">{{ job.format }}</i></span>
          <span><i class="format-pill target-format">{{ job.targetFormat || '—' }}</i></span>
          <span>{{ bytes(job.originalSize) }}</span>
          <span>{{ dimensions(job.originalWidth, job.originalHeight) }}</span>
          <span>{{ dimensions(job.targetWidth, job.targetHeight) }}</span>
          <span class="estimate-value">{{ bytes(job.estimatedSize) }}</span>
          <span class="status-cell">
            <span>{{ job.status }}</span>
            <button
              v-if="store.routesValid && job.routeNodeIds?.length"
              class="route-button"
              :class="{ active: store.highlightedJobId === job.id }"
              :title="store.highlightedJobId === job.id ? '取消显示工作流路径' : '显示这张图片的工作流路径'"
              :aria-label="store.highlightedJobId === job.id ? '取消显示工作流路径' : '显示工作流路径'"
              :disabled="busy"
              @click.stop="store.showJobRoute(job.id)"
            >
              <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="4" cy="14.5" r="1.7"/><circle cx="10" cy="6" r="1.7"/><circle cx="16" cy="11" r="1.7"/><path d="M5.2 13.1 8.8 7.4M11.5 7.1l3.1 2.7"/></svg>
            </button>
          </span>
        </div>
      </div>
    </div>
  </div>
</template>
