import { useCallback, useEffect, useRef, useState } from 'react'
import { browserCanShow, dayKey, dayLabel, gapi, type Media } from './gapi'
import { Icon, toastError } from './ui'

/** Loads an album (or all photos when album is null) page by page and draws it as a date-grouped grid. */
export function useMedia(album: string | null, version: number) {
  const [items, setItems] = useState<Media[]>([])
  const [total, setTotal] = useState<number | null>(null)
  const loading = useRef(false)
  const gen = useRef(0)

  useEffect(() => {
    const my = ++gen.current
    loading.current = true
    gapi.media(album, 0).then(r => {
      if (gen.current !== my) return
      setItems(r.items); setTotal(r.total)
    }).catch(e => { if (gen.current === my) { setTotal(0); toastError(e) } })
      .finally(() => { if (gen.current === my) loading.current = false })
  }, [album, version])

  const more = useCallback(() => {
    if (loading.current || total === null || items.length >= total) return
    const my = gen.current
    loading.current = true
    gapi.media(album, items.length).then(r => {
      if (gen.current !== my) return
      setItems(prev => [...prev, ...r.items.filter(i => !prev.some(p => p.path === i.path))]); setTotal(r.total)
    }).catch(toastError).finally(() => { loading.current = false })
  }, [album, items.length, total])

  return { items, total, more }
}

export function Thumb({ m, size = 's' }: { m: Media; size?: 's' | 'm' }) {
  const [src, setSrc] = useState(gapi.thumb(m.path, size))
  const [failed, setFailed] = useState(false)
  useEffect(() => { setSrc(gapi.thumb(m.path, size)); setFailed(false) }, [m.path, size])
  if (failed) return <div className="g-thumb-missing"><Icon name={m.video ? 'play' : 'photos'} size={28} /></div>
  return (
    <img src={src} alt="" loading="lazy" decoding="async" draggable={false}
      onError={() => {
        // no server preview (tools missing / odd file): a browser-friendly photo can still show itself
        if (!m.video && browserCanShow(m.name) && !src.startsWith('/api/files')) setSrc(gapi.original(m.path))
        else setFailed(true)
      }} />
  )
}

interface GridProps {
  items: Media[]
  total: number | null
  more: () => void
  selected: Set<string>
  selecting: boolean
  onOpen: (index: number) => void
  onToggle: (path: string, range?: boolean) => void
  empty: string
}

export function Grid({ items, total, more, selected, selecting, onOpen, onToggle, empty }: GridProps) {
  const sentinel = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const el = sentinel.current
    if (!el) return
    const io = new IntersectionObserver(e => { if (e[0].isIntersecting) more() }, { rootMargin: '1200px' })
    io.observe(el)
    return () => io.disconnect()
  }, [more])

  if (total === null) return <div className="g-center"><div className="g-spinner" /></div>
  if (total === 0) return <div className="g-center g-empty"><Icon name="photos" size={48} /><p>{empty}</p></div>

  // group consecutive items by day
  const groups: { key: string; label: string; start: number; list: Media[] }[] = []
  items.forEach((m, i) => {
    const k = dayKey(m.taken)
    const last = groups[groups.length - 1]
    if (last && last.key === k) last.list.push(m)
    else groups.push({ key: k, label: dayLabel(m.taken), start: i, list: [m] })
  })

  return (
    <div className="g-grid-wrap">
      {groups.map(g => {
        const allSel = selecting && g.list.every(m => selected.has(m.path))
        return (
          <section key={g.key + g.start}>
            <h3 className="g-day">
              <span>{g.label}</span>
              {selecting && (
                <button className={`g-day-check ${allSel ? 'on' : ''}`} aria-label="Bu günü seç"
                  onClick={() => g.list.forEach(m => { if (selected.has(m.path) === allSel) onToggle(m.path) })}>
                  <Icon name="check" size={16} />
                </button>
              )}
            </h3>
            <div className="g-grid">
              {g.list.map((m, j) => (
                <Cell key={m.path} m={m} selected={selected.has(m.path)} selecting={selecting}
                  onOpen={() => onOpen(g.start + j)} onToggle={range => onToggle(m.path, range)} />
              ))}
            </div>
          </section>
        )
      })}
      <div ref={sentinel} className="g-sentinel" />
    </div>
  )
}

function Cell({ m, selected, selecting, onOpen, onToggle }: {
  m: Media; selected: boolean; selecting: boolean; onOpen: () => void; onToggle: (range?: boolean) => void
}) {
  const timer = useRef<number | null>(null)
  const start = useRef<{ x: number; y: number } | null>(null)
  const longFired = useRef(false)

  const down = (e: React.PointerEvent) => {
    longFired.current = false
    start.current = { x: e.clientX, y: e.clientY }
    timer.current = window.setTimeout(() => {
      longFired.current = true
      navigator.vibrate?.(15)
      onToggle()
    }, 450)
  }
  const cancel = () => { if (timer.current) { clearTimeout(timer.current); timer.current = null } }
  const moveP = (e: React.PointerEvent) => {
    if (start.current && Math.hypot(e.clientX - start.current.x, e.clientY - start.current.y) > 10) cancel()
  }

  return (
    <button className={`g-cell ${selected ? 'sel' : ''}`} aria-label={m.name}
      onPointerDown={down} onPointerUp={cancel} onPointerLeave={cancel} onPointerMove={moveP} onPointerCancel={cancel}
      onContextMenu={e => e.preventDefault()}
      onClick={e => {
        if (longFired.current) { longFired.current = false; return }
        if (selecting) onToggle(e.shiftKey)
        else onOpen()
      }}>
      <Thumb m={m} />
      {m.video && <span className="g-badge"><Icon name="play" size={16} /></span>}
      {selecting && <span className={`g-check ${selected ? 'on' : ''}`}><Icon name="check" size={16} /></span>}
    </button>
  )
}
