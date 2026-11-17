// Upload queue. Each file goes through: hashing (SHA-256 on the device) -> tus upload in
// 16 MB chunks (resumable, retried) -> commit, where the server re-hashes and only then
// places the file. A checksum mismatch discards the server copy and uploads again.
//
// Made for long phone uploads (hundreds of photos and videos from an iPhone):
// - the screen is kept on while anything is uploading (awake.ts), so the phone does not lock and stop Safari;
// - a dropped connection (Wi-Fi to mobile data, Safari sent to the background) never fails a file: it waits
//   and carries on by itself when the connection is back or the page is shown again;
// - a file that is already in the folder with the same name and size is skipped, so picking the same photos
//   again after an interruption only uploads what is missing (half-sent files continue where they stopped);
// - what is still unfinished is remembered in the browser, so after Safari reloads the page it can say so.
import { Upload } from 'tus-js-client'
import { createSHA256 } from 'hash-wasm'
import { api, ApiError, csrfHeader } from './api'
import { awake } from './awake'

export type UploadStatus = 'queued' | 'hashing' | 'uploading' | 'verifying' | 'waiting' | 'done' | 'skipped' | 'error' | 'canceled'

export interface UploadItem {
  id: number
  batch: number
  file: File
  destination: string
  /** "" = main storage, otherwise the disk id; fixed when the file is queued */
  drive: string
  relativeName: string
  status: UploadStatus
  /** 0..1 within the current stage */
  progress: number
  error?: string
  resultPath?: string
  attempts: number
  /** a file with this name is already there and the person chose "Replace" */
  replace?: boolean
  /** computed once; a retry after a dropped connection does not read the whole file again */
  sha256?: string
  /** the connection dropped at least once: before sending again, check the file did not already arrive */
  interrupted?: boolean
}

export interface Batch {
  id: number
  destination: string
  drive: string
  label: string
  createdAt: number
}

/** Files that were still unfinished when the page was last open (kept in the browser). */
export interface Unfinished { destination: string; drive: string; count: number; names: string[]; at: number }

const CHUNK = 16 * 1024 * 1024
const PARALLEL = 3
const MAX_ATTEMPTS = 3
const PENDING_KEY = 'dh.unfinished'

let nextId = 1
let items: UploadItem[] = []
let batches: Batch[] = []
const running = new Map<number, Upload>()
const listeners = new Set<() => void>()
let snapshot = { items, batches }

function emit() {
  snapshot = { items, batches }
  listeners.forEach(l => l())
  awake.set(items.some(i => isActive(i.status)))
  schedulePersist()
}

function update(id: number, patch: Partial<UploadItem>) {
  items = items.map(i => (i.id === id ? { ...i, ...patch } : i))
  emit()
}

export const uploads = {
  subscribe(listener: () => void) {
    listeners.add(listener)
    return () => listeners.delete(listener)
  },
  getSnapshot: () => snapshot,

  /**
   * Files with their path relative to the drop target ("Folder/sub/file.jpg" for folder uploads).
   * `skipped` are files already in the folder (same name and size): listed as "already there", not sent.
   */
  add(destination: string, files: { file: File; relativeName: string; replace?: boolean }[], drive = '',
    skipped: { file: File; relativeName: string }[] = []) {
    if (files.length === 0 && skipped.length === 0) return
    const first = (files[0] ?? skipped[0]).relativeName
    const batch: Batch = {
      id: nextId++,
      destination,
      drive,
      label: files.length + skipped.length === 1 ? first : first.includes('/') ? first.split('/')[0] : `${files.length + skipped.length} dosya`,
      createdAt: Date.now(),
    }
    batches = [...batches, batch]
    items = [
      ...items,
      ...files.map(f => ({
        id: nextId++, batch: batch.id, file: f.file, destination, drive, relativeName: f.relativeName,
        status: 'queued' as const, progress: 0, attempts: 0, replace: f.replace,
      })),
      ...skipped.map(f => ({
        id: nextId++, batch: batch.id, file: f.file, destination, drive, relativeName: f.relativeName,
        status: 'skipped' as const, progress: 1, attempts: 0,
      })),
    ]
    // the same files picked again after an interruption: what was remembered is now in the queue again
    clearUnfinished(destination, drive)
    emit()
    pump()
  },

  cancel(id: number) {
    running.get(id)?.abort(true)
    running.delete(id)
    update(id, { status: 'canceled' })
    pump()
  },

  cancelBatch(batch: number) {
    for (const i of items) if (i.batch === batch && isActive(i.status)) uploads.cancel(i.id)
  },

  retry(id: number) {
    update(id, { status: 'queued', progress: 0, error: undefined, attempts: 0 })
    pump()
  },

  retryFailed(batch: number) {
    for (const i of items) if (i.batch === batch && i.status === 'error') uploads.retry(i.id)
  },

  clearFinished() {
    const keep = new Set(items.filter(i => isActive(i.status) || i.status === 'error').map(i => i.batch))
    items = items.filter(i => keep.has(i.batch))
    batches = batches.filter(b => keep.has(b.id))
    emit()
  },

  hasActive: () => items.some(i => isActive(i.status)),

  /** Unfinished uploads from an earlier visit (the page was closed or reloaded by the phone). */
  unfinished(): Unfinished[] {
    try { return JSON.parse(localStorage.getItem(PENDING_KEY) || '[]') as Unfinished[] } catch { return [] }
  },
  forgetUnfinished() { try { localStorage.removeItem(PENDING_KEY) } catch { /* private mode */ } },
}

