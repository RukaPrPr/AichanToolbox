import type { NodeSettings, NodeType, PortDefinition, PortKind, WorkflowDocument, WorkflowNode } from './types'

export const nodeMeta: Record<NodeType, { title: string; accent: string; width: number; height: number }> = {
  ZipExtract: { title: 'ZIP 解压', accent: '#ef9f5a', width: 360, height: 430 },
  Import: { title: '导入 / 文件列表', accent: '#70a7ff', width: 600, height: 350 },
  FormatFilter: { title: '格式筛选', accent: '#b993ff', width: 270, height: 220 },
  SizeFilter: { title: '大小筛选', accent: '#ffb768', width: 300, height: 210 },
  ResolutionFilter: { title: '分辨率筛选', accent: '#56d6c0', width: 320, height: 270 },
  ConvertJpg: { title: '转为 JPG', accent: '#ff7ea8', width: 250, height: 230 },
  Resize: { title: '按比例缩放', accent: '#66c8ff', width: 290, height: 260 },
  Descreen: { title: '逆网点化', accent: '#52bfa8', width: 300, height: 275 },
  Quality: { title: 'JPG 画质压缩', accent: '#f58cff', width: 290, height: 260 },
  TargetSize: { title: '目标体积压缩', accent: '#718cf0', width: 320, height: 380 },
  Output: { title: '保存输出', accent: '#7ee787', width: 320, height: 300 },
  ZipPack: { title: 'ZIP 压缩', accent: '#7c8cf2', width: 340, height: 400 },
  DeleteExtracted: { title: '删除解压目录', accent: '#e8738a', width: 320, height: 320 }
}

export function createSettings(type: NodeType): NodeSettings {
  return {
    sizeOperator: '>=',
    sizeMb: type === 'SizeFilter' ? 0.4 : 1,
    scalePercent: 80,
    qualityPercent: 100,
    targetSizeMb: 2,
    targetStartQuality: 90,
    targetQualitySpan: 5,
    targetMinimumQuality: 50,
    targetKeepSmallestOnUnmet: false,
    descreenLevel: 2,
    widthEnabled: true,
    heightEnabled: true,
    widthOperator: '>=',
    heightOperator: '>=',
    widthValue: 1920,
    heightValue: 1080,
    resolutionJoin: 'AND',
    sameFolder: true,
    outputDirectory: '',
    replaceOriginal: false,
    archiveEncoding: 'auto',
    preserveNonImageFiles: true,
    replaceSourceArchive: false
  }
}

export function makeNode(type: NodeType, x: number, y: number): WorkflowNode {
  const meta = nodeMeta[type]
  return {
    id: crypto.randomUUID().replaceAll('-', ''),
    type,
    title: meta.title,
    x,
    y,
    width: meta.width,
    height: meta.height,
    data: createSettings(type)
  }
}

export function outputPorts(type: NodeType): PortDefinition[] {
  switch (type) {
    case 'ZipExtract': return [{ id: 'batch', label: '图片批次', color: '#ef9f5a', kind: 'batch' }]
    case 'Import': return [{ id: 'out', label: '图片', color: '#70a7ff', kind: 'image' }]
    case 'FormatFilter': return [
      { id: 'jpg', label: 'JPG', color: '#fb7185', kind: 'image' },
      { id: 'png', label: 'PNG', color: '#c084fc', kind: 'image' },
      { id: 'webp', label: 'WebP', color: '#38bdf8', kind: 'image' },
      { id: 'other', label: '其他', color: '#94a3b8', kind: 'image' }
    ]
    case 'SizeFilter':
    case 'ResolutionFilter': return [
      { id: 'match', label: '符合', color: '#52d6a5', kind: 'image' },
      { id: 'else', label: '不符合', color: '#ffae62', kind: 'image' }
    ]
    case 'TargetSize': return [
      { id: 'out', label: '达标', color: '#52d6a5', kind: 'image' },
      { id: 'unmet', label: '未达标', color: '#ffae62', kind: 'image' }
    ]
    case 'Output': return [{ id: 'batch', label: '文件批次', color: '#7ee787', kind: 'batch' }]
    case 'ZipPack': return [{ id: 'batch', label: '文件批次', color: '#7c8cf2', kind: 'batch' }]
    case 'DeleteExtracted': return []
    default: return [{ id: 'out', label: '图片', color: nodeMeta[type].accent, kind: 'image' }]
  }
}

export function hasInput(type: NodeType) {
  return type !== 'ZipExtract'
}

export function inputKind(type: NodeType): PortKind | null {
  if (type === 'ZipExtract') return null
  if (type === 'Import' || type === 'ZipPack' || type === 'DeleteExtracted') return 'batch'
  return 'image'
}

export function canConnect(port: PortDefinition, targetType: NodeType) {
  return inputKind(targetType) === port.kind
}

function connect(from: WorkflowNode, fromPort: string, to: WorkflowNode) {
  return {
    id: crypto.randomUUID().replaceAll('-', ''),
    fromNodeId: from.id,
    fromPort,
    toNodeId: to.id,
    toPort: 'in'
  }
}

export function defaultWorkflow(): WorkflowDocument {
  const importNode = makeNode('Import', 50, 90)
  const formatNode = makeNode('FormatFilter', 700, 70)
  const convertNode = makeNode('ConvertJpg', 1040, 60)
  const sizeNode = makeNode('SizeFilter', 1040, 330)
  const qualityNode = makeNode('Quality', 1360, 315)
  const outputNode = makeNode('Output', 1340, 620)

  return {
    version: 9,
    parallelism: 6,
    autoGrayscale: true,
    cacheEstimates: true,
    viewport: { x: 32, y: 28, zoom: 1 },
    nodes: [importNode, formatNode, convertNode, sizeNode, qualityNode, outputNode],
    connections: [
      connect(importNode, 'out', formatNode),
      connect(formatNode, 'png', convertNode),
      connect(formatNode, 'webp', convertNode),
      connect(formatNode, 'other', convertNode),
      connect(formatNode, 'jpg', sizeNode),
      connect(convertNode, 'out', sizeNode),
      connect(sizeNode, 'match', qualityNode),
      connect(sizeNode, 'else', outputNode),
      connect(qualityNode, 'out', outputNode)
    ]
  }
}
