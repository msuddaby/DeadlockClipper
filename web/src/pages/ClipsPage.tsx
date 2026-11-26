import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { api, type HighlightType, type RenderInfo } from '../api'
import ClipCard from '../components/ClipCard'
import HealthBanner from '../components/HealthBanner'
import VideoModal from '../components/VideoModal'
import { Empty, HeroArt } from '../components/ui'
import { formatClock, highlightLabel, useHeroNames } from '../format'
import { renderStatusText, useRenders } from '../renders'

type Kind = 'all' | 'multi' | 'takedown' | 'chat' | 'fight' | 'compilation'
const kindOf = (t: HighlightType): Kind =>
  t === 'TakedownStreak' ? 'takedown' : t === 'ChatKill' ? 'chat' : t === 'TeamFight' ? 'fight' : t === 'KillCompilation' ? 'compilation' : 'multi'
const kindLabel: Record<Kind, string> = { all: 'All', multi: 'Multikills', takedown: 'Takedowns', chat: 'Chat kills', fight: 'Teamfights', compilation: 'Compilations' }

export default function ClipsPage() {
  const renders = useRenders()
  const heroName = useHeroNames()
  const queryClient = useQueryClient()
  const [kind, setKind] = useState<Kind>('all')
  const [playing, setPlaying] = useState<string | null>(null)
  const remove = useMutation({
    mutationFn: api.deleteRender,
    onSuccess: () => {
      setPlaying(null)
      queryClient.invalidateQueries({ queryKey: ['renders'] })
    },
  })

  const all = renders.data ?? []
  const active = all.filter((r) => r.status === 'Queued' || r.status === 'Rendering')
  const failed = all.filter((r) => r.status === 'Failed')
  const done = all.filter((r) => r.status === 'Done' && r.hasVideo)
  const kinds = (Object.keys(kindLabel) as Kind[]).filter((k) => k === 'all' || done.some((r) => kindOf(r.highlightType) === k))
  const shown = kind === 'all' ? done : done.filter((r) => kindOf(r.highlightType) === kind)
  const playingClip = done.find((r) => r.id === playing)

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>Clips</h1>
          <p className="muted">
            {done.length} {done.length === 1 ? 'clip' : 'clips'}
            {active.length > 0 && ` · ${active.length} rendering`}
          </p>
        </div>
        {kinds.length > 1 && (
          <div className="segmented">
            {kinds.map((k) => (
              <button key={k} className={kind === k ? 'on' : ''} onClick={() => setKind(k)}>
                {kindLabel[k]}
              </button>
            ))}
          </div>
        )}
      </div>

      <HealthBanner />

      {active.length > 0 && (
        <section className="card flush">
          <div className="section-head">
            <h2>Rendering</h2>
          </div>
          {active.map((r) => (
            <QueueRow key={r.id} r={r} heroName={heroName} />
          ))}
        </section>
      )}

      {renders.isLoading ? (
        <p className="muted">Loading…</p>
      ) : shown.length === 0 ? (
        <div className="card">
          <Empty title="No clips yet">
            <p className="small">Open a downloaded match and render a highlight, or switch on auto mode in Settings.</p>
          </Empty>
        </div>
      ) : (
        <div className="clip-grid">
          {shown.map((r) => (
            <ClipCard key={r.id} r={r} onOpen={() => setPlaying(r.id)} />
          ))}
        </div>
      )}

      {failed.length > 0 && (
        <details className="card flush">
          <summary className="section-head" style={{ padding: '14px 18px', margin: 0, cursor: 'pointer' }}>
            <h2>Failed ({failed.length})</h2>
          </summary>
          {failed.map((r) => (
            <div key={r.id} className="queue-row">
              <HeroArt heroId={r.heroId} name={heroName(r.heroId)} />
              <div className="grow">
                <div>
                  <strong>{highlightLabel[r.highlightType]}</strong> <span className="muted">· {r.playerName}</span>
                </div>
                <div className="error small">{r.error}</div>
              </div>
              <Link className="button ghost sm" to={`/demos/${r.demoId}`}>
                Open match
              </Link>
              <button className="button ghost sm danger" onClick={() => remove.mutate(r.id)}>
                Remove
              </button>
            </div>
          ))}
        </details>
      )}

      {playingClip && (
        <VideoModal renderId={playingClip.id} onClose={() => setPlaying(null)}>
          <div className="grow">
            <strong>{highlightLabel[playingClip.highlightType]}</strong> · {playingClip.playerName}{' '}
            <span className="muted small">
              ({heroName(playingClip.heroId)}) · {formatClock(playingClip.gameTimeSeconds)} · match {playingClip.matchId}
            </span>
          </div>
          <Link className="button ghost sm" to={`/demos/${playingClip.demoId}`}>
            Open match
          </Link>
          <button className="button ghost sm danger" disabled={remove.isPending} onClick={() => remove.mutate(playingClip.id)}>
            Delete
          </button>
        </VideoModal>
      )}
    </div>
  )
}

function QueueRow({ r, heroName }: { r: RenderInfo; heroName: (id: number) => string }) {
  return (
    <div className="queue-row">
      <HeroArt heroId={r.heroId} name={heroName(r.heroId)} />
      <div className="grow">
        <div>
          <strong>{highlightLabel[r.highlightType]}</strong> <span className="muted">· {r.playerName}</span>
        </div>
        <div className="faint small">
          {r.matchId ? `Match ${r.matchId}` : 'Demo'} · {formatClock(r.gameTimeSeconds)}
        </div>
      </div>
      <span className="pill busy">{renderStatusText(r)}</span>
    </div>
  )
}
