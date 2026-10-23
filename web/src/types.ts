export type EntryKind = 'Folder' | 'File'

export interface FileEntry {
  name: string
  path: string
  kind: EntryKind
  size: number | null
  modified: string
  extension: string | null
  hidden: boolean
  /** folders only: recursive totals, null while being calculated */
  files?: number | null
  folders?: number | null
}

export interface Listing {
  path: string
  entries: FileEntry[]
}

export interface TrashItem {
  id: string
  name: string
  originalPath: string
  kind: EntryKind
  size: number | null
  deletedAt: string
}

export interface AuthState {
  setupRequired: boolean
  user: string | null
}
