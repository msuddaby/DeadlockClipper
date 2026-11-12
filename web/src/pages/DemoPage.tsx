import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router'
import {
  api,
  previewUrl,
  videoUrl,
  type Highlight,
  type KillEvent,
  type MatchAnalysis,
  type PlayerInfo,
  type RenderInfo,
  type Team,
} from '../api'
import HealthBanner from '../components/HealthBanner'
import { compilationId, formatClock, highlightName, teamName, useHeroNames, useMe } from '../format'
import { renderStatusText, useRenderHealth, useRenders } from '../renders'

export default function DemoPage() {
  const { id } = useParams<{ id: string }>()
  const query = useQuery({
    queryKey: ['demo', id],
    queryFn: () => api.demo(id!),
    enabled: !!id,
    refetchInterval: (q) => {
      const s = q.state.data?.demo.status
      return s === 'Downloading' || s === 'Queued' || s === 'Parsing' ? 1000 : false
    },
  })

  if (query.isLoading) return <p className="muted">Loading…</p>
  if (query.error) return <p className="error">{(query.error as Error).message}</p>
  if (!query.data) return null

  const { demo, analysis } = query.data

  return (
    <div className="stack">
      <Link to="/" className="muted small">
        ← All demos
      </Link>
      <div className="card match-header">
        <div>
          <h1>{demo.matchId ? `Match ${demo.matchId}` : demo.fileName}</h1>
          <div className="muted">
            {analysis ? (
              <>
                {analysis.winningTeam !== 'Unknown' && (
                  <strong className={`team-${analysis.winningTeam.toLowerCase()}`}>{teamName(analysis.winningTeam)} won · </strong>
                )}
                {formatClock(analysis.durationSeconds)} · {analysis.kills.length} hero kills ·{' '}
                {analysis.tickRate.toFixed(0)} tick · build {analysis.buildNumber}
              </>
            ) : (
              demo.fileName
            )}
          </div>
        </div>
        <span className={`badge ${demo.status.toLowerCase()}`}>{demo.status}</span>
      </div>

      {(demo.status === 'Downloading' || demo.status === 'Queued' || demo.status === 'Parsing') && (
        <div className="card">
          <p>
            {demo.status === 'Downloading' ? 'Downloading replay from Valve' : 'Parsing demo'}…{' '}
            {Math.round((demo.progress ?? 0) * 100)}%
          </p>
          {demo.status === 'Downloading' && demo.error && <p className="muted small">{demo.error}</p>}
          {demo.status === 'Downloading' && <DownloadControls demoId={demo.id} matchId={demo.matchId} failed={false} />}
          <progress value={demo.progress ?? 0} max={1} className="wide" />
        </div>
      )}
      {demo.status === 'Failed' && (
        <div className="card">
          <p className="error">
            {demo.error?.startsWith('Download') ? demo.error : `Parsing failed: ${demo.error}`}
          </p>
          {demo.source === 'MatchId' && demo.sizeBytes === 0 && (
            <DownloadControls demoId={demo.id} matchId={demo.matchId} failed />
          )}
        </div>
      )}
      {analysis && demo.status === 'Done' && <HealthBanner />}
      {analysis && <Analysis analysis={analysis} demoId={demo.id} />}
    </div>
  )
}

/** Cancel a running download, or retry one that failed or was cancelled. */
function DownloadControls({ demoId, matchId, failed }: { demoId: string; matchId: number | null; failed: boolean }) {
  const queryClient = useQueryClient()
  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['demo', demoId] })
    queryClient.invalidateQueries({ queryKey: ['demos'] })
    queryClient.invalidateQueries({ queryKey: ['matches'] })
  }
  const cancel = useMutation({ mutationFn: () => api.cancelDownload(demoId), onSuccess: refresh })
  const retry = useMutation({ mutationFn: () => api.downloadMatch(matchId!), onSuccess: refresh })
  const error = (cancel.error ?? retry.error) as Error | null
  return (
    <div className="inline-form">
      {failed ? (
        matchId != null && (
          <button className="button" disabled={retry.isPending} onClick={() => retry.mutate()}>
            Retry download
          </button>
        )
      ) : (
        <button className="button ghost danger" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
          Cancel download
        </button>
      )}
      {error && <span className="error small">{error.message}</span>}
    </div>
  )
}

