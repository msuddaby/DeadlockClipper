import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { api, type DemoOverview, type DemoSummary, type HighlightType } from '../api'
import UploadDropzone from '../components/UploadDropzone'
import { formatBytes, formatClock, formatDate, highlightLabel, teamName, useHeroNames, useMe } from '../format'

const isBusy = (d: DemoSummary) => d.status === 'Downloading' || d.status === 'Queued' || d.status === 'Parsing'

export default function DemosPage() {
  const queryClient = useQueryClient()
  const demos = useQuery({
    queryKey: ['demos'],
    queryFn: api.demos,
    refetchInterval: (q) => (q.state.data?.some(isBusy) ? 1000 : false),
  })
  const local = useQuery({ queryKey: ['local-replays'], queryFn: api.localReplays })

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['demos'] })
    queryClient.invalidateQueries({ queryKey: ['local-replays'] })
  }
  const importLocal = useMutation({ mutationFn: api.importLocal, onSuccess: refresh })
  const remove = useMutation({ mutationFn: api.deleteDemo, onSuccess: refresh })
  const reparse = useMutation({ mutationFn: api.reparse, onSuccess: refresh })
  const heroName = useHeroNames()
  const [me] = useMe()
  const cancelDownload = useMutation({ mutationFn: api.cancelDownload, onSuccess: refresh })

  return (
    <div className="stack">
      <section className="grid-2">
        <div className="card">
          <h2>Upload</h2>
          <UploadDropzone />
        </div>

        <div className="card">
          <h2>Local replays</h2>
          {local.isLoading && <p className="muted">Loading…</p>}
          {local.error && <p className="error">{(local.error as Error).message}</p>}
          {local.data && !local.data.exists && (
            <p className="muted">
              Replays folder not found{local.data.replaysDir ? <> at <code>{local.data.replaysDir}</code></> : null}. Set{' '}
              <code>Clipper:ReplaysDir</code> in appsettings.json.
            </p>
          )}
          {local.data?.exists && local.data.replays.length === 0 && (
            <p className="muted">No replays downloaded yet. Open a match in-game to download its replay.</p>
          )}
          {local.data?.exists && local.data.replays.length > 0 && (
            <ul className="list">
              {local.data.replays.map((r) => (
                <li key={r.fileName} className="list-row">
                  <div>
                    <div className="mono">{r.fileName}</div>
                    <div className="muted small">
                      {formatBytes(r.sizeBytes)} · {formatDate(r.modifiedAt)}
                    </div>
                  </div>
                  {r.importedDemoId ? (
                    <Link to={`/demos/${r.importedDemoId}`} className="button ghost">
                      View
                    </Link>
                  ) : (
                    <button
                      className="button"
                      disabled={importLocal.isPending}
                      onClick={() => importLocal.mutate(r.fileName)}
                    >
                      Import
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
          {importLocal.error && <p className="error">{(importLocal.error as Error).message}</p>}
        </div>
      </section>

      <section className="card">
        <h2>Demos</h2>
        {demos.isLoading && <p className="muted">Loading…</p>}
        {demos.error && <p className="error">{(demos.error as Error).message}</p>}
        {demos.data?.length === 0 && <p className="muted">Nothing here yet. Upload or import a demo.</p>}
        {demos.data && demos.data.length > 0 && (
          <table className="table">
            <thead>
              <tr>
                <th>Match</th>
                <th>Result</th>
                <th>{me ? 'You' : 'Players'}</th>
                <th>{me ? 'Your highlights' : 'Highlights'}</th>
                <th>Length</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {demos.data.map((d) => (
                <tr key={d.id}>
                  <td>
                    <Link to={`/demos/${d.id}`} className="mono">
                      {d.matchId ? `Match ${d.matchId}` : d.fileName}
                    </Link>
                    <div className="muted small">
                      {d.playedAt ? `played ${formatDate(d.playedAt)}` : `added ${formatDate(d.createdAt)}`} ·{' '}
                      {d.source === 'LocalReplay' ? 'local replay' : d.source === 'MatchId' ? 'downloaded' : 'uploaded'} ·{' '}
                      {formatBytes(d.sizeBytes)}
                    </div>
                  </td>
                  <td>
                    {d.overview && d.status === 'Done' ? <ResultCell o={d.overview} me={me} /> : <StatusCell demo={d} />}
                  </td>
                  <td>{d.overview ? <MeCell o={d.overview} me={me} heroName={heroName} /> : <span className="muted">–</span>}</td>
                  <td>{d.overview ? <HighlightsCell o={d.overview} me={me} /> : <span className="muted">–</span>}</td>
                  <td className="mono">{d.durationSeconds != null ? formatClock(d.durationSeconds) : '–'}</td>
                  <td className="actions">
                    {d.status === 'Downloading' && (
                      <button className="button ghost danger" onClick={() => cancelDownload.mutate(d.id)}>
                        Cancel
                      </button>
                    )}
                    {(d.status === 'Failed' || d.status === 'Done') && d.sizeBytes > 0 && (
                      <button className="button ghost" onClick={() => reparse.mutate(d.id)}>
                        Re-parse
                      </button>
                    )}
                    <button className="button ghost danger" onClick={() => remove.mutate(d.id)}>
                      Remove
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  )
}

function StatusCell({ demo }: { demo: DemoSummary }) {
  if (isBusy(demo)) {
    return (
      <div className="status-progress">
        <span className={`badge ${demo.status.toLowerCase()}`}>{demo.status}</span>
        <progress value={demo.progress ?? 0} max={1} />
        <span className="small muted">{Math.round((demo.progress ?? 0) * 100)}%</span>
        {demo.error && <span className="small muted">{demo.error}</span>}
      </div>
    )
  }
  return (
    <div>
      <span className={`badge ${demo.status.toLowerCase()}`}>{demo.status}</span>
      {demo.error && (
        <div className="error small" title={demo.error}>
          {demo.error}
        </div>
      )}
    </div>
  )
}

const short: Record<HighlightType, string> = {
  ...highlightLabel,
  KillCompilation: 'Compilation',
  TakedownStreak: 'Takedowns',
}

function ResultCell({ o, me }: { o: DemoOverview; me: string | null }) {
  const mine = o.players.find((p) => p.steamId === me)
  if (o.winningTeam === 'Unknown') return <span className="muted small">No result</span>
  if (mine) {
    const won = mine.team === o.winningTeam
    return (
      <div>
        <span className={`badge ${won ? 'done' : 'failed'}`}>{won ? 'Win' : 'Loss'}</span>
        <div className={`small team-${mine.team.toLowerCase()}`}>{teamName(mine.team)}</div>
      </div>
    )
  }
  return <span className={`small team-${o.winningTeam.toLowerCase()}`}>{teamName(o.winningTeam)} won</span>
}

function MeCell({ o, me, heroName }: { o: DemoOverview; me: string | null; heroName: (id: number) => string }) {
  const mine = o.players.find((p) => p.steamId === me)
  if (!mine) {
    // Not in this match (or no "me" set): the top fragger says most about the game.
    const top = [...o.players].sort((a, b) => b.kills - a.kills)[0]
    return top ? (
      <span className="small">
        <span className="muted">Top: </span>
        {top.name} ({heroName(top.heroId)}) {top.kills}/{top.deaths}/{top.assists}
      </span>
    ) : null
  }
  const teamKills = o.players.filter((p) => p.team === mine.team).reduce((n, p) => n + p.kills, 0)
  return (
    <div>
      <strong>{heroName(mine.heroId)}</strong>{' '}
      <span className="mono">
        {mine.kills}/{mine.deaths}/{mine.assists}
      </span>
      <div className="muted small">
        {(mine.netWorth / 1000).toFixed(1)}k souls
        {teamKills > 0 && ` · in ${Math.round(((mine.kills + mine.assists) / teamKills) * 100)}% of team kills`}
      </div>
    </div>
  )
}

function HighlightsCell({ o, me }: { o: DemoOverview; me: string | null }) {
  const inMatch = o.players.some((p) => p.steamId === me)
  const list = inMatch
    ? o.highlights.filter((h) => h.playerSteamId === me || (h.type === 'TeamFight' && h.participants.includes(me!)))
    : o.highlights
  if (list.length === 0) return <span className="muted small">none</span>
  const counts = new Map<HighlightType, number>()
  for (const h of list) counts.set(h.type, (counts.get(h.type) ?? 0) + 1)
  return (
    <div className="hl-chips">
      {[...counts.entries()].map(([type, n]) => (
        <span key={type} className={`badge hl-${type.toLowerCase()}`}>
          {n > 1 ? `${n}× ` : ''}
          {short[type]}
        </span>
      ))}
    </div>
  )
}
