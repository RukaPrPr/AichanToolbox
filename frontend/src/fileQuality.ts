import type { FileJob } from './types'

type FinalQualityJob = Pick<FileJob, 'finalQuality' | 'status'>

export function finalQuality(job: FinalQualityJob): number | null {
  if (job.status === '不处理') return 100
  if (job.status === '已取消' || job.status.startsWith('失败')) return null
  if (job.finalQuality == null || !Number.isFinite(job.finalQuality)) return null
  return Math.min(100, Math.max(1, Math.round(job.finalQuality)))
}

export function formatFinalQuality(job: FinalQualityJob): string {
  const quality = finalQuality(job)
  return quality == null ? '—' : String(quality)
}
