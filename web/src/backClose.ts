import { useEffect, useRef } from 'react'

// A full-screen layer (photo / video viewer) adds one step to the browser history while it is open, so the phone's
// back gesture, the browser's back button and Android's back key close the layer instead of leaving the folder or
// the page. Closing it with its own button takes that step back out again.
let pendingBack: number | undefined

export function useBackToClose(onClose: () => void) {
  const close = useRef(onClose)
  close.current = onClose
  useEffect(() => {
    // a layer that was just closed and opened again (React re-mounting it) keeps the step it already added
    if (pendingBack !== undefined && history.state?.dhLayer) { clearTimeout(pendingBack); pendingBack = undefined }
    else history.pushState({ ...(history.state ?? {}), dhLayer: true }, '')
    let popped = false
    const onPop = () => { popped = true; close.current() }
    window.addEventListener('popstate', onPop)
    return () => {
      window.removeEventListener('popstate', onPop)
      if (!popped) pendingBack = window.setTimeout(() => { pendingBack = undefined; if (history.state?.dhLayer) history.back() }, 0)
    }
  }, [])
}
