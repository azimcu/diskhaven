import { useEffect, useState } from 'react'
import { ChevronRight, HardDrive, Link2, Trash2, Usb } from 'lucide-react'
import { api, folderEntries } from './api'
import { FileIcon } from './FileIcon'
import { t } from './strings'

interface Props {
  current: string | null // null = a special page is open
  special: 'trash' | 'disks' | 'shares' | 'users' | null
  version: number
  onOpen: (path: string) => void
  onOpenSpecial: (view: 'trash' | 'disks' | 'shares') => void
  isAdmin: boolean
  onDropItems: (dest: string, e: React.DragEvent) => void
}

/** Left navigation pane: folder tree of the NAS (loaded lazily) and the recycle bin. */
export function Tree({ current, special, version, onOpen, onOpenSpecial, isAdmin, onDropItems }: Props) {
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
      <TreeNode path="" name={t.thisNas} depth={0} root {...{ current, expanded, toggle, onOpen, version, onDropItems }} />
      <div className="tree-sep" />
      <button className={`tree-row ${special === 'shares' ? 'active' : ''}`} style={{ paddingLeft: 30 }} onClick={() => onOpenSpecial('shares')}>
        <Link2 size={16} className="ic" strokeWidth={1.5} /> <span className="tree-label">{t.shares}</span>
      </button>
      {isAdmin && <button className={`tree-row ${special === 'disks' ? 'active' : ''}`} style={{ paddingLeft: 30 }} onClick={() => onOpenSpecial('disks')}>
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
