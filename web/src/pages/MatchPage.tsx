import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router'
import { api, type ChatMessage, type Highlight, type KillEvent, type MatchAnalysis, type PlayerInfo, type RenderInfo, type Team } from '../api'
import HealthBanner from '../components/HealthBanner'
import VideoModal from '../components/VideoModal'
import { Empty, HeroArt, HighlightChip, Kda, Stat } from '../components/ui'
import { compilationId, formatClock, highlightName, teamName, useHeroNames, useMe } from '../format'
import { setActivePlayer, usePlayers } from '../players'
import { renderStatusText, useRenderHealth, useRenders } from '../renders'

type Players = Map<string, PlayerInfo>
type Tab = 'highlights' | 'scoreboard' | 'feed' | 'chat'

export default function MatchPage() {
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
  const busy = demo.status === 'Downloading' || demo.status === 'Queued' || demo.status === 'Parsing'

  return (
    <div className="stack">
      <Link to="/matches" className="muted small">
        ← Matches
      </Link>
      {busy && (
        <div className="card">
          <div className="section-head">
            <h3>
              {demo.status === 'Downloading' ? 'Downloading replay from Valve' : 'Parsing demo'} · match {demo.matchId}
            </h3>
            <span className="pill busy">{Math.round((demo.progress ?? 0) * 100)}%</span>
          </div>
          <progress value={demo.progress ?? 0} max={1} className="wide" />
          {demo.status === 'Downloading' && demo.error && <p className="muted small" style={{ marginTop: 8 }}>{demo.error}</p>}
          {demo.status === 'Downloading' && <DownloadControls demoId={demo.id} matchId={demo.matchId} failed={false} />}
        </div>
      )}
      {demo.status === 'Failed' && (
        <div className="card">
          <p className="error">{demo.error?.startsWith('Download') ? demo.error : `Parsing failed: ${demo.error}`}</p>
          {demo.source === 'MatchId' && demo.sizeBytes === 0 && <DownloadControls demoId={demo.id} matchId={demo.matchId} failed />}
        </div>
      )}
      {analysis && <Match analysis={analysis} demoId={demo.id} matchId={demo.matchId} />}
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
    <div className="inline-form" style={{ marginTop: 12 }}>
      {failed ? (
        matchId != null && (
          <button className="button" disabled={retry.isPending} onClick={() => retry.mutate()}>
            Retry download
          </button>
        )
      ) : (
        <button className="button ghost sm danger" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
          Cancel download
        </button>
      )}
      {error && <span className="error small">{error.message}</span>}
    </div>
  )
}

