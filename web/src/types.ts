export type EntryKind = 'Folder' | 'File'

export interface FileEntry {
  name: string
  path: string
  kind: EntryKind
  size: number | null
  modified: string
  extension: string | null
  hidden: boolean
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
