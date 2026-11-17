import { api, csrfHeader } from '../api'

export interface Media { path: string; album: string; name: string; size: number; modified: number; taken: number; video: boolean }
export interface AlbumInfo { path: string; name: string; count: number; videos: number; cover: string; latest: number; hidden: boolean; pinned: boolean }
export interface TreeFolder { path: string; count: number; selected: boolean }
export interface AlbumsResult { albums: AlbumInfo[]; scanning: boolean; lastScan: string | null; newAlbumParent: string }

async function call<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method, credentials: 'same-origin',
    headers: { ...csrfHeader, ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (res.status === 401) window.dispatchEvent(new Event('diskhaven:unauthorized'))
  if (!res.ok) {
    let msg = res.statusText
    try { const p = await res.json(); msg = p.detail || p.title || msg } catch { /* not JSON */ }
    if (res.status === 404 && url.startsWith('/api/gallery')) msg = 'Galeri eklentisi kurulu değil. DiskHaven → Yönetim → Mağaza\'dan "Galeri"yi kur.'
    throw new Error(msg)
  }
  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

const q = encodeURIComponent

export const gapi = {
  albums: () => call<AlbumsResult>('GET', '/api/gallery/albums'),
  media: (album: string | null, offset: number, limit = 300) =>
    call<{ total: number; items: Media[] }>('GET', `/api/gallery/media?offset=${offset}&limit=${limit}${album !== null ? `&album=${q(album)}` : ''}`),
  scan: () => call<{ count: number; scanning: boolean }>('POST', '/api/gallery/scan'),
  forget: (paths: string[]) => call<void>('POST', '/api/gallery/forget', { paths }),
  settings: (s: { hidden?: string[]; pinned?: string[]; newAlbumParent?: string }) => call<void>('PUT', '/api/gallery/settings', s),
  tree: () => call<{ folders: TreeFolder[]; scanning: boolean }>('GET', '/api/gallery/tree'),
  select: (add: string[], remove: string[] = []) => call<void>('POST', '/api/gallery/albums/select', { add, remove }),
  thumb: (path: string, size: 's' | 'm' = 's') => `/api/gallery/thumb?path=${q(path)}&size=${size}`,
  original: (path: string, download = false) => api.contentUrl(path, download),
}

// names a browser shows itself; others (HEIC, TIFF...) only through the server-made preview
const webImage = /\.(jpe?g|png|gif|webp|avif|bmp)$/i
export const browserCanShow = (name: string) => webImage.test(name)

const day = new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'long', year: 'numeric', weekday: 'short' })
const dayNoYear = new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'long', weekday: 'short' })
const full = new Intl.DateTimeFormat('tr-TR', { dateStyle: 'long', timeStyle: 'short' })

export function dayKey(ms: number) {
  const d = new Date(ms)
  return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`
}

export function dayLabel(ms: number) {
  const d = new Date(ms)
  const today = new Date(); today.setHours(0, 0, 0, 0)
  const that = new Date(d); that.setHours(0, 0, 0, 0)
  const diff = Math.round((today.getTime() - that.getTime()) / 86400000)
  if (diff === 0) return 'Bugün'
  if (diff === 1) return 'Dün'
  return d.getFullYear() === today.getFullYear() ? dayNoYear.format(d) : day.format(d)
}

export const fullDate = (ms: number) => full.format(new Date(ms))

export function sizeText(b: number) {
  if (b < 1024) return `${b} B`
  const u = ['KB', 'MB', 'GB', 'TB']
  let v = b / 1024, i = 0
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++ }
  return `${v.toLocaleString('tr-TR', { maximumFractionDigits: v < 10 ? 1 : 0 })} ${u[i]}`
}

export const parentOf = (p: string) => (p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '')
export const nameOf = (p: string) => p.slice(p.lastIndexOf('/') + 1)
export const albumTitle = (path: string) => (path === '' ? 'Ana klasör' : nameOf(path))
