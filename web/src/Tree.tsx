import { useEffect, useState } from 'react'
import { Archive, ChevronRight, Clock, HardDrive, Link2, Star, Trash2, Usb } from 'lucide-react'
import { usePlugins } from './plugins'
import { formatSize } from './format'
import type { Disk } from './types'
import { api, folderEntries } from './api'
import { FileIcon } from './FileIcon'
import { t } from './strings'

interface Props {
  current: string | null // null = a special page is open
  special: string | null
  version: number
  onOpen: (path: string) => void
  onOpenSpecial: (view: 'trash' | 'disks' | 'shares' | 'hideaway') => void
  isAdmin: boolean
  onDropItems: (dest: string, e: React.DragEvent) => void
  storage: { total: number; free: number } | null
}

/** Left navigation pane: folder tree of the NAS (loaded lazily) and the recycle bin. */
export function Tree({ current, special, version, onOpen, onOpenSpecial, isAdmin, onDropItems, storage }: Props) {
  // drives, like "This PC": the NAS storage and (for admins) the other disks with their fill level
  const plugins = usePlugins()
  const [disks, setDisks] = useState<Disk[]>([])
  const [quick, setQuick] = useState<{ favorites: string[]; recent: string[] }>({ favorites: [], recent: [] })
  useEffect(() => {
    if (!plugins.has('quick')) return
    api.quick().then(setQuick).catch(() => {})
  }, [version, current, plugins.has('quick')])
  useEffect(() => {
    if (!isAdmin || !plugins.has('disks')) return
    let alive = true
    const load = () => api.disks().then(r => alive && setDisks(r.disks)).catch(() => {})
    load()
    const timer = window.setInterval(load, 30000)
    return () => { alive = false; window.clearInterval(timer) }
  }, [isAdmin, version, plugins.has('disks')])
  // expand the path to the current folder automatically
  const [expanded, setExpanded] = useState<Set<string>>(new Set(['']))
  useEffect(() => {
    if (current == null) return
    setExpanded(prev => {
      const next = new Set(prev)
      const parts = current.split('/').filter(Boolean)
      for (let i = 0; i < parts.length; i++) next.add(parts.slice(0, i).join('/'))
      next.add('')
      return next
    })
  }, [current])

  const toggle = (path: string) =>
    setExpanded(prev => {
      const next = new Set(prev)
      if (next.has(path)) next.delete(path)
      else next.add(path)
      return next
    })

  return (
    <nav className="tree" aria-label="Klasörler">
      {plugins.has('quick') && quick.favorites.length > 0 && <>
        <div className="drives-title">Favoriler</div>
        {quick.favorites.map(p => (
          <button key={p} className={`tree-row ${current === p ? 'active' : ''}`} style={{ paddingLeft: 30 }} onClick={() => onOpen(p)} title={`/${p}`}>
            <Star size={15} className="ic" strokeWidth={1.5} /> <span className="tree-label">{p.split('/').pop()}</span>
          </button>
        ))}
        <div className="tree-sep" />
      </>}
      <TreeNode path="" name={t.thisNas} depth={0} root {...{ current, expanded, toggle, onOpen, version, onDropItems }} />
      <div className="tree-sep" />
      <div className="drives" aria-label="Sürücüler">
        <div className="drives-title">Sürücüler</div>
        {storage && <Drive name={t.thisNas} icon={<HardDrive size={18} strokeWidth={1.5} />} total={storage.total} free={storage.free}
          onClick={() => onOpen('')} />}
        {disks.map(d => (
          <Drive key={d.id} name={d.name || d.label || d.model} icon={<Usb size={18} strokeWidth={1.5} />}
            total={d.usage?.total ?? d.size} free={d.usage?.free ?? null} off={d.state !== 'mounted'}
            note={d.state === 'mounted' ? undefined : d.state === 'off' ? 'kapalı' : 'bağlı değil'}
            active={special === 'disks'} onClick={() => onOpenSpecial('disks')} />
        ))}
      </div>
      <div className="tree-sep" />
      {plugins.has('quick') && quick.recent.length > 0 && (
        <details className="recent">
          <summary className="tree-row" style={{ paddingLeft: 30 }}><Clock size={16} className="ic" strokeWidth={1.5} /> <span className="tree-label">Son kullanılanlar</span></summary>
          {quick.recent.slice(0, 10).map(p => (
            <button key={p} className="tree-row" style={{ paddingLeft: 52 }} onClick={() => onOpen(p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '')} title={`/${p}`}>
              <span className="tree-label">{p.split('/').pop()}</span>
            </button>
          ))}
        </details>
      )}
      {plugins.has('sharing') && <button className={`tree-row ${special === 'shares' ? 'active' : ''}`} style={{ paddingLeft: 30 }} onClick={() => onOpenSpecial('shares')}>
        <Link2 size={16} className="ic" strokeWidth={1.5} /> <span className="tree-label">{t.shares}</span>
      </button>}
      {plugins.has('vault') && <button className={`tree-row ${special === 'hideaway' ? 'active' : ''}`} style={{ paddingLeft: 30 }} onClick={() => onOpenSpecial('hideaway')}>
        <Archive size={16} className="ic" strokeWidth={1.5} /> <span className="tree-label">{plugins.vaultLabel ?? 'Gizli klasör'}</span>
      </button>}
      {isAdmin && plugins.has('disks') && <button className={`tree-row ${special === 'disks' ? 'active' : ''}`} style={{ paddingLeft: 30 }} onClick={() => onOpenSpecial('disks')}>
        <Usb size={16} className="ic" strokeWidth={1.5} /> <span className="tree-label">{t.disks}</span>
      </button>}
      <button className={`tree-row ${special === 'trash' ? 'active' : ''}`} style={{ paddingLeft: 30 }} onClick={() => onOpenSpecial('trash')}>
        <Trash2 size={16} className="ic" strokeWidth={1.5} /> <span className="tree-label">{t.trash}</span>
      </button>
    </nav>
  )
}

