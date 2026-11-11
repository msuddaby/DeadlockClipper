import { useQuery } from '@tanstack/react-query'
import { useCallback, useState } from 'react'
import { api, type HighlightType } from './api'

export function formatClock(seconds: number): string {
  const s = Math.max(0, Math.round(seconds))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = String(s % 60).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`
}

export function formatBytes(bytes: number): string {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(0)} MB`
  return `${(bytes / 1024).toFixed(0)} KB`
}

export function formatDate(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

export const highlightLabel: Record<HighlightType, string> = {
  DoubleKill: 'Double Kill',
  TripleKill: 'Triple Kill',
  QuadKill: 'Quad Kill',
  PentaKill: 'Penta Kill',
  MegaKill: 'Mega Kill',
}

/** Hero names come from the backend's cached catalog; falls back to the numeric id when offline. */
export function useHeroNames() {
  const { data } = useQuery({ queryKey: ['heroes'], queryFn: api.heroes, staleTime: Infinity })
  return useCallback(
    (id: number | null | undefined) => {
      if (!id) return 'Unknown'
      return data?.find((h) => h.id === id)?.name ?? `Hero #${id}`
    },
    [data],
  )
}

const ME_KEY = 'deadlockclipper.me'

/** The viewer's own Steam ID, remembered per browser. */
export function useMe(): [string | null, (steamId: string | null) => void] {
  const [me, setMeState] = useState<string | null>(() => {
    try {
      return localStorage.getItem(ME_KEY)
    } catch {
      return null
    }
  })
  const setMe = useCallback((steamId: string | null) => {
    setMeState(steamId)
    try {
      if (steamId) localStorage.setItem(ME_KEY, steamId)
      else localStorage.removeItem(ME_KEY)
    } catch {
      // storage unavailable; keep in-memory value
    }
  }, [])
  return [me, setMe]
}
