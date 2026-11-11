import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router'
import { api, type ScannedMatch } from '../api'
import SteamAccounts from '../components/SteamAccounts'
import { formatClock, formatDate, highlightLabel, useHeroNames, useMe } from '../format'

/**
 * "Best first" ranking: confirmed multikills, then matches not scored yet (by kills, which history already gives),
 * then matches whose metadata showed none.
 */
const tier = (m: ScannedMatch) => ((m.score ?? 0) > 0 ? 0 : m.metadataStatus === 'Pending' ? 1 : 2)

/** Valve's replay lifetime (replay_valid_through is exactly match start + 21 days). */
const REPLAY_DAYS = 21

const isBusy = (m: ScannedMatch) =>
  m.metadataStatus === 'Pending' ||
  m.demoStatus === 'Downloading' ||
  m.demoStatus === 'Queued' ||
  m.demoStatus === 'Parsing'

export default function MatchesPage() {
  const [me, setMe] = useMe()
  const status = useQuery({ queryKey: ['steam-status'], queryFn: api.steamStatus, refetchInterval: 5_000 })
  // "Me" lives in this browser only (and the roster ☆ can toggle it off); the server knows whose history it scanned.
  const scanned = useQuery({ queryKey: ['scanned-players'], queryFn: api.scannedPlayers, enabled: !me })
  useEffect(() => {
    if (!me && scanned.data?.length === 1) setMe(scanned.data[0])
  }, [me, scanned.data, setMe])

  return (
    <div className="stack">
      <SteamAccounts status={status.data} />

      <section className="card">
        <h2>Your account</h2>
        <SteamIdForm key={me ?? ''} me={me} onSet={setMe} />
        {!me && (scanned.data?.length ?? 0) > 1 && (
          <p className="small">
            Scanned before:{' '}
            {scanned.data!.map((id) => (
              <button key={id} className="button ghost mono" onClick={() => setMe(id)}>
                {id}
              </button>
            ))}
          </p>
        )}
      </section>

      {me && (
        <Matches
          steamId={me}
          canScan={(status.data?.accounts?.length ?? 0) > 0}
          pausedUntil={status.data?.scanPausedUntil ?? null}
        />
      )}
    </div>
  )
}

function SteamIdForm({ me, onSet }: { me: string | null; onSet: (id: string | null) => void }) {
  const [value, setValue] = useState(me ?? '')
  const valid = /^7656\d{13}$/.test(value.trim())
  if (me && value === me) {
    return (
      <p className="muted small">
        Your SteamID: <span className="mono">{me}</span>{' '}
        <button className="button ghost" onClick={() => setValue('')}>
          Change
        </button>
      </p>
    )
  }
  return (
    <form
      className="inline-form"
      onSubmit={(e) => {
        e.preventDefault()
        if (valid) onSet(value.trim())
      }}
    >
      <label htmlFor="steamid" className="small">
        Your SteamID64
      </label>
      <input
        id="steamid"
        className="text-input mono"
        placeholder="7656119…"
        value={value}
        onChange={(e) => setValue(e.target.value)}
      />
      <button className="button" disabled={!valid}>
        Save
      </button>
    </form>
  )
}

