import { useEffect, useState, useSyncExternalStore } from 'react'
import { AlertCircle, CheckCircle2, ChevronDown, ChevronUp, History, RotateCcw, WifiOff, X } from 'lucide-react'
import { isActive, isDone, uploads, type Unfinished, type UploadItem } from './uploads'
import { formatSize } from './format'
import { t } from './strings'

const stageText: Record<UploadItem['status'], string> = {
  queued: 'Sırada',
  hashing: 'Parmak izi hesaplanıyor',
  uploading: 'Yükleniyor',
  verifying: 'Sunucuda doğrulanıyor',
  waiting: 'Bağlantı bekleniyor, kendisi devam edecek',
  done: 'Doğrulandı',
  skipped: 'Zaten vardı, atlandı',
  error: 'Hata',
  canceled: 'İptal edildi',
}

// The phone's bottom bar has an "İşlemler" button that hides and shows this panel.
let panelHidden = false
const panelSubs = new Set<() => void>()
export const transfersPanel = {
  get hidden() { return panelHidden },
  set(hidden: boolean) { if (hidden !== panelHidden) { panelHidden = hidden; panelSubs.forEach(f => f()) } },
  subscribe(f: () => void) { panelSubs.add(f); return () => { panelSubs.delete(f) } },
}

/** Bottom-right panel listing upload batches with a verified/total summary per batch. */
export function Transfers() {
  const { items, batches } = useSyncExternalStore(uploads.subscribe, uploads.getSnapshot)
  const [collapsed, setCollapsed] = useState(false)
  const [openBatch, setOpenBatch] = useState<number | null>(null)
  // files that were still uploading when the page was last closed (Safari can reload a page in the background)
  const [earlier, setEarlier] = useState<Unfinished[]>(() => uploads.unfinished())
  useEffect(() => { if (items.length) setEarlier(uploads.unfinished()) }, [items.length])

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

  const hidden = useSyncExternalStore(transfersPanel.subscribe, () => transfersPanel.hidden)
  // a new upload always shows the panel again
  useEffect(() => { if (batches.length) transfersPanel.set(false) }, [batches.length])

  const forget = () => { uploads.forgetUnfinished(); setEarlier([]) }
  const leftover = earlier.filter(u => !items.some(i => i.destination === u.destination && i.drive === u.drive))
  if ((batches.length === 0 && leftover.length === 0) || hidden) return null
  const active = items.filter(i => isActive(i.status)).length
  const waiting = items.some(i => i.status === 'waiting')

  return (
    <section className="transfers" aria-label={t.transfers}>
      <header className="tr-head">
        <strong>{active > 0 ? (waiting ? 'Bağlantı bekleniyor…' : `${active} dosya aktarılıyor`) : t.transfers}</strong>
        <button className="icon-btn" onClick={() => { uploads.clearFinished(); if (!active) forget() }} title={t.clearFinished}><X size={16} /></button>
        <button className="icon-btn" onClick={() => setCollapsed(c => !c)} title={collapsed ? 'Aç' : 'Küçült'}>
          {collapsed ? <ChevronUp size={16} /> : <ChevronDown size={16} />}
        </button>
      </header>
      {!collapsed && active > 0 && (
        <p className="tr-tip small">
          {waiting ? <><WifiOff size={13} /> Bağlantı gelince kendiliğinden sürecek. Bu sayfayı açık tut.</>
            : 'Bitene kadar bu sayfayı açık tut; ekran kendiliğinden kapanmaz.'}
        </p>
      )}
      {!collapsed && leftover.map(u => (
        <div key={`${u.drive}|${u.destination}`} className="tr-earlier">
          <History size={18} className="ic" />
          <div className="tr-info">
            <div className="tr-name">Yarım kalan yükleme: {u.count} dosya</div>
            <div className="tr-sub">
              «{u.destination.split('/').pop() || 'Ana klasör'}» klasörüne. Aynı dosyaları yeniden seç: yüklenmiş olanlar atlanır,
              yarım kalanlar kaldığı yerden sürer.{u.names.length > 0 && <> ({u.names.slice(0, 3).join(', ')}{u.count > 3 ? '…' : ''})</>}
            </div>
            <div className="tr-actions">
              <a className="link" href={u.drive ? `#/d/${encodeURIComponent(u.drive)}/${u.destination.split('/').map(encodeURIComponent).join('/')}` : `#/files/${u.destination.split('/').map(encodeURIComponent).join('/')}`}>Klasörü aç</a>
              <button className="link" onClick={forget}>Unut</button>
            </div>
          </div>
        </div>
      ))}
      {!collapsed && (
        <div className="tr-list">
          {[...batches].reverse().map(b => {
            const list = items.filter(i => i.batch === b.id)
            const done = list.filter(i => isDone(i.status)).length
            const skipped = list.filter(i => i.status === 'skipped').length
            const waits = list.some(i => i.status === 'waiting')
            const failed = list.filter(i => i.status === 'error').length
            const running = list.filter(i => isActive(i.status)).length
            const total = list.filter(i => i.status !== 'canceled').length
            const bytes = list.reduce((s, i) => s + i.file.size, 0)
            const doneBytes = list.reduce((s, i) => s + (isDone(i.status) ? i.file.size : i.status === 'uploading' ? i.file.size * i.progress : 0), 0)
            const pct = bytes ? doneBytes / bytes : running ? 0 : 1
            return (
              <div key={b.id} className={`tr-batch ${failed ? 'has-error' : ''}`}>
                <div className="tr-row" onClick={() => setOpenBatch(o => (o === b.id ? null : b.id))}>
                  {failed ? <AlertCircle size={18} className="ic err" /> : waits ? <WifiOff size={18} className="ic" /> : running ? <span className="spinner" /> : <CheckCircle2 size={18} className="ic ok" />}
                  <div className="tr-info">
                    <div className="tr-name" title={b.label}>{b.label}</div>
                    <div className="tr-sub">
                      {running > 0
                        ? `${done}/${total} tamam · ${formatSize(doneBytes)} / ${formatSize(bytes)}${waits ? ' · bağlantı bekleniyor' : ''}`
                        : failed > 0
                          ? `${done}/${total} tamam · ${failed} dosya yüklenemedi`
                          : skipped === total ? `${skipped} dosya zaten vardı, hiçbiri yeniden yüklenmedi`
                            : `${done - skipped} dosya yüklendi ve doğrulandı${skipped ? ` · ${skipped} dosya zaten vardı` : ''}`}
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
