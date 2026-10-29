import { useEffect, useState } from 'react'
import { api } from './api'

/** Installed plugins, loaded once after sign-in and refreshed when an admin installs or removes one. */
let state: { installed: Set<string>; vaultLabel: string | null; loaded: boolean } = { installed: new Set(), vaultLabel: null, loaded: false }
const listeners = new Set<() => void>()

export async function loadPlugins() {
  try {
    const r = await api.plugins()
    state = { installed: new Set(r.installed), vaultLabel: r.vaultLabel, loaded: true }
  } catch { state = { ...state, loaded: true } }
  listeners.forEach(l => l())
}

export const hasPlugin = (id: string) => state.installed.has(id)

export function usePlugins() {
  const [, redraw] = useState(0)
  useEffect(() => { const l = () => redraw(n => n + 1); listeners.add(l); return () => { listeners.delete(l) } }, [])
  return { has: hasPlugin, vaultLabel: state.vaultLabel, loaded: state.loaded }
}
