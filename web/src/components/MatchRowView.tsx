import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router'
import { api } from '../api'
import { formatClock, teamName, useHeroNames } from '../format'
import type { MatchRow } from '../matches'
import { HeroArt, HighlightChip, Kda, ScoreBadge } from './ui'

const date = (iso: string) =>
  new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) +
  ' · ' +
  new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })

/** More than this and a row turns into a wall of chips; the match page lists them all. */
const MAX_CHIPS = 4

export function MatchRowView({ row }: { row: MatchRow }) {
  const heroName = useHeroNames()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const download = useMutation({
    mutationFn: () => api.downloadMatch(row.matchId!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['matches'] })
      queryClient.invalidateQueries({ queryKey: ['demos'] })
    },
  })
  const open = row.status === 'ready' && row.demoId ? () => navigate(`/demos/${row.demoId}`) : undefined
  const daysLeft = row.validThrough
    ? Math.max(0, Math.floor((new Date(row.validThrough).getTime() - Date.now()) / 86_400_000))
    : null

  return (
    <div className="match-row" onClick={open} style={{ cursor: open ? 'pointer' : undefined }}>
      <div className={`stripe ${row.result ?? ''}`} />
      <HeroArt heroId={row.heroId} name={heroName(row.heroId)} />
      <div style={{ minWidth: 0 }}>
        <div className="title">
          {row.heroId ? heroName(row.heroId) : 'Unknown hero'}{' '}
          {row.result && <span className={`small ${row.result}`}>{row.result === 'win' ? 'Victory' : 'Defeat'}</span>}
        </div>
        <div className="muted small">
          {date(row.playedAt)}
          {row.durationSeconds ? ` · ${formatClock(row.durationSeconds)}` : ''}
        </div>
        <div className="faint small mono">{row.matchId ?? 'local demo'}</div>
      </div>
      <div>
        <Kda k={row.kills} d={row.deaths} a={row.assists} />
        {row.team && <div className={`small team-${row.team.toLowerCase()}`}>{teamName(row.team)}</div>}
      </div>
      <div className="hl-col chips">
        {row.scan?.metadataStatus === 'Pending' && <span className="faint small">Reading match data…</span>}
        {row.highlights.slice(0, MAX_CHIPS).map((h, i) => (
          <HighlightChip
            key={i}
            type={h.type}
            kills={h.kills}
            timeSeconds={h.timeSeconds}
            extra={h.type === 'TeamFight' && h.playerKills != null ? `${h.playerKills}/${h.kills}` : undefined}
          />
        ))}
        {row.highlights.length > MAX_CHIPS && <span className="chip more">+{row.highlights.length - MAX_CHIPS}</span>}
      </div>
      <div className="score">
        <ScoreBadge score={row.score} />
      </div>
      <div className="action" onClick={(e) => e.stopPropagation()}>
        <Action row={row} daysLeft={daysLeft} onDownload={() => download.mutate()} pending={download.isPending} />
      </div>
    </div>
  )
}

function Action({ row, daysLeft, onDownload, pending }: { row: MatchRow; daysLeft: number | null; onDownload: () => void; pending: boolean }) {
  switch (row.status) {
    case 'ready':
      return (
        <Link className="button ghost sm" to={`/demos/${row.demoId}`}>
          Open
        </Link>
      )
    case 'downloading':
      return <span className="pill busy">Downloading</span>
    case 'parsing':
      return <span className="pill busy">Parsing</span>
    case 'expired':
      return <span className="faint small">Replay expired</span>
    case 'unscanned':
      return <span className="faint small">{row.demoStatus ?? ''}</span>
    default:
      return (
        <div>
          <button className="button sm" disabled={pending || row.matchId == null} onClick={onDownload} title="Downloads the replay (200-600 MB)">
            {row.status === 'failed' ? 'Retry' : 'Get demo'}
          </button>
          {daysLeft != null && <div className="faint small">{daysLeft}d left</div>}
        </div>
      )
  }
}