function Analysis({ analysis, demoId }: { analysis: MatchAnalysis; demoId: string }) {
  const heroName = useHeroNames()
  const [me, setMe] = useMe()
  const meInMatch = analysis.players.some((p) => p.steamId === me)
  const players = useMemo(() => new Map(analysis.players.map((p) => [p.steamId, p])), [analysis.players])
  const myTeam = players.get(me ?? '')?.team
  const [onlyMe, setOnlyMe] = useState(true)
  const filterToMe = onlyMe && meInMatch

  const queryClient = useQueryClient()
  const renders = useRenders(demoId)
  const health = useRenderHealth()
  const canRender = health.data?.ok ?? false
  const rendersByHighlight = useMemo(
    () => new Map((renders.data ?? []).map((r) => [r.highlightId, r])),
    [renders.data],
  )
  const refreshRenders = () => queryClient.invalidateQueries({ queryKey: ['renders'] })
  const renderMine = useMutation({
    mutationFn: () => api.renderPlayer(demoId, me!),
    onSuccess: refreshRenders,
  })
  // A teamfight counts as mine if I killed, died or assisted in it, not only when I topped it.
  const involvesMe = (h: Highlight) =>
    h.playerSteamId === me ||
    (h.type === 'TeamFight' &&
      h.kills.some((k) => k.attackerSteamId === me || k.victimSteamId === me || k.assisterSteamIds.includes(me ?? '')))
  const myHighlights = analysis.highlights.filter((h) => h.playerSteamId === me)
  const myUnrendered = myHighlights.filter((h) => !rendersByHighlight.has(h.id))

  const highlights = filterToMe ? analysis.highlights.filter(involvesMe) : analysis.highlights
  const teams: Team[] = ['Amber', 'Sapphire']

  return (
    <>
      {myTeam && analysis.winningTeam !== 'Unknown' && (
        <div className={`result-banner ${analysis.winningTeam === myTeam ? 'win' : 'loss'}`}>
          {analysis.winningTeam === myTeam ? 'Victory' : 'Defeat'}
          <span className="small"> · you played {heroName(players.get(me!)!.heroId)} for {teamName(myTeam)}</span>
        </div>
      )}

      <section className="grid-2">
        {teams.map((team) => (
          <Roster
            key={team}
            team={team}
            won={analysis.winningTeam === team}
            players={analysis.players.filter((p) => p.team === team)}
            me={me}
            onSetMe={setMe}
            heroName={heroName}
          />
        ))}
      </section>

      <section className="card">
        <div className="section-head">
          <h2>Highlights</h2>
          {meInMatch && (
            <div className="head-actions">
              <label className="toggle">
                <input type="checkbox" checked={onlyMe} onChange={(e) => setOnlyMe(e.target.checked)} />
                Only mine
              </label>
              {myHighlights.length > 0 && (
                <button
                  className="button"
                  disabled={!canRender || renderMine.isPending || myUnrendered.length === 0}
                  title={myUnrendered.length === 0 ? 'All your highlights are rendered or queued' : undefined}
                  onClick={() => renderMine.mutate()}
                >
                  Render all mine{myUnrendered.length > 0 ? ` (${myUnrendered.length})` : ''}
                </button>
              )}
            </div>
          )}
        </div>
        {renderMine.error && <p className="error small">{(renderMine.error as Error).message}</p>}
        {!meInMatch && (
          <p className="muted small">Tip: click ☆ next to your name in a roster to filter highlights to yourself.</p>
        )}
        {highlights.length === 0 ? (
          <p className="muted">No highlights{filterToMe ? ' for you' : ''} in this match.</p>
        ) : (
          <ul className="list">
            {highlights.map((h) => (
              <HighlightRow
                key={h.id}
                h={h}
                demoId={demoId}
                render={rendersByHighlight.get(h.id)}
                canRender={canRender}
                onChanged={refreshRenders}
                heroName={heroName}
                tickRate={analysis.tickRate}
                players={players}
                me={me}
              />
            ))}
          </ul>
        )}
      </section>

      <Compilations
        analysis={analysis}
        demoId={demoId}
        onlySteamId={filterToMe ? me : null}
        rendersByHighlight={rendersByHighlight}
        canRender={canRender}
        onChanged={refreshRenders}
        heroName={heroName}
      />

      <KillFeed kills={analysis.kills} heroName={heroName} me={me} players={players} />
    </>
  )
}