function Matches({
  steamId,
  canScan,
  pausedUntil,
}: {
  steamId: string
  canScan: boolean
  pausedUntil: string | null
}) {
  const queryClient = useQueryClient()
  const heroName = useHeroNames()
  const [sort, setSort] = useState<'best' | 'newest'>('best')
  const matches = useQuery({
    queryKey: ['matches', steamId],
    queryFn: () => api.matches(steamId),
    refetchInterval: (q) => (q.state.data?.some(isBusy) ? 2000 : false),
  })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['matches', steamId] })
  const [reachedEnd, setReachedEnd] = useState(false)
  const scan = useMutation({
    mutationFn: (older: boolean) => api.scanMatches(steamId, older),
    onSuccess: (data, older) => {
      queryClient.setQueryData(['matches', steamId], data.matches)
      // A newest-first scan that stopped at known matches says nothing about how far back the history goes.
      if (older || data.reachedEnd) setReachedEnd(data.reachedEnd)
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['steam-status'] }),
  })
  const download = useMutation({ mutationFn: api.downloadMatch, onSuccess: refresh })

  const rows = useMemo(() => {
    const list = [...(matches.data ?? [])]
    if (sort === 'best') list.sort((a, b) => tier(a) - tier(b) || (b.score ?? 0) - (a.score ?? 0) || b.kills - a.kills)
    return list
  }, [matches.data, sort])
  const pending = matches.data?.filter((m) => m.metadataStatus === 'Pending').length ?? 0
  // Valve keeps replays 21 days; nothing older can be clipped, so there's no point loading it.
  const oldest = matches.data?.reduce((min, m) => Math.min(min, new Date(m.startTime).getTime()), Infinity) ?? Infinity
  const pastReplayWindow = oldest < Date.now() - REPLAY_DAYS * 86_400_000

  return (
    <section className="card">
      <div className="section-head">
        <h2>Recent matches</h2>
        <div className="head-actions">
          <label className="toggle">
            <input type="checkbox" checked={sort === 'best'} onChange={(e) => setSort(e.target.checked ? 'best' : 'newest')} />
            Best first
          </label>
          <button className="button" disabled={!canScan || scan.isPending} onClick={() => scan.mutate(false)}>
            {scan.isPending && !scan.variables ? 'Scanning…' : 'Scan match history'}
          </button>
        </div>
      </div>
      {scan.error && <p className="error small">{(scan.error as Error).message}</p>}
      {download.error && <p className="error small">{(download.error as Error).message}</p>}
      {pending > 0 && (
        <p className="muted small">
          {pausedUntil
            ? `All accounts are rate-limited by Valve: ${pending} matches left, resuming at ${new Date(pausedUntil).toLocaleTimeString()}.`
            : `Reading match data from Valve, one match per account every couple of minutes… ${pending} to go.`}
        </p>
      )}
      {matches.data?.length === 0 && (
        <p className="muted">No matches yet. Scan your match history to find the ones worth clipping.</p>
      )}
      {rows.length > 0 && (
        <div className="table-scroll">
          <table className="table">
            <thead>
              <tr>
                <th>Match</th>
                <th>Hero</th>
                <th className="num">K / D / A</th>
                <th>Highlights</th>
                <th className="num">Score</th>
                <th>Replay</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((m) => (
                <MatchRow key={m.matchId} m={m} heroName={heroName} onDownload={() => download.mutate(m.matchId)} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      {rows.length > 0 && !reachedEnd && !pastReplayWindow && (
        <div className="load-more">
          <button className="button ghost" disabled={!canScan || scan.isPending} onClick={() => scan.mutate(true)}>
            {scan.isPending && scan.variables ? 'Loading…' : `Load older matches (${rows.length} so far)`}
          </button>
          <span className="muted small">Replays expire {REPLAY_DAYS} days after a match.</span>
        </div>
      )}
      {(reachedEnd || pastReplayWindow) && (
        <p className="muted small">
          {pastReplayWindow
            ? `That covers every match from the last ${REPLAY_DAYS} days; Valve deletes replays after that.`
            : "That's the whole match history."}
        </p>
      )}
    </section>
  )
}

function MatchRow({
  m,
  heroName,
  onDownload,
}: {
  m: ScannedMatch
  heroName: (id: number) => string
  onDownload: () => void
}) {
  const expired =
    m.metadataStatus === 'Unavailable' || (m.replayValidThrough != null && new Date(m.replayValidThrough) < new Date())
  return (
    <tr>
      <td>
        <div className="mono">{m.matchId}</div>
        <div className="muted small">
          {formatDate(m.startTime)} · {formatClock(m.durationSeconds)}
        </div>
      </td>
      <td>{heroName(m.heroId)}</td>
      <td className="num mono">
        {m.kills} / {m.deaths} / {m.assists}
      </td>
      <td>
        {m.metadataStatus === 'Pending' && <span className="muted small">…</span>}
        {m.metadataStatus === 'Failed' && (
          <span className="error small" title={m.error ?? undefined}>
            Couldn't read
          </span>
        )}
        {m.metadataStatus === 'Done' && m.highlights.length === 0 && <span className="muted small">–</span>}
        {m.highlights.length > 0 && (
          <div className="hl-chips">
            {m.highlights.map((h) => (
              <span key={h.timeSeconds} className={`badge hl-${h.type.toLowerCase()}`} title={`${h.kills} kills`}>
                {highlightLabel[h.type]} {formatClock(h.timeSeconds)}
              </span>
            ))}
          </div>
        )}
      </td>
      <td className="num mono score">{m.score != null && m.score > 0 ? m.score.toFixed(0) : ''}</td>
      <td>
        <ReplayCell m={m} expired={expired} onDownload={onDownload} />
      </td>
    </tr>
  )
}

function ReplayCell({ m, expired, onDownload }: { m: ScannedMatch; expired: boolean; onDownload: () => void }) {
  if (m.demoId && m.demoStatus !== 'Failed') {
    return (
      <Link to={`/demos/${m.demoId}`} className={m.demoStatus === 'Done' ? 'button ghost' : `badge ${m.demoStatus?.toLowerCase()}`}>
        {m.demoStatus === 'Done' ? 'Open' : m.demoStatus}
      </Link>
    )
  }
  if (expired) return <span className="muted small">Expired</span>
  const daysLeft = m.replayValidThrough
    ? Math.max(0, Math.floor((new Date(m.replayValidThrough).getTime() - Date.now()) / 86_400_000))
    : null
  return (
    <div>
      <button className="button ghost" onClick={onDownload} title="Downloads the full replay (200-400 MB) so it can be rendered">
        {m.demoStatus === 'Failed' ? 'Retry demo download' : 'Get demo'}
      </button>
      {daysLeft != null && <div className="muted small">{daysLeft}d left</div>}
    </div>
  )
}
