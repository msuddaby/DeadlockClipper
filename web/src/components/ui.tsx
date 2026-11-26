import { useState, type ReactNode } from 'react'
import type { HighlightType } from '../api'
import { formatClock, highlightName } from '../format'

/** Hero portrait from the local game files; falls back to initials. */
export function HeroArt({ heroId, name, className = '', kind = 'card' }: { heroId: number; name?: string; className?: string; kind?: 'card' | 'gloat' }) {
  const [failed, setFailed] = useState(false)
  return (
    <div className={`hero-art ${className}`} title={name}>
      {heroId > 0 && !failed ? (
        <img src={`/api/heroes/${heroId}/art/${kind}`} alt={name ?? ''} loading="lazy" onError={() => setFailed(true)} />
      ) : (
        <span className="initials">{(name ?? '?').slice(0, 2).toUpperCase()}</span>
      )}
    </div>
  )
}

export function HighlightChip({ type, kills, timeSeconds, extra }: { type: HighlightType; kills?: number; timeSeconds?: number; extra?: string }) {
  return (
    <span className={`chip hl-${type.toLowerCase()}`}>
      {highlightName(type, kills ?? 0)}
      {timeSeconds != null && <span className="t">{formatClock(timeSeconds)}</span>}
      {extra && <span className="t">{extra}</span>}
    </span>
  )
}

/** Clip-potential score, tinted by how good it is (a double kill ~6, a triple ~13, a carried fight 25+). */
export function ScoreBadge({ score }: { score: number | null | undefined }) {
  if (score == null) return <span className="score-badge" title="Not scored yet">…</span>
  const tier = score >= 40 ? 4 : score >= 20 ? 3 : score >= 10 ? 2 : score > 0 ? 1 : 0
  return (
    <span className={`score-badge s${tier}`} title="Clip potential">
      {score > 0 ? Math.round(score) : '–'}
    </span>
  )
}

export function Kda({ k, d, a }: { k: number; d: number; a: number }) {
  return (
    <span className="kda">
      {k}
      <span className="sep">/</span>
      <span className="loss">{d}</span>
      <span className="sep">/</span>
      {a}
    </span>
  )
}

export function Stat({ label, value, sub }: { label: string; value: ReactNode; sub?: ReactNode }) {
  return (
    <div className="stat">
      <div className="stat-label">{label}</div>
      <div className="stat-value">{value}</div>
      {sub && <div className="stat-sub">{sub}</div>}
    </div>
  )
}

export function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <h3>{title}</h3>
      {children}
    </div>
  )
}

const paths: Record<string, string> = {
  home: 'M3 10.5 12 3l9 7.5V21a1 1 0 0 1-1 1h-5v-7h-6v7H4a1 1 0 0 1-1-1z',
  matches: 'M4 5h16M4 12h16M4 19h16',
  clips: 'M4 6a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2zM10 9l5 3-5 3z',
  settings:
    'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zm7.4-3a7.4 7.4 0 0 0-.1-1.3l2-1.6-2-3.4-2.4 1a7.5 7.5 0 0 0-2.2-1.3L14.4 3h-4l-.4 2.4A7.5 7.5 0 0 0 7.8 6.7l-2.4-1-2 3.4 2 1.6a7.4 7.4 0 0 0 0 2.6l-2 1.6 2 3.4 2.4-1a7.5 7.5 0 0 0 2.2 1.3l.4 2.4h4l.4-2.4a7.5 7.5 0 0 0 2.2-1.3l2.4 1 2-3.4-2-1.6c.1-.4.1-.9.1-1.3z',
}

export function Icon({ name }: { name: keyof typeof paths }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d={paths[name]} />
    </svg>
  )
}