function Match({ analysis, demoId, matchId }: { analysis: MatchAnalysis; demoId: string; matchId: number | null }) {
  const heroName = useHeroNames()
  const [me] = useMe()
  const players: Players = useMemo(() => new Map(analysis.players.map((p) => [p.steamId, p])), [analysis.players])
  const mine = players.get(me ?? '')
  const [tab, setTab] = useState<Tab>('highlights')
  const [playing, setPlaying] = useState<RenderInfo | null>(null)

  const result = mine && analysis.winningTeam !== 'Unknown' ? (analysis.winningTeam === mine.team ? 'win' : 'loss') : null
  const teamKills = mine ? analysis.players.filter((p) => p.team === mine.team).reduce((s, p) => s + p.kills, 0) : 0
  const mvp = [...analysis.players].sort((a, b) => b.kills - a.kills)[0]
  const focus = mine ?? mvp
  const heroKills = analysis.kills.filter((k) => k.attackerSteamId && k.attackerSteamId !== k.victimSteamId)
  // The tab shows what the Highlights list shows by default: yours when you're in the match.
  const highlightCount = mine
    ? analysis.highlights.filter(
        (h) =>
          h.playerSteamId === me ||
          (h.type === 'TeamFight' && h.kills.some((k) => k.attackerSteamId === me || k.victimSteamId === me || k.assisterSteamIds.includes(me!))),
      ).length
    : analysis.highlights.length

  return (
    <>
      <section className="card match-banner">
        <div className="backdrop">{focus && <img src={`/api/heroes/${focus.heroId}/art/${result === 'win' ? 'gloat' : 'card'}`} alt="" />}</div>
        <div>
          <div className={`result-word ${result ?? ''}`}>{result === 'win' ? 'VICTORY' : result === 'loss' ? 'DEFEAT' : 'MATCH'}</div>
          <div className="muted small" style={{ marginTop: 8 }}>
            {matchId ? `Match ${matchId}` : 'Demo'} · {formatClock(analysis.durationSeconds)}
            {analysis.winningTeam !== 'Unknown' && (
              <>
                {' · '}
                <span className={`team-${analysis.winningTeam.toLowerCase()}`}>{teamName(analysis.winningTeam)} won</span>
              </>
            )}
          </div>
        </div>
        {mine && (
          <div className="player-cell" style={{ gap: 14 }}>
            <HeroArt heroId={mine.heroId} name={heroName(mine.heroId)} className="" />
            <div>
              <div style={{ fontWeight: 750, fontSize: 16 }}>{heroName(mine.heroId)}</div>
              <div className={`small team-${mine.team.toLowerCase()}`}>{teamName(mine.team)}</div>
            </div>
          </div>
        )}
        <div className="banner-stats">
          {mine ? (
            <>
              <Stat label="K / D / A" value={<Kda k={mine.kills} d={mine.deaths} a={mine.assists} />} />
              <Stat label="Souls" value={`${(mine.netWorth / 1000).toFixed(1)}k`} />
              <Stat label="Kill share" value={teamKills ? `${Math.round(((mine.kills + mine.assists) / teamKills) * 100)}%` : '–'} />
            </>
          ) : (
            <>
              <Stat label="Hero kills" value={heroKills.length} />
              <Stat label="Top fragger" value={mvp?.kills ?? 0} sub={mvp?.name} />
            </>
          )}
        </div>
      </section>

      <HealthBanner />

      <div>
        <div className="tabs">
          {(
            [
              ['highlights', 'Highlights', highlightCount],
              ['scoreboard', 'Scoreboard', null],
              ['feed', 'Kill feed', heroKills.length],
              ['chat', 'Chat', analysis.chat?.length ?? 0],
            ] as const
          ).map(([t, label, count]) => (
            <button key={t} className={tab === t ? 'on' : ''} onClick={() => setTab(t)}>
              {label}
              {count != null && <span className="count">{count}</span>}
            </button>
          ))}
        </div>
        {tab === 'highlights' && <Highlights analysis={analysis} demoId={demoId} players={players} me={me} onWatch={setPlaying} />}
        {tab === 'scoreboard' && <Scoreboard analysis={analysis} me={me} />}
        {tab === 'feed' && <KillFeed kills={analysis.kills} me={me} players={players} />}
        {tab === 'chat' && <Chat analysis={analysis} me={me} players={players} />}
      </div>

      {playing && (
        <VideoModal renderId={playing.id} onClose={() => setPlaying(null)}>
          <div className="grow">
            <strong>{highlightName(playing.highlightType, 0)}</strong> · {playing.playerName}{' '}
            <span className="muted small">{formatClock(playing.gameTimeSeconds)}</span>
          </div>
        </VideoModal>
      )}
    </>
  )
}

