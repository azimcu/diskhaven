import type { FileEntry } from './types'
import { t } from './strings'

export function formatSize(bytes: number | null | undefined): string {
  if (bytes == null) return ''
  if (bytes < 1024) return `${bytes} bayt`
  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes / 1024
  let i = 0
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024
    i++
  }
  return `${value.toLocaleString('tr-TR', { maximumFractionDigits: value < 10 ? 1 : 0 })} ${units[i]}`
}

const dateFormat = new Intl.DateTimeFormat('tr-TR', { dateStyle: 'short', timeStyle: 'short' })
export const formatDate = (iso: string) => dateFormat.format(new Date(iso))

export const typeName = (e: Pick<FileEntry, 'kind' | 'extension'>) => (e.kind === 'Folder' ? t.folderType : t.fileType(e.extension))

export const parentOf = (path: string) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')
export const nameOf = (path: string) => path.slice(path.lastIndexOf('/') + 1)
export const join = (folder: string, name: string) => (folder ? `${folder}/${name}` : name)

export type PreviewKind = 'image' | 'video' | 'audio' | 'pdf' | 'text' | null
const kinds: Record<string, PreviewKind> = {}
for (const e of ['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg', 'avif', 'heic']) kinds[e] = 'image'
for (const e of ['mp4', 'webm', 'mov', 'm4v', 'mkv']) kinds[e] = 'video'
for (const e of ['mp3', 'wav', 'ogg', 'm4a', 'flac', 'aac', 'opus']) kinds[e] = 'audio'
kinds.pdf = 'pdf'
for (const e of ['txt', 'md', 'log', 'json', 'xml', 'csv', 'ini', 'yml', 'yaml', 'ts', 'tsx', 'js', 'cs', 'py', 'ino', 'c', 'cpp', 'h', 'html', 'css', 'sh', 'conf'])
  kinds[e] = 'text'
export const previewKind = (ext: string | null): PreviewKind => (ext ? kinds[ext] ?? null : null)

/** Name for a new item that does not clash with existing names (case-insensitive), Windows style. */
export function uniqueName(existing: string[], base: string): string {
  const taken = new Set(existing.map(n => n.toLocaleLowerCase('tr-TR')))
  if (!taken.has(base.toLocaleLowerCase('tr-TR'))) return base
  for (let i = 2; ; i++) {
    const candidate = `${base} (${i})`
    if (!taken.has(candidate.toLocaleLowerCase('tr-TR'))) return candidate
  }
}
