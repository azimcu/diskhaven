import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  ArrowDown, ArrowLeft, ArrowRight, ArrowUp, ChevronRight, ClipboardPaste, Copy, Download, FolderOpen, FolderPlus,
  FolderUp, LayoutGrid, List, MoreVertical, PanelRight, Pencil, RefreshCw, Scissors, Search, Trash2, Upload,
} from 'lucide-react'
import { api } from './api'
import { toast, toastError } from './dialogs'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize, nameOf, parentOf, previewKind, typeName, uniqueName } from './format'
import { PreviewModal, PreviewPane } from './Preview'
import { t } from './strings'
import type { FileEntry } from './types'
import { filesFromInput, uploadEvents, uploads } from './uploads'

export interface Clipboard { mode: 'copy' | 'cut'; paths: string[] }
type SortKey = 'name' | 'modified' | 'type' | 'size'
type Menu = { x: number; y: number; onItem: boolean }

const coarse = () => window.matchMedia('(pointer: coarse)').matches
const collator = new Intl.Collator('tr-TR', { numeric: true, sensitivity: 'base' })
const readPref = (k: string, d: string) => { try { return localStorage.getItem(k) ?? d } catch { return d } }
const writePref = (k: string, v: string) => { try { localStorage.setItem(k, v) } catch { /* private mode */ } }

interface Props {
  path: string
  version: number
  clipboard: Clipboard | null
  setClipboard: (c: Clipboard | null) => void
  navigate: (path: string) => void
  changed: () => void
  onDropItems: (dest: string, e: React.DragEvent) => void
  storage: { total: number; free: number } | null
}

