import type { AfterSync, AuthState, Disk, FileEntry, Listing, SyncJob, SyncPlanResult, SmartInfo, SyncReport, SyncSchedule, TrashItem } from './types'
import { translateServerMessage } from './strings'

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message)
  }
}

/** Every state-changing call carries this header; the server rejects calls without it (CSRF guard). */
export const csrfHeader = { 'X-HomeVault': '1' }

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
  if (res.status === 401 && !url.startsWith('/api/auth')) window.dispatchEvent(new Event('homevault:unauthorized'))
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

export const api = {
  authState: () => request<AuthState>('GET', '/api/auth/state'),
  setup: (token: string, userName: string, password: string) =>
    request<{ user: string }>('POST', '/api/auth/setup', { token, userName, password }),
  login: (userName: string, password: string) => request<{ user: string }>('POST', '/api/auth/login', { userName, password }),
  logout: () => request<void>('POST', '/api/auth/logout'),
  changePassword: (currentPassword: string, newPassword: string) =>
    request<void>('POST', '/api/auth/password', { currentPassword, newPassword }),

  list: (path: string) => request<Listing>('GET', `/api/files?path=${q(path)}`),
  storage: () => request<{ total: number; free: number }>('GET', '/api/storage'),
  createFolder: (parent: string, name: string) => request<{ path: string }>('POST', '/api/files/folder', { parent, name }),
  rename: (path: string, newName: string) => request<{ path: string }>('POST', '/api/files/rename', { path, newName }),
  move: (paths: string[], destination: string) => request<{ paths: string[] }>('POST', '/api/files/move', { paths, destination }),
  copy: (paths: string[], destination: string) => request<{ paths: string[] }>('POST', '/api/files/copy', { paths, destination }),
  remove: (paths: string[]) => request<{ items: TrashItem[] }>('POST', '/api/files/delete', { paths }),

  trash: () => request<TrashItem[]>('GET', '/api/trash'),
  restore: (id: string) => request<{ path: string }>('POST', `/api/trash/${id}/restore`),
  purge: (id: string) => request<void>('DELETE', `/api/trash/${id}`),
  emptyTrash: () => request<void>('DELETE', '/api/trash'),

  commitUpload: (id: string, destination: string, relativeName: string, sha256: string, lastModified: number) =>
    request<{ path: string; sha256: string; size: number }>('POST', `/api/uploads/${id}/commit`,
      { destination, relativeName, sha256, lastModified }),

  disks: () => request<{ agent: boolean; disks: Disk[]; job: SyncJob | null }>('GET', '/api/disks'),
  diskAction: (id: string, action: 'mount' | 'unmount' | 'standby' | 'power-off' | 'power-on') =>
    request<void>('POST', `/api/disks/${encodeURIComponent(id)}/${action}`),
  adoptDisk: (id: string, name: string) => request<unknown>('POST', `/api/disks/${encodeURIComponent(id)}/adopt`, { name }),
  forgetDisk: (id: string) => request<void>('DELETE', `/api/disks/${encodeURIComponent(id)}`),
  updateDisk: (id: string, patch: { name?: string; folders?: string[]; schedule?: SyncSchedule; after?: AfterSync }) =>
    request<unknown>('PUT', `/api/disks/${encodeURIComponent(id)}`, patch),
  smart: (id: string) => request<SmartInfo>('GET', `/api/disks/${encodeURIComponent(id)}/smart`),
  planSync: (id: string) => request<SyncPlanResult>('POST', `/api/disks/${encodeURIComponent(id)}/plan`),
  startSync: (id: string) => request<SyncJob>('POST', `/api/disks/${encodeURIComponent(id)}/sync`),
  syncJob: () => request<SyncJob | null>('GET', '/api/sync/job'),
  cancelSync: () => request<void>('POST', '/api/sync/job/cancel'),
  syncHistory: (id: string) => request<SyncReport[]>('GET', `/api/disks/${encodeURIComponent(id)}/history`),

  contentUrl: (path: string, download = false) => `/api/files/content?path=${q(path)}${download ? '&download=true' : ''}`,
}

export function folderEntries(entries: FileEntry[]) {
  return entries.filter(e => e.kind === 'Folder')
}
