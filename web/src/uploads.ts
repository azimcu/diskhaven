// Upload queue. Each file goes through: hashing (SHA-256 on the device) -> tus upload in
// 16 MB chunks (resumable, retried) -> commit, where the server re-hashes and only then
// places the file. A checksum mismatch discards the server copy and uploads again.
import { Upload } from 'tus-js-client'
import { createSHA256 } from 'hash-wasm'
import { api, ApiError, csrfHeader } from './api'

export type UploadStatus = 'queued' | 'hashing' | 'uploading' | 'verifying' | 'done' | 'error' | 'canceled'

export interface UploadItem {
  id: number
  batch: number
  file: File
  destination: string
  relativeName: string
  status: UploadStatus
  /** 0..1 within the current stage */
  progress: number
  error?: string
  resultPath?: string
  attempts: number
}

export interface Batch {
  id: number
  destination: string
  label: string
  createdAt: number
}

const CHUNK = 16 * 1024 * 1024
const PARALLEL = 2
const MAX_ATTEMPTS = 3

let nextId = 1
let items: UploadItem[] = []
let batches: Batch[] = []
const running = new Map<number, Upload>()
const listeners = new Set<() => void>()
let snapshot = { items, batches }

function emit() {
  snapshot = { items, batches }
  listeners.forEach(l => l())
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

  /** Files with their path relative to the drop target ("Folder/sub/file.jpg" for folder uploads). */
  add(destination: string, files: { file: File; relativeName: string }[]) {
    if (files.length === 0) return
    const batch: Batch = {
      id: nextId++,
      destination,
      label: files.length === 1 ? files[0].relativeName : files[0].relativeName.split('/')[0],
      createdAt: Date.now(),
    }
    batches = [...batches, batch]
    items = [
      ...items,
      ...files.map(f => ({
        id: nextId++, batch: batch.id, file: f.file, destination, relativeName: f.relativeName,
        status: 'queued' as const, progress: 0, attempts: 0,
      })),
    ]
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
}

export const isActive = (s: UploadStatus) => s === 'queued' || s === 'hashing' || s === 'uploading' || s === 'verifying'

/** Fired after each committed file so open folders can refresh. */
export const uploadEvents = new EventTarget()

function pump() {
  const busy = items.filter(i => i.status === 'hashing' || i.status === 'uploading' || i.status === 'verifying').length
  const next = items.filter(i => i.status === 'queued').slice(0, Math.max(0, PARALLEL - busy))
  for (const item of next) void run(item.id)
}

const current = (id: number) => items.find(i => i.id === id)
const canceled = (id: number) => current(id)?.status === 'canceled'

async function run(id: number) {
  const item = current(id)
  if (!item) return
  update(id, { status: 'hashing', progress: 0, attempts: item.attempts + 1 })
  try {
    const sha256 = await hashFile(item.file, p => update(id, { progress: p }), () => canceled(id))
    if (canceled(id)) return
    update(id, { status: 'uploading', progress: 0 })
    const uploadId = await tusUpload(item, p => update(id, { progress: p }))
    if (canceled(id)) return
    update(id, { status: 'verifying', progress: 0 })
    // "hideaway:<folder id>" uploads go into the hidden folder, encrypted on commit
    const result = item.destination.startsWith('hideaway:')
      ? await api.hideaway.commit(uploadId, item.destination.slice(9), item.relativeName, sha256, item.file.lastModified)
      : await api.commitUpload(uploadId, item.destination, item.relativeName, sha256, item.file.lastModified)
    update(id, { status: 'done', progress: 1, resultPath: result.path })
    uploadEvents.dispatchEvent(new CustomEvent('committed', { detail: item.destination }))
  } catch (e) {
    if (canceled(id)) return
    const latest = current(id)!
    if (e instanceof ApiError && e.status === 422 && latest.attempts < MAX_ATTEMPTS) {
      // received data did not match: the server dropped it, upload from scratch
      update(id, { status: 'queued', progress: 0 })
    } else {
      update(id, { status: 'error', error: e instanceof Error ? e.message : String(e) })
    }
  } finally {
    running.delete(id)
    pump()
  }
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
      retryDelays: [0, 1000, 3000, 5000, 10000, 20000, 30000, 60000],
      headers: csrfHeader,
      // a hidden-folder upload leaves no real name in the server's temporary upload files
      metadata: item.destination.startsWith('hideaway:') ? { filename: 'file', destination: 'x' } : { filename: item.relativeName, destination: item.destination },
      // same file into the same place -> continue the earlier, interrupted upload
      fingerprint: async () =>
        ['diskhaven', item.destination, item.relativeName, item.file.size, item.file.lastModified].join('|'),
      storeFingerprintForResuming: true,
      removeFingerprintOnSuccess: true,
      onProgress: (sent, total) => onProgress(total ? sent / total : 1),
      onSuccess: () => resolve(upload.url!.split('/').pop()!),
      onError: err => {
        const status = (err as { originalResponse?: { getStatus(): number } }).originalResponse?.getStatus?.()
        reject(new ApiError(status === 507 ? 'Diskte yeterli boş alan yok.' : 'Aktarım kesildi. Tekrar dene.', status ?? 0))
      },
    })
    running.set(item.id, upload)
    upload.findPreviousUploads().then(previous => {
      if (previous.length > 0) upload.resumeFromPreviousUpload(previous[0])
      upload.start()
    })
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
