import { useEffect, useRef, useState } from 'react'

// A big folder can hold thousands of photos. Each preview is only asked for while its cell is on (or just
// below) the screen, and a request still waiting when the cell scrolls away is dropped, so the server
// spends its time on what is being looked at instead of working through the folder from the top.

/** A see-through 1x1 GIF: put in place of a preview that should not load (yet); it also cancels a pending one. */
export const BLANK = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw=='

const watchers = new Map<Element, (on: boolean) => void>()
// one observer per scrolling box: the margin only works against the box that actually scrolls the list
const observers = new Map<Element | null, IntersectionObserver>()
function scrollBox(el: Element): Element | null {
  for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
    const o = getComputedStyle(p).overflowY
    if (o === 'auto' || o === 'scroll') return p
  }
  return null
}
function observer(root: Element | null) {
  for (const [r, o] of observers) if (r && !r.isConnected) { o.disconnect(); observers.delete(r) }   // the folder view was closed
  let io = observers.get(root)
  if (!io) {
    io = new IntersectionObserver(list => { for (const e of list) watchers.get(e.target)?.(e.isIntersecting) }, { root, rootMargin: '600px 0px' })
    observers.set(root, io)
  }
  return io
}

/** True while the element is within ~600 px of the visible part of its scrolling box. */
export function useNearScreen<T extends Element>(enabled = true) {
  const ref = useRef<T>(null)
  const [near, setNear] = useState(false)
  useEffect(() => {
    const el = ref.current
    if (!enabled || !el || typeof IntersectionObserver === 'undefined') { setNear(enabled); return }
    const io = observer(scrollBox(el))
    watchers.set(el, setNear)
    io.observe(el)
    return () => { watchers.delete(el); io.unobserve(el) }
  }, [enabled])
  return [ref, near] as const
}
