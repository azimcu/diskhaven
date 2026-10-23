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

export type AfterSync = 'PowerOff' | 'Standby' | 'StayOn'

export interface SyncSchedule { enabled: boolean; time: string; lastRun: string | null }

export interface SyncSummary {
  finished: string; success: boolean; canceled: boolean; copied: number; updated: number; deleted: number
  verified: number; errors: number; bytesCopied: number; message: string | null
}

export interface Disk {
  id: string; kind: 'usb' | 'virtual'; label: string; name: string; fsType: string; size: number; model: string
  state: 'mounted' | 'attached' | 'off'; power: string; adopted: boolean; foreign: boolean
  usage: { total: number; free: number } | null
  folders: string[]; schedule: SyncSchedule | null; after: AfterSync | null; lastSync: SyncSummary | null
}

export interface SyncProgress { phase: string; filesDone: number; filesTotal: number; bytesDone: number; bytesTotal: number; current: string | null }
export interface SyncPlanSummary { createFolders: number; copy: number; update: number; delete: number; bytes: number; skipped: number }
export interface SyncReport {
  started: string; finished: string; created: number; copied: number; updated: number; deleted: number; verified: number
  bytesCopied: number; errors: { path: string; message: string }[]; skipped: string[]; canceled: boolean; success: boolean
}
export interface SyncJob {
  id: string; fsId: string; diskName: string; scheduled: boolean; started: string
  status: 'Running' | 'Done' | 'Failed' | 'Canceled'; phase: string
  progress: SyncProgress | null; report: SyncReport | null; error: string | null; plan: SyncPlanSummary | null
}
export interface SyncPlanResult {
  summary: SyncPlanSummary; free: number; total: number
  actions: { kind: string; path: string; size: number }[]; skipped: string[]
}
