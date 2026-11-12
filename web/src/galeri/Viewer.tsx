import { useEffect, useRef, useState } from 'react'
import { albumTitle, browserCanShow, fullDate, gapi, sizeText, type Media } from './gapi'
import { Icon } from './ui'

interface Props {
  items: Media[]
  start: number
  onClose: () => void
  onNeedMore: () => void
  onDelete: (m: Media) => Promise<boolean>
  onMove: (m: Media, copy: boolean) => Promise<boolean>
}

/** Full-screen photo / video viewer: swipe or arrow keys to go on, double-tap to zoom, tap to hide the bars. */
export function Viewer({ items, start, onClose, onNeedMore, onDelete, onMove }: Props) {
  const [index, setIndex] = useState(start)
  const [chrome, setChrome] = useState(true)
  const [info, setInfo] = useState(false)
  const [dx, setDx] = useState(0)
  const [zoom, setZoom] = useState({ s: 1, x: 0, y: 0 })
  const touch = useRef<{ x: number; y: number; t: number; zx: number; zy: number } | null>(null)
  const lastTap = useRef(0)
  const m = items[Math.min(index, items.length - 1)]

  useEffect(() => { if (index >= items.length - 5) onNeedMore() }, [index, items.length, onNeedMore])
  useEffect(() => { setZoom({ s: 1, x: 0, y: 0 }) }, [index])
  useEffect(() => { if (items.length === 0) onClose() }, [items.length, onClose])

  const go = (d: number) => setIndex(i => Math.max(0, Math.min(items.length - 1, i + d)))

  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (e.key === 'ArrowRight') go(1)
      else if (e.key === 'ArrowLeft') go(-1)
      else if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  })

  if (!m) return null

  const onStart = (e: React.TouchEvent) => {
    if (e.touches.length !== 1) return
    touch.current = { x: e.touches[0].clientX, y: e.touches[0].clientY, t: Date.now(), zx: zoom.x, zy: zoom.y }
  }
  const onMoveT = (e: React.TouchEvent) => {
    const t = touch.current
    if (!t || e.touches.length !== 1) return
    const mx = e.touches[0].clientX - t.x, my = e.touches[0].clientY - t.y
    if (zoom.s > 1) setZoom(z => ({ ...z, x: t.zx + mx, y: t.zy + my }))
    else if (Math.abs(mx) > Math.abs(my)) setDx(mx)
  }
  const onEnd = (e: React.TouchEvent) => {
    const t = touch.current
    touch.current = null
    if (!t) return
    const end = e.changedTouches[0]
    const mx = end.clientX - t.x, my = end.clientY - t.y
    setDx(0)
    if (zoom.s === 1) {
      if (Math.abs(mx) > 60 && Math.abs(mx) > Math.abs(my)) { go(mx < 0 ? 1 : -1); return }
      if (my > 120 && Math.abs(my) > Math.abs(mx)) { onClose(); return }
    }
    if (Math.hypot(mx, my) < 10 && Date.now() - t.t < 300) tap(end.clientX, end.clientY)
  }
  const tap = (x: number, y: number) => {
    const now = Date.now()
    if (now - lastTap.current < 300 && !m.video) {
      lastTap.current = 0
      if (zoom.s > 1) setZoom({ s: 1, x: 0, y: 0 })
      else setZoom({ s: 2.5, x: (window.innerWidth / 2 - x) * 1.5, y: (window.innerHeight / 2 - y) * 1.5 })
      return
    }
    lastTap.current = now
    window.setTimeout(() => { if (lastTap.current === now) setChrome(c => !c) }, 300)
  }

  const photoSrc = browserCanShow(m.name) && m.size < 12_000_000 ? gapi.original(m.path) : gapi.thumb(m.path, 'm')

  return (
    <div className="g-viewer" onTouchStart={onStart} onTouchMove={onMoveT} onTouchEnd={onEnd}
      onClick={e => { if (!('ontouchstart' in window) && e.target === e.currentTarget) setChrome(c => !c) }}>
      <div className="g-stage" style={{ transform: `translateX(${dx}px)` }}>
        {m.video
          ? <video key={m.path} src={gapi.original(m.path)} poster={gapi.thumb(m.path, 'm')} controls playsInline autoPlay preload="metadata" />
          : <img key={m.path} src={photoSrc} alt={m.name} draggable={false}
              onError={e => { const img = e.currentTarget; if (!img.src.includes('/api/gallery/thumb')) img.src = gapi.thumb(m.path, 'm') }}
              onDoubleClick={e => tap(e.clientX, e.clientY)}
              style={{ transform: `translate(${zoom.x}px, ${zoom.y}px) scale(${zoom.s})` }} />}
      </div>
      {/* preload neighbours */}
      <div hidden>
        {[items[index + 1], items[index - 1]].filter(Boolean).filter(n => !n.video).map(n => <img key={n.path} src={browserCanShow(n.name) && n.size < 12_000_000 ? gapi.original(n.path) : gapi.thumb(n.path, 'm')} alt="" />)}
      </div>

      {chrome && (
        <>
          <header className="g-viewer-top">
            <button className="g-icon-btn light" onClick={onClose} aria-label="Geri"><Icon name="back" /></button>
            <div className="g-viewer-title">
              <b>{fullDate(m.taken)}</b>
              <span>{albumTitle(m.album)} · {index + 1}/{items.length}</span>
            </div>
            {!('ontouchstart' in window) && (
              <>
                <button className="g-icon-btn light" onClick={() => go(-1)} disabled={index === 0} aria-label="Önceki"><Icon name="back" /></button>
                <button className="g-icon-btn light flip" onClick={() => go(1)} disabled={index >= items.length - 1} aria-label="Sonraki"><Icon name="back" /></button>
              </>
            )}
          </header>
          <footer className="g-viewer-bar">
            <button onClick={() => setInfo(true)}><Icon name="info" /><span>Bilgi</span></button>
            <a href={gapi.original(m.path, true)} download><Icon name="download" /><span>İndir</span></a>
            <button onClick={() => onMove(m, true)}><Icon name="copy" /><span>Kopyala</span></button>
            <button onClick={() => onMove(m, false)}><Icon name="move" /><span>Taşı</span></button>
            <button onClick={async () => { if (await onDelete(m) && index >= items.length - 1) setIndex(i => Math.max(0, i - 1)) }}>
              <Icon name="trash" /><span>Sil</span>
            </button>
          </footer>
        </>
      )}

      {info && (
        <div className="g-scrim" onClick={() => setInfo(false)}>
          <div className="g-bottom-sheet" onClick={e => e.stopPropagation()}>
            <div className="g-handle" />
            <h2>Ayrıntılar</h2>
            <dl className="g-info">
              <dt>Tarih</dt><dd>{fullDate(m.taken)}</dd>
              <dt>Ad</dt><dd>{m.name}</dd>
              <dt>Albüm</dt><dd>{albumTitle(m.album)}</dd>
              <dt>Konum</dt><dd className="g-path">/{m.path}</dd>
              <dt>Boyut</dt><dd>{sizeText(m.size)}</dd>
              <dt>Değiştirilme</dt><dd>{fullDate(m.modified)}</dd>
            </dl>
            <button className="g-text-btn accent" onClick={() => setInfo(false)}>Kapat</button>
          </div>
        </div>
      )}
    </div>
  )
}
