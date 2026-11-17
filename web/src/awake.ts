// Keeps the phone's screen on while files are uploading. A locked iPhone suspends Safari within
// seconds and the upload stops; with the screen on it simply keeps going.
// The Screen Wake Lock API only exists on HTTPS pages; DiskHaven is often opened over plain HTTP
// (Tailscale / home network), so a tiny muted looping video does the same job there (iOS and
// Android keep the screen on while a video plays).

let wanted = false
let lock: { release(): Promise<void> } | null = null
let video: HTMLVideoElement | null = null

function getVideo() {
  if (!video) {
    video = document.createElement('video')
    video.src = '/uyanik.mp4'
    video.muted = true
    video.loop = true
    video.playsInline = true
    video.setAttribute('playsinline', '')
    video.setAttribute('muted', '')
    video.setAttribute('aria-hidden', 'true')
    video.style.cssText = 'position:fixed;width:1px;height:1px;opacity:0;pointer-events:none;left:0;bottom:0'
    document.body.appendChild(video)
  }
  return video
}

async function acquire() {
  if (!wanted || document.visibilityState !== 'visible') return
  const wl = (navigator as Navigator & { wakeLock?: { request(t: 'screen'): Promise<{ release(): Promise<void> }> } }).wakeLock
  if (wl && window.isSecureContext) {
    try {
      if (!lock) lock = await wl.request('screen')
      return
    } catch { /* not allowed right now: fall back to the video */ }
  }
  try { await getVideo().play() } catch { /* needs a tap first; prime() runs on the upload button */ }
}

function release() {
  lock?.release().catch(() => {})
  lock = null
  video?.pause()
}

document.addEventListener('visibilitychange', () => {
  // the browser drops the wake lock when the page is hidden; take it again when it comes back
  if (document.visibilityState === 'visible') { lock = null; void acquire() }
})

export const awake = {
  /** Call from a tap (e.g. the upload button) so the video may start later without one. */
  prime() {
    if (typeof window.isSecureContext === 'boolean' && window.isSecureContext && 'wakeLock' in navigator) return
    const v = getVideo()
    v.play().then(() => { if (!wanted) v.pause() }).catch(() => {})
  },
  set(on: boolean) {
    if (on === wanted) return
    wanted = on
    if (on) void acquire()
    else release()
  },
  get on() { return wanted },
}
