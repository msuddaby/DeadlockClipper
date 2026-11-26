import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type AutoModeSettings } from '../api'
import { usePlayerLabel } from '../matches'
import { usePlayers } from '../players'

const time = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })

/** Hands-off clipping: scan the chosen players' histories, download the best new matches, render their highlights. */
export default function AutoModeCard() {
  const { players, active } = usePlayers()
  const label = usePlayerLabel()
  const queryClient = useQueryClient()
  const auto = useQuery({ queryKey: ['auto-mode'], queryFn: api.autoMode, refetchInterval: 10_000 })
  const onSaved = (data: Awaited<ReturnType<typeof api.autoMode>>) => queryClient.setQueryData(['auto-mode'], data)
  const save = useMutation({ mutationFn: api.saveAutoMode, onSuccess: onSaved })
  const run = useMutation({ mutationFn: api.runAutoMode, onSuccess: onSaved })

  if (!auto.data) return null
  const s = auto.data.settings
  const status = auto.data.status
  const update = (patch: Partial<AutoModeSettings>) => save.mutate({ ...s, ...patch })
  // Players auto mode clips but who aren't in this browser's list still show, so they can be switched off.
  const choices = [...new Set([...players.map((p) => p.steamId), ...s.steamIds])]
  const toggleEnabled = (enabled: boolean) =>
    update({ enabled, steamIds: enabled && s.steamIds.length === 0 && active ? [active] : s.steamIds })
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
              disabled={save.isPending || (!s.enabled && s.steamIds.length === 0 && !active)}
              onChange={(e) => toggleEnabled(e.target.checked)}
            />
            {s.enabled ? 'On' : 'Off'}
          </label>
        </div>
      </div>
      <p className="muted small">
        Scans the chosen players' match histories every {s.scanIntervalMinutes} minutes, downloads matches that score at
        least {s.minScore} (a double kill is about 6, a triple about 13, a big teamfight someone carried 25+), and renders
        their highlights once they're parsed. Only matches played after you switch it on (plus {s.lookbackDays} days back)
        are touched.
      </p>
      {choices.length === 0 ? (
        <p className="muted small">Add a player above first.</p>
      ) : (
        <div className="inline-form" style={{ margin: '4px 0 12px' }}>
          <span className="small muted">Clip matches of</span>
          {choices.map((id) => (
            <label key={id} className="toggle small">
              <input
                type="checkbox"
                checked={s.steamIds.includes(id)}
                disabled={save.isPending}
                onChange={(e) =>
                  update({
                    steamIds: e.target.checked ? [...s.steamIds, id] : s.steamIds.filter((x) => x !== id),
                    // Unticking the last player turns auto mode off rather than leaving it on with nobody to clip.
                    enabled: s.enabled && (e.target.checked || s.steamIds.length > 1),
                  })
                }
              />
              {label(id)}
            </label>
          ))}
        </div>
      )}

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
          Render their multikills and takedown streaks
        </label>
        <label className="toggle small">
          <input type="checkbox" checked={s.renderChatKills} onChange={(e) => update({ renderChatKills: e.target.checked })} />
          Render single kills the victim typed in chat about
        </label>
        <label className="toggle small">
          <input type="checkbox" checked={s.renderTeamfights} onChange={(e) => update({ renderTeamfights: e.target.checked })} />
          Render teamfights they fought in (2+ kills or assists)
        </label>
        <label className="toggle small">
          <input type="checkbox" checked={s.renderCompilation} onChange={(e) => update({ renderCompilation: e.target.checked })} />
          Render a kill compilation when someone gets{' '}
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
