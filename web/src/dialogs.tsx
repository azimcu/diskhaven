import { useEffect, useRef, useState, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { t } from './strings'

function Modal({ children, onClose }: { children: ReactNode; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])
  return (
    <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true">{children}</div>
    </div>
  )
}

function mount<T>(render: (done: (v: T) => void) => ReactNode): Promise<T> {
  return new Promise(resolve => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    const done = (v: T) => {
      root.unmount()
      host.remove()
      resolve(v)
    }
    root.render(render(done))
  })
}

export function confirmDialog(message: string, confirmLabel: string, danger = false): Promise<boolean> {
  return mount<boolean>(done => (
    <Modal onClose={() => done(false)}>
      <p className="modal-text">{message}</p>
      <div className="modal-actions">
        <button className="btn" onClick={() => done(false)}>{t.cancel}</button>
        <button className={`btn ${danger ? 'danger' : 'primary'}`} autoFocus onClick={() => done(true)}>{confirmLabel}</button>
      </div>
    </Modal>
  ))
}

function PromptBody({ title, initial, done, type }: { title: string; initial: string; done: (v: string | null) => void; type: string }) {
  const [value, setValue] = useState(initial)
  const ref = useRef<HTMLInputElement>(null)
  useEffect(() => ref.current?.select(), [])
  return (
    <Modal onClose={() => done(null)}>
      <form onSubmit={e => { e.preventDefault(); done(value) }}>
        <label className="modal-title">{title}
          <input ref={ref} className="input" type={type} value={value} onChange={e => setValue(e.target.value)} autoFocus />
        </label>
        <div className="modal-actions">
          <button type="button" className="btn" onClick={() => done(null)}>{t.cancel}</button>
          <button type="submit" className="btn primary">{t.ok}</button>
        </div>
      </form>
    </Modal>
  )
}

export function promptDialog(title: string, initial = '', type = 'text'): Promise<string | null> {
  return mount<string | null>(done => <PromptBody title={title} initial={initial} done={done} type={type} />)
}

// --- toasts -------------------------------------------------------------------------------
export interface Toast { id: number; text: string; error?: boolean; action?: { label: string; run: () => void } }
let toastId = 1
export const toastEvents = new EventTarget()
export function toast(text: string, opts: Omit<Toast, 'id' | 'text'> = {}) {
  toastEvents.dispatchEvent(new CustomEvent<Toast>('toast', { detail: { id: toastId++, text, ...opts } }))
}
export const toastError = (e: unknown) => toast(e instanceof Error ? e.message : String(e), { error: true })

export function Toasts() {
  const [list, setList] = useState<Toast[]>([])
  useEffect(() => {
    const on = (e: Event) => {
      const item = (e as CustomEvent<Toast>).detail
      setList(l => [...l.slice(-3), item])
      setTimeout(() => setList(l => l.filter(x => x.id !== item.id)), item.error ? 8000 : 5000)
    }
    toastEvents.addEventListener('toast', on)
    return () => toastEvents.removeEventListener('toast', on)
  }, [])
  return (
    <div className="toasts" aria-live="polite">
      {list.map(x => (
        <div key={x.id} className={`toast ${x.error ? 'error' : ''}`}>
          <span>{x.text}</span>
          {x.action && (
            <button className="link" onClick={() => { x.action!.run(); setList(l => l.filter(y => y.id !== x.id)) }}>
              {x.action.label}
            </button>
          )}
        </div>
      ))}
    </div>
  )
}
