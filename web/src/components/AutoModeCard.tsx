import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type AutoModeSettings } from '../api'

const time = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })

/** Hands-off clipping: scan history, download the best new matches, render your highlights. */
export default function AutoModeCard({ me }: { me: string | null }) {
  const queryClient = useQueryClient()
  const auto = useQuery({ queryKey: ['auto-mode'], queryFn: api.autoMode, refetchInterval: 10_000 })
  const onSaved = (data: Awaited<ReturnType<typeof api.autoMode>>) => queryClient.setQueryData(['auto-mode'], data)
  const save = useMutation({ mutationFn: api.saveAutoMode, onSuccess: onSaved })
  const run = useMutation({ mutationFn: api.runAutoMode, onSuccess: onSaved })

  if (!auto.data) return null
  const s = auto.data.settings
  const status = auto.data.status
  const update = (patch: Partial<AutoModeSettings>) => save.mutate({ ...s, steamId: s.steamId ?? me, ...patch })
  const num = (value: string, fallback: number) => (Number.isFinite(Number(value)) && value !== '' ? Number(value) : fallback)

  return (
    <section className="card">
      <div className="section-head">
        <h2>Auto mode</h2>
        <div className="head-actions">
          {s.enabled && (
            <button className="button ghost" disabled={run.isPending} onClick={() => run.mutate()}>
              Run now
            </button>
          )}
          <label className="toggle">
            <input
              type="checkbox"
              checked={s.enabled}
              disabled={!me || save.isPending}
              onChange={(e) => update({ enabled: e.target.checked, steamId: me })}
            />
            {s.enabled ? 'On' : 'Off'}
          </label>
        </div>
      </div>
      <p className="muted small">
        Scans your match history every {s.scanIntervalMinutes} minutes, downloads matches that score at least {s.minScore}{' '}
        (a double kill is about 6, a triple about 13, a big teamfight you carried 25+), and renders your highlights once
        they're parsed. Only matches played after you switch it on (plus {s.lookbackDays} days back) are touched.
      </p>
      {!me && <p className="muted small">Set your SteamID below first.</p>}

      <div className="auto-grid">
        <label className="small">
          Download matches scoring at least{' '}
          <input
            className="text-input num-input"
            type="number"
            min={0}
            defaultValue={s.minScore}
            onBlur={(e) => update({ minScore: num(e.target.value, s.minScore) })}
          />
        </label>
        <label className="small">
          At most{' '}
          <input
            className="text-input num-input"
            type="number"
            min={0}
            defaultValue={s.maxDownloadsPerDay}
            onBlur={(e) => update({ maxDownloadsPerDay: num(e.target.value, s.maxDownloadsPerDay) })}
          />{' '}
          downloads a day <span className="muted">(200-600 MB each)</span>
        </label>
        <label className="toggle small">
          <input type="checkbox" checked={s.renderMultikills} onChange={(e) => update({ renderMultikills: e.target.checked })} />
          Render my multikills and takedown streaks
        </label>
        <label className="toggle small">
          <input type="checkbox" checked={s.renderTeamfights} onChange={(e) => update({ renderTeamfights: e.target.checked })} />
          Render teamfights I fought in (2+ kills or assists)
        </label>
        <label className="toggle small">
          <input type="checkbox" checked={s.renderCompilation} onChange={(e) => update({ renderCompilation: e.target.checked })} />
          Render a kill compilation when I get{' '}
          <input
            className="text-input num-input"
            type="number"
            min={2}
            defaultValue={s.compilationMinKills}
            onBlur={(e) => update({ compilationMinKills: num(e.target.value, s.compilationMinKills) })}
          />{' '}
          + kills
        </label>
        <label className="toggle small">
          <input type="checkbox" checked={s.importLocalReplays} onChange={(e) => update({ importLocalReplays: e.target.checked })} />
          Also clip replays the game downloads
        </label>
      </div>

      {save.error && <p className="error small">{(save.error as Error).message}</p>}
      {s.enabled && (
        <p className="muted small">
          {status.lastScanAt ? `Last scan ${time(status.lastScanAt)}` : 'Not scanned yet'}
          {status.nextScanAt && ` · next ${time(status.nextScanAt)}`}
          {s.since && ` · clipping matches since ${new Date(s.since).toLocaleDateString()}`}
        </p>
      )}
      {status.lastError && <p className="error small">{status.lastError}</p>}
      {status.recent.length > 0 && (
        <ul className="auto-log small">
          {status.recent.slice(0, 8).map((a, i) => (
            <li key={i}>
              <span className="muted mono">{time(a.at)}</span> {a.message}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
