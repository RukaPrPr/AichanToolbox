import { defineStore } from 'pinia'
import { createSettings, defaultWorkflow, makeNode, nodeMeta } from './defaults'
import type { ArchiveJob, FileJob, NodeType, WorkflowConnection, WorkflowDocument, WorkflowNode, WorkSummary } from './types'

export const useAppStore = defineStore('app', {
  state: () => ({
    workflow: defaultWorkflow(),
    jobs: [] as FileJob[],
    archives: [] as ArchiveJob[],
    archivePasswords: {} as Record<string, string>,
    selectedNodeIds: [] as string[],
    highlightedJobId: null as string | null,
    routesValid: false,
    busy: false,
    workMode: '' as '' | 'estimate' | 'run' | 'preprocess',
    workStage: '' as '' | 'preprocess' | 'images' | 'postprocess' | 'cleanup' | 'complete',
    progress: 0,
    progressTotal: 0,
    status: '准备就绪',
    version: '7.6.4',
    processorCount: 0
  }),
  getters: {
    selectedCount: state => state.jobs.filter(job => job.checked).length,
    estimatedTotal: state => state.jobs.filter(job => job.checked).reduce((sum, job) => sum + (job.estimatedSize ?? 0), 0),
    archiveCount: state => state.archives.length,
    allChecked: state => state.jobs.length > 0 && state.jobs.every(job => job.checked),
    highlightedNodeIds: state => state.jobs.find(job => job.id === state.highlightedJobId)?.routeNodeIds ?? [],
    highlightedConnectionIds: state => state.jobs.find(job => job.id === state.highlightedJobId)?.routeConnectionIds ?? []
  },
  actions: {
    replaceWorkflow(workflow: WorkflowDocument) {
      workflow.nodes.forEach(node => {
        node.data = { ...createSettings(node.type), ...node.data }
        node.title = nodeMeta[node.type].title
        if (node.type !== 'Import') node.height = nodeMeta[node.type].height
        const legacyData = node.data as typeof node.data & { inspectOriginal?: boolean }
        delete legacyData.inspectOriginal
        if (!['>=', '>', '<=', '<'].includes(node.data.sizeOperator)) node.data.sizeOperator = '>='
        if (!['>=', '>', '<=', '<'].includes(node.data.widthOperator)) node.data.widthOperator = '>='
        if (!['>=', '>', '<=', '<'].includes(node.data.heightOperator)) node.data.heightOperator = '>='
        node.data.resolutionJoin = String(node.data.resolutionJoin).toUpperCase() === 'OR' ? 'OR' : 'AND'
        node.data.archiveEncoding = ['utf8', 'gb18030', 'cp932'].includes(node.data.archiveEncoding) ? node.data.archiveEncoding : 'auto'
        node.data.preserveNonImageFiles = node.data.preserveNonImageFiles !== false
        node.data.replaceSourceArchive = Boolean(node.data.replaceSourceArchive)
      })
      workflow.parallelism = Math.min(16, Math.max(1, Math.round(Number(workflow.parallelism) || 1)))
      workflow.autoGrayscale = workflow.autoGrayscale !== false
      workflow.version = 9
      this.workflow = workflow
      this.selectedNodeIds = []
      this.invalidateRoutes()
      this.status = '工作流已加载'
    },
    addNode(type: NodeType, x = 320, y = 180) {
      if (type === 'Import' && this.workflow.nodes.some(node => node.type === 'Import')) {
        this.status = '工作流中只能有一个导入节点'
        return
      }
      const offset = this.workflow.nodes.length * 24
      const node = makeNode(type, x + offset, y + offset)
      this.workflow.nodes.push(node)
      this.selectedNodeIds = [node.id]
      this.invalidateRoutes()
      this.status = `已添加“${node.title}”节点`
    },
    removeNode(id: string) {
      this.workflow.nodes = this.workflow.nodes.filter(node => node.id !== id)
      this.workflow.connections = this.workflow.connections.filter(connection => connection.fromNodeId !== id && connection.toNodeId !== id)
      this.archives = this.archives.filter(archive => archive.nodeId !== id)
      delete this.archivePasswords[id]
      this.selectedNodeIds = this.selectedNodeIds.filter(value => value !== id)
      this.invalidateRoutes()
    },
    duplicateNode(source: WorkflowNode) {
      if (source.type === 'Import') {
        this.status = '导入节点只能保留一个，无法复制'
        return
      }
      const node: WorkflowNode = {
        ...source,
        id: crypto.randomUUID().replaceAll('-', ''),
        title: source.title,
        x: source.x + 38,
        y: source.y + 38,
        data: { ...source.data }
      }
      this.workflow.nodes.push(node)
      this.selectedNodeIds = [node.id]
      this.invalidateRoutes()
      this.status = `已复制“${source.title}”节点`
    },
    clearNodes() {
      this.workflow.nodes = []
      this.workflow.connections = []
      this.archives = []
      this.archivePasswords = {}
      this.selectedNodeIds = []
      this.invalidateRoutes()
    },
    connect(connection: WorkflowConnection) {
      this.workflow.connections = this.workflow.connections.filter(value => !(value.fromNodeId === connection.fromNodeId && value.fromPort === connection.fromPort))
      this.workflow.connections.push(connection)
      this.invalidateRoutes()
    },
    clearOutput(nodeId: string, port: string) {
      this.workflow.connections = this.workflow.connections.filter(value => !(value.fromNodeId === nodeId && value.fromPort === port))
      this.invalidateRoutes()
    },
    setJobs(jobs: FileJob[]) {
      this.jobs = jobs
      if (!jobs.length) this.invalidateRoutes()
      else if (this.highlightedJobId && !jobs.some(job => job.id === this.highlightedJobId)) this.clearRouteHighlight()
    },
    setArchives(archives: ArchiveJob[]) {
      this.archives = archives
    },
    setArchivePassword(nodeId: string, password: string) {
      if (password) this.archivePasswords[nodeId] = password
      else delete this.archivePasswords[nodeId]
    },
    updateJob(job: FileJob) {
      const index = this.jobs.findIndex(value => value.id === job.id)
      if (index >= 0) this.jobs[index] = job
      else this.jobs.push(job)
    },
    setWorkState(value: { busy: boolean; mode: 'estimate' | 'run' | 'preprocess'; stage?: 'preprocess' | 'images' | 'postprocess' | 'cleanup' | 'complete'; total?: number; summary?: WorkSummary }) {
      this.busy = value.busy
      this.workMode = value.busy ? value.mode : ''
      this.workStage = value.busy ? (value.stage ?? 'images') : ''
      if (value.busy) {
        this.invalidateRoutes()
        this.progress = 0
        this.progressTotal = value.total ?? 0
        this.status = value.stage === 'preprocess' ? '正在执行 ZIP 解压预处理…' : value.stage === 'postprocess' ? '正在执行 ZIP Store 打包…' : value.stage === 'cleanup' ? '正在安全删除解压文件夹…' : value.mode === 'estimate' ? '正在精确预估并缓存…' : '正在运行图片工作流…'
      } else if (value.summary) {
        this.routesValid = !value.summary.cancelled && value.summary.successes > 0
        this.status = value.mode === 'preprocess'
          ? (value.summary.cancelled ? 'ZIP 解压预处理已取消' : 'ZIP 解压预处理完成')
          : `完成 ${value.summary.successes}/${value.summary.total} · ZIP ${value.summary.packedArchives ?? 0} · 清理 ${value.summary.cleanedExtractionFolders ?? 0} · 不处理 ${value.summary.skipped ?? 0} · 缓存命中 ${value.summary.cacheHits} · 失败 ${(value.summary.failures ?? 0) + (value.summary.archiveFailures ?? 0)}`
      }
    },
    setProgress(value: { completed: number; total: number }) {
      this.progress = value.completed
      this.progressTotal = value.total
    },
    selectNode(id: string, append = false) {
      if (append) {
        this.selectedNodeIds = this.selectedNodeIds.includes(id)
          ? this.selectedNodeIds.filter(value => value !== id)
          : [...this.selectedNodeIds, id]
      } else this.selectedNodeIds = [id]
    },
    updateNodePosition(id: string, x: number, y: number) {
      const node = this.workflow.nodes.find(value => value.id === id)
      if (node) { node.x = x; node.y = y; this.invalidateRoutes() }
    },
    updateNodeSize(id: string, width: number, height: number) {
      const node = this.workflow.nodes.find(value => value.id === id)
      if (node) { node.width = width; node.height = height; this.invalidateRoutes() }
    },
    showJobRoute(id: string) {
      if (!this.routesValid) return
      const job = this.jobs.find(value => value.id === id)
      if (!job?.routeNodeIds?.length) return
      this.highlightedJobId = this.highlightedJobId === id ? null : id
    },
    clearRouteHighlight() {
      this.highlightedJobId = null
    },
    invalidateRoutes() {
      this.highlightedJobId = null
      this.routesValid = false
    }
  }
})