function Roster({
  team,
  won,
  players,
  me,
  onSetMe,
  heroName,
}: {
  team: Team
  won: boolean
  players: PlayerInfo[]
  me: string | null
  onSetMe: (id: string | null) => void
  heroName: (id: number) => string
}) {
  return (
    <div className={`card roster ${team.toLowerCase()}`}>
      <h2>
        {teamName(team)} {won && <span className="badge done">Won</span>}
      </h2>
      <table className="table compact">
        <thead>
          <tr>
            <th />
            <th>Hero</th>
            <th>Player</th>
            <th className="num">K / D / A</th>
            <th className="num">Souls</th>
          </tr>
        </thead>
        <tbody>
          {players.map((p) => {
            const isMe = p.steamId === me
            return (
              <tr key={p.steamId} className={isMe ? 'me' : undefined}>
                <td>
                  <button
                    className="star"
                    title={isMe ? 'This is me' : 'Mark as me'}
                    onClick={() => onSetMe(isMe ? null : p.steamId)}
                  >
                    {isMe ? '★' : '☆'}
                  </button>
                </td>
                <td>{heroName(p.heroId)}</td>
                <td>{p.name}</td>
                <td className="num mono">
                  {p.kills} / {p.deaths} / {p.assists}
                </td>
                <td className="num mono">{(p.netWorth / 1000).toFixed(1)}k</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

function HighlightRow({
  h,
  demoId,
  render,
  canRender,
  onChanged,
  heroName,
  tickRate,
  players,
  me,
}: {
  h: Highlight
  demoId: string
  render: RenderInfo | undefined
  canRender: boolean
  onChanged: () => void
  heroName: (id: number) => string
  tickRate: number
  players: Map<string, PlayerInfo>
  me: string | null
}) {
  const [open, setOpen] = useState(false)
  const clipSeconds = h.segments
    ? h.segments.reduce((sum, s) => sum + s.endTick - s.startTick, 0) / tickRate
    : (h.clipEndTick - h.clipStartTick) / tickRate
  const start = useMutation({ mutationFn: () => api.renderHighlight(demoId, h.id), onSuccess: onChanged })
  const ready = render?.status === 'Done' && render.hasVideo
  return (
    <li className="highlight">
      <div className="highlight-row">
        <button className="highlight-main" onClick={() => setOpen((o) => !o)}>
          <span className={`badge hl-${h.type.toLowerCase()}`}>{highlightName(h.type, h.kills.length)}</span>
          {h.type === 'TeamFight' ? (
            <FightSummary h={h} players={players} heroName={heroName} me={me} />
          ) : (
            <span>
              <strong>{h.playerName}</strong> <span className="muted">({heroName(h.heroId)})</span>
              {h.type === 'TakedownStreak' ? (
                <TakedownSummary h={h} />
              ) : (
                h.type !== 'KillCompilation' && <span className="muted small"> · {h.kills.length} kills</span>
              )}
            </span>
          )}
          <span className="mono">{formatClock(h.startTimeSeconds)}</span>
          <span className="muted small mono">{clipSeconds.toFixed(0)}s clip</span>
          <span className="muted small">{open ? '▾' : '▸'}</span>
        </button>
        <div className="highlight-actions">
          {!render || render.status === 'Failed' ? (
            <button
              className="button ghost"
              disabled={!canRender || start.isPending}
              title={render?.error ?? undefined}
              onClick={() => start.mutate()}
            >
              {render?.status === 'Failed' ? 'Retry' : 'Render'}
            </button>
          ) : ready ? (
            <button className="button" onClick={() => setOpen(true)}>
              ▶ Watch
            </button>
          ) : (
            <span className={`badge ${render.status === 'Rendering' ? 'parsing' : 'queued'}`}>{renderStatusText(render)}</span>
          )}
        </div>
      </div>
      {render?.status === 'Failed' && render.error && <p className="error small render-error">Render failed: {render.error}</p>}
      {open && ready && (
        <div className="clip-player">
          <video src={previewUrl(render.id)} controls preload="metadata" />
          <a className="small" href={videoUrl(render.id)} download>
            Download clip
          </a>
        </div>
      )}
      {open && (
        <ul className="kills-inline">
          {h.kills.map((k) => (
            <li key={k.tick + k.victimSteamId} className="small">
              <span className="mono">{formatClock(k.gameTimeSeconds)}</span>{' '}
              {(h.type === 'TeamFight' || h.type === 'TakedownStreak') && (
                <>
                  <PlayerTag id={k.attackerSteamId} players={players} heroName={heroName} me={me} /> →{' '}
                </>
              )}
              <PlayerTag id={k.victimSteamId} players={players} heroName={heroName} me={me} />
              <Assists ids={k.assisterSteamIds} players={players} me={me} />{' '}
              {k.weapon && <span className="muted mono">[{k.weapon}]</span>}
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}

/** One clip per player of all their hero kills, spliced together. */
function Compilations({
  analysis,
  demoId,
  onlySteamId,
  rendersByHighlight,
  canRender,
  onChanged,
  heroName,
}: {
  analysis: MatchAnalysis
  demoId: string
  onlySteamId: string | null
  rendersByHighlight: Map<string, RenderInfo>
  canRender: boolean
  onChanged: () => void
  heroName: (id: number) => string
}) {
  const rows = useMemo(() => {
    const kills = new Map<string, number>()
    for (const k of analysis.kills)
      if (k.attackerSteamId && k.attackerSteamId !== k.victimSteamId)
        kills.set(k.attackerSteamId, (kills.get(k.attackerSteamId) ?? 0) + 1)
    return analysis.players
      .map((p) => ({ player: p, kills: kills.get(p.steamId) ?? 0 }))
      .filter((r) => r.kills >= 2 && (!onlySteamId || r.player.steamId === onlySteamId))
      .sort((a, b) => b.kills - a.kills)
  }, [analysis, onlySteamId])

  return (
    <section className="card">
      <div className="section-head">
        <h2>Kill compilations</h2>
      </div>
      {rows.length === 0 ? (
        <p className="muted">Nobody{onlySteamId ? ' (you included)' : ''} has 2+ hero kills to compile.</p>
      ) : (
        <ul className="list">
          {rows.map(({ player, kills }) => (
            <CompilationRow
              key={player.steamId}
              player={player}
              kills={kills}
              demoId={demoId}
              render={rendersByHighlight.get(compilationId(player.steamId))}
              canRender={canRender}
              onChanged={onChanged}
              heroName={heroName}
            />
          ))}
        </ul>
      )}
    </section>
  )
}

function CompilationRow({
  player,
  kills,
  demoId,
  render,
  canRender,
  onChanged,
  heroName,
}: {
  player: PlayerInfo
  kills: number
  demoId: string
  render: RenderInfo | undefined
  canRender: boolean
  onChanged: () => void
  heroName: (id: number) => string
}) {
  const [open, setOpen] = useState(false)
  const start = useMutation({ mutationFn: () => api.renderCompilation(demoId, player.steamId), onSuccess: onChanged })
  const ready = render?.status === 'Done' && render.hasVideo
  return (
    <li className="highlight">
      <div className="highlight-row">
        <button className="highlight-main" onClick={() => setOpen((o) => !o)} disabled={!ready}>
          <span className="badge hl-killcompilation">{kills} kills</span>
          <span>
            <strong>{player.name}</strong> <span className="muted">({heroName(player.heroId)})</span>
          </span>
          {ready && <span className="muted small">{open ? '▾' : '▸'}</span>}
        </button>
        <div className="highlight-actions">
          {!render || render.status === 'Failed' ? (
            <button
              className="button ghost"
              disabled={!canRender || start.isPending}
              title={render?.error ?? undefined}
              onClick={() => start.mutate()}
            >
              {render?.status === 'Failed' ? 'Retry' : 'Render'}
            </button>
          ) : ready ? (
            <button className="button" onClick={() => setOpen(true)}>
              ▶ Watch
            </button>
          ) : (
            <span className={`badge ${render.status === 'Rendering' ? 'parsing' : 'queued'}`}>{renderStatusText(render)}</span>
          )}
        </div>
      </div>
      {start.error && <p className="error small render-error">{(start.error as Error).message}</p>}
      {render?.status === 'Failed' && render.error && <p className="error small render-error">Render failed: {render.error}</p>}
      {open && ready && (
        <div className="clip-player">
          <video src={previewUrl(render.id)} controls preload="metadata" />
          <a className="small" href={videoUrl(render.id)} download>
            Download clip
          </a>
        </div>
      )}
    </li>
  )
}

function KillFeed({
  kills,
  heroName,
  me,
  players,
}: {
  kills: KillEvent[]
  heroName: (id: number) => string
  me: string | null
  players: Map<string, PlayerInfo>
}) {
  const [heroOnly, setHeroOnly] = useState(true)
  const shown = useMemo(() => (heroOnly ? kills.filter((k) => k.attackerSteamId) : kills), [kills, heroOnly])
  return (
    <section className="card">
      <div className="section-head">
        <h2>Kill feed</h2>
        <label className="toggle">
          <input type="checkbox" checked={heroOnly} onChange={(e) => setHeroOnly(e.target.checked)} />
          Hero kills only
        </label>
      </div>
      <table className="table compact">
        <thead>
          <tr>
            <th>Time</th>
            <th>Killer</th>
            <th>Victim</th>
            <th>Assists</th>
            <th>Weapon / ability</th>
            <th className="num">Tick</th>
          </tr>
        </thead>
        <tbody>
          {shown.map((k) => (
            <tr
              key={`${k.tick}-${k.victimSteamId}`}
              className={
                k.attackerSteamId === me || k.victimSteamId === me || (me != null && k.assisterSteamIds.includes(me))
                  ? 'me'
                  : undefined
              }
            >
              <td className="mono">{formatClock(k.gameTimeSeconds)}</td>
              <td>
                {k.attackerName ?? <span className="muted">World</span>}
                {k.attackerHeroId ? <span className="muted"> ({heroName(k.attackerHeroId)})</span> : null}
              </td>
              <td>
                {k.victimName} <span className="muted">({heroName(k.victimHeroId)})</span>
              </td>
              <td className="small">
                {k.assisterSteamIds.length === 0 ? (
                  <span className="muted">–</span>
                ) : (
                  k.assisterSteamIds.map((id, i) => (
                    <span key={id}>
                      {i > 0 && ', '}
                      <span className={id === me ? 'my-assist' : undefined} title={id === me ? 'Your assist' : undefined}>
                        {players.get(id)?.name ?? 'Unknown'}
                      </span>
                    </span>
                  ))
                )}
              </td>
              <td className="muted mono small">{k.weapon || '–'}</td>
              <td className="num mono small">{k.tick}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}

/** One-line teamfight summary: kills per team, the top killers, and your part in it. */
function FightSummary({
  h,
  players,
  heroName,
  me,
}: {
  h: Highlight
  players: Map<string, PlayerInfo>
  heroName: (id: number) => string
  me: string | null
}) {
  const teamOf = (id: string | null) => (id ? players.get(id)?.team : undefined)
  const hk = h.kills.filter((k) => teamOf(k.attackerSteamId) === 'Amber').length
  const am = h.kills.filter((k) => teamOf(k.attackerSteamId) === 'Sapphire').length
  const byKiller = new Map<string, number>()
  for (const k of h.kills) if (k.attackerSteamId) byKiller.set(k.attackerSteamId, (byKiller.get(k.attackerSteamId) ?? 0) + 1)
  const top = [...byKiller.entries()].sort((a, b) => b[1] - a[1]).slice(0, 2)
  const mine = me
    ? {
        kills: h.kills.filter((k) => k.attackerSteamId === me).length,
        assists: h.kills.filter((k) => k.assisterSteamIds.includes(me)).length,
        deaths: h.kills.filter((k) => k.victimSteamId === me).length,
      }
    : null
  const involved = mine && mine.kills + mine.assists + mine.deaths > 0
  return (
    <span>
      <strong>{h.kills.length} kills</strong>{' '}
      <span className="small">
        (<span className="team-amber">{teamName('Amber')} {hk}</span> – <span className="team-sapphire">{am} {teamName('Sapphire')}</span>)
      </span>
      <span className="muted small">
        {' '}
        · top:{' '}
        {top.map(([id, n], i) => (
          <span key={id}>
            {i > 0 && ', '}
            {players.get(id)?.name ?? 'Unknown'} ({heroName(players.get(id)?.heroId ?? 0)}) {n}
          </span>
        ))}
      </span>
      {involved && (
        <span className="small my-assist">
          {' '}
          · you {mine.kills}/{mine.deaths}/{mine.assists}
        </span>
      )}
    </span>
  )
}

function PlayerTag({
  id,
  players,
  heroName,
  me,
}: {
  id: string | null
  players: Map<string, PlayerInfo>
  heroName: (id: number) => string
  me: string | null
}) {
  const p = id ? players.get(id) : undefined
  if (!p) return <span className="muted">World</span>
  return (
    <span className={`team-${p.team.toLowerCase()}${id === me ? ' me-name' : ''}`}>
      {p.name} <span className="muted">({heroName(p.heroId)})</span>
    </span>
  )
}

function Assists({ ids, players, me }: { ids: string[]; players: Map<string, PlayerInfo>; me: string | null }) {
  if (ids.length === 0) return null
  return (
    <span className="muted">
      {' '}
      + {ids.map((id, i) => (
        <span key={id}>
          {i > 0 && ', '}
          <span className={id === me ? 'my-assist' : undefined}>{players.get(id)?.name ?? 'Unknown'}</span>
        </span>
      ))}
    </span>
  )
}

/** " · 3 takedowns: 1 kill, 2 assists in 5s" */
function TakedownSummary({ h }: { h: Highlight }) {
  const kills = h.kills.filter((k) => k.attackerSteamId === h.playerSteamId).length
  const assists = h.kills.length - kills
  const span = Math.round(h.endTimeSeconds - h.startTimeSeconds)
  return (
    <span className="muted small">
      {' '}
      · {h.kills.length} takedowns: {kills} {kills === 1 ? 'kill' : 'kills'}, {assists} {assists === 1 ? 'assist' : 'assists'} in {span}s
    </span>
  )
}
