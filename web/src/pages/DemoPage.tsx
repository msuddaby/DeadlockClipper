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
import { formatClock, highlightLabel, useHeroNames, useMe } from '../format'
import { renderStatusText, useRenderHealth, useRenders } from '../renders'

export default function DemoPage() {
  const { id } = useParams<{ id: string }>()
  const query = useQuery({
    queryKey: ['demo', id],
    queryFn: () => api.demo(id!),
    enabled: !!id,
    refetchInterval: (q) => {
      const s = q.state.data?.demo.status
      return s === 'Queued' || s === 'Parsing' ? 1000 : false
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

      {(demo.status === 'Queued' || demo.status === 'Parsing') && (
        <div className="card">
          <p>Parsing demo… {Math.round((demo.progress ?? 0) * 100)}%</p>
          <progress value={demo.progress ?? 0} max={1} className="wide" />
        </div>
      )}
      {demo.status === 'Failed' && (
        <div className="card">
          <p className="error">Parsing failed: {demo.error}</p>
        </div>
      )}
      {analysis && demo.status === 'Done' && <HealthBanner />}
      {analysis && <Analysis analysis={analysis} demoId={demo.id} />}
    </div>
  )
}

function Analysis({ analysis, demoId }: { analysis: MatchAnalysis; demoId: string }) {
  const heroName = useHeroNames()
  const [me, setMe] = useMe()
  const meInMatch = analysis.players.some((p) => p.steamId === me)
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
  const myHighlights = analysis.highlights.filter((h) => h.playerSteamId === me)
  const myUnrendered = myHighlights.filter((h) => !rendersByHighlight.has(h.id))

  const highlights = filterToMe ? analysis.highlights.filter((h) => h.playerSteamId === me) : analysis.highlights
  const teams: Team[] = ['Amber', 'Sapphire']

  return (
    <>
      <section className="grid-2">
        {teams.map((team) => (
          <Roster
            key={team}
            team={team}
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
          <p className="muted">No multikills{filterToMe ? ' for you' : ''} in this match.</p>
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
              />
            ))}
          </ul>
        )}
      </section>

      <KillFeed kills={analysis.kills} heroName={heroName} me={me} />
    </>
  )
}

function Roster({
  team,
  players,
  me,
  onSetMe,
  heroName,
}: {
  team: Team
  players: PlayerInfo[]
  me: string | null
  onSetMe: (id: string | null) => void
  heroName: (id: number) => string
}) {
  return (
    <div className={`card roster ${team.toLowerCase()}`}>
      <h2>{team === 'Amber' ? 'Amber Hand' : 'Sapphire Flame'}</h2>
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
}: {
  h: Highlight
  demoId: string
  render: RenderInfo | undefined
  canRender: boolean
  onChanged: () => void
  heroName: (id: number) => string
  tickRate: number
}) {
  const [open, setOpen] = useState(false)
  const clipSeconds = (h.clipEndTick - h.clipStartTick) / tickRate
  const start = useMutation({ mutationFn: () => api.renderHighlight(demoId, h.id), onSuccess: onChanged })
  const ready = render?.status === 'Done' && render.hasVideo
  return (
    <li className="highlight">
      <div className="highlight-row">
        <button className="highlight-main" onClick={() => setOpen((o) => !o)}>
          <span className={`badge hl-${h.type.toLowerCase()}`}>{highlightLabel[h.type]}</span>
          <span>
            <strong>{h.playerName}</strong> <span className="muted">({heroName(h.heroId)})</span>
          </span>
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
            <li key={k.tick + k.victimSteamId} className="mono small">
              {formatClock(k.gameTimeSeconds)} → {k.victimName} ({heroName(k.victimHeroId)}){' '}
              {k.weapon && <span className="muted">[{k.weapon}]</span>}
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}

function KillFeed({ kills, heroName, me }: { kills: KillEvent[]; heroName: (id: number) => string; me: string | null }) {
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
            <th>Weapon / ability</th>
            <th className="num">Tick</th>
          </tr>
        </thead>
        <tbody>
          {shown.map((k) => (
            <tr
              key={`${k.tick}-${k.victimSteamId}`}
              className={k.attackerSteamId === me || k.victimSteamId === me ? 'me' : undefined}
            >
              <td className="mono">{formatClock(k.gameTimeSeconds)}</td>
              <td>
                {k.attackerName ?? <span className="muted">World</span>}
                {k.attackerHeroId ? <span className="muted"> ({heroName(k.attackerHeroId)})</span> : null}
              </td>
              <td>
                {k.victimName} <span className="muted">({heroName(k.victimHeroId)})</span>
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