interface NodeProps {
  path: string
  name: string
  depth: number
  root?: boolean
  current: string | null
  expanded: Set<string>
  version: number
  toggle: (p: string) => void
  onOpen: (p: string) => void
  onDropItems: (dest: string, e: React.DragEvent) => void
}

function TreeNode(props: NodeProps) {
  const { path, name, depth, root, current, expanded, version, toggle, onOpen, onDropItems } = props
  const open = expanded.has(path)
  const [children, setChildren] = useState<{ name: string; path: string }[] | null>(null)
  const [dropping, setDropping] = useState(false)

  useEffect(() => {
    if (!open) return
    let alive = true
    api.list(path)
      .then(l => alive && setChildren(folderEntries(l.entries).filter(e => !e.hidden).map(e => ({ name: e.name, path: e.path }))))
      .catch(() => alive && setChildren([]))
    return () => { alive = false }
  }, [open, path, version])

  return (
    <>
      <div
        className={`tree-row ${current === path ? 'active' : ''} ${dropping ? 'drop' : ''}`}
        style={{ paddingLeft: 6 + depth * 14 }}
        onClick={() => onOpen(path)}
        onDragOver={e => { e.preventDefault(); setDropping(true) }}
        onDragLeave={() => setDropping(false)}
        onDrop={e => { setDropping(false); onDropItems(path, e) }}
        role="treeitem"
        aria-expanded={open}
        aria-selected={current === path}
      >
        <button
          className={`tree-chevron ${open ? 'open' : ''} ${children?.length === 0 ? 'none' : ''}`}
          onClick={e => { e.stopPropagation(); toggle(path) }}
          aria-label={open ? 'Daralt' : 'Genişlet'}
        >
          <ChevronRight size={14} />
        </button>
        {root ? <HardDrive size={16} className="ic" strokeWidth={1.5} /> : <FileIcon entry={{ kind: 'Folder', extension: null }} size={16} />}
        <span className="tree-label" title={name}>{name}</span>
      </div>
      {open && children?.map(c => <TreeNode key={c.path} {...props} path={c.path} name={c.name} depth={depth + 1} root={false} />)}
    </>
  )
}

function Drive({ name, icon, total, free, active, off, note, onClick }: {
  name: string; icon: React.ReactNode; total: number; free: number | null; active?: boolean; off?: boolean; note?: string; onClick: () => void
}) {
  const used = free == null || !total ? null : (total - free) / total
  return (
    <button className={`drive ${active ? 'active' : ''} ${off ? 'off' : ''}`} onClick={onClick} title={name}>
      <span className="ic">{icon}</span>
      <b>{name}</b>
      {used != null && <span className={`bar ${used > 0.9 ? 'full' : ''}`}><i style={{ width: `${Math.round(used * 100)}%` }} /></span>}
      <small>{used != null ? `${formatSize(free)} boş / ${formatSize(total)}` : note ?? formatSize(total)}</small>
    </button>
  )
}
