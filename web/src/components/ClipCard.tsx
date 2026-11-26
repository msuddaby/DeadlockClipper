import type { RenderInfo } from '../api'
import { formatClock, highlightLabel, useHeroNames } from '../format'
import { HeroArt } from './ui'

/** An Allstar-style thumbnail card for a finished clip. */
export default function ClipCard({ r, onOpen }: { r: RenderInfo; onOpen: () => void }) {
  const heroName = useHeroNames()
  return (
    <button className="clip-card" onClick={onOpen}>
      <div className="clip-thumb">
        <img src={`/api/renders/${r.id}/thumb`} alt="" loading="lazy" />
        <span className={`chip hl-${r.highlightType.toLowerCase()}`}>{highlightLabel[r.highlightType]}</span>
        <div className="play">
          <span>▶</span>
        </div>
      </div>
      <div className="clip-body">
        <HeroArt heroId={r.heroId} name={heroName(r.heroId)} />
        <div>
          <div className="who">{r.playerName}</div>
          <div className="faint small">
            {heroName(r.heroId)} · {formatClock(r.gameTimeSeconds)}
            {r.matchId ? ` · ${r.matchId}` : ''}
          </div>
        </div>
      </div>
    </button>
  )
}
