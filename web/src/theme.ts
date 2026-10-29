/** Light / dark / follow-the-system theme, remembered per browser. */
export type Theme = 'system' | 'light' | 'dark'
const KEY = 'diskhaven-theme'

export function savedTheme(): Theme {
  try {
    const t = localStorage.getItem(KEY)
    return t === 'light' || t === 'dark' ? t : 'system'
  } catch { return 'system' }
}

export function applyTheme(t: Theme) {
  if (t === 'system') document.documentElement.removeAttribute('data-theme')
  else document.documentElement.setAttribute('data-theme', t)
  try { t === 'system' ? localStorage.removeItem(KEY) : localStorage.setItem(KEY, t) } catch { /* private mode */ }
}

export const nextTheme = (t: Theme): Theme => (t === 'system' ? 'light' : t === 'light' ? 'dark' : 'system')
