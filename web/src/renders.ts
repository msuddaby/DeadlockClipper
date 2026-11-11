import { useQuery } from '@tanstack/react-query'
import { api, type RenderInfo, type RenderStage } from './api'

const isActive = (r: RenderInfo) => r.status === 'Queued' || r.status === 'Rendering'

/** Renders (optionally for one demo), polled every second while any are queued or rendering. */
export function useRenders(demoId?: string) {
  return useQuery({
    queryKey: ['renders', demoId ?? 'all'],
    queryFn: () => api.renders(demoId),
    refetchInterval: (q) => (q.state.data?.some(isActive) ? 1000 : false),
  })
}

export function useRenderHealth() {
  return useQuery({ queryKey: ['render-health'], queryFn: api.renderHealth, staleTime: 30_000 })
}

const stageLabel: Record<RenderStage, string> = {
  Queued: 'Queued',
  Launching: 'Starting game',
  LoadingDemo: 'Loading demo',
  Seeking: 'Seeking',
  Recording: 'Recording',
  Encoding: 'Encoding',
  Done: 'Done',
  Failed: 'Failed',
}

/** Short human status for a render, e.g. "Seeking 3/5" or "Close Deadlock to continue". */
export function renderStatusText(r: RenderInfo): string {
  if (r.status === 'Done') return 'Ready'
  if (r.status === 'Failed') return 'Failed'
  if (r.stage === 'Queued' && r.detail) return r.detail
  if (!r.stage) return r.status === 'Queued' ? 'Queued' : 'Rendering'
  return `${stageLabel[r.stage]}${r.detail ? ` ${r.detail}` : ''}`
}
