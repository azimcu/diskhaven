import { useEffect, useRef, useState } from 'react'
import { ChevronLeft, ChevronRight, Download, ExternalLink, Share, X } from 'lucide-react'
import { useBackToClose } from './backClose'
import { toast, toastError } from './dialogs'
import { api } from './api'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize, previewKind, typeName } from './format'
import { t } from './strings'
import type { FileEntry } from './types'

/** Shows a file's content: image, video, audio, PDF or the start of a text file. */
export function PreviewBody({ entry }: { entry: FileEntry }) {
  const kind = previewKind(entry.extension)
  const url = entry.src ?? api.contentUrl(entry.path, false, entry.drive)
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
    return failed ? <p className="pv-note">{t.cannotPlay}</p> : <video className="pv-media" src={url} controls playsInline preload="metadata" onError={() => setFailed(true)} />
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

const MIME: Record<string, string> = {
  jpg: 'image/jpeg', jpeg: 'image/jpeg', png: 'image/png', gif: 'image/gif', webp: 'image/webp', heic: 'image/heic', heif: 'image/heif', avif: 'image/avif',
  mp4: 'video/mp4', m4v: 'video/mp4', mov: 'video/quicktime', webm: 'video/webm', mkv: 'video/x-matroska', pdf: 'application/pdf',
  mp3: 'audio/mpeg', m4a: 'audio/mp4', wav: 'audio/wav', txt: 'text/plain',
}
// photos the browser may not draw itself (HEIC outside Safari, TIFF, RAW) are shown as the server's large preview
const viaPreview = new Set(['heic', 'heif', 'tif', 'tiff', 'dng', 'avif'])
const SHARE_LIMIT = 200 * 1024 * 1024
const touchDevice = () => window.matchMedia('(pointer: coarse)').matches

/**
 * Full-screen viewer, like the phone's Photos app: the file fills the screen; close (top left), the name and
 * "3 / 20", share and download stay on top; previous / next on the sides, swipe sideways to go on, swipe down to
 * close, double-tap to zoom a photo and tap a photo to hide the bars. The phone's back gesture closes it too.
 * A video never hides the bars, so the close button is always there.
 */
export function PreviewModal({ entry, onClose, onPrev, onNext, position, preload }: {
  entry: FileEntry; onClose: () => void; onPrev?: () => void; onNext?: () => void
  /** "3 / 20" */
  position?: string
  /** the neighbours, loaded ahead so swiping shows them at once */
  preload?: FileEntry[]
}) {
  useBackToClose(onClose)
  const kind = previewKind(entry.extension)
  const ext = (entry.extension ?? '').toLowerCase()
  const [chrome, setChrome] = useState(true)
  const [dx, setDx] = useState(0)
  const [dy, setDy] = useState(0)
  const [zoom, setZoom] = useState({ s: 1, x: 0, y: 0 })
  const [failed, setFailed] = useState(false)
  const [useThumb, setUseThumb] = useState(false)
  const [shareFile, setShareFile] = useState<{ path: string; file: File } | null>(null)
  const [sharing, setSharing] = useState(false)
  const touch = useRef<{ x: number; y: number; t: number; zx: number; zy: number } | null>(null)
  const lastTap = useRef(0)
  const isMain = entry.src == null
  const contentUrl = entry.src ?? api.contentUrl(entry.path, false, entry.drive)
  const downloadUrl = entry.downloadSrc ?? api.contentUrl(entry.path, true, entry.drive)
  const bigPreview = isMain ? api.thumbUrl(entry.path, 'm', entry.drive) : null

  useEffect(() => { setZoom({ s: 1, x: 0, y: 0 }); setFailed(false); setUseThumb(false); setDx(0); setDy(0) }, [entry.path])
  useEffect(() => { if (kind !== 'image') setChrome(true) }, [kind])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      else if (e.key === 'ArrowLeft' && onPrev) onPrev()
      else if (e.key === 'ArrowRight' && onNext) onNext()
    }
    window.addEventListener('keydown', onKey)
    document.documentElement.classList.add('viewer-open')
    return () => { window.removeEventListener('keydown', onKey); document.documentElement.classList.remove('viewer-open') }
  }, [onClose, onPrev, onNext])

  const imgSrc = (e: FileEntry) =>
    e.src ?? (viaPreview.has((e.extension ?? '').toLowerCase()) || (e.size ?? 0) > 12_000_000 ? api.thumbUrl(e.path, 'm', e.drive) : api.contentUrl(e.path, false, e.drive))

  // ---- touch: swipe sideways = next / previous, swipe down = close, double tap = zoom, tap = bars on / off -----------
  const onStart = (e: React.TouchEvent) => {
    // the video's own controls, a PDF and a text file keep their touches
    if (e.touches.length !== 1 || (e.target as HTMLElement).closest('video, iframe, .pv-text, button, a, audio')) { touch.current = null; return }
    touch.current = { x: e.touches[0].clientX, y: e.touches[0].clientY, t: Date.now(), zx: zoom.x, zy: zoom.y }
  }
  const onMove = (e: React.TouchEvent) => {
    const t = touch.current
    if (!t || e.touches.length !== 1) return
    const mx = e.touches[0].clientX - t.x, my = e.touches[0].clientY - t.y
    if (zoom.s > 1) setZoom(z => ({ ...z, x: t.zx + mx, y: t.zy + my }))
    else if (Math.abs(mx) > Math.abs(my)) { setDx(mx); setDy(0) }
    else if (my > 0) { setDy(my); setDx(0) }
  }
  const onEnd = (e: React.TouchEvent) => {
    const t = touch.current
    touch.current = null
    if (!t) return
    const end = e.changedTouches[0]
    const mx = end.clientX - t.x, my = end.clientY - t.y
    setDx(0); setDy(0)
    if (zoom.s === 1) {
      if (Math.abs(mx) > 60 && Math.abs(mx) > Math.abs(my)) { (mx < 0 ? onNext : onPrev)?.(); return }
      if (my > 110 && Math.abs(my) > Math.abs(mx)) { onClose(); return }
    }
    if (Math.hypot(mx, my) < 10 && Date.now() - t.t < 300) tap(end.clientX, end.clientY)
  }
  const tap = (x: number, y: number) => {
    if (kind !== 'image') return
    const now = Date.now()
    if (now - lastTap.current < 300) {
      lastTap.current = 0
      if (zoom.s > 1) setZoom({ s: 1, x: 0, y: 0 })
      else setZoom({ s: 2.5, x: (window.innerWidth / 2 - x) * 1.5, y: (window.innerHeight / 2 - y) * 1.5 })
      return
    }
    lastTap.current = now
    window.setTimeout(() => { if (lastTap.current === now) setChrome(c => !c) }, 300)
  }

  // ---- share: the phone's share sheet with the file itself (save to Photos, send in a chat) ----------------------
  const canShare = typeof navigator.share === 'function'
  const share = async () => {
    try {
      let file = shareFile?.path === entry.path ? shareFile.file : null
      if (!file) {
        if ((entry.size ?? 0) > SHARE_LIMIT) { toast('Bu dosya paylaşmak için çok büyük. İndir ile kaydedebilirsin.'); return }
        setSharing(true)
        const r = await fetch(downloadUrl)
        if (!r.ok) throw new Error('Dosya alınamadı.')
        const blob = await r.blob()
        file = new File([blob], entry.name, { type: MIME[ext] ?? (blob.type || 'application/octet-stream') })
        setShareFile({ path: entry.path, file })
        setSharing(false)
      }
      if (navigator.canShare && !navigator.canShare({ files: [file] })) { toast('Bu cihaz dosya paylaşmayı desteklemiyor. İndir ile kaydedebilirsin.'); return }
      await navigator.share({ files: [file], title: entry.name })
    } catch (e) {
      setSharing(false)
      const name = (e as DOMException)?.name
      if (name === 'AbortError') return
      // the download took long enough for the phone to forget the tap: the file is ready now, one more tap shares it
      if (name === 'NotAllowedError') { toast('Dosya hazır. Paylaşmak için bir kez daha dokun.'); return }
      toastError(e)
    }
  }

  let stage: React.ReactNode
  if (kind === 'image')
    stage = failed
      ? <div className="pv-none"><FileIcon entry={entry} size={64} /><p>{t.noPreview}</p></div>
      : <img key={entry.path} className="vw-img" src={useThumb && bigPreview ? bigPreview : imgSrc(entry)} alt={entry.name} draggable={false}
          onError={() => { if (!useThumb && bigPreview && !viaPreview.has(ext)) setUseThumb(true); else setFailed(true) }}
          onDoubleClick={e => tap(e.clientX, e.clientY)}
          style={{ transform: `translate(${zoom.x}px, ${zoom.y}px) scale(${zoom.s})` }} />
  else if (kind === 'video')
    stage = failed
      ? <p className="pv-note">{t.cannotPlay}</p>
      : <video key={entry.path} className="vw-video" src={contentUrl} poster={bigPreview ?? undefined} controls playsInline autoPlay preload="metadata"
          onError={() => setFailed(true)} />
  else if (kind === 'pdf' && touchDevice())
    // a PDF inside a page is one frozen page on a phone and swallows every touch: show its first page, open it whole
    stage = (
      <div className="vw-doc">
        {bigPreview && !failed ? <img src={bigPreview} alt="" onError={() => setFailed(true)} /> : <FileIcon entry={entry} size={72} />}
        <a className="btn primary" href={contentUrl} target="_blank" rel="noopener"><ExternalLink size={16} /> PDF'i aç</a>
      </div>
    )
  else
    stage = <div className="vw-body"><PreviewBody entry={entry} /></div>

  return (
    <div className={`viewer ${chrome ? '' : 'bare'}`} role="dialog" aria-modal="true" aria-label={entry.name}
      onTouchStart={onStart} onTouchMove={onMove} onTouchEnd={onEnd}
      onClick={e => { if (!touchDevice() && e.target === e.currentTarget) onClose() }}>
      <div className="vw-stage" style={{ transform: `translate(${dx}px, ${dy}px)`, opacity: dy ? Math.max(.35, 1 - dy / 400) : 1 }}
        onClick={e => { if (!touchDevice() && e.target === e.currentTarget) onClose() }}>{stage}</div>
      <div hidden>{(preload ?? []).filter(n => previewKind(n.extension) === 'image').map(n => <img key={n.path} src={imgSrc(n)} alt="" />)}</div>

      <header className="vw-top">
        <button className="vw-btn" onClick={onClose} aria-label={t.close} title={`${t.close} (Esc)`}><X size={24} /></button>
        <div className="vw-title">
          <b>{entry.name}</b>
          {position && <span>{position}</span>}
        </div>
        {canShare && <button className="vw-btn" onClick={share} disabled={sharing} aria-label="Paylaş" title="Paylaş">
          {sharing ? <span className="spinner light" /> : <Share size={22} />}</button>}
        <a className="vw-btn" href={downloadUrl} download={entry.name} aria-label={t.download} title={t.download}><Download size={22} /></a>
      </header>
      {onPrev && <button className="vw-nav prev" onClick={onPrev} aria-label="Önceki" title="Önceki (←)"><ChevronLeft size={30} /></button>}
      {onNext && <button className="vw-nav next" onClick={onNext} aria-label="Sonraki" title="Sonraki (→)"><ChevronRight size={30} /></button>}
    </div>
  )
}