export const isActive = (s: UploadStatus) =>
  s === 'queued' || s === 'hashing' || s === 'uploading' || s === 'verifying' || s === 'waiting'

/** Finished well: uploaded and verified, or already there. */
export const isDone = (s: UploadStatus) => s === 'done' || s === 'skipped'

/** Fired after each committed file so open folders can refresh. */
export const uploadEvents = new EventTarget()

function pump() {
  const busy = items.filter(i => i.status === 'hashing' || i.status === 'uploading' || i.status === 'verifying').length
  const next = items.filter(i => i.status === 'queued').slice(0, Math.max(0, PARALLEL - busy))
  for (const item of next) void run(item.id)
}

const current = (id: number) => items.find(i => i.id === id)
const canceled = (id: number) => current(id)?.status === 'canceled'

/** No connection, or the server / proxy is briefly away: wait and carry on, never fail the file. */
const transient = (e: unknown) =>
  e instanceof ApiError ? e.status === 0 || e.status === 502 || e.status === 503 || e.status === 504 || e.status === 408 || e.status === 429
    : false

async function run(id: number) {
  const item = current(id)
  if (!item) return
  try {
    if (item.interrupted && !item.replace && !item.destination.startsWith('hideaway:') && await alreadyThere(item)) {
      // the connection dropped right after the file arrived: it is there, do not send it a second time
      update(id, { status: 'done', progress: 1 })
      uploadEvents.dispatchEvent(new CustomEvent('committed', { detail: { drive: item.drive, path: item.destination } }))
      return
    }
    let sha256 = item.sha256
    if (!sha256) {
      update(id, { status: 'hashing', progress: 0, attempts: item.attempts + 1 })
      sha256 = await hashFile(item.file, p => update(id, { progress: p }), () => canceled(id))
      if (canceled(id)) return
      update(id, { sha256 })
    } else update(id, { attempts: item.attempts + 1 })
    update(id, { status: 'uploading', progress: 0 })
    const uploadId = await tusUpload(item, p => update(id, { progress: p }))
    if (canceled(id)) return
    update(id, { status: 'verifying', progress: 0 })
    // "hideaway:<folder id>" uploads go into the hidden folder, encrypted on commit
    const result = item.destination.startsWith('hideaway:')
      ? await api.hideaway.commit(uploadId, item.destination.slice(9), item.relativeName, sha256, item.file.lastModified)
      : await api.commitUpload(uploadId, item.destination, item.relativeName, sha256, item.file.lastModified, item.replace, item.drive || undefined)
    update(id, { status: 'done', progress: 1, resultPath: result.path })
    uploadEvents.dispatchEvent(new CustomEvent('committed', { detail: { drive: item.drive, path: item.destination } }))
  } catch (e) {
    if (canceled(id)) return
    const latest = current(id)!
    if (transient(e)) {
      // the attempt does not count: a phone can lose its connection many times during a long upload
      update(id, { status: 'waiting', progress: 0, attempts: Math.max(0, latest.attempts - 1), interrupted: true })
      wakeLater()
    } else if (e instanceof ApiError && e.status === 422 && latest.attempts < MAX_ATTEMPTS) {
      // received data did not match: the server dropped it, upload from scratch
      update(id, { status: 'queued', progress: 0, sha256: undefined })
    } else {
      const unreadable = e instanceof DOMException && (e.name === 'NotReadableError' || e.name === 'NotFoundError')
      update(id, {
        status: 'error',
        error: unreadable ? 'Telefon dosyaya artık erişim vermiyor; bu dosyaları yeniden seç (yüklenmiş olanlar atlanır).'
          : e instanceof Error ? e.message : String(e),
      })
    }
  } finally {
    running.delete(id)
    pump()
  }
}

