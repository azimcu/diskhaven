import { useEffect, useState, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'

// ---- back button ---------------------------------------------------------------------------------------------
// Every layer (album, photo viewer, selection, dialog) registers a handler here and gets one history entry, so the
// Android back gesture / browser back closes the topmost layer only. Closing a layer from a button pops its entry
// silently (the skip counter), so the layer below stays open.
const backStack: (() => void)[] = []
let skip = 0
window.addEventListener('popstate', () => {
  if (skip > 0) { skip--; return }
  backStack.pop()?.()
})
/** Registers a back handler; the returned function closes the layer from code (no-op if back already closed it). */
export function pushBack(handler: () => void): () => void {
  backStack.push(handler)
  history.pushState({ g: backStack.length }, '')
  return () => {
    const i = backStack.lastIndexOf(handler)
    if (i < 0) return
    backStack.splice(i, 1)
    skip++
    history.back()
  }
}

/** Shows a component on top of everything until it calls done(value). Android back / browser back closes it too. */
export function sheet<T>(render: (done: (v: T) => void) => ReactNode, cancelValue: T): Promise<T> {
  return new Promise(resolve => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    let closed = false
    const finish = (v: T) => {
      if (closed) return
      closed = true
      root.unmount()
      host.remove()
      resolve(v)
    }
    const close = pushBack(() => finish(cancelValue))
    root.render(render(v => { close(); finish(v) }))
  })
}

export function confirmSheet(title: string, text: string, ok: string, danger = false) {
  return sheet<boolean>(done => (
    <div className="g-scrim" onClick={() => done(false)}>
      <div className="g-dialog" onClick={e => e.stopPropagation()} role="dialog" aria-modal="true">
        <h2>{title}</h2>
        {text && <p>{text}</p>}
        <div className="g-dialog-actions">
          <button className="g-text-btn" onClick={() => done(false)}>Vazgeç</button>
          <button className={`g-text-btn ${danger ? 'danger' : 'accent'}`} onClick={() => done(true)} autoFocus>{ok}</button>
        </div>
      </div>
    </div>
  ), false)
}

export function promptSheet(title: string, initial = '', ok = 'Tamam') {
  return sheet<string | null>(done => <Prompt title={title} initial={initial} ok={ok} done={done} />, null)
}

function Prompt({ title, initial, ok, done }: { title: string; initial: string; ok: string; done: (v: string | null) => void }) {
  const [v, setV] = useState(initial)
  return (
    <div className="g-scrim" onClick={() => done(null)}>
      <form className="g-dialog" onClick={e => e.stopPropagation()} onSubmit={e => { e.preventDefault(); if (v.trim()) done(v.trim()) }}>
        <h2>{title}</h2>
        <input className="g-input" value={v} onChange={e => setV(e.target.value)} autoFocus />
        <div className="g-dialog-actions">
          <button type="button" className="g-text-btn" onClick={() => done(null)}>Vazgeç</button>
          <button className="g-text-btn accent" disabled={!v.trim()}>{ok}</button>
        </div>
      </form>
    </div>
  )
}

// ---- small toast ----------------------------------------------------------------------------------------------
const toastBus = new EventTarget()
export function toast(text: string, error = false) {
  toastBus.dispatchEvent(new CustomEvent('t', { detail: { text, error, id: Date.now() + Math.random() } }))
}
export const toastError = (e: unknown) => toast(e instanceof Error ? e.message : String(e), true)

export function Toasts() {
  const [list, setList] = useState<{ text: string; error: boolean; id: number }[]>([])
  useEffect(() => {
    const on = (e: Event) => {
      const t = (e as CustomEvent).detail
      setList(l => [...l, t])
      setTimeout(() => setList(l => l.filter(x => x.id !== t.id)), t.error ? 6000 : 3000)
    }
    toastBus.addEventListener('t', on)
    return () => toastBus.removeEventListener('t', on)
  }, [])
  return <div className="g-toasts" aria-live="polite">{list.map(t => <div key={t.id} className={`g-toast ${t.error ? 'error' : ''}`}>{t.text}</div>)}</div>
}

