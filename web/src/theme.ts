/** Light or dark. Until the user picks one, the device setting decides (and keeps deciding when it changes). */
export type Theme = 'light' | 'dark'
const KEY = 'diskhaven-theme'
const darkQuery = () => window.matchMedia('(prefers-color-scheme: dark)')

/** The theme the user picked, or null when the page follows the device. */
export function savedTheme(): Theme | null {
  try {
    const t = localStorage.getItem(KEY)
    return t === 'light' || t === 'dark' ? t : null
  } catch { return null }
}

export const deviceTheme = (): Theme => (darkQuery().matches ? 'dark' : 'light')
export const effectiveTheme = (picked: Theme | null): Theme => picked ?? deviceTheme()

/** Picking the theme the device already uses goes back to following the device. */
export function pickTheme(t: Theme): Theme | null {
  const picked = t === deviceTheme() ? null : t
  if (picked) document.documentElement.setAttribute('data-theme', picked)
  else document.documentElement.removeAttribute('data-theme')
  try { picked ? localStorage.setItem(KEY, picked) : localStorage.removeItem(KEY) } catch { /* private mode */ }
  return picked
}

export function onDeviceThemeChange(run: () => void) {
  const q = darkQuery()
  q.addEventListener('change', run)
  return () => q.removeEventListener('change', run)
}
