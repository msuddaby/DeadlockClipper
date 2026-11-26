import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import { api } from '../api'
import { MatchRowView } from '../components/MatchRowView'
import { Empty } from '../components/ui'
import { useMe } from '../format'
import { REPLAY_DAYS, useMatchRows, useMyName, type MatchRow } from '../matches'

type Sort = 'recent' | 'best'
type Filter = 'all' | 'clippable' | 'ready'

/** Best first: confirmed highlights, then not-yet-scored (by kills), then nothing to clip. */
const tier = (r: MatchRow) => ((r.score ?? 0) > 0 || r.highlights.length > 0 ? 0 : r.score == null ? 1 : 2)

/** Remounts per player, so a scan in flight or "reached the end" doesn't carry over to the next one. */
export default function MatchesPage() {
  const [me] = useMe()
  return <PlayerMatches key={me ?? ''} me={me} />
}

function PlayerMatches({ me }: { me: string | null }) {
  const queryClient = useQueryClient()
  const { rows, isLoading } = useMatchRows(me)
  const name = useMyName(me)
  const [sort, setSort] = useState<Sort>('best')
  const [filter, setFilter] = useState<Filter>('all')
  const [reachedEnd, setReachedEnd] = useState(false)
  const status = useQuery({ queryKey: ['steam-status'], queryFn: api.steamStatus, refetchInterval: 10_000 })
  const scan = useMutation({
    mutationFn: (older: boolean) => api.scanMatches(me!, older),
    onSuccess: (data, older) => {
      queryClient.setQueryData(['matches', me], data.matches)
      if (older || data.reachedEnd) setReachedEnd(data.reachedEnd)
    },
  })

  const shown = useMemo(() => {
    let list = rows
    if (filter === 'clippable') list = list.filter((r) => r.status !== 'expired' && ((r.score ?? 0) > 0 || r.highlights.length > 0))
    if (filter === 'ready') list = list.filter((r) => r.status === 'ready')
    if (sort === 'best')
      list = [...list].sort((a, b) => tier(a) - tier(b) || (b.score ?? 0) - (a.score ?? 0) || b.kills - a.kills)
    return list
  }, [rows, sort, filter])

  const pending = rows.filter((r) => r.scan?.metadataStatus === 'Pending').length
  const paused = status.data?.scanPausedUntil
  const hasAccounts = (status.data?.accounts?.length ?? 0) > 0
  const oldest = rows.reduce((min, r) => Math.min(min, new Date(r.playedAt).getTime()), Infinity)
  const pastWindow = oldest < Date.now() - REPLAY_DAYS * 86_400_000

  if (!me)
    return (
      <div className="card">
        <Empty title="Whose matches?">
          <p>Add yourself or a friend in Settings to see their matches.</p>
          <Link className="button" to="/settings#players">
            Open settings
          </Link>
        </Empty>
      </div>
    )

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>{name ? `${name}'s matches` : 'Matches'}</h1>
          <p className="muted">Match history, scored for clip potential before anything is downloaded.</p>
        </div>
        <div className="head-actions">
          <div className="segmented">
            {(['all', 'clippable', 'ready'] as Filter[]).map((f) => (
              <button key={f} className={filter === f ? 'on' : ''} onClick={() => setFilter(f)}>
                {f === 'all' ? 'All' : f === 'clippable' ? 'Has highlights' : 'Downloaded'}
              </button>
            ))}
          </div>
          <div className="segmented">
            {(['best', 'recent'] as Sort[]).map((s) => (
              <button key={s} className={sort === s ? 'on' : ''} onClick={() => setSort(s)}>
                {s === 'best' ? 'Best' : 'Recent'}
              </button>
            ))}
          </div>
          <button className="button" disabled={!hasAccounts || scan.isPending} onClick={() => scan.mutate(false)}>
            {scan.isPending && !scan.variables ? 'Scanning…' : 'Scan history'}
          </button>
        </div>
      </div>

      {!hasAccounts && status.data && (
        <div className="card banner small">
          Add a clipper Steam account in <Link to="/settings#accounts">Settings</Link> to scan match histories.
        </div>
      )}
      {scan.error && <p className="error small">{(scan.error as Error).message}</p>}

      <div className="card flush">
        {pending > 0 && (
          <p className="muted small" style={{ padding: '12px 18px' }}>
            {paused
              ? `Valve's rate limit: ${pending} matches waiting, resuming at ${new Date(paused).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}.`
              : `Reading match data from Valve… ${pending} to go.`}
          </p>
        )}
        {isLoading && <p className="muted" style={{ padding: 18 }}>Loading…</p>}
        {!isLoading && shown.length === 0 && (
          <Empty title={rows.length === 0 ? 'No matches yet' : 'Nothing matches this filter'}>
            {rows.length === 0 && <p>Scan the history, or import a replay in Settings.</p>}
          </Empty>
        )}
        {shown.map((r) => (
          <MatchRowView key={r.key} row={r} />
        ))}
        {rows.length > 0 && (
          <div className="list-foot">
            {!reachedEnd && !pastWindow ? (
              <button className="button ghost sm" disabled={!hasAccounts || scan.isPending} onClick={() => scan.mutate(true)}>
                {scan.isPending && scan.variables ? 'Loading…' : 'Load older matches'}
              </button>
            ) : null}
            <span className="faint small">
              {pastWindow || reachedEnd
                ? `Covers the last ${REPLAY_DAYS} days; Valve deletes replays after that.`
                : `Replays expire ${REPLAY_DAYS} days after a match.`}
            </span>
          </div>
        )}
      </div>
    </div>
  )
}
