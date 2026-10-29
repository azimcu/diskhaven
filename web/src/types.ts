export type EntryKind = 'Folder' | 'File'

export interface FileEntry {
  name: string
  path: string
  kind: EntryKind
  size: number | null
  modified: string
  extension: string | null
  hidden: boolean
  /** set for items that do not live in the NAS tree (hidden folder): where to load the content from */
  src?: string
  downloadSrc?: string
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
  isAdmin?: boolean
  /** set on the public demo instance: the sign-in shown on the login page */
  demo?: { userName: string; password: string } | null
}

export interface Share {
  id: string; token: string; path: string; kind: EntryKind; createdBy: string
  createdAt: string; expiresAt: string | null; downloads: number
}

export interface PublicItem {
  name: string; kind: EntryKind; size: number | null; by: string; expiresAt: string | null
  entries: FileEntry[] | null
}

export interface UserInfo { userName: string; isAdmin: boolean; createdAt: string }

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
export interface SyncPlanSummary { createFolders: number; copy: number; update: number; delete: number; bytes: number; skipped: number; move: number }
export interface SyncReport {
  started: string; finished: string; created: number; copied: number; updated: number; deleted: number; verified: number; moved: number
  bytesCopied: number; errors: { path: string; message: string }[]; skipped: string[]; canceled: boolean; success: boolean
}
export interface SyncJob {
  id: string; fsId: string; diskName: string; scheduled: boolean; started: string
  status: 'Running' | 'Done' | 'Failed' | 'Canceled'; phase: string
  progress: SyncProgress | null; report: SyncReport | null; error: string | null; plan: SyncPlanSummary | null
}
export interface SyncPlanResult {
  summary: SyncPlanSummary; free: number; total: number
  actions: { kind: string; path: string; size: number; targetPath: string | null }[]; skipped: string[]
}

export interface SmartInfo {
  available: boolean; passed?: boolean | null; temperature?: number | null; powerOnHours?: number | null
  reallocated?: number | null; pending?: number | null; uncorrectable?: number | null
}

export interface ItemProperties {
  entry: FileEntry; created: string | null; accessed: string; readOnly: boolean; sizeOnDisk: number | null; diskFree: number | null
}

export interface SystemSample {
  at: string; cpu: number | null; memUsed: number | null; memTotal: number | null; temperature: number | null
  readPerSec: number | null; writePerSec: number | null; cpuHistory: number[]
}

export interface PluginInfo { id: string; name: string; description: string; category: string; version: string; features: string[]; installed: boolean }
export interface AdminOverview {
  version: string; startedAt: string; demo: boolean; storage: { total: number; free: number }; users: number; shares: number; trash: number
  plugins: number; system: SystemSample; dotnet: string; os: string
}
export interface ActivityItem { at: string; user: string; action: string; paths: string[]; detail: string | null; ip: string | null }
export interface HideawayState { setUp: boolean; unlocked: boolean; label: string; suggestions: string[]; idleMinutes: number }