export function Explorer({ path, version, clipboard, setClipboard, navigate, changed, onDropItems, storage }: Props) {
  const [entries, setEntries] = useState<FileEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [anchor, setAnchor] = useState<string | null>(null)
  const [sort, setSort] = useState<{ key: SortKey; asc: boolean }>({ key: 'name', asc: true })
  const [view, setView] = useState(() => readPref('hv.view', 'details'))
  const [pane, setPane] = useState(() => readPref('hv.pane', '0') === '1')
  const [filter, setFilter] = useState('')
  const [renaming, setRenaming] = useState<string | null>(null)
  const [menu, setMenu] = useState<Menu | null>(null)
  const [viewer, setViewer] = useState<FileEntry | null>(null)
  const [dragOver, setDragOver] = useState(false)
  const [editingPath, setEditingPath] = useState(false)
  const listRef = useRef<HTMLDivElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const folderInput = useRef<HTMLInputElement>(null)
  const longPress = useRef<number | undefined>(undefined)

  const load = useCallback(() => {
    api.list(path)
      .then(l => { setEntries(l.entries); setError(null) })
      .catch(e => { setEntries([]); setError(e.message) })
  }, [path])

  useEffect(() => { setEntries(null); setSelected(new Set()); setFilter(''); setRenaming(null) }, [path])
  useEffect(load, [load, version])
  useEffect(() => {
    const on = (e: Event) => { if ((e as CustomEvent).detail === path) load() }
    uploadEvents.addEventListener('committed', on)
    return () => uploadEvents.removeEventListener('committed', on)
  }, [path, load])
  useEffect(() => { listRef.current?.focus({ preventScroll: true }) }, [path])

  const shown = useMemo(() => {
    if (!entries) return []
    const f = filter.trim().toLocaleLowerCase('tr-TR')
    const list = entries.filter(e => !e.hidden && (!f || e.name.toLocaleLowerCase('tr-TR').includes(f)))
    const dir = sort.asc ? 1 : -1
    return list.sort((a, b) => {
      if (a.kind !== b.kind) return a.kind === 'Folder' ? -1 : 1 // folders always first, like Explorer
      let c = 0
      if (sort.key === 'modified') c = a.modified.localeCompare(b.modified)
      else if (sort.key === 'size') c = (a.size ?? 0) - (b.size ?? 0)
      else if (sort.key === 'type') c = collator.compare(typeName(a), typeName(b))
      return (c || collator.compare(a.name, b.name)) * dir
    })
  }, [entries, filter, sort])

  const sel = shown.filter(e => selected.has(e.path))
  const single = sel.length === 1 ? sel[0] : null
  const previewable = shown.filter(e => e.kind === 'File' && previewKind(e.extension))

  // ---- actions ----------------------------------------------------------------------------
  const open = (e: FileEntry) => {
    if (e.kind === 'Folder') return navigate(e.path)
    if (previewKind(e.extension)) return setViewer(e)
    window.location.href = api.contentUrl(e.path, true)
  }

  const download = (list: FileEntry[]) => {
    for (const e of list.filter(x => x.kind === 'File')) {
      const a = document.createElement('a')
      a.href = api.contentUrl(e.path, true)
      a.download = e.name
      document.body.appendChild(a)
      a.click()
      a.remove()
    }
  }

  const newFolder = async () => {
    const name = uniqueName((entries ?? []).map(e => e.name), t.newFolder)
    try {
      const r = await api.createFolder(path, name)
      load()
      changed()
      setSelected(new Set([r.path]))
      setRenaming(r.path)
    } catch (e) { toastError(e) }
  }

  const commitRename = async (entry: FileEntry, value: string) => {
    setRenaming(null)
    const name = value.trim()
    if (!name || name === entry.name) return
    try {
      const r = await api.rename(entry.path, name)
      setSelected(new Set([r.path]))
      load()
      changed()
    } catch (e) { toastError(e) }
  }

  const remove = async (list: FileEntry[]) => {
    if (list.length === 0) return
    try {
      const r = await api.remove(list.map(e => e.path))
      setSelected(new Set())
      load()
      changed()
      toast(t.movedToTrash(r.items.length), {
        action: {
          label: t.undo,
          run: async () => {
            try { for (const i of r.items) await api.restore(i.id) } catch (e) { toastError(e) }
            load()
            changed()
          },
        },
      })
    } catch (e) { toastError(e) }
  }

  const toClipboard = (mode: Clipboard['mode'], list: FileEntry[]) => {
    if (list.length === 0) return
    setClipboard({ mode, paths: list.map(e => e.path) })
    toast(mode === 'copy' ? `${list.length} öğe kopyalandı` : `${list.length} öğe kesildi`)
  }

  const paste = async () => {
    if (!clipboard) return
    try {
      const r = clipboard.mode === 'copy' ? await api.copy(clipboard.paths, path) : await api.move(clipboard.paths, path)
      if (clipboard.mode === 'cut') setClipboard(null)
      setSelected(new Set(r.paths))
      load()
      changed()
    } catch (e) { toastError(e) }
  }

  const pickUpload = (folder: boolean) => (folder ? folderInput : fileInput).current?.click()
  const onPicked = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files) uploads.add(path, filesFromInput(e.target.files))
    e.target.value = ''
  }

  // ---- selection --------------------------------------------------------------------------
  const clickRow = (e: React.MouseEvent, entry: FileEntry) => {
    if (coarse()) {
      if (selected.size === 0) return open(entry)
      return setSelected(prev => { const n = new Set(prev); if (n.has(entry.path)) n.delete(entry.path); else n.add(entry.path); return n })
    }
    if (e.shiftKey && anchor) {
      const a = shown.findIndex(x => x.path === anchor)
      const b = shown.findIndex(x => x.path === entry.path)
      const [from, to] = a < b ? [a, b] : [b, a]
      const range = shown.slice(from, to + 1).map(x => x.path)
      setSelected(prev => new Set(e.ctrlKey || e.metaKey ? [...prev, ...range] : range))
      return
    }
    if (e.ctrlKey || e.metaKey) {
      setSelected(prev => { const n = new Set(prev); if (n.has(entry.path)) n.delete(entry.path); else n.add(entry.path); return n })
    } else {
      setSelected(new Set([entry.path]))
    }
    setAnchor(entry.path)
  }

  const contextRow = (e: React.MouseEvent | { clientX: number; clientY: number; preventDefault(): void }, entry: FileEntry | null) => {
    e.preventDefault()
    if (entry && !selected.has(entry.path)) { setSelected(new Set([entry.path])); setAnchor(entry.path) }
    if (!entry) setSelected(new Set())
    setMenu({ x: e.clientX, y: e.clientY, onItem: entry !== null })
  }

  const touchStart = (e: React.TouchEvent, entry: FileEntry) => {
    const touch = e.touches[0]
    longPress.current = window.setTimeout(() => {
      setSelected(prev => new Set([...prev, entry.path]))
      setMenu({ x: touch.clientX, y: touch.clientY, onItem: true })
    }, 550)
  }
  const touchEnd = () => window.clearTimeout(longPress.current)

  const moveFocus = (delta: number, extend: boolean) => {
    if (shown.length === 0) return
    const current = anchor ? shown.findIndex(x => x.path === anchor) : -1
    const idx = Math.max(0, Math.min(shown.length - 1, current < 0 ? 0 : current + delta))
    const target = shown[idx]
    if (extend && anchor) setSelected(prev => new Set([...prev, target.path]))
    else setSelected(new Set([target.path]))
    setAnchor(target.path)
    listRef.current?.querySelector(`[data-path="${CSS.escape(target.path)}"]`)?.scrollIntoView({ block: 'nearest' })
  }

  const onKey = (e: React.KeyboardEvent) => {
    if ((e.target as HTMLElement).tagName === 'INPUT') return
    const mod = e.ctrlKey || e.metaKey
    const k = e.key
    const handled = () => { e.preventDefault(); e.stopPropagation() }
    if (k === 'ArrowDown') { handled(); moveFocus(1, e.shiftKey) }
    else if (k === 'ArrowUp' && e.altKey) { handled(); if (path) navigate(parentOf(path)) }
    else if (k === 'ArrowUp') { handled(); moveFocus(-1, e.shiftKey) }
    else if (k === 'ArrowLeft' && e.altKey) { handled(); history.back() }
    else if (k === 'ArrowRight' && e.altKey) { handled(); history.forward() }
    else if (k === 'Backspace') { handled(); history.back() }
    else if (k === 'Enter' && single) { handled(); open(single) }
    else if (k === 'Delete') { handled(); void remove(sel) }
    else if (k === 'F2' && single) { handled(); setRenaming(single.path) }
    else if (k === 'F5') { handled(); load() }
    else if (k === 'Escape') { setSelected(new Set()); setMenu(null) }
    else if (mod && k.toLowerCase() === 'a') { handled(); setSelected(new Set(shown.map(x => x.path))) }
    else if (mod && k.toLowerCase() === 'c') { handled(); toClipboard('copy', sel) }
    else if (mod && k.toLowerCase() === 'x') { handled(); toClipboard('cut', sel) }
    else if (mod && k.toLowerCase() === 'v') { handled(); void paste() }
    else if (mod && e.shiftKey && k.toLowerCase() === 'n') { handled(); void newFolder() }
  }

  // ---- drag & drop ------------------------------------------------------------------------
  const dragStart = (e: React.DragEvent, entry: FileEntry) => {
    const paths = selected.has(entry.path) ? [...selected] : [entry.path]
    if (!selected.has(entry.path)) setSelected(new Set([entry.path]))
    e.dataTransfer.setData('application/x-homevault', JSON.stringify(paths))
    e.dataTransfer.effectAllowed = 'copyMove'
  }

  const sortBy = (key: SortKey) => setSort(s => ({ key, asc: s.key === key ? !s.asc : true }))
  const SortMark = ({ k }: { k: SortKey }) => (sort.key === k ? (sort.asc ? <ArrowUp size={12} /> : <ArrowDown size={12} />) : null)

  const crumbs = path ? path.split('/') : []
  const folderName = path ? nameOf(path) : t.thisNas
  const selectedSize = sel.reduce((s, e) => s + (e.size ?? 0), 0)

  return (
    <div className="main-col" onClick={() => setMenu(null)}>
      <input ref={fileInput} type="file" multiple hidden onChange={onPicked} />
      <input ref={folderInput} type="file" hidden onChange={onPicked} {...{ webkitdirectory: '', directory: '' }} />

      <div className="commandbar" role="toolbar">
        <button className="cmd" onClick={newFolder} title={`${t.newFolder} (Ctrl+Shift+N)`}><FolderPlus size={16} /> <span>{t.newFolder}</span></button>
        <button className="cmd" onClick={() => pickUpload(false)}><Upload size={16} /> <span>{t.upload}</span></button>
        <button className="cmd hide-sm" onClick={() => pickUpload(true)}><FolderUp size={16} /> <span>{t.uploadFolder}</span></button>
        <span className="cmd-sep" />
        <button className="cmd icon-only" disabled={!sel.length} onClick={() => toClipboard('cut', sel)} title={`${t.cut} (Ctrl+X)`}><Scissors size={16} /></button>
        <button className="cmd icon-only" disabled={!sel.length} onClick={() => toClipboard('copy', sel)} title={`${t.copy} (Ctrl+C)`}><Copy size={16} /></button>
        <button className="cmd icon-only" disabled={!clipboard} onClick={paste} title={`${t.paste} (Ctrl+V)`}><ClipboardPaste size={16} /></button>
        <button className="cmd icon-only" disabled={!single} onClick={() => single && setRenaming(single.path)} title={`${t.rename} (F2)`}><Pencil size={16} /></button>
        <button className="cmd icon-only" disabled={!sel.length} onClick={() => remove(sel)} title={`${t.delete} (Del)`}><Trash2 size={16} /></button>
        <button className="cmd icon-only" disabled={!sel.some(e => e.kind === 'File')} onClick={() => download(sel)} title={t.download}><Download size={16} /></button>
        <span className="cmd-grow" />
        <button className={`cmd icon-only ${view === 'details' ? 'on' : ''}`} onClick={() => { setView('details'); writePref('hv.view', 'details') }} title={t.details}><List size={16} /></button>
        <button className={`cmd icon-only ${view === 'icons' ? 'on' : ''}`} onClick={() => { setView('icons'); writePref('hv.view', 'icons') }} title={t.largeIcons}><LayoutGrid size={16} /></button>
        <button className={`cmd icon-only hide-sm ${pane ? 'on' : ''}`} onClick={() => { setPane(!pane); writePref('hv.pane', pane ? '0' : '1') }} title={t.previewPane}><PanelRight size={16} /></button>
      </div>

      <div className="addressbar">
        <button className="icon-btn" onClick={() => history.back()} title={`${t.back} (Alt+←)`}><ArrowLeft size={16} /></button>
        <button className="icon-btn hide-sm" onClick={() => history.forward()} title={`${t.forward} (Alt+→)`}><ArrowRight size={16} /></button>
        <button className="icon-btn" disabled={!path} onClick={() => navigate(parentOf(path))} title={`${t.up} (Alt+↑)`}><ArrowUp size={16} /></button>
        <button className="icon-btn hide-sm" onClick={load} title={`${t.refresh} (F5)`}><RefreshCw size={15} /></button>
        {editingPath ? (
          <form className="crumbs editing" onSubmit={e => { e.preventDefault(); setEditingPath(false); navigate((new FormData(e.currentTarget).get('p') as string).replace(/^\/+|\/+$/g, '')) }}>
            <input name="p" className="path-input" defaultValue={path ? `/${path}` : '/'} autoFocus onBlur={() => setEditingPath(false)} onKeyDown={e => e.key === 'Escape' && setEditingPath(false)} />
          </form>
        ) : (
          <div className="crumbs" onClick={e => e.target === e.currentTarget && setEditingPath(true)}>
            <button className={`crumb ${crumbs.length === 0 ? 'current' : ''}`} onClick={() => navigate('')}>{t.thisNas}</button>
            {crumbs.map((c, i) => (
              <span key={i} className="crumb-wrap">
                <ChevronRight size={14} className="crumb-sep" />
                <button className={`crumb ${i === crumbs.length - 1 ? 'current' : ''}`} onClick={() => navigate(crumbs.slice(0, i + 1).join('/'))}>{c}</button>
              </span>
            ))}
          </div>
        )}
        <label className="search">
          <Search size={14} />
          <input value={filter} onChange={e => setFilter(e.target.value)} placeholder={t.searchIn(folderName)} />
        </label>
      </div>

      <div className="content">
        <div
          ref={listRef}
          className={`list-wrap ${dragOver ? 'dragover' : ''}`}
          tabIndex={0}
          onKeyDown={onKey}
          onClick={e => { if (e.target === e.currentTarget) setSelected(new Set()) }}
          onContextMenu={e => { if (!(e.target as HTMLElement).closest('[data-path]')) contextRow(e, null) }}
          onDragOver={e => { e.preventDefault(); setDragOver(true) }}
          onDragLeave={e => { if (e.currentTarget === e.target) setDragOver(false) }}
          onDrop={e => { setDragOver(false); if (!(e.target as HTMLElement).closest('[data-folder]')) onDropItems(path, e) }}
        >
          {entries === null && <div className="loading"><span className="spinner" /></div>}
          {error && <p className="empty-note error">{error}</p>}
          {entries && !error && shown.length === 0 && <p className="empty-note">{filter ? t.noMatch : t.emptyFolder}<br /><small>{!filter && t.dropHere}</small></p>}

          {view === 'details' && shown.length > 0 && (
            <table className="details" role="grid">
              <thead>
                <tr>
                  <th onClick={() => sortBy('name')}>{t.colName} <SortMark k="name" /></th>
                  <th className="c-date" onClick={() => sortBy('modified')}>{t.colModified} <SortMark k="modified" /></th>
                  <th className="c-type" onClick={() => sortBy('type')}>{t.colType} <SortMark k="type" /></th>
                  <th className="c-size" onClick={() => sortBy('size')}>{t.colSize} <SortMark k="size" /></th>
                  <th className="c-more" />
                </tr>
              </thead>
              <tbody>
                {shown.map(e => (
                  <Row key={e.path} entry={e} selected={selected.has(e.path)} cut={clipboard?.mode === 'cut' && clipboard.paths.includes(e.path)}
                    renaming={renaming === e.path} onRename={v => commitRename(e, v)} onCancelRename={() => setRenaming(null)}
                    onClick={ev => clickRow(ev, e)} onDoubleClick={() => open(e)} onContextMenu={ev => contextRow(ev, e)}
                    onDragStart={ev => dragStart(ev, e)} onDropItems={onDropItems}
                    onTouchStart={ev => touchStart(ev, e)} onTouchEnd={touchEnd}
                    onMore={ev => { ev.stopPropagation(); const r = (ev.currentTarget as HTMLElement).getBoundingClientRect(); contextRow({ clientX: r.left, clientY: r.bottom, preventDefault() {} }, e) }} />
                ))}
              </tbody>
            </table>
          )}

          {view === 'icons' && shown.length > 0 && (
            <div className="grid">
              {shown.map(e => (
                <div key={e.path} data-path={e.path} {...(e.kind === 'Folder' ? { 'data-folder': '' } : {})}
                  className={`tile ${selected.has(e.path) ? 'sel' : ''} ${clipboard?.mode === 'cut' && clipboard.paths.includes(e.path) ? 'cut' : ''}`}
                  draggable onDragStart={ev => dragStart(ev, e)}
                  onDragOver={ev => { if (e.kind === 'Folder') { ev.preventDefault(); ev.currentTarget.classList.add('drop') } }}
                  onDragLeave={ev => ev.currentTarget.classList.remove('drop')}
                  onDrop={ev => { ev.currentTarget.classList.remove('drop'); if (e.kind === 'Folder') { ev.stopPropagation(); onDropItems(e.path, ev) } }}
                  onClick={ev => clickRow(ev, e)} onDoubleClick={() => open(e)} onContextMenu={ev => contextRow(ev, e)}
                  onTouchStart={ev => touchStart(ev, e)} onTouchEnd={touchEnd} onTouchMove={touchEnd}>
                  <Thumb entry={e} />
                  {renaming === e.path
                    ? <RenameInput entry={e} onDone={v => commitRename(e, v)} onCancel={() => setRenaming(null)} />
                    : <span className="tile-name" title={e.name}>{e.name}</span>}
                </div>
              ))}
            </div>
          )}
        </div>
        {pane && <PreviewPane entry={single} count={sel.length} />}
      </div>

      <footer className="statusbar">
        <span>{t.items(shown.length)}</span>
        {sel.length > 0 && <span>{t.selected(sel.length)}{selectedSize > 0 ? ` · ${formatSize(selectedSize)}` : ''}</span>}
        <span className="grow" />
        {storage && <span className="hide-sm">{t.free(formatSize(storage.free), formatSize(storage.total))}</span>}
      </footer>

      {menu && (
        <ContextMenu x={menu.x} y={menu.y} onClose={() => setMenu(null)} items={menu.onItem ? [
          single && { label: single.kind === 'Folder' ? t.open : previewKind(single.extension) ? t.open : t.download, icon: <FolderOpen size={16} />, run: () => open(single) },
          sel.some(e => e.kind === 'File') && { label: t.download, icon: <Download size={16} />, run: () => download(sel) },
          'sep',
          { label: t.cut, icon: <Scissors size={16} />, hint: 'Ctrl+X', run: () => toClipboard('cut', sel) },
          { label: t.copy, icon: <Copy size={16} />, hint: 'Ctrl+C', run: () => toClipboard('copy', sel) },
          'sep',
          single && { label: t.rename, icon: <Pencil size={16} />, hint: 'F2', run: () => setRenaming(single.path) },
          { label: t.delete, icon: <Trash2 size={16} />, hint: 'Del', run: () => remove(sel) },
        ] : [
          { label: t.newFolder, icon: <FolderPlus size={16} />, hint: 'Ctrl+Shift+N', run: newFolder },
          clipboard && { label: t.paste, icon: <ClipboardPaste size={16} />, hint: 'Ctrl+V', run: paste },
          'sep',
          { label: t.uploadFiles, icon: <Upload size={16} />, run: () => pickUpload(false) },
          { label: t.uploadFolder, icon: <FolderUp size={16} />, run: () => pickUpload(true) },
          'sep',
          { label: t.refresh, icon: <RefreshCw size={16} />, hint: 'F5', run: load },
          { label: t.selectAll, hint: 'Ctrl+A', run: () => setSelected(new Set(shown.map(x => x.path))) },
        ]} />
      )}

      {viewer && (
        <PreviewModal entry={viewer} onClose={() => setViewer(null)}
          onPrev={previewable.indexOf(viewer) > 0 ? () => setViewer(previewable[previewable.indexOf(viewer) - 1]) : undefined}
          onNext={previewable.indexOf(viewer) < previewable.length - 1 ? () => setViewer(previewable[previewable.indexOf(viewer) + 1]) : undefined} />
      )}
    </div>
  )
}

