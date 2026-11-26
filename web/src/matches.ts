import { useQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import { usePlayers } from './players'
import { api, type DemoStatus, type DemoSummary, type HighlightType, type ScannedMatch, type Team } from './api'

/** Valve keeps replays 21 days (replay_valid_through is exactly match start + 21 days). */
export const REPLAY_DAYS = 21

export type RowStatus = 'ready' | 'downloading' | 'parsing' | 'failed' | 'available' | 'expired' | 'unscanned'

export interface RowHighlight {
  type: HighlightType
  timeSeconds: number
  kills: number
  /** For teamfights from metadata: your kills in it. */
  playerKills?: number | null
}

/** One match, merging what the history scan knows with any demo of it. */
export interface MatchRow {
  key: string
  matchId: number | null
  playedAt: string
  durationSeconds: number | null
  heroId: number
  kills: number
  deaths: number
  assists: number
  result: 'win' | 'loss' | null
  team: Team | null
  score: number | null
  highlights: RowHighlight[]
  status: RowStatus
  demoId: string | null
  demoStatus: DemoStatus | null
  scan: ScannedMatch | null
  validThrough: string | null
}

const demoBusy = (s: DemoStatus | null) => s === 'Downloading' || s === 'Queued' || s === 'Parsing'

function statusOf(scan: ScannedMatch | null, demo: DemoSummary | null): RowStatus {
  if (demo) {
    if (demo.status === 'Done') return 'ready'
    if (demo.status === 'Downloading') return 'downloading'
    if (demo.status === 'Queued' || demo.status === 'Parsing') return 'parsing'
    if (demo.status === 'Failed' && demo.sizeBytes > 0) return 'failed'
  }
  if (!scan) return demo?.status === 'Failed' ? 'failed' : 'unscanned'
  const expired =
    scan.metadataStatus === 'Unavailable' ||
    new Date(scan.startTime).getTime() + REPLAY_DAYS * 86_400_000 < Date.now()
  if (expired) return 'expired'
  return demo?.status === 'Failed' ? 'failed' : 'available'
}

/** Your matches: the scanned history plus demos you added (local replays, uploads), newest first. */
export function useMatchRows(me: string | null) {
  const scanned = useQuery({
    queryKey: ['matches', me],
    queryFn: () => api.matches(me!),
    enabled: !!me,
    refetchInterval: (q) => (q.state.data?.some((m) => m.metadataStatus === 'Pending' || demoBusy(m.demoStatus)) ? 3000 : false),
  })
  const demos = useQuery({
    queryKey: ['demos'],
    queryFn: api.demos,
    refetchInterval: (q) => (q.state.data?.some((d) => demoBusy(d.status)) ? 2000 : false),
  })

  const rows = useMemo<MatchRow[]>(() => {
    const byMatch = new Map<number, DemoSummary>()
    for (const d of demos.data ?? []) if (d.matchId != null && !byMatch.has(d.matchId)) byMatch.set(d.matchId, d)
    const out: MatchRow[] = []
    const seen = new Set<number>()

    for (const m of scanned.data ?? []) {
      seen.add(m.matchId)
      const demo = byMatch.get(m.matchId) ?? null
      out.push(fromScan(m, demo, me))
    }
    for (const d of demos.data ?? []) {
      if (d.matchId != null && seen.has(d.matchId)) continue
      const row = fromDemo(d, me)
      if (row) out.push(row)
    }
    return out.sort((a, b) => b.playedAt.localeCompare(a.playedAt))
  }, [scanned.data, demos.data, me])

  return { rows, isLoading: scanned.isLoading || demos.isLoading, scanned, demos }
}

function mineFromOverview(d: DemoSummary, me: string | null) {
  const o = d.overview
  if (!o || !me) return null
  const p = o.players.find((x) => x.steamId === me)
  if (!p) return null
  const highlights: RowHighlight[] = o.highlights
    .filter((h) => h.playerSteamId === me || (h.type === 'TeamFight' && h.participants.includes(me)))
    .map((h) => ({ type: h.type, timeSeconds: h.timeSeconds, kills: h.kills }))
    .sort((a, b) => a.timeSeconds - b.timeSeconds)
  const result: MatchRow['result'] = o.winningTeam === 'Unknown' ? null : o.winningTeam === p.team ? 'win' : 'loss'
  return { p, highlights, result }
}

function fromScan(m: ScannedMatch, demo: DemoSummary | null, me: string | null): MatchRow {
  const mine = demo ? mineFromOverview(demo, me) : null
  return {
    key: `m${m.matchId}`,
    matchId: m.matchId,
    playedAt: m.startTime,
    durationSeconds: m.durationSeconds,
    heroId: m.heroId,
    kills: m.kills,
    deaths: m.deaths,
    assists: m.assists,
    result: mine?.result ?? null,
    team: mine?.p.team ?? null,
    score: m.metadataStatus === 'Done' ? (m.score ?? 0) : null,
    // The parsed demo knows more (assists, takedowns); otherwise what the metadata scan found.
    highlights: mine
      ? mine.highlights
      : m.highlights.map((h) => ({ type: h.type, timeSeconds: h.timeSeconds, kills: h.kills, playerKills: h.playerKills })),
    status: statusOf(m, demo),
    demoId: demo?.id ?? null,
    demoStatus: demo?.status ?? null,
    scan: m,
    validThrough: m.replayValidThrough,
  }
}

function fromDemo(d: DemoSummary, me: string | null): MatchRow | null {
  const mine = mineFromOverview(d, me)
  if (d.status === 'Done' && !mine && me) return null // a demo you're not in isn't one of "your matches"
  return {
    key: `d${d.id}`,
    matchId: d.matchId,
    playedAt: d.playedAt ?? d.createdAt,
    durationSeconds: d.durationSeconds,
    heroId: mine?.p.heroId ?? 0,
    kills: mine?.p.kills ?? 0,
    deaths: mine?.p.deaths ?? 0,
    assists: mine?.p.assists ?? 0,
    result: mine?.result ?? null,
    team: mine?.p.team ?? null,
    score: null,
    highlights: mine?.highlights ?? [],
    status: statusOf(null, d),
    demoId: d.id,
    demoStatus: d.status,
    scan: null,
    validThrough: null,
  }
}

/**
 * A player's name: as it appears in parsed demos (the history scan only has account ids), else their Steam name from
 * when they were added.
 */
export function useMyName(me: string | null) {
  const demos = useQuery({ queryKey: ['demos'], queryFn: api.demos, enabled: !!me })
  const { players } = usePlayers()
  for (const d of demos.data ?? []) {
    const p = d.overview?.players.find((x) => x.steamId === me)
    if (p) return p.name
  }
  return players.find((p) => p.steamId === me)?.name ?? null
}

/** Display name for any player id: their name if known, else the end of their SteamID. */
export function usePlayerLabel() {
  const demos = useQuery({ queryKey: ['demos'], queryFn: api.demos })
  const { players } = usePlayers()
  return (steamId: string) => {
    for (const d of demos.data ?? []) {
      const p = d.overview?.players.find((x) => x.steamId === steamId)
      if (p) return p.name
    }
    return players.find((p) => p.steamId === steamId)?.name ?? `Player …${steamId.slice(-6)}`
  }
}
