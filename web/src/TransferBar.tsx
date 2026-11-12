import { useEffect, useState, useSyncExternalStore } from 'react'
import { AlertTriangle, CheckCircle2, X } from 'lucide-react'
import { api } from './api'
import { toast } from './dialogs'
import { formatSize } from './format'
import type { TransferJob } from './types'
import { translateServerMessage as tr } from './strings'

// The one copy / move between drives that runs on the server, polled while it runs.
let job: TransferJob | null = null
let dismissed: string | null = null
const listeners = new Set<() => void>()
let timer: number | undefined

const emit = () => listeners.forEach(l => l())

async function poll() {
  try { job = await api.transferJob() } catch { /* offline for a moment: keep the last state */ }
  emit()
  window.clearTimeout(timer)
  if (job?.status === 'Running') timer = window.setTimeout(poll, 700)
  else if (job) window.dispatchEvent(new Event('diskhaven:transfer-finished'))
}

/** Starts a verified copy or move on the server. Errors (no space, read-only, ...) are thrown before anything is written. */
export async function startTransfer(fromDrive: string, paths: string[], toDrive: string, destination: string, move: boolean) {
  job = await api.startTransfer(fromDrive, paths, toDrive, destination, move)
  dismissed = null
  emit()
  toast(move ? `${paths.length} öğe taşınıyor; her dosya doğrulanınca kaynak çöp kutusuna gider` : `${paths.length} öğe kopyalanıyor ve doğrulanıyor`)
  void poll()
}

const store = {
  subscribe(l: () => void) { listeners.add(l); return () => listeners.delete(l) },
  get: () => job,
}

/** Bottom-left card with the running (or just finished) transfer. */
export function TransferBar({ onFinished }: { onFinished: () => void }) {
  const current = useSyncExternalStore(store.subscribe, store.get)
  const [, redraw] = useState(0)
  useEffect(() => {
    void poll()   // a job started in another tab, or before a reload, shows up too
    const done = () => { onFinished(); redraw(n => n + 1) }
    window.addEventListener('diskhaven:transfer-finished', done)
    return () => window.removeEventListener('diskhaven:transfer-finished', done)
  }, [onFinished])
  useEffect(() => {
    const warn = (e: BeforeUnloadEvent) => { if (job?.status === 'Running') { e.preventDefault() } }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [])

  if (!current || dismissed === current.id) return null
  // an old finished job (from an earlier visit) is not shown again
  if (current.status !== 'Running' && Date.now() - new Date(current.started).getTime() > 6 * 3600_000) return null
  const p = current.progress
  const pct = p && p.bytesTotal > 0 ? (p.bytesDone / p.bytesTotal) * 100 : p && p.filesTotal ? (p.filesDone / p.filesTotal) * 100 : 0
  const r = current.report
  const verb = current.move ? 'Taşıma' : 'Kopyalama'
  return (
    <section className={`job-card transfer-bar ${current.status.toLowerCase()}`} aria-live="polite">
      <header>
        {current.status === 'Running' ? <span className="spinner" /> : current.status === 'Done' ? <CheckCircle2 size={20} className="ic ok" /> : <AlertTriangle size={20} className="ic err" />}
        <strong>{current.status === 'Running' ? `${verb} sürüyor` : current.status === 'Done' ? `${verb} bitti` : current.status === 'Canceled' ? `${verb} durduruldu` : `${verb}: hata`}</strong>
        <span className="grow" />
        {current.status === 'Running'
          ? <button className="btn" onClick={() => api.cancelTransfer()}><X size={14} /> Durdur</button>
          : <button className="icon-btn" aria-label="Kapat" onClick={() => { dismissed = current.id; redraw(n => n + 1) }}><X size={16} /></button>}
      </header>
      {current.status === 'Running' && p && (
        <>
          <div className="bar big"><div style={{ width: `${pct}%` }} /></div>
          <div className="muted small">{p.filesDone}/{p.filesTotal} dosya · {formatSize(p.bytesDone)} / {formatSize(p.bytesTotal)}{p.current ? ` · ${p.current}` : ''}</div>
        </>
      )}
      {r && (
        <p className="small">
          <b>{r.filesCopied} dosya kopyalandı ve diskten geri okunup doğrulandı</b> ({formatSize(r.bytesCopied)})
          {current.move && ` · ${r.sourcesMovedToTrash} öğenin aslı kendi diskinin çöp kutusuna taşındı (30 gün geri alınabilir)`}
          {r.canceled && ' · durduruldu: kaynaklara dokunulmadı'}
          {r.errors.length > 0 && <span className="err-text"> · {r.errors.length} hata (bu öğelerin aslı yerinde bırakıldı)</span>}
          {r.skipped.length > 0 && <span className="muted"> · {r.skipped.length} kısayol/özel dosya atlandı (veri içermez)</span>}
          {(r.renamed?.length ?? 0) > 0 && <span className="muted"> · {r.renamed!.length} öğe, hedef disk büyük/küçük harfi ayırmadığı için " (2)" adıyla kondu</span>}
        </p>
      )}
      {current.error && <p className="err-text small">{tr(current.error)}</p>}
      {r && r.errors.length > 0 && <ul className="small err-list">{r.errors.slice(0, 10).map(e => <li key={e.path}><b>{e.path}</b>: {tr(e.message)}</li>)}</ul>}
    </section>
  )
}
