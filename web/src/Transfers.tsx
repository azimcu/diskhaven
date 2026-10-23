import { useEffect, useState, useSyncExternalStore } from 'react'
import { AlertCircle, CheckCircle2, ChevronDown, ChevronUp, RotateCcw, X } from 'lucide-react'
import { isActive, uploads, type UploadItem } from './uploads'
import { formatSize } from './format'
import { t } from './strings'

const stageText: Record<UploadItem['status'], string> = {
  queued: 'Sırada',
  hashing: 'Parmak izi hesaplanıyor',
  uploading: 'Yükleniyor',
  verifying: 'Sunucuda doğrulanıyor',
  done: 'Doğrulandı',
  error: 'Hata',
  canceled: 'İptal edildi',
}

/** Bottom-right panel listing upload batches with a verified/total summary per batch. */
export function Transfers() {
  const { items, batches } = useSyncExternalStore(uploads.subscribe, uploads.getSnapshot)
  const [collapsed, setCollapsed] = useState(false)
  const [openBatch, setOpenBatch] = useState<number | null>(null)

  useEffect(() => {
    const warn = (e: BeforeUnloadEvent) => {
      if (uploads.hasActive()) {
        e.preventDefault()
        e.returnValue = t.leaveWarning
      }
    }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [])

  if (batches.length === 0) return null
  const active = items.filter(i => isActive(i.status)).length

  return (
    <section className="transfers" aria-label={t.transfers}>
      <header className="tr-head">
        <strong>{active > 0 ? `${active} dosya aktarılıyor` : t.transfers}</strong>
        <button className="icon-btn" onClick={() => uploads.clearFinished()} title={t.clearFinished}><X size={16} /></button>
        <button className="icon-btn" onClick={() => setCollapsed(c => !c)} title={collapsed ? 'Aç' : 'Küçült'}>
          {collapsed ? <ChevronUp size={16} /> : <ChevronDown size={16} />}
        </button>
      </header>
      {!collapsed && (
        <div className="tr-list">
          {[...batches].reverse().map(b => {
            const list = items.filter(i => i.batch === b.id)
            const done = list.filter(i => i.status === 'done').length
            const failed = list.filter(i => i.status === 'error').length
            const running = list.filter(i => isActive(i.status)).length
            const total = list.filter(i => i.status !== 'canceled').length
            const bytes = list.reduce((s, i) => s + i.file.size, 0)
            const doneBytes = list.reduce((s, i) => s + (i.status === 'done' ? i.file.size : i.status === 'uploading' ? i.file.size * i.progress : 0), 0)
            const pct = bytes ? doneBytes / bytes : running ? 0 : 1
            return (
              <div key={b.id} className={`tr-batch ${failed ? 'has-error' : ''}`}>
                <div className="tr-row" onClick={() => setOpenBatch(o => (o === b.id ? null : b.id))}>
                  {failed ? <AlertCircle size={18} className="ic err" /> : running ? <span className="spinner" /> : <CheckCircle2 size={18} className="ic ok" />}
                  <div className="tr-info">
                    <div className="tr-name" title={b.label}>{b.label}</div>
                    <div className="tr-sub">
                      {running > 0
                        ? `${done}/${total} doğrulandı · ${formatSize(doneBytes)} / ${formatSize(bytes)}`
                        : failed > 0
                          ? `${done}/${total} doğrulandı · ${failed} dosya yüklenemedi`
                          : `${done} dosya yüklendi · tümü doğrulandı`}
                    </div>
                    {running > 0 && <div className="bar"><div style={{ width: `${Math.round(pct * 100)}%` }} /></div>}
                  </div>
                  {failed > 0 && !running && (
                    <button className="icon-btn" title={t.retry} onClick={e => { e.stopPropagation(); uploads.retryFailed(b.id) }}><RotateCcw size={16} /></button>
                  )}
                  {running > 0 && (
                    <button className="icon-btn" title={t.cancel} onClick={e => { e.stopPropagation(); uploads.cancelBatch(b.id) }}><X size={16} /></button>
                  )}
                </div>
                {openBatch === b.id && (
                  <ul className="tr-files">
                    {list.map(i => (
                      <li key={i.id} className={i.status}>
                        <span className="tr-file" title={i.relativeName}>{i.relativeName}</span>
                        <span className="tr-stage">
                          {i.status === 'error' ? i.error : stageText[i.status]}
                          {(i.status === 'hashing' || i.status === 'uploading') && ` %${Math.round(i.progress * 100)}`}
                        </span>
                        {i.status === 'error' && <button className="link" onClick={() => uploads.retry(i.id)}>{t.retry}</button>}
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            )
          })}
        </div>
      )}
    </section>
  )
}