interface RowProps {
  entry: FileEntry
  selected: boolean
  cut: boolean
  renaming: boolean
  onRename: (v: string) => void
  onCancelRename: () => void
  onClick: (e: React.MouseEvent) => void
  onDoubleClick: () => void
  onContextMenu: (e: React.MouseEvent) => void
  onDragStart: (e: React.DragEvent) => void
  onDropItems: (dest: string, e: React.DragEvent) => void
  onTouchStart: (e: React.TouchEvent) => void
  onTouchEnd: () => void
  onMore: (e: React.MouseEvent) => void
}

function Row(p: RowProps) {
  const { entry: e } = p
  const [drop, setDrop] = useState(false)
  const folder = e.kind === 'Folder'
  return (
    <tr data-path={e.path} {...(folder ? { 'data-folder': '' } : {})}
      className={`${p.selected ? 'sel' : ''} ${p.cut ? 'cut' : ''} ${drop ? 'drop' : ''}`}
      draggable={!p.renaming} onDragStart={p.onDragStart}
      onDragOver={ev => { if (folder) { ev.preventDefault(); setDrop(true) } }}
      onDragLeave={() => setDrop(false)}
      onDrop={ev => { setDrop(false); if (folder) { ev.stopPropagation(); p.onDropItems(e.path, ev) } }}
      onClick={p.onClick} onDoubleClick={p.onDoubleClick} onContextMenu={p.onContextMenu}
      onTouchStart={p.onTouchStart} onTouchEnd={p.onTouchEnd} onTouchMove={p.onTouchEnd}
      aria-selected={p.selected}>
      <td className="c-name">
        <FileIcon entry={e} />
        {p.renaming ? <RenameInput entry={e} onDone={p.onRename} onCancel={p.onCancelRename} /> : <span className="name-text" title={e.name}>{e.name}</span>}
      </td>
      <td className="c-date">{formatDate(e.modified)}</td>
      <td className="c-type">{typeName(e)}</td>
      <td className="c-size">{folder ? '' : formatSize(e.size)}</td>
      <td className="c-more"><button className="icon-btn" onClick={p.onMore} aria-label="Diğer"><MoreVertical size={16} /></button></td>
    </tr>
  )
}

