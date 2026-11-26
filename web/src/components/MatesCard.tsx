import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { api, type Mate, type MateRecord } from '../api'
import { useHeroNames } from '../format'
import { usePlayers } from '../players'
import { Empty, HeroArt } from './ui'

type Sort = 'games' | 'with' | 'against'
const PAGE = 12

/** Who the player keeps running into: games together and apart, and how those went. */
export default function MatesCard({ steamId, name }: { steamId: string; name: string }) {
  const heroName = useHeroNames()
  const { players: tracked } = usePlayers()
  const [sort, setSort] = useState<Sort>('games')
  const [oneOffs, setOneOffs] = useState(false)
  const [shown, setShown] = useState(PAGE)
  const mates = useQuery({
    queryKey: ['mates', steamId],
    queryFn: () => api.mates(steamId),
    // Rosters fill in in the background; keep the numbers moving while they do.
    refetchInterval: (q) => ((q.state.data?.waiting ?? 0) > 0 ? 30_000 : false),
  })

  const list = useMemo(() => {
    const all = (mates.data?.players ?? []).filter((m) => oneOffs || m.with.games + m.against.games >= 2)
    const key = (m: Mate) => (sort === 'with' ? m.with.games : sort === 'against' ? m.against.games : m.with.games + m.against.games)
    return [...all].sort((a, b) => key(b) - key(a) || (b.lastPlayed ?? '').localeCompare(a.lastPlayed ?? ''))
  }, [mates.data, sort, oneOffs])
  const page = list.slice(0, shown)

  // Metadata only has account ids: ask Steam for the names of the rows on screen that don't have one.
  const unnamed = page.filter((m) => !m.name && !tracked.some((t) => t.steamId === m.steamId && t.name)).map((m) => m.steamId)
  const names = useQuery({
    queryKey: ['player-names', unnamed],
    queryFn: () => api.playerNames(unnamed),
    enabled: unnamed.length > 0,
    staleTime: Infinity,
  })
  const nameOf = (m: Mate) =>
    m.name ?? tracked.find((t) => t.steamId === m.steamId)?.name ?? names.data?.[m.steamId] ?? `Player …${m.steamId.slice(-6)}`

  const d = mates.data
  return (
    <section className="card flush">
      <div className="section-head">
        <h2>Played with &amp; against</h2>
        <div className="head-actions">
          <label className="toggle small">
            <input type="checkbox" checked={oneOffs} onChange={(e) => setOneOffs(e.target.checked)} />
            Include one-offs
          </label>
          <div className="segmented">
            {(['games', 'with', 'against'] as Sort[]).map((s) => (
              <button key={s} className={sort === s ? 'on' : ''} onClick={() => setSort(s)}>
                {s === 'games' ? 'Most games' : s === 'with' ? 'Teammates' : 'Opponents'}
              </button>
            ))}
          </div>
        </div>
      </div>
      {d && (
        <p className="muted small list-note">
          From {d.counted} of {d.matches} matches
          {d.waiting > 0 ? ` · fetching team lists for ${d.waiting} more in the background` : ''}. Records are {name}'s.
        </p>
      )}
      {mates.isLoading ? (
        <p className="muted" style={{ padding: 18 }}>Loading…</p>
      ) : list.length === 0 ? (
        <Empty title={d?.counted ? 'Nobody twice yet' : 'No team lists yet'}>
          <p className="small">
            {d?.counted
              ? 'Nobody has shown up in more than one match. Tick "Include one-offs" to see everyone.'
              : 'Team lists come from the match data Valve keeps for 21 days; they fill in as matches are scanned.'}
          </p>
        </Empty>
      ) : (
        <div className="table-wrap">
          <table className="table mates-table">
            <thead>
              <tr>
                <th>Player</th>
                <th className="num">With</th>
                <th className="num">W–L with</th>
                <th className="num">Against</th>
                <th className="num">W–L against</th>
                <th className="num">Last seen</th>
              </tr>
            </thead>
            <tbody>
              {page.map((m) => (
                <tr key={m.steamId}>
                  <td>
                    <div className="player-cell">
                      <HeroArt heroId={m.heroId} name={heroName(m.heroId)} />
                      <div style={{ minWidth: 0 }}>
                        <div style={{ fontWeight: 650 }}>{nameOf(m)}</div>
                        <div className="faint small">last on {heroName(m.heroId)}</div>
                      </div>
                    </div>
                  </td>
                  <td className="num mono">{m.with.games || <span className="faint">–</span>}</td>
                  <td className="num">
                    <Record r={m.with} />
                  </td>
                  <td className="num mono">{m.against.games || <span className="faint">–</span>}</td>
                  <td className="num">
                    <Record r={m.against} />
                  </td>
                  <td className="num faint small">{m.lastPlayed ? ago(m.lastPlayed) : '–'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {list.length > shown && (
        <div className="list-foot">
          <button className="button ghost sm" onClick={() => setShown((n) => n + PAGE * 2)}>
            Show more ({list.length - shown})
          </button>
        </div>
      )}
    </section>
  )
}

/** "3–1" with the win rate, tinted; unfinished/abandoned games count as games but not as either. */
function Record({ r }: { r: MateRecord }) {
  const decided = r.wins + r.losses
  if (r.games === 0) return <span className="faint">–</span>
  if (decided === 0) return <span className="faint small">no result</span>
  const pct = Math.round((r.wins / decided) * 100)
  return (
    <span className="mono">
      {r.wins}–{r.losses} <span className={`small ${pct > 50 ? 'win' : pct < 50 ? 'loss' : 'muted'}`}>{pct}%</span>
    </span>
  )
}

function ago(iso: string) {
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000)
  return days <= 0 ? 'today' : days === 1 ? 'yesterday' : days < 60 ? `${days}d ago` : new Date(iso).toLocaleDateString()
}