function Highlights({
  analysis,
  demoId,
  players,
  me,
  onWatch,
}: {
  analysis: MatchAnalysis
  demoId: string
  players: Players
  me: string | null
  onWatch: (r: RenderInfo) => void
}) {
  const meInMatch = players.has(me ?? '')
  const myName = players.get(me ?? '')?.name ?? 'You'
  // Tracked players who were in this match: switch between them here to clip each one's plays.
  const tracked = usePlayers().players.filter((p) => players.has(p.steamId))
  const [onlyMe, setOnlyMe] = useState(true)
  const filterToMe = onlyMe && meInMatch
  const queryClient = useQueryClient()
  const renders = useRenders(demoId)
  const health = useRenderHealth()
  const canRender = health.data?.ok ?? false
  const byHighlight = useMemo(() => new Map((renders.data ?? []).map((r) => [r.highlightId, r])), [renders.data])
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['renders'] })
  const renderMine = useMutation({ mutationFn: () => api.renderPlayer(demoId, me!), onSuccess: refresh })

  // A teamfight counts as mine if I killed, died or assisted in it, not only when I topped it.
  const involvesMe = (h: Highlight) =>
    h.playerSteamId === me ||
    (h.type === 'TeamFight' && h.kills.some((k) => k.attackerSteamId === me || k.victimSteamId === me || k.assisterSteamIds.includes(me ?? '')))
  const shown = (filterToMe ? analysis.highlights.filter(involvesMe) : analysis.highlights)
    .slice()
    .sort((a, b) => a.startTick - b.startTick)
  const myUnrendered = analysis.highlights.filter((h) => h.playerSteamId === me && !byHighlight.has(h.id))

  const compilers = useMemo(() => {
    const kills = new Map<string, number>()
    for (const k of analysis.kills)
      if (k.attackerSteamId && k.attackerSteamId !== k.victimSteamId) kills.set(k.attackerSteamId, (kills.get(k.attackerSteamId) ?? 0) + 1)
    return analysis.players
      .map((p) => ({ player: p, kills: kills.get(p.steamId) ?? 0 }))
      .filter((r) => r.kills >= 2 && (!filterToMe || r.player.steamId === me))
      .sort((a, b) => b.kills - a.kills)
  }, [analysis, filterToMe, me])

  return (
    <div className="stack">
      <section className="card flush">
        <div className="section-head">
          <h2>{filterToMe ? `${myName}'s highlights` : 'All highlights'}</h2>
          <div className="head-actions">
            {tracked.length > 1 && (
              <div className="segmented" title="Players you track who were in this match">
                {tracked.map((p) => (
                  <button key={p.steamId} className={p.steamId === me ? 'on' : ''} onClick={() => setActivePlayer(p.steamId)}>
                    {players.get(p.steamId)!.name}
                  </button>
                ))}
              </div>
            )}
            {meInMatch && (
              <label className="toggle small">
                <input type="checkbox" checked={onlyMe} onChange={(e) => setOnlyMe(e.target.checked)} />
                Only {myName}'s
              </label>
            )}
            {meInMatch && (
              <button
                className="button sm"
                disabled={!canRender || renderMine.isPending || myUnrendered.length === 0}
                title={myUnrendered.length === 0 ? `All ${myName}'s highlights are rendered or queued` : undefined}
                onClick={() => renderMine.mutate()}
              >
                Render all {myName}'s{myUnrendered.length > 0 ? ` (${myUnrendered.length})` : ''}
              </button>
            )}
          </div>
        </div>
        {!meInMatch && <p className="muted small list-note">Star a player (☆) on the Scoreboard tab to see and clip just their highlights.</p>}
        {renderMine.error && <p className="error small list-note">{(renderMine.error as Error).message}</p>}
        {shown.length === 0 ? (
          <Empty title="No highlights">
            <p className="small">Nothing{filterToMe ? ` of ${myName}'s` : ''} cleared the bar in this match.</p>
          </Empty>
        ) : (
          shown.map((h) => (
            <HighlightCard
              key={h.id}
              h={h}
              reactions={victimChat(analysis, h.playerSteamId, h.kills, h.type === 'TakedownStreak')}
              tickRate={analysis.tickRate}
              players={players}
              me={me}
              render={byHighlight.get(h.id)}
              canRender={canRender}
              onRender={() => api.renderHighlight(demoId, h.id).then(refresh)}
              onWatch={onWatch}
            />
          ))
        )}
      </section>

      {compilers.length > 0 && (
        <section className="card flush">
          <div className="section-head">
            <h2>Kill compilations</h2>
            <span className="faint small">Every kill of one player, spliced into one clip</span>
          </div>
          {compilers.map(({ player, kills }) => (
            <CompilationCard
              key={player.steamId}
              player={player}
              reactions={victimChat(
                analysis,
                player.steamId,
                analysis.kills.filter((k) => k.attackerSteamId === player.steamId),
                false,
              )}
              kills={kills}
              render={byHighlight.get(compilationId(player.steamId))}
              canRender={canRender}
              onRender={() => api.renderCompilation(demoId, player.steamId).then(refresh)}
              onWatch={onWatch}
            />
          ))}
        </section>
      )}
    </div>
  )
}

