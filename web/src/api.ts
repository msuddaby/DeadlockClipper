// Mirrors the .NET DTOs. Steam IDs are strings (64-bit values exceed JS number precision).

export type DemoSource = 'Upload' | 'LocalReplay' | 'MatchId'
export type DemoStatus = 'Queued' | 'Parsing' | 'Done' | 'Failed'
export type Team = 'Unknown' | 'Spectator' | 'Amber' | 'Sapphire'
export type HighlightType = 'DoubleKill' | 'TripleKill' | 'QuadKill' | 'PentaKill' | 'MegaKill'

export interface DemoSummary {
  id: string
  fileName: string
  source: DemoSource
  sizeBytes: number
  matchId: number | null
  status: DemoStatus
  progress: number | null
  error: string | null
  map: string | null
  durationSeconds: number | null
  highlightCount: number | null
  createdAt: string
  parsedAt: string | null
}

export interface PlayerInfo {
  steamId: string
  name: string
  team: Team
  heroId: number
  kills: number
  deaths: number
  assists: number
  netWorth: number
}

export interface KillEvent {
  tick: number
  gameTimeSeconds: number
  attackerSteamId: string | null
  attackerName: string | null
  attackerHeroId: number | null
  victimSteamId: string
  victimName: string
  victimHeroId: number
  weapon: string
  assisterSteamIds: string[]
}

export interface Highlight {
  type: HighlightType
  playerSteamId: string
  playerName: string
  heroId: number
  startTick: number
  endTick: number
  startTimeSeconds: number
  endTimeSeconds: number
  clipStartTick: number
  clipEndTick: number
  score: number
  kills: KillEvent[]
}

export interface MatchAnalysis {
  matchId: number | null
  map: string
  buildNumber: number
  tickRate: number
  totalTicks: number
  durationSeconds: number
  players: PlayerInfo[]
  kills: KillEvent[]
  highlights: Highlight[]
}

export interface DemoDetail {
  demo: DemoSummary
  analysis: MatchAnalysis | null
}

export interface LocalReplay {
  fileName: string
  sizeBytes: number
  modifiedAt: string
  importedDemoId: string | null
}

export interface LocalReplays {
  replaysDir: string | null
  exists: boolean
  replays: LocalReplay[]
}

export interface HeroInfo {
  id: number
  name: string
  iconUrl: string | null
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, init)
  if (!res.ok) throw new Error(await errorMessage(res))
  return res.status === 204 ? (undefined as T) : res.json()
}

async function errorMessage(res: Response): Promise<string> {
  try {
    const body = await res.json()
    return body.detail ?? body.title ?? `${res.status} ${res.statusText}`
  } catch {
    return `${res.status} ${res.statusText}`
  }
}

export const api = {
  demos: () => request<DemoSummary[]>('/api/demos'),
  demo: (id: string) => request<DemoDetail>(`/api/demos/${id}`),
  deleteDemo: (id: string) => request<void>(`/api/demos/${id}`, { method: 'DELETE' }),
  reparse: (id: string) => request<DemoSummary>(`/api/demos/${id}/reparse`, { method: 'POST' }),
  localReplays: () => request<LocalReplays>('/api/local-replays'),
  importLocal: (fileName: string) =>
    request<DemoSummary>('/api/demos/import-local', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ fileName }),
    }),
  heroes: () => request<HeroInfo[]>('/api/heroes'),
}

/** fetch() can't report upload progress, so uploads use XHR. */
export function uploadDemo(file: File, onProgress: (fraction: number) => void): Promise<DemoSummary> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('POST', '/api/demos')
    xhr.responseType = 'json'
    xhr.upload.onprogress = (e) => e.lengthComputable && onProgress(e.loaded / e.total)
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) resolve(xhr.response)
      else reject(new Error(xhr.response?.detail ?? `Upload failed (${xhr.status})`))
    }
    xhr.onerror = () => reject(new Error('Upload failed (network error)'))
    const form = new FormData()
    form.append('file', file)
    xhr.send(form)
  })
}
