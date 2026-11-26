import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import ClipCard from '../components/ClipCard'
import MatesCard from '../components/MatesCard'
import { MatchRowView } from '../components/MatchRowView'
import VideoModal from '../components/VideoModal'
import { Empty, HeroArt, Stat } from '../components/ui'
import { useHeroNames, useMe } from '../format'
import { useMatchRows, useMyName } from '../matches'
import { useRenders } from '../renders'

export default function HomePage() {
  const [me] = useMe()
  const name = useMyName(me)
  const heroName = useHeroNames()
  const { rows } = useMatchRows(me)
  const renders = useRenders()
  const [playing, setPlaying] = useState<string | null>(null)

  const recent = rows.slice(0, 20)
  const stats = useMemo(() => {
    const n = recent.length || 1
    const avg = (f: (r: (typeof recent)[number]) => number) => (recent.reduce((s, r) => s + f(r), 0) / n).toFixed(1)
    const decided = rows.filter((r) => r.result)
    const wins = decided.filter((r) => r.result === 'win').length
    const heroes = new Map<number, { games: number; kills: number }>()
    for (const r of rows) {
      if (!r.heroId) continue
      const h = heroes.get(r.heroId) ?? { games: 0, kills: 0 }
      heroes.set(r.heroId, { games: h.games + 1, kills: h.kills + r.kills })
    }
    const topHeroes = [...heroes.entries()].sort((a, b) => b[1].games - a[1].games).slice(0, 3)
    return { k: avg((r) => r.kills), d: avg((r) => r.deaths), a: avg((r) => r.assists), wins, decided: decided.length, topHeroes }
  }, [rows, recent])

  const toClip = rows
    .filter((r) => r.status !== 'expired' && r.status !== 'ready' && (r.score ?? 0) > 0)
    .sort((a, b) => (b.score ?? 0) - (a.score ?? 0))
    .slice(0, 5)
  const clips = (renders.data ?? []).filter((r) => r.status === 'Done' && r.hasVideo).slice(0, 8)
  const playingClip = clips.find((c) => c.id === playing)

  if (!me)
    return (
      <div className="card">
        <Empty title="Welcome to Deadlock Clipper">
          <p>Add yourself or a friend, add a clipper Steam account, and it finds and clips the best plays.</p>
          <Link className="button" to="/settings">
            Get started
          </Link>
        </Empty>
      </div>
    )

  const mainHero = stats.topHeroes[0]?.[0] ?? rows[0]?.heroId ?? 0
  return (
    <div className="stack">
      <section className="card profile-hero">
        <div className="backdrop">{mainHero > 0 && <img src={`/api/heroes/${mainHero}/art/card`} alt="" />}</div>
        <HeroArt heroId={mainHero} name={name ?? 'You'} />
        <div>
          <h1>{name ?? 'You'}</h1>
          <p className="muted small mono">{me}</p>
          <div className="stats" style={{ marginTop: 14 }}>
            <Stat label="Matches" value={rows.length} sub="tracked" />
            <Stat
              label="Record"
              value={stats.decided ? `${stats.wins}–${stats.decided - stats.wins}` : '–'}
              sub={stats.decided ? `${Math.round((stats.wins / stats.decided) * 100)}% in parsed games` : 'from parsed games'}
            />
            <Stat label="Avg K / D / A" value={`${stats.k} / ${stats.d} / ${stats.a}`} sub="last 20" />
            <Stat label="Clips" value={(renders.data ?? []).filter((r) => r.status === 'Done').length} sub="rendered" />
          </div>
        </div>
        {stats.topHeroes.length > 0 && (
          <div className="top-heroes">
            {stats.topHeroes.map(([id, h]) => (
              <div key={id} className="top-hero">
                <HeroArt heroId={id} name={heroName(id)} />
                <div className="small" style={{ marginTop: 6, fontWeight: 650 }}>{heroName(id)}</div>
                <div className="faint small">{h.games} games</div>
              </div>
            ))}
          </div>
        )}
      </section>

      <section className="card flush">
        <div className="section-head">
          <h2>Best matches to clip</h2>
          <Link to="/matches" className="button ghost sm">
            All matches
          </Link>
        </div>
        {toClip.length === 0 ? (
          <Empty title="Nothing waiting">
            <p className="small">Matches with highlights that you haven't downloaded show up here.</p>
          </Empty>
        ) : (
          toClip.map((r) => <MatchRowView key={r.key} row={r} />)
        )}
      </section>

      <MatesCard key={me} steamId={me} name={name ?? 'You'} />

      <section>
        <div className="section-head">
          <h2>Latest clips</h2>
          <Link to="/clips" className="button ghost sm">
            All clips
          </Link>
        </div>
        {clips.length === 0 ? (
          <div className="card">
            <Empty title="No clips yet">
              <p className="small">Open a downloaded match and render a highlight, or switch on auto mode in Settings.</p>
            </Empty>
          </div>
        ) : (
          <div className="clip-grid">
            {clips.map((r) => (
              <ClipCard key={r.id} r={r} onOpen={() => setPlaying(r.id)} />
            ))}
          </div>
        )}
      </section>

      {playingClip && (
        <VideoModal renderId={playingClip.id} onClose={() => setPlaying(null)}>
          <div className="grow">
            <strong>{playingClip.playerName}</strong>{' '}
            <span className="muted small">
              {heroName(playingClip.heroId)} · match {playingClip.matchId}
            </span>
          </div>
          <Link className="button ghost sm" to={`/demos/${playingClip.demoId}`}>
            Open match
          </Link>
        </VideoModal>
      )}
    </div>
  )
}