function RenameInput({ entry, onDone, onCancel }: { entry: FileEntry; onDone: (v: string) => void; onCancel: () => void }) {
  const ref = useRef<HTMLInputElement>(null)
  const done = useRef(false)
  useEffect(() => {
    const el = ref.current!
    el.focus()
    // select the name without its extension, like Explorer
    const dot = entry.kind === 'File' ? entry.name.lastIndexOf('.') : -1
    el.setSelectionRange(0, dot > 0 ? dot : entry.name.length)
  }, [entry])
  const finish = (save: boolean) => {
    if (done.current) return
    done.current = true
    if (save) onDone(ref.current!.value)
    else onCancel()
  }
  return (
    <input ref={ref} className="rename" defaultValue={entry.name}
      onClick={e => e.stopPropagation()} onDoubleClick={e => e.stopPropagation()}
      onKeyDown={e => { e.stopPropagation(); if (e.key === 'Enter') finish(true); if (e.key === 'Escape') finish(false) }}
      onBlur={() => finish(true)} />
  )
}

function Thumb({ entry }: { entry: FileEntry }) {
  const [broken, setBroken] = useState(false)
  if (entry.kind === 'File' && previewKind(entry.extension) === 'image' && !broken)
    return <img className="thumb" src={api.contentUrl(entry.path)} alt="" loading="lazy" onError={() => setBroken(true)} />
  return <span className="thumb icon"><FileIcon entry={entry} size={48} /></span>
}

