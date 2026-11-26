import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link, NavLink } from 'react-router'
import { api } from '../api'
import { useMyName, usePlayerLabel } from '../matches'
import { seedPlayers, setActivePlayer, useMe, usePlayers } from '../players'
import { useRenders } from '../renders'
import { HeroArt, Icon } from './ui'

export default function Layout({ children }: { children: ReactNode }) {
  const [me] = useMe()
  // The player list lives in this browser; the server knows whose histories it scanned, so those are added once.
  const scannedPlayers = useQuery({ queryKey: ['scanned-players'], queryFn: api.scannedPlayers })
  useEffect(() => {
    if (scannedPlayers.data) seedPlayers(scannedPlayers.data)
  }, [scannedPlayers.data])
  const name = useMyName(me)
  const renders = useRenders()
  const busy = (renders.data ?? []).filter((r) => r.status === 'Queued' || r.status === 'Rendering').length
  const scanned = useQuery({ queryKey: ['matches', me], queryFn: () => api.matches(me!), enabled: !!me })
  const lastHero = scanned.data?.[0]?.heroId ?? 0

  return (
    <div className="shell">
      <aside className="sidebar">
        <Link to="/" className="brand">
          <span className="brand-mark">◆</span> Deadlock Clipper
        </Link>
        <NavLink to="/" end className="nav-link">
          <Icon name="home" /> <span className="label">Home</span>
        </NavLink>
        <NavLink to="/matches" className="nav-link">
          <Icon name="matches" /> <span className="label">Matches</span>
        </NavLink>
        <NavLink to="/clips" className="nav-link">
          <Icon name="clips" /> <span className="label">Clips</span>
          {busy > 0 && <span className="nav-count" title={`${busy} rendering`}>{busy}</span>}
        </NavLink>
        <NavLink to="/settings" className="nav-link">
          <Icon name="settings" /> <span className="label">Settings</span>
        </NavLink>
        <div className="sidebar-foot">
          <PlayerSwitcher>
            <HeroArt heroId={lastHero} name={name ?? 'Player'} />
            <div style={{ minWidth: 0 }}>
              <div className="name">{name ?? (me ? `Player …${me.slice(-6)}` : 'Add a player')}</div>
              <div className="faint small">{me ? 'Switch player' : 'Settings'}</div>
            </div>
          </PlayerSwitcher>
        </div>
      </aside>
      <main className="content">{children}</main>
    </div>
  )
}

/** The sidebar chip: shows who the pages are for and switches between tracked players. */
function PlayerSwitcher({ children }: { children: ReactNode }) {
  const { players, active } = usePlayers()
  const label = usePlayerLabel()
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const close = (e: MouseEvent) => ref.current && !ref.current.contains(e.target as Node) && setOpen(false)
    const esc = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('mousedown', close)
    document.addEventListener('keydown', esc)
    return () => {
      document.removeEventListener('mousedown', close)
      document.removeEventListener('keydown', esc)
    }
  }, [open])

  if (players.length === 0)
    return (
      <Link to="/settings#players" className="profile-chip">
        {children}
      </Link>
    )
  return (
    <div className="player-switcher" ref={ref}>
      {open && (
        <div className="player-menu" role="menu">
          {players.map((p) => (
            <button
              key={p.steamId}
              role="menuitemradio"
              aria-checked={p.steamId === active}
              className={p.steamId === active ? 'on' : ''}
              onClick={() => {
                setActivePlayer(p.steamId)
                setOpen(false)
              }}
            >
              <span className="grow">{label(p.steamId)}</span>
              {p.steamId === active && <span aria-hidden>✓</span>}
            </button>
          ))}
          <Link to="/settings#players" className="add" onClick={() => setOpen(false)}>
            + Add or manage players
          </Link>
        </div>
      )}
      <button className="profile-chip" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        {children}
      </button>
    </div>
  )
}
