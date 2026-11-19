export type EntryKind = 'Folder' | 'File'

export interface FileEntry {
  name: string
  path: string
  kind: EntryKind
  size: number | null
  modified: string
  extension: string | null
  hidden: boolean
  /** listed but cannot be opened: 'bad-name' (name not valid UTF-8), 'special' (pipe, socket, device) */
  problem?: 'bad-name' | 'special' | null
  /** the drive the item is on: missing or "" = main storage, otherwise a disk id (added by the client after listing) */
  drive?: string
  /** set for items that do not live in the NAS tree (hidden folder): where to load the content from */
  src?: string
  downloadSrc?: string
  /** folders only: recursive totals, null while being calculated */
  files?: number | null
  folders?: number | null
}

export interface FileVersion { id: string; savedAt: string; size: number }

/** What a person may do with an item: None = only the way to something shared inside it. */
export type AccessLevel = 'None' | 'Read' | 'Write' | 'Owner'

export interface Listing {
  path: string
  entries: FileEntry[]
  /** what the signed-in person may do in this folder (admins: Owner) */
  access?: AccessLevel
  /** names of the items here that carry a permission of their own (for admins and owners) */
  shared?: string[] | null
}

/** A permission: <principal> ("*" = everyone) has <level> on <path> of <drive> and everything inside it. */
export interface Grant {
  id: string; drive: string; path: string; principal: string; level: AccessLevel; createdBy: string; createdAt: string
  driveName?: string
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
  role?: 'admin' | 'user' | null
  /** set on the public demo instance: the sign-in shown on the login page */
  demo?: { userName: string; password: string } | null
}

export type ShareMode = 'View' | 'UploadOnly'

export interface Share {
  id: string; token: string; path: string; kind: EntryKind; createdBy: string
  createdAt: string; expiresAt: string | null; downloads: number
  mode: ShareMode; hasPassword: boolean; uploads: number
}

/** A link's public view; `locked` alone means the password has not been entered yet. */
export type PublicItem = { locked: true } | {
  locked?: false; name: string; kind: EntryKind; size: number | null; by: string; expiresAt: string | null
  mode: ShareMode; entries: FileEntry[] | null
}

export interface UserInfo { userName: string; isAdmin: boolean; createdAt: string }

export type AfterSync = 'PowerOff' | 'Standby' | 'StayOn'

export interface SyncSchedule { enabled: boolean; time: string; lastRun: string | null }

export interface SyncSummary {
  finished: string; success: boolean; canceled: boolean; copied: number; updated: number; deleted: number
  verified: number; errors: number; bytesCopied: number; message: string | null
}

export interface Disk {
  id: string; kind: 'usb' | 'sata' | 'nvme' | 'virtual'; label: string; name: string; fsType: string; size: number; model: string
  state: 'mounted' | 'attached' | 'off' | 'system'; power: string; adopted: boolean; foreign: boolean
  usage: { total: number; free: number } | null
  folders: string[]; schedule: SyncSchedule | null; after: AfterSync | null; lastSync: SyncSummary | null
  /** in use by the server (system, photos, server backup) or protected: only shown, never touched */
  system: boolean; systemReason: string | null; systemMount: string | null
  /** the owner's setting (new disks start read-only) and what the kernel reports for the current mount */
  readOnly: boolean; mountedReadOnly: boolean | null; registered: boolean
}

/** A drive in the side bar: the main storage (id "") or a disk. */
export interface DriveInfo {
  id: string; name: string; kind: 'main' | 'usb' | 'sata' | 'nvme' | 'virtual'; state: Disk['state']; readOnly: boolean
  system: boolean; systemMount?: string | null; total: number; free: number | null; fsType?: string
  /** what the signed-in person may do at the drive's top (None = only the way to something shared inside it) */
  access?: AccessLevel
}

export interface TransferProgress { filesDone: number; filesTotal: number; bytesDone: number; bytesTotal: number; current: string | null }
export interface TransferReport {
  created: string[]; filesCopied: number; bytesCopied: number; sourcesMovedToTrash: number
  errors: { path: string; message: string }[]; skipped: string[]; canceled: boolean; success: boolean
  /** names the target disk saw as taken (letter case only), with the name they got: "a/Rapor.txt -> Rapor (2).txt" */
  renamed?: string[] | null
}
export interface TransferJob {
  id: string; fromDrive: string; toDrive: string; move: boolean; paths: string[]; destination: string; started: string
  status: 'Running' | 'Done' | 'Failed' | 'Canceled'; progress: TransferProgress | null; report: TransferReport | null; error: string | null
}

export interface SyncProgress { phase: string; filesDone: number; filesTotal: number; bytesDone: number; bytesTotal: number; current: string | null }
export interface SyncPlanSummary {
  createFolders: number; copy: number; update: number; delete: number; bytes: number; skipped: number; move: number
  sourceEmpty: boolean
}
export interface SyncReport {
  started: string; finished: string; created: number; copied: number; updated: number; deleted: number; verified: number; moved: number
  bytesCopied: number; errors: { path: string; message: string }[]; skipped: string[]; canceled: boolean; success: boolean
  /** names the target disk stored differently (letter case, trailing space) */
  renamed?: string[] | null
}

/** What the last run of a backup did; the full reports are in its history. */
export interface BackupSummary {
  finished: string; success: boolean; canceled: boolean; awaitingApproval: boolean
  copied: number; updated: number; deleted: number; skipped: number; errors: number
  bytesCopied: number; durationSeconds: number; message: string | null
}
/** A one-way mirror: chosen folders of a source drive into a folder of a target drive. */
export interface BackupJob {
  id: string; name: string; sourceDrive: string; sourceFolders: string[]
  targetDrive: string; targetFolder: string; enabled: boolean; time: string; deleteThresholdPercent: number
  createdAt: string; lastRun: BackupSummary | null; lastSuccessAt: string | null
  legacy: boolean; adoptExisting: boolean
}
export interface BackupBody {
  name: string; sourceDrive: string; sourceFolders: string[]; targetDrive: string; targetFolder: string
  enabled: boolean; time: string; deleteThresholdPercent: number; adoptExisting: boolean
}
export interface BackupRun {
  id: string; jobId: string; jobName: string; scheduled: boolean; started: string
  status: 'Running' | 'Done' | 'Failed' | 'Canceled' | 'AwaitingApproval'; phase: string
  progress: SyncProgress | null; plan: SyncPlanSummary | null; report: SyncReport | null
  error: string | null; message: string | null
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
