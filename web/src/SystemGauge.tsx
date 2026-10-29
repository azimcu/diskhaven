import { useEffect, useState } from 'react'
import { Cpu, MemoryStick, Thermometer } from 'lucide-react'
import { api } from './api'
import { formatSize } from './format'
import type { SystemSample } from './types'

const pct = (n: number) => `%${Math.round(n)}`
const rate = (n: number | null) => (n == null ? '–' : n < 1024 ? '0' : `${formatSize(n)}/sn`)

/** Small live gauge in the status bar corner: CPU (with a one-minute line), memory, temperature, disk read/write.
 *  Polls only while the tab is visible; the server answers from its last sample, so this never slows the page. */
export function SystemGauge() {
  const [s, setS] = useState<SystemSample | null>(null)
  const [open, setOpen] = useState(false)
  useEffect(() => {
    let timer: number | undefined
    let alive = true
    const tick = () => {
      if (document.hidden) return
      api.systemStats().then(v => alive && setS(v)).catch(() => {})
    }
    tick()
    timer = window.setInterval(tick, 2000)
    document.addEventListener('visibilitychange', tick)
    return () => { alive = false; window.clearInterval(timer); document.removeEventListener('visibilitychange', tick) }
  }, [])
  if (!s || s.cpu == null) return null
  const mem = s.memUsed != null && s.memTotal ? (s.memUsed / s.memTotal) * 100 : null
  const hot = s.temperature != null && s.temperature >= 80
  return (
    <button className="gauge" onClick={() => setOpen(o => !o)} title="Sunucu durumu" aria-expanded={open}>
      <Spark values={s.cpuHistory} />
      <span><Cpu size={12} /> {pct(s.cpu)}</span>
      {mem != null && <span className="hide-sm"><MemoryStick size={12} /> {pct(mem)}</span>}
      {s.temperature != null && <span className={hot ? 'hot' : ''}><Thermometer size={12} /> {Math.round(s.temperature)}°</span>}
      <span className="hide-sm rw" title="Disk okuma / yazma">↓{rate(s.readPerSec)} ↑{rate(s.writePerSec)}</span>
      {open && (
        <span className="gauge-pop" onClick={e => e.stopPropagation()}>
          <b>Sunucu</b>
          <span>İşlemci <em>{pct(s.cpu)}</em></span>
          {mem != null && <span>Bellek <em>{formatSize(s.memUsed)} / {formatSize(s.memTotal)} ({pct(mem)})</em></span>}
          {s.temperature != null && <span>Sıcaklık <em>{s.temperature.toLocaleString('tr-TR')} °C</em></span>}
          <span>Disk okuma <em>{rate(s.readPerSec)}</em></span>
          <span>Disk yazma <em>{rate(s.writePerSec)}</em></span>
          <Spark values={s.cpuHistory} big />
          <small>Son 2 dakika işlemci kullanımı</small>
        </span>
      )}
    </button>
  )
}

function Spark({ values, big }: { values: number[]; big?: boolean }) {
  const w = big ? 220 : 42, h = big ? 46 : 14
  if (values.length < 2) return <svg width={w} height={h} className="spark" />
  const pts = values.map((v, i) => `${(i / (values.length - 1)) * w},${h - 1 - (Math.min(100, v) / 100) * (h - 2)}`).join(' ')
  return (
    <svg width={w} height={h} className="spark" viewBox={`0 0 ${w} ${h}`} aria-hidden="true">
      <polyline points={`0,${h} ${pts} ${w},${h}`} className="spark-fill" />
      <polyline points={pts} className="spark-line" />
    </svg>
  )
}
