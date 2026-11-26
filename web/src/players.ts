import { useCallback, useSyncExternalStore } from 'react'

/** A player whose matches you clip: you, or anyone on a clipper account's Steam friends list. */
export interface TrackedPlayer {
  steamId: string
  /** Steam name when known (from the friends list or a parsed demo). */
  name?: string | null
}

interface State {
  players: TrackedPlayer[]
  /** The player the pages show ("viewing as"). */
  active: string | null
}

const PLAYERS_KEY = 'deadlockclipper.players'
const ACTIVE_KEY = 'deadlockclipper.me' // the key from when there was only "me"
const SEEDED_KEY = 'deadlockclipper.players.seeded'

function read(): State {
  try {
    const active = localStorage.getItem(ACTIVE_KEY)
    const raw = localStorage.getItem(PLAYERS_KEY)
    let players: TrackedPlayer[] = raw ? JSON.parse(raw) : []
    if (!Array.isArray(players)) players = []
    players = players.filter((p) => p && typeof p.steamId === 'string')
    if (active && !players.some((p) => p.steamId === active)) players = [{ steamId: active }, ...players]
    return { players, active }
  } catch {
    return { players: [], active: null }
  }
}

let state: State = read()
const listeners = new Set<() => void>()

function write(next: State) {
  state = next
  try {
    localStorage.setItem(PLAYERS_KEY, JSON.stringify(next.players))
    if (next.active) localStorage.setItem(ACTIVE_KEY, next.active)
    else localStorage.removeItem(ACTIVE_KEY)
  } catch {
    // storage unavailable: keep the in-memory state
  }
  listeners.forEach((l) => l())
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  const onStorage = (e: StorageEvent) => {
    if (e.key === PLAYERS_KEY || e.key === ACTIVE_KEY) {
      state = read()
      listener()
    }
  }
  window.addEventListener('storage', onStorage)
  return () => {
    listeners.delete(listener)
    window.removeEventListener('storage', onStorage)
  }
}

const snapshot = () => state

export function usePlayers() {
  return useSyncExternalStore(subscribe, snapshot)
}

/** Adds the player (or updates their name) without switching to them. */
export function addPlayer(steamId: string, name?: string | null) {
  const existing = state.players.find((p) => p.steamId === steamId)
  if (existing) {
    if (name && existing.name !== name)
      write({ ...state, players: state.players.map((p) => (p.steamId === steamId ? { ...p, name } : p)) })
    return
  }
  write({ ...state, players: [...state.players, { steamId, name: name ?? null }], active: state.active ?? steamId })
}

export function setActivePlayer(steamId: string | null, name?: string | null) {
  if (steamId) addPlayer(steamId, name)
  write({ ...state, active: steamId })
}

export function removePlayer(steamId: string) {
  const players = state.players.filter((p) => p.steamId !== steamId)
  write({ players, active: state.active === steamId ? (players[0]?.steamId ?? null) : state.active })
}

/** Players scanned before the list existed: added once, so removing one later sticks. */
export function seedPlayers(steamIds: string[]) {
  try {
    if (localStorage.getItem(SEEDED_KEY)) return
    localStorage.setItem(SEEDED_KEY, '1')
  } catch {
    return
  }
  for (const id of steamIds) addPlayer(id)
}

/** The player the pages are showing, shared by every component (the old per-browser "me"). */
export function useMe(): [string | null, (steamId: string | null) => void] {
  const { active } = usePlayers()
  const set = useCallback((steamId: string | null) => setActivePlayer(steamId), [])
  return [active, set]
}

const STEAM64_BASE = 76561197960265728n

/**
 * A SteamID64 from what people paste: the SteamID64 itself, a friend code (the account id Steam shows under
 * "Add a friend"), [U:1:n], or a steamcommunity.com/profiles/ link. Custom /id/ URLs need Steam's web API, so not those.
 */
export function parseSteamId(input: string): string | null {
  const s = input.trim()
  const profile = s.match(/steamcommunity\.com\/profiles\/(\d{17})/)
  if (profile) return profile[1]
  const u = s.match(/^\[?U:1:(\d+)\]?$/i)
  const digits = u ? u[1] : /^\d+$/.test(s) ? s : null
  if (!digits) return null
  const n = BigInt(digits)
  if (/^7656\d{13}$/.test(digits)) return digits
  if (n > 0n && n < 2n ** 32n) return (STEAM64_BASE + n).toString()
  return null
}
