// Mirrors the .NET DTOs. Steam IDs are strings (64-bit values exceed JS number precision).

export type DemoSource = 'Upload' | 'LocalReplay' | 'MatchId'
export type DemoStatus = 'Downloading' | 'Queued' | 'Parsing' | 'Done' | 'Failed'
export type Team = 'Unknown' | 'Spectator' | 'Amber' | 'Sapphire'
export type HighlightType = 'DoubleKill' | 'TripleKill' | 'QuadKill' | 'PentaKill' | 'MegaKill' | 'KillCompilation' | 'TeamFight' | 'TakedownStreak'

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
  overview: DemoOverview | null
  /** When the match was played, if known. */
  playedAt: string | null
}

export interface DemoOverview {
  winningTeam: Team
  heroKills: number
  players: {
    steamId: string
    name: string
    heroId: number
    team: Team
    kills: number
    deaths: number
    assists: number
    netWorth: number
  }[]
  highlights: { type: HighlightType; playerSteamId: string; timeSeconds: number; kills: number; participants: string[] }[]
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
  lane: number
  entityIndex: number
  topBarSlot: number
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
  id: string
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
  /** Set when the clip is several pieces of the demo joined (compilations, teamfights). */
  segments: ClipSegment[] | null
}

export interface ClipSegment {
  startTick: number
  endTick: number
  /** Player the camera follows in this segment. */
  focusSteamId: string | null
}

export interface MatchAnalysis {
  /** 'Unknown' when the demo ends before the Patron falls. */
  winningTeam: Team
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

export type RenderStatus = 'Queued' | 'Rendering' | 'Done' | 'Failed'
export type RenderStage =
  | 'Queued'
  | 'Launching'
  | 'LoadingDemo'
  | 'Seeking'
  | 'Recording'
  | 'Encoding'
  | 'Done'
  | 'Failed'

export interface RenderInfo {
  id: string
  demoId: string
  highlightId: string
  status: RenderStatus
  stage: RenderStage | null
  detail: string | null
  error: string | null
  playerName: string
  highlightType: HighlightType
  heroId: number
  gameTimeSeconds: number
  matchId: number | null
  createdAt: string
  finishedAt: string | null
  hasVideo: boolean
}

export interface HealthCheck {
  id: string
  label: string
  ok: boolean
  fix: string | null
}

export interface RenderHealth {
  ok: boolean
  checks: HealthCheck[]
}

export type MetadataStatus = 'Pending' | 'Done' | 'Unavailable' | 'Failed'

export interface MatchHighlight {
  type: HighlightType
  timeSeconds: number
  kills: number
  score: number
}

export interface ScannedMatch {
  matchId: number
  startTime: string
  durationSeconds: number
  heroId: number
  kills: number
  deaths: number
  assists: number
  netWorth: number
  metadataStatus: MetadataStatus
  error: string | null
  score: number | null
  highlights: MatchHighlight[]
  replayValidThrough: string | null
  demoId: string | null
  demoStatus: DemoStatus | null
}

export interface ScanResult {
  matches: ScannedMatch[]
  /** The history has no matches older than these. */
  reachedEnd: boolean
}

export interface SteamAccount {
  account: string
  connected: boolean
  lastError: string | null
  /** Paced: when this account may make its next metadata request. */
  nextRequestAt: string | null
  /** Set while Valve is refusing this account's metadata requests. */
  rateLimitedUntil: string | null
  requestsMade: number
}

export interface SteamStatus {
  accounts: SteamAccount[]
  /** Every account is rate-limited: metadata reading resumes at this time. */
  scanPausedUntil: string | null
  metadataIntervalSeconds: number
}

export type LoginState = 'Starting' | 'NeedsCode' | 'ConfirmOnPhone' | 'Verifying' | 'Done' | 'Failed'

export interface LoginStatus {
  id: string
  username: string
  state: LoginState
  codeKind: 'authenticator' | 'email' | null
  emailHint: string | null
  previousCodeWrong: boolean
  error: string | null
}

export const videoUrl = (renderId: string) => `/api/renders/${renderId}/video`
/** Same video, but the browser shows the frame at 1s as the poster instead of the (often dark) first frame. */
export const previewUrl = (renderId: string) => `${videoUrl(renderId)}#t=1`

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
  renders: (demoId?: string) => request<RenderInfo[]>(demoId ? `/api/renders?demoId=${demoId}` : '/api/renders'),
  renderHighlight: (demoId: string, highlightId: string) =>
    request<RenderInfo>(`/api/demos/${demoId}/highlights/${encodeURIComponent(highlightId)}/render`, {
      method: 'POST',
    }),
  renderPlayer: (demoId: string, steamId: string) =>
    request<RenderInfo[]>(`/api/demos/${demoId}/render-player`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ steamId }),
    }),
  renderCompilation: (demoId: string, steamId: string) =>
    request<RenderInfo>(`/api/demos/${demoId}/players/${steamId}/compilation`, { method: 'POST' }),
  steamStatus: () => request<SteamStatus>('/api/steam/status'),
  addSteamAccount: (username: string, password: string) =>
    request<LoginStatus>('/api/steam/accounts', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ username, password }),
    }),
  steamLogin: (id: string) => request<LoginStatus>(`/api/steam/logins/${id}`),
  submitSteamCode: (id: string, code: string) =>
    request<LoginStatus>(`/api/steam/logins/${id}/code`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ code }),
    }),
  removeSteamAccount: (name: string) =>
    request<void>(`/api/steam/accounts/${encodeURIComponent(name)}`, { method: 'DELETE' }),
  scannedPlayers: () => request<string[]>('/api/matches/players'),
  matches: (steamId: string) => request<ScannedMatch[]>(`/api/matches?steamId=${steamId}`),
  scanMatches: (steamId: string, older = false) =>
    request<ScanResult>('/api/matches/scan', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ steamId, older }),
    }),
  downloadMatch: (matchId: number) => request<DemoSummary>(`/api/matches/${matchId}/download`, { method: 'POST' }),
  cancelDownload: (demoId: string) => request<DemoSummary>(`/api/demos/${demoId}/cancel-download`, { method: 'POST' }),
  deleteRender: (id: string) => request<void>(`/api/renders/${id}`, { method: 'DELETE' }),
  renderHealth: () => request<RenderHealth>('/api/render/health'),
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
