import { useEffect, useState } from 'react'
import { Download, X } from 'lucide-react'
import { api } from './api'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize, previewKind, typeName } from './format'
import { t } from './strings'
import type { FileEntry } from './types'

/** Shows a file's content: image, video, audio, PDF or the start of a text file. */
export function PreviewBody({ entry }: { entry: FileEntry }) {
  const kind = previewKind(entry.extension)
  const url = entry.src ?? api.contentUrl(entry.path)
  const [text, setText] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    setFailed(false)
    setText(null)
    if (kind !== 'text') return
    const ctl = new AbortController()
    fetch(url, { headers: { Range: 'bytes=0-262143' }, signal: ctl.signal })
      .then(r => r.text())
      .then(setText)
      .catch(() => {})
    return () => ctl.abort()
  }, [url, kind])

  if (kind === 'image') return <img className="pv-media" src={url} alt={entry.name} onError={() => setFailed(true)} hidden={failed} />
  if (kind === 'video')
    return failed ? <p className="pv-note">{t.cannotPlay}</p> : <video className="pv-media" src={url} controls preload="metadata" onError={() => setFailed(true)} />
  if (kind === 'audio') return <audio className="pv-audio" src={url} controls preload="metadata" />
  if (kind === 'pdf') return <iframe className="pv-frame" src={url} title={entry.name} />
  if (kind === 'text') return <pre className="pv-text">{text ?? '…'}</pre>
  return (
    <div className="pv-none">
      <FileIcon entry={entry} size={64} />
      <p>{t.noPreview}</p>
    </div>
  )
}

export function PreviewPane({ entry, count }: { entry: FileEntry | null; count: number }) {
  if (entry?.kind === 'Folder')
    return (
      <aside className="preview-pane">
        <div className="pv-body"><div className="pv-none"><FileIcon entry={entry} size={64} /></div></div>
        <h3 className="pv-name">{entry.name}</h3>
        <dl className="pv-meta">
          <dt>{t.colType}</dt><dd>{typeName(entry)}</dd>
          <dt>{t.colSize}</dt><dd>{entry.size == null ? t.calculating : formatSize(entry.size)}</dd>
          {entry.files != null && <><dt>{t.colContents}</dt><dd>{t.contents(entry.files, entry.folders ?? 0)}</dd></>}
          <dt>{t.colModified}</dt><dd>{formatDate(entry.modified)}</dd>
        </dl>
      </aside>
    )
  if (!entry)
    return <aside className="preview-pane empty"><p>{count > 1 ? t.selected(count) : t.selectToPreview}</p></aside>
  return (
    <aside className="preview-pane">
      <div className="pv-body"><PreviewBody entry={entry} /></div>
      <h3 className="pv-name">{entry.name}</h3>
      <dl className="pv-meta">
        <dt>{t.colType}</dt><dd>{typeName(entry)}</dd>
        <dt>{t.colSize}</dt><dd>{formatSize(entry.size)}</dd>
        <dt>{t.colModified}</dt><dd>{formatDate(entry.modified)}</dd>
      </dl>
    </aside>
  )
}

export function PreviewModal({ entry, onClose, onPrev, onNext }: { entry: FileEntry; onClose: () => void; onPrev?: () => void; onNext?: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      else if (e.key === 'ArrowLeft' && onPrev) onPrev()
      else if (e.key === 'ArrowRight' && onNext) onNext()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose, onPrev, onNext])
  return (
    <div className="viewer" onMouseDown={e => e.target === e.currentTarget && onClose()}>
      <header className="viewer-bar">
        <span className="viewer-name">{entry.name}</span>
        <a className="icon-btn" href={entry.downloadSrc ?? api.contentUrl(entry.path, true)} title={t.download}><Download size={18} /></a>
        <button className="icon-btn" onClick={onClose} title={t.close}><X size={18} /></button>
      </header>
      {onPrev && <button className="viewer-nav prev" onClick={onPrev} aria-label="Önceki">‹</button>}
      <div className="viewer-body" onMouseDown={e => e.target === e.currentTarget && onClose()}><PreviewBody entry={entry} /></div>
      {onNext && <button className="viewer-nav next" onClick={onNext} aria-label="Sonraki">›</button>}
    </div>
  )
}
