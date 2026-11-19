import type { AccessLevel, Grant, ActivityItem, AdminOverview, AfterSync, AuthState, Disk, DriveInfo, FileEntry, TransferJob, FileVersion, HideawayState, ItemProperties, Listing, PluginInfo, SystemSample, PublicItem, Share, ShareMode, BackupJob, BackupBody, BackupRun, SmartInfo, SyncReport, SyncSchedule, TrashItem, UserInfo } from './types'
import { translateServerMessage } from './strings'

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message)
  }
}

/** Every state-changing call carries this header; the server rejects calls without it (CSRF guard). */
export const csrfHeader = { 'X-DiskHaven': '1' }

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  let res: Response
  try {
    res = await fetch(url, {
      method,
      credentials: 'same-origin',
      headers: { ...csrfHeader, ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })
  } catch {
    throw new ApiError(translateServerMessage('network'), 0)
  }
  if (res.status === 401 && !url.startsWith('/api/auth')) window.dispatchEvent(new Event('diskhaven:unauthorized'))
  if (!res.ok) {
    let message = res.statusText
    try {
      const problem = await res.json()
      message = problem.detail || problem.title || message
    } catch { /* not JSON */ }
    throw new ApiError(translateServerMessage(message, res.status), res.status)
  }
  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

const q = (path: string) => encodeURIComponent(path)
/** "&drive=<id>" for a disk; nothing for the main storage, so main-storage calls are exactly as before */
const dq = (drive?: string) => (drive ? `&drive=${encodeURIComponent(drive)}` : '')
const db = (drive?: string) => (drive ? { drive } : {})

export const api = {
  authState: () => request<AuthState>('GET', '/api/auth/state'),
  setup: (token: string, userName: string, password: string) =>
    request<{ user: string }>('POST', '/api/auth/setup', { token, userName, password }),
  login: (userName: string, password: string) => request<{ user: string }>('POST', '/api/auth/login', { userName, password }),
  logout: () => request<void>('POST', '/api/auth/logout'),
  changePassword: (currentPassword: string, newPassword: string) =>
    request<void>('POST', '/api/auth/password', { currentPassword, newPassword }),

  // every file call takes the drive explicitly ("" or undefined = main storage), so nothing can go to the wrong disk
  list: (path: string, drive?: string) => request<Listing>('GET', `/api/files?path=${q(path)}${dq(drive)}`),
  search: (path: string, query: string, drive?: string) =>
    request<Listing & { truncated: boolean }>('GET', `/api/files/search?path=${q(path)}&q=${q(query)}${dq(drive)}`),
  storage: () => request<{ total: number; free: number }>('GET', '/api/storage'),
  createFolder: (parent: string, name: string, drive?: string) => request<{ path: string }>('POST', '/api/files/folder', { parent, name, ...db(drive) }),
  rename: (path: string, newName: string, drive?: string) => request<{ path: string }>('POST', '/api/files/rename', { path, newName, ...db(drive) }),
  move: (paths: string[], destination: string, drive?: string) => request<{ paths: string[] }>('POST', '/api/files/move', { paths, destination, ...db(drive) }),
  copy: (paths: string[], destination: string, drive?: string) => request<{ paths: string[] }>('POST', '/api/files/copy', { paths, destination, ...db(drive) }),
  remove: (paths: string[], drive?: string) => request<{ items: TrashItem[] }>('POST', '/api/files/delete', { paths, ...db(drive) }),

  trash: (drive?: string) => request<TrashItem[]>('GET', `/api/trash?x=1${dq(drive)}`),
  restore: (id: string, drive?: string) => request<{ path: string }>('POST', `/api/trash/${id}/restore?x=1${dq(drive)}`),
  purge: (id: string, drive?: string) => request<void>('DELETE', `/api/trash/${id}?x=1${dq(drive)}`),
  emptyTrash: (drive?: string) => request<void>('DELETE', `/api/trash?x=1${dq(drive)}`),

  drives: () => request<DriveInfo[]>('GET', '/api/drives'),
  openDrive: (id: string) => request<void>('POST', `/api/drives/${encodeURIComponent(id)}/open`),
  setReadOnly: (id: string, readOnly: boolean) => request<Disk>('PUT', `/api/disks/${encodeURIComponent(id)}/read-only`, { readOnly }),
  startTransfer: (fromDrive: string, paths: string[], toDrive: string, destination: string, move: boolean) =>
    request<TransferJob>('POST', '/api/transfers', { fromDrive, paths, toDrive, destination, move }),
  transferJob: () => request<TransferJob | null>('GET', '/api/transfers/job'),
  cancelTransfer: () => request<void>('POST', '/api/transfers/job/cancel'),

  commitUpload: (id: string, destination: string, relativeName: string, sha256: string, lastModified: number, replace = false, drive?: string) =>
    request<{ path: string; sha256: string; size: number }>('POST', `/api/uploads/${id}/commit`,
      { destination, relativeName, sha256, lastModified, replace, ...db(drive) }),
  versions: (path: string) => request<FileVersion[]>('GET', `/api/versions?path=${q(path)}`),
  versionUrl: (path: string, id: string) => `/api/versions/content?path=${q(path)}&id=${encodeURIComponent(id)}`,
  restoreVersion: (path: string, id: string) => request<void>('POST', '/api/versions/restore', { path, id }),

  disks: () => request<{ agent: boolean; demo?: boolean; disks: Disk[] }>('GET', '/api/disks'),
  diskAction: (id: string, action: 'mount' | 'unmount' | 'standby' | 'power-off' | 'power-on') =>
    request<void>('POST', `/api/disks/${encodeURIComponent(id)}/${action}`),
  adoptDisk: (id: string, name: string) => request<unknown>('POST', `/api/disks/${encodeURIComponent(id)}/adopt`, { name }),
  forgetDisk: (id: string) => request<void>('DELETE', `/api/disks/${encodeURIComponent(id)}`),
  updateDisk: (id: string, patch: { name?: string; folders?: string[]; schedule?: SyncSchedule; after?: AfterSync }) =>
    request<unknown>('PUT', `/api/disks/${encodeURIComponent(id)}`, patch),
  smart: (id: string) => request<SmartInfo>('GET', `/api/disks/${encodeURIComponent(id)}/smart`),
  backups: () => request<{ jobs: BackupJob[]; job: BackupRun | null }>('GET', '/api/backups'),
  createBackup: (body: BackupBody) => request<BackupJob>('POST', '/api/backups', body),
  updateBackup: (id: string, body: BackupBody) => request<BackupJob>('PUT', `/api/backups/${encodeURIComponent(id)}`, body),
  deleteBackup: (id: string) => request<void>('DELETE', `/api/backups/${encodeURIComponent(id)}`),
  runBackup: (id: string, approveDeletes = false) =>
    request<BackupRun>('POST', `/api/backups/${encodeURIComponent(id)}/run`, { approveDeletes }),
  backupJob: () => request<BackupRun | null>('GET', '/api/backups/job'),
  cancelBackup: () => request<void>('POST', '/api/backups/job/cancel'),
  backupHistory: (id: string) => request<SyncReport[]>('GET', `/api/backups/${encodeURIComponent(id)}/history`),

  shares: () => request<Share[]>('GET', '/api/shares'),
  createShare: (path: string, days: number | null, mode: ShareMode = 'View', password: string | null = null) =>
    request<Share>('POST', '/api/shares', { path, days, mode, password }),
  revokeShare: (id: string) => request<void>('DELETE', `/api/shares/${id}`),
  shareUrl: (token: string) => `${location.origin}/s/${token}`,
  publicItem: (token: string, path: string) => request<PublicItem>('GET', `/api/public/${encodeURIComponent(token)}?path=${q(path)}`),
  unlockShare: (token: string, password: string) => request<void>('POST', `/api/public/${encodeURIComponent(token)}/unlock`, { password }),
  /** Sends one file into a drop-box link. XHR rather than fetch, for upload progress. */
  dropFile: (token: string, file: File, onProgress: (sent: number) => void) => new Promise<void>((resolve, reject) => {
    const x = new XMLHttpRequest()
    x.open('POST', `/api/public/${encodeURIComponent(token)}/upload?name=${q(file.name)}`)
    x.setRequestHeader('X-DiskHaven', '1')
    x.setRequestHeader('Content-Type', 'application/octet-stream')
    x.upload.onprogress = e => onProgress(e.loaded)
    x.onload = () => {
      if (x.status >= 200 && x.status < 300) return resolve()
      let message = x.statusText
      try { const p = JSON.parse(x.responseText); message = p.detail || p.title || message } catch { /* not JSON */ }
      reject(new ApiError(translateServerMessage(message, x.status), x.status))
    }
    x.onerror = () => reject(new ApiError(translateServerMessage('network'), 0))
    x.send(file)
  }),
  publicContentUrl: (token: string, path: string, download = false) =>
    `/api/public/${encodeURIComponent(token)}/content?path=${q(path)}${download ? '&download=true' : ''}`,

  users: () => request<UserInfo[]>('GET', '/api/users'),
  addUser: (userName: string, password: string, isAdmin: boolean) => request<UserInfo>('POST', '/api/users', { userName, password, isAdmin }),
  removeUser: (name: string) => request<void>('DELETE', `/api/users/${encodeURIComponent(name)}`),
  setRole: (name: string, isAdmin: boolean) => request<void>('PUT', `/api/users/${encodeURIComponent(name)}/role`, { isAdmin }),
  // permissions: who may see and change a folder
  people: () => request<{ userName: string; isAdmin: boolean }[]>('GET', '/api/access/people'),
  access: (path: string, drive?: string) =>
    request<{ path: string; drive: string; own: Grant[]; above: Grant[]; canGiveOwner: boolean }>('GET', `/api/access?path=${q(path)}${dq(drive)}`),
  setAccess: (path: string, principal: string, level: AccessLevel, drive?: string) =>
    request<Grant | null>('PUT', '/api/access', { path, principal, level, ...db(drive) }),
  accessMine: () => request<Grant[]>('GET', '/api/access/mine'),
  accessOfUser: (name: string) => request<Grant[]>('GET', `/api/access/user/${encodeURIComponent(name)}`),
  removeAccess: (id: string) => request<void>('DELETE', `/api/access/${encodeURIComponent(id)}`),
  userFolders: (drive: string, parent: string, users: string[], common: string | null) =>
    request<{ created: string[] }>('POST', '/api/access/user-folders', { drive: drive || undefined, parent, users, common }),
  resetPassword: (name: string, newPassword: string) =>
    request<void>('POST', `/api/users/${encodeURIComponent(name)}/password`, { newPassword }),

  setHidden: (paths: string[], hidden: boolean, drive?: string) => request<void>('POST', '/api/files/hidden', { paths, hidden, ...db(drive) }),
  properties: (path: string, drive?: string) => request<ItemProperties>('GET', `/api/files/properties?path=${q(path)}${dq(drive)}`),
  checksum: (path: string, drive?: string) => request<{ sha256: string }>('GET', `/api/files/checksum?path=${q(path)}${dq(drive)}`),
  compress: (paths: string[], name?: string, drive?: string) => request<{ path: string }>('POST', '/api/files/compress', { paths, name, ...db(drive) }),
  extract: (path: string, drive?: string) => request<{ path: string }>('POST', '/api/files/extract', { path, ...db(drive) }),
  archiveUrl: (paths: string[], name?: string, drive?: string) =>
    `/api/files/archive?${paths.map(p => `paths=${q(p)}`).join('&')}${name ? `&name=${q(name)}` : ''}${dq(drive)}`,
  systemStats: () => request<SystemSample>('GET', '/api/system/stats'),
  plugins: () => request<{ installed: string[]; vaultLabel: string | null }>('GET', '/api/plugins'),
  quick: () => request<{ favorites: string[]; recent: string[] }>('GET', '/api/quick'),
  favorite: (path: string, on: boolean) => request<void>('POST', '/api/quick/favorite', { path, on }),
  recent: (path: string) => request<void>('POST', '/api/quick/recent', { path }),

  adminOverview: () => request<AdminOverview>('GET', '/api/admin/overview'),
  adminPlugins: () => request<PluginInfo[]>('GET', '/api/admin/plugins'),
  setPlugin: (id: string, installed: boolean) => request<void>('POST', `/api/admin/plugins/${encodeURIComponent(id)}`, { installed }),
  activity: (q: string, user: string, action: string) =>
    request<ActivityItem[]>('GET', `/api/admin/activity?limit=300${q ? `&q=${encodeURIComponent(q)}` : ''}${user ? `&user=${encodeURIComponent(user)}` : ''}${action ? `&action=${action}` : ''}`),

  hideaway: {
    state: () => request<HideawayState>('GET', '/api/hideaway/state'),
    setup: (password: string, decoyPassword: string | null) => request<{ recoveryKey: string }>('POST', '/api/hideaway/setup', { password, decoyPassword }),
    unlock: (password: string) => request<void>('POST', '/api/hideaway/unlock', { password }),
    lock: () => request<void>('POST', '/api/hideaway/lock'),
    list: (folder: string) => request<{ path: { id: string; name: string }[]; entries: FileEntry[] }>('GET', `/api/hideaway/list?folder=${encodeURIComponent(folder)}`),
    createFolder: (parent: string, name: string) => request<FileEntry>('POST', '/api/hideaway/folder', { parent, name }),
    rename: (id: string, name: string) => request<FileEntry>('POST', '/api/hideaway/rename', { id, name }),
    move: (ids: string[], parent: string) => request<void>('POST', '/api/hideaway/move', { ids, parent }),
    copy: (ids: string[], parent: string) => request<void>('POST', '/api/hideaway/copy', { ids, parent }),
    remove: (ids: string[]) => request<void>('POST', '/api/hideaway/delete', { ids, parent: null }),
    password: (currentPassword: string, newPassword: string) => request<void>('POST', '/api/hideaway/password', { currentPassword, newPassword }),
    decoy: (decoyPassword: string, currentPassword: string) => request<void>('POST', '/api/hideaway/decoy', { decoyPassword, currentPassword }),
    label: (label: string) => request<void>('PUT', '/api/hideaway/label', { label }),
    commit: (uploadId: string, parent: string, relativeName: string, sha256: string, lastModified: number) =>
      request<FileEntry>('POST', '/api/hideaway/commit', { uploadId, parent, relativeName, sha256, lastModified }),
    contentUrl: (id: string, download = false) => `/api/hideaway/content?id=${encodeURIComponent(id)}${download ? '&download=true' : ''}`,
    archiveUrl: (ids: string[]) => `/api/hideaway/archive?${ids.map(i => `ids=${encodeURIComponent(i)}`).join('&')}`,
  },

  thumbUrl: (path: string, size: 's' | 'm' = 's', drive?: string) => `/api/files/thumb?path=${q(path)}&size=${size}${dq(drive)}`,
  warmThumbs: (path: string, drive?: string) => request<{ queued: number }>('POST', '/api/files/thumbs/warm', { path, ...db(drive) }),
  contentUrl: (path: string, download = false, drive?: string) => `/api/files/content?path=${q(path)}${download ? '&download=true' : ''}${dq(drive)}`,
}

export function folderEntries(entries: FileEntry[]) {
  return entries.filter(e => e.kind === 'Folder')
}
