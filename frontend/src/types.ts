export type NodeType =
  | 'ZipExtract'
  | 'Import'
  | 'FormatFilter'
  | 'SizeFilter'
  | 'ResolutionFilter'
  | 'ConvertJpg'
  | 'Resize'
  | 'Descreen'
  | 'Quality'
  | 'TargetSize'
  | 'Output'
  | 'ZipPack'
  | 'DeleteExtracted'

export interface NodeSettings {
  sizeOperator: string
  sizeMb: number
  scalePercent: number
  qualityPercent: number
  targetSizeMb: number
  targetStartQuality: number
  targetQualitySpan: number
  targetMinimumQuality: number
  targetKeepSmallestOnUnmet: boolean
  descreenLevel: number
  widthEnabled: boolean
  heightEnabled: boolean
  widthOperator: string
  heightOperator: string
  widthValue: number
  heightValue: number
  resolutionJoin: 'AND' | 'OR'
  sameFolder: boolean
  outputDirectory: string
  replaceOriginal: boolean
  archiveEncoding: 'auto' | 'utf8' | 'gb18030' | 'cp932'
  preserveNonImageFiles: boolean
  replaceSourceArchive: boolean
}

export interface WorkflowNode {
  id: string
  type: NodeType
  title: string
  x: number
  y: number
  width: number
  height: number
  data: NodeSettings
}

export interface WorkflowConnection {
  id: string
  fromNodeId: string
  fromPort: string
  toNodeId: string
  toPort: string
}

export interface ViewportState {
  x: number
  y: number
  zoom: number
}

export interface WorkflowDocument {
  version: number
  parallelism: number
  autoGrayscale: boolean
  cacheEstimates: boolean
  viewport: ViewportState
  nodes: WorkflowNode[]
  connections: WorkflowConnection[]
}

export interface FileJob {
  id: string
  sourcePath: string
  name: string
  format: string
  targetFormat: string
  originalSize: number
  originalWidth: number
  originalHeight: number
  targetWidth: number
  targetHeight: number
  estimatedSize: number | null
  status: string
  checked: boolean
  outputPath?: string | null
  routeNodeIds: string[]
  routeConnectionIds: string[]
}

export interface ArchiveJob {
  id: string
  nodeId: string
  sourcePath: string
  name: string
  size: number
  status: string
  progress: number
  entryCount: number
  imageCount: number
  outputDirectory: string
}

export interface StartupSnapshot {
  version: string
  theme?: string
  processorCount: number
  jobs: FileJob[]
  archives: ArchiveJob[]
  maximized: boolean
  profiles: string[]
  selectedProfile: string
  workflow: WorkflowDocument | null
}

export type PortKind = 'image' | 'batch'

export interface PortDefinition {
  id: string
  label: string
  color: string
  kind: PortKind
}

export interface WorkSummary {
  total: number
  successes: number
  failures: number
  cacheHits: number
  replaced: number
  skipped: number
  packedArchives: number
  replacedArchives: number
  archiveFailures: number
  cleanedExtractionFolders: number
  cancelled?: boolean
}

export interface ReplacedSourceConfirmation {
  proceed: boolean
  prompted: boolean
  count: number
  unavailable?: boolean
  message?: string
  ids?: string[]
}

export interface ReplacedArchiveConfirmation {
  proceed: boolean
  prompted: boolean
  count: number
  unavailable?: boolean
  message?: string
  ids?: string[]
}