type MenuItem = { label: string; icon?: React.ReactNode; hint?: string; run: () => void } | 'sep' | false | null | undefined

function ContextMenu({ x, y, items, onClose }: { x: number; y: number; items: MenuItem[]; onClose: () => void }) {
  const ref = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState({ left: x, top: y })
  useEffect(() => {
    const r = ref.current!.getBoundingClientRect()
    setPos({ left: Math.max(4, Math.min(x, innerWidth - r.width - 4)), top: Math.max(4, Math.min(y, innerHeight - r.height - 4)) })
    const close = () => onClose()
    window.addEventListener('blur', close)
    window.addEventListener('resize', close)
    return () => { window.removeEventListener('blur', close); window.removeEventListener('resize', close) }
  }, [x, y, onClose])
  // drop leading/trailing/double separators left over from hidden items
  const clean = items.filter(Boolean).filter((it, i, arr) => it !== 'sep' || (i > 0 && i < arr.length - 1 && arr[i - 1] !== 'sep'))
  return (
    <>
      <div className="menu-backdrop" onMouseDown={onClose} onContextMenu={e => { e.preventDefault(); onClose() }} />
      <div ref={ref} className="menu" style={pos} role="menu">
        {clean.map((it, i) => it === 'sep'
          ? <div key={i} className="menu-sep" />
          : it && (
            <button key={i} className="menu-item" role="menuitem" onClick={e => { e.stopPropagation(); onClose(); it.run() }}>
              <span className="menu-ic">{it.icon}</span><span>{it.label}</span>{it.hint && <kbd>{it.hint}</kbd>}
            </button>
          ))}
      </div>
    </>
  )
}