// ---- waiting for the connection --------------------------------------------------------------
let waitTimer: number | undefined
let waitDelay = 5000

function resumeWaiting() {
  window.clearTimeout(waitTimer)
  waitTimer = undefined
  if (!items.some(i => i.status === 'waiting')) { waitDelay = 5000; return }
  items = items.map(i => (i.status === 'waiting' ? { ...i, status: 'queued' as const } : i))
  emit()
  pump()
}

function wakeLater() {
  if (waitTimer !== undefined) return
  waitTimer = window.setTimeout(() => {
    waitTimer = undefined
    waitDelay = Math.min(60000, waitDelay * 2)
    resumeWaiting()
  }, waitDelay)
}

window.addEventListener('online', () => { waitDelay = 5000; resumeWaiting() })
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible') { waitDelay = 5000; resumeWaiting() }
})

/** Is this file already in its folder with the same name and size? */
async function alreadyThere(item: UploadItem): Promise<boolean> {
  const dir = [item.destination, item.relativeName.includes('/') ? item.relativeName.slice(0, item.relativeName.lastIndexOf('/')) : '']
    .filter(Boolean).join('/')
  const name = item.relativeName.split('/').pop()!
  const listing = await api.list(dir, item.drive || undefined).catch(e => { if (transient(e)) throw e; return null })
  return !!listing?.entries.some(e => e.kind === 'File' && e.name === name && e.size === item.file.size && sameTime(e.modified, item.file.lastModified))
}

/** The server gives a committed file the date of the original, so equal dates (FAT keeps 2 s) mean the same file. */
const sameTime = (modified: string, lastModified: number) => Math.abs(Date.parse(modified) - lastModified) <= 2000

/**
 * Splits picked files into those to send and those already in the folder, so picking the same files again
 * after an interruption only sends what is missing. Files in sub-folders are checked in their own folder;
 * if a folder cannot be read, its files are simply sent.
 * Same file = same name, size and date. `sizeIsEnough` (the gallery: photos are not edited in place) also
 * counts name + size alone, because an iPhone gives a photo picked again a new date.
 */
export async function splitIdentical<T extends { file: File; relativeName: string }>(destination: string, files: T[], drive = '', sizeIsEnough = false) {
  if (destination.startsWith('hideaway:')) return { send: files, same: [] as T[] }
  const byDir = new Map<string, T[]>()
  for (const f of files) {
    const dir = f.relativeName.includes('/') ? f.relativeName.slice(0, f.relativeName.lastIndexOf('/')) : ''
    byDir.set(dir, [...(byDir.get(dir) ?? []), f])
  }
  const send: T[] = [], same: T[] = []
  for (const [dir, list] of byDir) {
    let there = new Map<string, { size: number; modified: string }>()
    try {
      const listing = await api.list([destination, dir].filter(Boolean).join('/'), drive || undefined)
      there = new Map(listing.entries.filter(e => e.kind === 'File').map(e => [e.name, { size: e.size ?? -1, modified: e.modified }]))
    } catch { /* new folder or cannot read: send all */ }
    for (const f of list) {
      const e = there.get(f.relativeName.split('/').pop()!)
      const isSame = !!e && e.size === f.file.size && (sizeIsEnough || sameTime(e.modified, f.file.lastModified))
      ;(isSame ? same : send).push(f)
    }
  }
  return { send, same }
}

// ---- remembering unfinished files across a reload ---------------------------------------------
let persistTimer: number | undefined

function schedulePersist() {
  if (persistTimer !== undefined) return
  persistTimer = window.setTimeout(() => { persistTimer = undefined; persist() }, 1000)
}

function persist() {
  const open = items.filter(i => isActive(i.status) || (i.status === 'error' && i.interrupted))
  const groups = new Map<string, Unfinished>()
  for (const i of open) {
    if (i.destination.startsWith('hideaway:')) continue   // no names from the hidden folder in the browser
    const key = `${i.drive}|${i.destination}`
    const g = groups.get(key) ?? { destination: i.destination, drive: i.drive, count: 0, names: [], at: Date.now() }
    g.count++
    if (g.names.length < 5) g.names.push(i.relativeName)
    groups.set(key, g)
  }
  // keep what an earlier visit left for other folders until those files are picked again
  const earlier = uploads.unfinished().filter(u => !groups.has(`${u.drive}|${u.destination}`) && !items.some(i => i.drive === u.drive && i.destination === u.destination))
  const all = [...earlier, ...groups.values()]
  try {
    if (all.length) localStorage.setItem(PENDING_KEY, JSON.stringify(all))
    else localStorage.removeItem(PENDING_KEY)
  } catch { /* private mode */ }
}