function RenderButton({
  render,
  canRender,
  onRender,
  onWatch,
}: {
  render: RenderInfo | undefined
  canRender: boolean
  onRender: () => Promise<unknown>
  onWatch: (r: RenderInfo) => void
}) {
  const start = useMutation({ mutationFn: onRender })
  if (render?.status === 'Done' && render.hasVideo)
    return (
      <button className="button sm" onClick={() => onWatch(render)}>
        ▶ Watch
      </button>
    )
  if (render && render.status !== 'Failed') return <span className="pill busy">{renderStatusText(render)}</span>
  return (
    <button className="button ghost sm" disabled={!canRender || start.isPending} title={render?.error ?? undefined} onClick={() => start.mutate()}>
      {render?.status === 'Failed' ? 'Retry' : 'Render'}
    </button>
  )
}

function HighlightCard({
  h,
  reactions,
  tickRate,
  players,
  me,
  render,
  canRender,
  onRender,
  onWatch,
}: {
  h: Highlight
  reactions: ChatMessage[]
  tickRate: number
  players: Players
  me: string | null
  render: RenderInfo | undefined
  canRender: boolean
  onRender: () => Promise<unknown>
  onWatch: (r: RenderInfo) => void
}) {
  const heroName = useHeroNames()
  const [open, setOpen] = useState(false)
  const clipSeconds = (h.segments ? h.segments.reduce((s, x) => s + x.endTick - x.startTick, 0) : h.clipEndTick - h.clipStartTick) / tickRate
  const fight = h.type === 'TeamFight'

  return (
    <div className="highlight-card">
      <HeroArt heroId={h.heroId} name={heroName(h.heroId)} />
      <div style={{ minWidth: 0 }}>
        <div className="line1">
          <HighlightChip type={h.type} kills={h.kills.length} timeSeconds={h.startTimeSeconds} />
          {fight ? (
            <FightSummary h={h} players={players} me={me} />
          ) : (
            <span>
              <strong className={h.playerSteamId === me ? 'me-name' : undefined}>{h.playerName}</strong>{' '}
              <span className="muted">{heroName(h.heroId)}</span>
            </span>
          )}
          <Reactions messages={reactions} players={players} />
        </div>
        <div className="sub">
          {h.type === 'TakedownStreak' ? <TakedownSummary h={h} /> : `${h.kills.length} ${h.kills.length === 1 ? 'kill' : 'kills'}`}
          {' · '}
          {Math.round(clipSeconds)}s clip
          {fight && h.segments && ` from ${h.segments.length} moments`}
          {' · '}
          <button className="star" style={{ fontSize: 12, color: 'var(--muted)' }} onClick={() => setOpen((o) => !o)}>
            {open ? 'Hide kills ▴' : 'Show kills ▾'}
          </button>
        </div>
        {render?.status === 'Failed' && render.error && <div className="error small">Render failed: {render.error}</div>}
      </div>
      <div className="actions">
        <RenderButton render={render} canRender={canRender} onRender={onRender} onWatch={onWatch} />
      </div>
      {open && (
        <div className="highlight-detail">
          <ul className="kill-lines">
            {h.kills.map((k) => (
              <li key={`${k.tick}-${k.victimSteamId}`}>
                <span className="mono faint">{formatClock(k.gameTimeSeconds)}</span>{' '}
                <PlayerTag id={k.attackerSteamId} players={players} me={me} /> <span className="faint">→</span>{' '}
                <PlayerTag id={k.victimSteamId} players={players} me={me} />
                <Assists ids={k.assisterSteamIds} players={players} me={me} />
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}

function CompilationCard({
  player,
  reactions,
  kills,
  render,
  canRender,
  onRender,
  onWatch,
}: {
  player: PlayerInfo
  reactions: ChatMessage[]
  kills: number
  render: RenderInfo | undefined
  canRender: boolean
  onRender: () => Promise<unknown>
  onWatch: (r: RenderInfo) => void
}) {
  const heroName = useHeroNames()
  return (
    <div className="highlight-card">
      <HeroArt heroId={player.heroId} name={heroName(player.heroId)} />
      <div>
        <div className="line1">
          <HighlightChip type="KillCompilation" extra={`${kills} kills`} />
          <strong>{player.name}</strong> <span className="muted">{heroName(player.heroId)}</span>
          <Reactions messages={reactions} players={new Map()} />
        </div>
        {render?.status === 'Failed' && render.error && <div className="error small">Render failed: {render.error}</div>}
      </div>
      <div className="actions">
        <RenderButton render={render} canRender={canRender} onRender={onRender} onWatch={onWatch} />
      </div>
    </div>
  )
}

function Scoreboard({ analysis, me }: { analysis: MatchAnalysis; me: string | null }) {
  const heroName = useHeroNames()
  const teams: Team[] = ['Amber', 'Sapphire']
  return (
    <div className="grid-2">
      {teams.map((team) => {
        const roster = analysis.players.filter((p) => p.team === team).sort((a, b) => b.netWorth - a.netWorth)
        const k = roster.reduce((s, p) => s + p.kills, 0)
        return (
          <section key={team} className="card">
            <div className="team-head">
              <span className="bar" style={{ background: team === 'Amber' ? 'var(--amber)' : 'var(--sapphire)' }} />
              <h3 className={`team-${team.toLowerCase()}`}>{teamName(team)}</h3>
              {analysis.winningTeam === team && <span className="pill win">Won</span>}
              <span className="faint small" style={{ marginLeft: 'auto' }}>
                {k} kills
              </span>
            </div>
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th />
                    <th>Player</th>
                    <th className="num">K / D / A</th>
                    <th className="num">Souls</th>
                  </tr>
                </thead>
                <tbody>
                  {roster.map((p) => {
                    const isMe = p.steamId === me
                    return (
                      <tr key={p.steamId} className={isMe ? 'me' : undefined}>
                        <td style={{ width: 24 }}>
                          <button
                            className={`star ${isMe ? 'on' : ''}`}
                            title={isMe ? 'Viewing as this player' : 'View and clip as this player (adds them to your players)'}
                            onClick={() => !isMe && setActivePlayer(p.steamId, p.name)}
                          >
                            {isMe ? '★' : '☆'}
                          </button>
                        </td>
                        <td>
                          <div className="player-cell">
                            <HeroArt heroId={p.heroId} name={heroName(p.heroId)} />
                            <div style={{ minWidth: 0 }}>
                              <div style={{ fontWeight: 650 }}>{p.name}</div>
                              <div className="faint small">{heroName(p.heroId)}</div>
                            </div>
                          </div>
                        </td>
                        <td className="num">
                          <Kda k={p.kills} d={p.deaths} a={p.assists} />
                        </td>
                        <td className="num mono">{(p.netWorth / 1000).toFixed(1)}k</td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          </section>
        )
      })}
    </div>
  )
}

function KillFeed({ kills, me, players }: { kills: KillEvent[]; me: string | null; players: Players }) {
  const [heroOnly, setHeroOnly] = useState(true)
  const [mineOnly, setMineOnly] = useState(false)
  const involves = (k: KillEvent) => k.attackerSteamId === me || k.victimSteamId === me || (me != null && k.assisterSteamIds.includes(me))
  const shown = kills.filter((k) => (!heroOnly || k.attackerSteamId) && (!mineOnly || involves(k)))
  return (
    <section className="card">
      <div className="section-head">
        <h2>Kill feed</h2>
        <div className="head-actions">
          {me && players.has(me) && (
            <label className="toggle small">
              <input type="checkbox" checked={mineOnly} onChange={(e) => setMineOnly(e.target.checked)} />
              Involving me
            </label>
          )}
          <label className="toggle small">
            <input type="checkbox" checked={heroOnly} onChange={(e) => setHeroOnly(e.target.checked)} />
            Hero kills only
          </label>
        </div>
      </div>
      <div className="table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Time</th>
              <th>Killer</th>
              <th>Victim</th>
              <th>Assists</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((k) => (
              <tr key={`${k.tick}-${k.victimSteamId}`} className={involves(k) ? 'me' : undefined}>
                <td className="mono faint">{formatClock(k.gameTimeSeconds)}</td>
                <td>
                  <FeedPlayer id={k.attackerSteamId} fallback={k.attackerName} players={players} me={me} />
                </td>
                <td>
                  <FeedPlayer id={k.victimSteamId} fallback={k.victimName} players={players} me={me} />
                </td>
                <td className="small">
                  {k.assisterSteamIds.length === 0 ? (
                    <span className="faint">–</span>
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
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function FeedPlayer({ id, fallback, players, me }: { id: string | null; fallback: string | null; players: Players; me: string | null }) {
  const heroName = useHeroNames()
  const p = id ? players.get(id) : undefined
  if (!p) return <span className="faint">{fallback ?? 'World'}</span>
  return (
    <div className="player-cell">
      <HeroArt heroId={p.heroId} name={heroName(p.heroId)} className="mini" />
      <span className={`team-${p.team.toLowerCase()}${id === me ? ' me-name' : ''}`}>{p.name}</span>
    </div>
  )
}

/** Kills per team, the top killers, and your part in it. */
function FightSummary({ h, players, me }: { h: Highlight; players: Players; me: string | null }) {
  const teamOf = (id: string | null) => (id ? players.get(id)?.team : undefined)
  const hk = h.kills.filter((k) => teamOf(k.attackerSteamId) === 'Amber').length
  const am = h.kills.filter((k) => teamOf(k.attackerSteamId) === 'Sapphire').length
  const mine = me
    ? {
        k: h.kills.filter((k) => k.attackerSteamId === me).length,
        a: h.kills.filter((k) => k.assisterSteamIds.includes(me)).length,
        d: h.kills.filter((k) => k.victimSteamId === me).length,
      }
    : null
  return (
    <span>
      <strong>{h.kills.length}-kill fight</strong>{' '}
      <span className="small">
        <span className="team-amber">{hk}</span> <span className="faint">–</span> <span className="team-sapphire">{am}</span>
      </span>
      {mine && mine.k + mine.a + mine.d > 0 && (
        <span className="small my-assist">
          {' '}
          · you {mine.k}/{mine.d}/{mine.a}
        </span>
      )}
      <span className="muted small"> · top {h.playerName}</span>
    </span>
  )
}

function TakedownSummary({ h }: { h: Highlight }) {
  const kills = h.kills.filter((k) => k.attackerSteamId === h.playerSteamId).length
  const assists = h.kills.length - kills
  return (
    <>
      {kills} {kills === 1 ? 'kill' : 'kills'}, {assists} {assists === 1 ? 'assist' : 'assists'} in {Math.round(h.endTimeSeconds - h.startTimeSeconds)}s
    </>
  )
}

function PlayerTag({ id, players, me }: { id: string | null; players: Players; me: string | null }) {
  const p = id ? players.get(id) : undefined
  if (!p) return <span className="faint">World</span>
  return <span className={`team-${p.team.toLowerCase()}${id === me ? ' me-name' : ''}`}>{p.name}</span>
}

function Assists({ ids, players, me }: { ids: string[]; players: Players; me: string | null }) {
  if (ids.length === 0) return null
  return (
    <span className="faint">
      {' '}
      +{' '}
      {ids.map((id, i) => (
        <span key={id}>
          {i > 0 && ', '}
          <span className={id === me ? 'my-assist' : undefined}>{players.get(id)?.name ?? 'Unknown'}</span>
        </span>
      ))}
    </span>
  )
}

/** Seconds after dying that a message still counts as a reaction (matches the backend's ChatWindowSeconds). */
const REACTION_SECONDS = 20

function Chat({ analysis, me, players }: { analysis: MatchAnalysis; me: string | null; players: Players }) {
  const heroName = useHeroNames()
  const [channel, setChannel] = useState<'all' | 'everything' | 'team'>('everything')
  if (!analysis.chat)
    return (
      <div className="card">
        <Empty title="No chat recorded">
          <p className="small">This demo was parsed before chat was read. It's re-parsed automatically after an update.</p>
        </Empty>
      </div>
    )

  // "Died to you" reactions: the sender was killed by you shortly before (these go into your clips).
  const reactedToMe = (m: ChatMessage) =>
    me != null &&
    analysis.kills.some(
      (k) => k.attackerSteamId === me && k.victimSteamId === m.senderSteamId && m.tick > k.tick && m.tick - k.tick <= REACTION_SECONDS * analysis.tickRate,
    )
  const shown = analysis.chat.filter((m) => channel === 'everything' || (channel === 'all') === m.allChat)

  return (
    <section className="card">
      <div className="section-head">
        <h2>Chat</h2>
        <div className="segmented">
          {(
            [
              ['everything', 'Everything'],
              ['all', 'All chat'],
              ['team', 'Team chat'],
            ] as const
          ).map(([c, label]) => (
            <button key={c} className={channel === c ? 'on' : ''} onClick={() => setChannel(c)}>
              {label}
            </button>
          ))}
        </div>
      </div>
      <p className="muted small" style={{ marginBottom: 12 }}>
        Includes both teams' team chat. Messages from someone you'd just killed are marked; those show up in your clips.
      </p>
      {shown.length === 0 ? (
        <Empty title="Nobody said anything" />
      ) : (
        <ul className="chat-log">
          {shown.map((m, i) => {
            const p = m.senderSteamId ? players.get(m.senderSteamId) : undefined
            return (
              <li key={i} className={m.senderSteamId === me ? 'mine' : undefined}>
                <span className="mono faint">{formatClock(m.gameTimeSeconds)}</span>
                <span className={`pill ${m.allChat ? '' : 'team'}`}>{m.allChat ? 'All' : 'Team'}</span>
                <span className="player-cell">
                  {p && <HeroArt heroId={p.heroId} name={heroName(p.heroId)} className="mini" />}
                  <strong className={p ? `team-${p.team.toLowerCase()}` : 'faint'}>{p?.name ?? (m.senderName || 'Unknown')}</strong>
                </span>
                <span className="text">{m.text}</span>
                {reactedToMe(m) && <span className="chip hl-killcompilation">died to you</span>}
              </li>
            )
          })}
        </ul>
      )}
    </section>
  )
}

/**
 * What the victims typed after dying, i.e. what the rendered clip will show as chat bubbles. Mirrors the renderer
 * (CaptionPlan.VictimChat): messages from people this player killed (or, in a takedown streak, helped kill) within
 * 20 s of dying, at most 2.
 */
function victimChat(analysis: MatchAnalysis, playerSteamId: string, kills: KillEvent[], withAssists: boolean): ChatMessage[] {
  const chat = analysis.chat
  if (!chat?.length) return []
  const window = REACTION_SECONDS * analysis.tickRate
  const found = new Set<ChatMessage>()
  for (const k of kills) {
    if (k.attackerSteamId !== playerSteamId && !(withAssists && k.assisterSteamIds.includes(playerSteamId))) continue
    for (const m of chat) if (m.senderSteamId === k.victimSteamId && m.tick > k.tick && m.tick - k.tick <= window) found.add(m)
  }
  return [...found].sort((a, b) => a.tick - b.tick).slice(0, 2)
}

/** "This clip comes with a reaction": a quote bubble of what the victim typed after dying. */
function Reactions({ messages, players }: { messages: ChatMessage[]; players: Players }) {
  if (messages.length === 0) return null
  const who = (m: ChatMessage) => (m.senderSteamId && players.get(m.senderSteamId)?.name) || m.senderName
  const title = messages.map((m) => `${who(m)} [${m.allChat ? 'all' : 'team'} chat]: ${m.text}`).join('\n')
  return (
    <span className="reaction" title={`In the clip:\n${title}`}>
      <span className="reaction-icon">💬</span>
      <span className="reaction-text">“{messages[0].text}”</span>
      {messages.length > 1 && <span className="reaction-more">+{messages.length - 1}</span>}
    </span>
  )
}