// ---- icons (Material-style, inline so the app works offline of any CDN) --------------------------------------
const P: Record<string, string> = {
  back: 'M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z',
  close: 'M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z',
  more: 'M12 8a2 2 0 1 0 0-4 2 2 0 0 0 0 4zm0 2a2 2 0 1 0 0 4 2 2 0 0 0 0-4zm0 6a2 2 0 1 0 0 4 2 2 0 0 0 0-4z',
  photos: 'M21 19V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2zM8.5 13.5l2.5 3 3.5-4.5 4.5 6H5l3.5-4.5z',
  albums: 'M3 5h2v14H3zm4 0h2v14H7zm4-2h10a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H11a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1zm1 13h8l-2.5-3.33L15.5 15l-1.5-2z',
  trash: 'M6 19a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z',
  move: 'M10 4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8l-2-2zm2 13v-3H8v-4h4V7l5 5-5 5z',
  copy: 'M16 1H4a2 2 0 0 0-2 2v14h2V3h12V1zm3 4H8a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2zm0 16H8V7h11v14z',
  download: 'M5 20h14v-2H5v2zM19 9h-4V3H9v6H5l7 7 7-7z',
  info: 'M11 7h2v2h-2zm0 4h2v6h-2zm1-9a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm0 18a8 8 0 1 1 0-16 8 8 0 0 1 0 16z',
  add: 'M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z',
  upload: 'M9 16h6v-6h4l-7-7-7 7h4zm-4 2h14v2H5z',
  check: 'M9 16.17 4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z',
  play: 'M8 5v14l11-7z',
  pin: 'M16 9V4h1V2H7v2h1v5c0 1.66-1.34 3-3 3v2h5.97v7l1 1 1-1v-7H19v-2c-1.66 0-3-1.34-3-3z',
  hide: 'M12 7c2.76 0 5 2.24 5 5 0 .65-.13 1.26-.36 1.83l2.92 2.92A11.8 11.8 0 0 0 22.99 12 11.83 11.83 0 0 0 8.08 5.2l2.16 2.16C10.74 7.13 11.35 7 12 7zM2 4.27l2.28 2.28.46.46A11.8 11.8 0 0 0 1 12a11.83 11.83 0 0 0 15.32 6.75l.42.42L19.73 22 21 20.73 3.27 3 2 4.27zM7.53 9.8l1.55 1.55c-.05.21-.08.43-.08.65a3 3 0 0 0 3 3c.22 0 .44-.03.65-.08l1.55 1.55A5 5 0 0 1 7.53 9.8zm4.31-.78 3.15 3.15.02-.16a3 3 0 0 0-3-3l-.17.01z',
  show: 'M12 4.5C7 4.5 2.73 7.61 1 12c1.73 4.39 6 7.5 11 7.5s9.27-3.11 11-7.5c-1.73-4.39-6-7.5-11-7.5zM12 17a5 5 0 1 1 0-10 5 5 0 0 1 0 10zm0-8a3 3 0 1 0 0 6 3 3 0 0 0 0-6z',
  folder: 'M10 4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8l-2-2z',
  settings: 'M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.49.49 0 0 0-.59-.22l-2.39.96a7.03 7.03 0 0 0-1.62-.94l-.36-2.54a.48.48 0 0 0-.48-.41h-3.84a.48.48 0 0 0-.47.41l-.36 2.54a7.3 7.3 0 0 0-1.62.94l-2.39-.96a.48.48 0 0 0-.59.22L2.74 8.87a.47.47 0 0 0 .12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32a.47.47 0 0 0-.12-.61l-2.01-1.58zM12 15.6a3.6 3.6 0 1 1 0-7.2 3.6 3.6 0 0 1 0 7.2z',
  restore: 'M13 3a9 9 0 0 0-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42A8.95 8.95 0 0 0 13 21a9 9 0 0 0 0-18z',
  rename: 'M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z',
  selectAll: 'M3 5h2V3a2 2 0 0 0-2 2zm0 8h2v-2H3v2zm4 8h2v-2H7v2zM3 9h2V7H3v2zm10-6h-2v2h2V3zm6 0v2h2a2 2 0 0 0-2-2zM5 21v-2H3a2 2 0 0 0 2 2zm-2-4h2v-2H3v2zM9 3H7v2h2V3zm2 18h2v-2h-2v2zm8-8h2v-2h-2v2zm0 8a2 2 0 0 0 2-2h-2v2zm0-12h2V7h-2v2zm0 8h2v-2h-2v2zm-4 4h2v-2h-2v2zm0-16h2V3h-2v2zM7 17h10V7H7v10zm2-8h6v6H9V9z',
  chevron: 'M10 6 8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z',
}
export function Icon({ name, size = 24 }: { name: keyof typeof P | string; size?: number }) {
  return <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true"><path d={P[name] ?? ''} fill="currentColor" /></svg>
}