function clearUnfinished(destination: string, drive: string) {
  const rest = uploads.unfinished().filter(u => !(u.destination === destination && u.drive === drive))
  try {
    if (rest.length) localStorage.setItem(PENDING_KEY, JSON.stringify(rest))
    else localStorage.removeItem(PENDING_KEY)
  } catch { /* private mode */ }
}

async function hashFile(file: File, onProgress: (p: number) => void, stop: () => boolean): Promise<string> {
  const hasher = await createSHA256()
  hasher.init()
  const slice = 8 * 1024 * 1024
  for (let offset = 0; offset < file.size; offset += slice) {
    if (stop()) return ''
    hasher.update(new Uint8Array(await file.slice(offset, offset + slice).arrayBuffer()))
    onProgress(Math.min(1, (offset + slice) / file.size))
  }
  return hasher.digest('hex')
}

function tusUpload(item: UploadItem, onProgress: (p: number) => void): Promise<string> {
  return new Promise((resolve, reject) => {
    const upload = new Upload(item.file, {
      endpoint: '/api/tus',
      chunkSize: CHUNK,
      retryDelays: [0, 1000, 3000, 5000, 10000, 20000, 30000],
      headers: csrfHeader,
      // a hidden-folder upload leaves no real name in the server's temporary upload files
      metadata: item.destination.startsWith('hideaway:') ? { filename: 'file', destination: 'x' } : { filename: item.relativeName, destination: item.destination },
      // same file into the same place -> continue the earlier, interrupted upload. No date in it: an iPhone
      // gives a photo picked again a new date. A wrong match cannot do harm: the commit checks the SHA-256
      // and a mismatch sends the file again from the start.
      fingerprint: async () =>
        ['diskhaven2', item.drive, item.destination, item.relativeName, item.file.size].join('|'),
      storeFingerprintForResuming: true,
      removeFingerprintOnSuccess: true,
      onProgress: (sent, total) => onProgress(total ? sent / total : 1),
      onSuccess: () => resolve(upload.url!.split('/').pop()!),
      onError: err => {
        const status = (err as { originalResponse?: { getStatus(): number } | null }).originalResponse?.getStatus?.() ?? 0
        reject(new ApiError(status === 507 ? 'Diskte yeterli boş alan yok.' : 'Aktarım kesildi. Tekrar dene.', status))
      },
    })
    running.set(item.id, upload)
    upload.findPreviousUploads().then(previous => {
      if (previous.length > 0) upload.resumeFromPreviousUpload(previous[0])
      upload.start()
    }).catch(() => upload.start())
  })
}

/** Reads files out of a drag-and-drop, including whole folders. */
export async function filesFromDrop(dt: DataTransfer): Promise<{ file: File; relativeName: string }[]> {
  const entries = Array.from(dt.items)
    .map(i => (i.kind === 'file' ? i.webkitGetAsEntry() : null))
    .filter((e): e is FileSystemEntry => e !== null)
  if (entries.length === 0) return Array.from(dt.files).map(f => ({ file: f, relativeName: f.name }))
  const out: { file: File; relativeName: string }[] = []
  async function walk(entry: FileSystemEntry, prefix: string) {
    if (entry.isFile) {
      const file = await new Promise<File>((res, rej) => (entry as FileSystemFileEntry).file(res, rej))
      out.push({ file, relativeName: prefix + entry.name })
    } else if (entry.isDirectory) {
      const reader = (entry as FileSystemDirectoryEntry).createReader()
      // readEntries returns results in pages; keep reading until empty
      for (;;) {
        const page = await new Promise<FileSystemEntry[]>((res, rej) => reader.readEntries(res, rej))
        if (page.length === 0) break
        for (const child of page) await walk(child, `${prefix}${entry.name}/`)
      }
    }
  }
  for (const e of entries) await walk(e, '')
  return out
}

export function filesFromInput(list: FileList): { file: File; relativeName: string }[] {
  return Array.from(list).map(f => ({ file: f, relativeName: f.webkitRelativePath || f.name }))
}
