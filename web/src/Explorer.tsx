import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react'
import { createPortal } from 'react-dom'
import {
  ArrowDown, ArrowLeft, ArrowRight, ArrowUp, Check, ChevronDown, ChevronRight, ClipboardPaste, Copy, Download, Eye, EyeOff,
  FileArchive, FolderInput, FolderOpen, FolderPlus, FolderUp, Info, LayoutGrid, LayoutList, Link2, List, MoreVertical, PanelRight, Pencil,
  Lock, RefreshCw, Rows3, Scissors, Search, SquareStack, Star, StarOff, Trash2, Upload, CheckCircle2, Circle, CheckSquare, X, ArrowUpDown, ArrowDownUp, CheckCheck, Play, Users, Images, ImageOff, ImagePlus,
} from 'lucide-react'

const VIEWS = ['icons', 'list', 'details', 'tiles', 'content']
// Windows Explorer's icon sizes; the slider and Ctrl + wheel still give any size in between
const ICON_PRESETS = [
  { size: 256, glyph: 18, label: t.viewIconsXL },
  { size: 160, glyph: 17, label: t.viewIconsL },
  { size: 96, glyph: 16, label: t.viewIconsM },
  { size: 48, glyph: 14, label: t.viewIconsS },
]
const viewIcon = (v: string, size = 16) =>
  v === 'icons' ? <LayoutGrid size={size} /> : v === 'list' ? <Rows3 size={size} /> : v === 'tiles' ? <SquareStack size={size} />
    : v === 'content' ? <LayoutList size={size} /> : <List size={size} />
import { api } from './api'
import { toast, toastError } from './dialogs'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize, hasThumb, isVideoExt, nameOf, parentOf, previewKind, typeName, uniqueName } from './format'
import { BLANK, useNearScreen } from './nearScreen'
import { PreviewModal, PreviewPane } from './Preview'
import { propertiesDialog } from './Properties'
import { SystemGauge } from './SystemGauge'
import { hasPlugin, usePlugins } from './plugins'
import { t } from './strings'
import type { AccessLevel, DriveInfo, FileEntry } from './types'
import { accessDialog } from './Access'
import { uploadFiles } from './conflicts'
import { filesFromInput, isActive, uploadEvents, uploads } from './uploads'
import { transfersPanel } from './Transfers'
import { gapi } from './galeri/gapi'
import { startTransfer } from './TransferBar'
import { awake } from './awake'

/** Cut / copied items with the drive they are on ("" = main storage). */
export interface Clipboard { mode: 'copy' | 'cut'; paths: string[]; drive: string }
type SortKey = 'name' | 'modified' | 'type' | 'size'
const SORT_KEYS: [SortKey, string][] = [['name', 'Ad'], ['modified', 'Değiştirme tarihi'], ['type', 'Tür'], ['size', 'Boyut']]
const sortDirLabel = (k: SortKey, asc: boolean) =>
  k === 'modified' ? (asc ? 'Eskiden yeniye' : 'Yeniden eskiye') : k === 'size' ? (asc ? 'Küçükten büyüğe' : 'Büyükten küçüğe') : (asc ? "A'dan Z'ye" : "Z'den A'ya")
type Menu = { x: number; y: number; onItem: boolean; above?: boolean }

const coarse = () => window.matchMedia('(pointer: coarse)').matches
const collator = new Intl.Collator('tr-TR', { numeric: true, sensitivity: 'base' })
const readPref = (k: string, d: string) => { try { return localStorage.getItem(k) ?? d } catch { return d } }
const writePref = (k: string, v: string) => { try { localStorage.setItem(k, v) } catch { /* private mode */ } }

interface Props {
  path: string
  /** "" = main storage, otherwise the disk id; every call below names it explicitly */
  drive: string
  /** the disk's entry from the drive list (null for the main storage or while loading) */
  driveInfo: DriveInfo | null
  version: number
  clipboard: Clipboard | null
  setClipboard: (c: Clipboard | null) => void
  navigate: (path: string) => void
  changed: () => void
  onDropItems: (dest: string, e: React.DragEvent, destDrive: string) => void
  storage: { total: number; free: number } | null
  /** the signed-in person: admins may share anything, owners what is inside their folder */
  me: string
  isAdmin: boolean
}

export function Explorer({ path, drive, driveInfo, version, clipboard, setClipboard, navigate, changed, onDropItems: dropOnDrive, storage, me, isAdmin }: Props) {
  const onDisk = drive !== ''
  // what the person may do in this folder (from the listing); a folder only looked into is read-only for them
  const [access, setAccess] = useState<AccessLevel>(isAdmin ? 'Owner' : 'None')
  const [sharedNames, setSharedNames] = useState<Set<string>>(new Set())
  const canWrite = access === 'Write' || access === 'Owner'
  const canShare = isAdmin || access === 'Owner'
  // read-only disk (or a folder shared to look at): nothing that changes it is offered (the server refuses it anyway)
  const ro = (onDisk && (driveInfo?.readOnly ?? true)) || !canWrite
  const d = onDisk ? drive : undefined
  const tag = (list: FileEntry[]) => (onDisk ? list.map(e => ({ ...e, drive })) : list)
  const onDropItems = (dest: string, e: React.DragEvent) => {
    if (ro) { e.preventDefault(); toast(t.readOnlyDrive); return }
    dropOnDrive(dest, e, drive)
  }
  const [entries, setEntries] = useState<FileEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [anchor, setAnchor] = useState<string | null>(null)
  // sort order, kept between visits; folders first unless turned off (Windows keeps them first too)
  const [sort, setSortState] = useState<{ key: SortKey; asc: boolean }>(() => {
    const [k, a] = readPref('hv.sort', 'name:1').split(':')
    return { key: (['name', 'modified', 'type', 'size'].includes(k) ? k : 'name') as SortKey, asc: a !== '0' }
  })
  const setSort = (v: { key: SortKey; asc: boolean } | ((s: { key: SortKey; asc: boolean }) => { key: SortKey; asc: boolean })) =>
    setSortState(s => { const n = typeof v === 'function' ? v(s) : v; writePref('hv.sort', `${n.key}:${n.asc ? 1 : 0}`); return n })
  const [foldersFirst, setFoldersFirstState] = useState(() => readPref('hv.foldersFirst', '1') === '1')
  const setFoldersFirst = (v: boolean) => { setFoldersFirstState(v); writePref('hv.foldersFirst', v ? '1' : '0') }
  const [sortMenu, setSortMenu] = useState<{ top?: number; bottom?: number; right: number } | null>(null)
  const sortBtn = useRef<HTMLButtonElement>(null)
  const toggleSortMenu = (btn: HTMLElement | null) => {
    if (sortMenu) return setSortMenu(null)
    const r = btn?.getBoundingClientRect()
    if (!r) return
    const right = Math.max(8, Math.round(window.innerWidth - r.right))
    setSortMenu(r.top > window.innerHeight / 2 ? { bottom: Math.round(window.innerHeight - r.top + 4), right } : { top: Math.round(r.bottom + 4), right })
  }
  // view, like Windows Explorer: icons (four preset sizes or any size from the slider / Ctrl + wheel), list,
  // details (columns), tiles (icon + name, type, size) and content (one wide row per item)
  const [view, setViewState] = useState(() => { const v = readPref('hv.view', 'details'); return VIEWS.includes(v) ? v : 'details' })
  const setView = (v: string) => { setViewState(v); writePref('hv.view', v) }
  const [iconSize, setIconSizeState] = useState(() => Math.min(256, Math.max(32, Number(readPref('hv.size', '80')) || 80)))
  const setIconSize = (n: number) => { const v = Math.min(256, Math.max(32, Math.round(n))); setIconSizeState(v); writePref('hv.size', String(v)) }
  const [showHidden, setShowHiddenState] = useState(() => readPref('hv.hidden', '0') === '1')
  const setShowHidden = (v: boolean) => { setShowHiddenState(v); writePref('hv.hidden', v ? '1' : '0') }
  // the view menu opens fixed under its button: the command bar scrolls sideways and would clip a menu inside it
  const [viewMenu, setViewMenuPos] = useState<{ top?: number; bottom?: number; right: number } | null>(null)
  const closeViewMenu = () => setViewMenuPos(null)
  const viewBtn = useRef<HTMLButtonElement>(null)
  const toggleViewMenu = (btn: HTMLElement | null = viewBtn.current) => {
    if (viewMenu) return setViewMenuPos(null)
    const r = btn?.getBoundingClientRect()
    if (!r) return
    const right = Math.max(8, Math.round(window.innerWidth - r.right))
    // a button low on the screen (the phone's bottom bar) opens the menu upwards
    setViewMenuPos(r.top > window.innerHeight / 2 ? { bottom: Math.round(window.innerHeight - r.top + 4), right } : { top: Math.round(r.bottom + 4), right })
  }
  const basePlugins = usePlugins()
  // sharing, favourites and previous versions belong to the main storage
  const plugins = { ...basePlugins, has: (id: string) => basePlugins.has(id) && (!onDisk || !['sharing', 'quick', 'versions'].includes(id)) }
  // DiskHaven Gallery: which folders are albums (only the main storage has a gallery)
  const galleryOn = basePlugins.has('gallery') && !onDisk
  const [galleryAlbums, setGalleryAlbums] = useState<Set<string> | null>(null)
  useEffect(() => {
    if (!galleryOn) { setGalleryAlbums(null); return }
    gapi.tree().then(r => setGalleryAlbums(new Set(r.folders.filter(f => f.selected).map(f => f.path)))).catch(() => setGalleryAlbums(null))
  }, [galleryOn, path, version])
  const setGalleryAlbum = async (folder: string, on: boolean) => {
    try {
      await gapi.select(on ? [folder] : [], on ? [] : [folder])
      setGalleryAlbums(s => { const n = new Set(s ?? []); if (on) n.add(folder); else n.delete(folder); return n })
      toast(on ? 'Klasör galeride albüm oldu.' : 'Klasör galeriden çıkarıldı (dosyalar yerinde).')
    } catch (e) { toastError(e) }
  }
  const [favorites, setFavorites] = useState<Set<string>>(new Set())
  useEffect(() => { if (plugins.has('quick')) api.quick().then(q => setFavorites(new Set(q.favorites))).catch(() => {}) }, [plugins.has('quick'), version])
  const toggleFavorite = async (e: FileEntry) => {
    const on = !favorites.has(e.path)
    setFavorites(f => { const n = new Set(f); if (on) n.add(e.path); else n.delete(e.path); return n })
    try { await api.favorite(e.path, on); toast(on ? 'Favorilere eklendi' : 'Favorilerden çıkarıldı'); changed() } catch (err) { toastError(err) }
  }
  const [pane, setPane] = useState(() => readPref('hv.pane', '0') === '1')
  const [filter, setFilter] = useState('')
  const [renaming, setRenaming] = useState<string | null>(null)
  const [menu, setMenu] = useState<Menu | null>(null)
  const [viewer, setViewer] = useState<FileEntry | null>(null)
  const [dragOver, setDragOver] = useState(false)
  const [editingPath, setEditingPath] = useState(false)
  // recursive search (Enter in the search box); null = normal folder view
  const [results, setResults] = useState<{ q: string; entries: FileEntry[]; truncated: boolean } | null>(null)
  const [searching, setSearching] = useState(false)
  const listRef = useRef<HTMLDivElement>(null)
  // a long address on a narrow screen shows its end (where you are); the start scrolls off to the left
  const crumbsRef = useRef<HTMLDivElement>(null)
  const [crumbsCut, setCrumbsCut] = useState(false)
  useLayoutEffect(() => {
    const el = crumbsRef.current
    if (!el) return
    const toEnd = () => { el.scrollLeft = el.scrollWidth; setCrumbsCut(el.scrollLeft > 2) }
    toEnd()
    window.addEventListener('resize', toEnd)
    return () => window.removeEventListener('resize', toEnd)
  }, [path, drive, editingPath, driveInfo?.name])
  const fileInput = useRef<HTMLInputElement>(null)
  const folderInput = useRef<HTMLInputElement>(null)
  const longPress = useRef<number | undefined>(undefined)
  // the click that follows a long press must not undo the selection it just made
  const longPressed = useRef(false)
  // phone: selection mode (long press, or "Seç" in the bottom bar); a tap then ticks items instead of opening them
  const [selectMode, setSelectMode] = useState(false)
  const [phoneMore, setPhoneMore] = useState<{ x: number; y: number } | null>(null)
  const moreBtn = useRef<HTMLButtonElement>(null)
  const [toolSlot, setToolSlot] = useState<HTMLElement | null>(null)
  useEffect(() => setToolSlot(document.getElementById('title-tools')), [])
  const upl = useSyncExternalStore(uploads.subscribe, uploads.getSnapshot)
  const busy = upl.items.filter(i => isActive(i.status)).length
  const hasTransfers = upl.batches.length > 0 || uploads.unfinished().length > 0
  const selecting = selectMode || (coarse() && selected.size > 0)
  const endSelect = () => { setSelectMode(false); setSelected(new Set()) }

  const runSearch = useCallback(async (query: string) => {
    const q = query.trim()
    if (!q) { setResults(null); return }
    setSearching(true)
    try {
      const r = await api.search(path, q, d)
      setResults({ q, entries: tag(r.entries), truncated: r.truncated })
      setSelected(new Set())
    } catch (e) { toastError(e) }
    setSearching(false)
  }, [path, drive])

  const resultsRef = useRef(results)
  resultsRef.current = results
  const load = useCallback(() => {
    if (resultsRef.current) void runSearch(resultsRef.current.q) // keep search results fresh after rename/delete
    api.list(path, d)
      .then(l => {
        setEntries(tag(l.entries)); setError(null)
        setAccess(l.access ?? (isAdmin ? 'Owner' : 'None'))
        setSharedNames(new Set(l.shared ?? []))
        // make the previews of the whole folder in the background, so scrolling down finds them ready
        if (l.entries.some(e => e.kind === 'File' && hasThumb(e.extension))) api.warmThumbs(path, d).catch(() => {})
      })
      .catch(e => { setEntries([]); setError(e.message) })
  }, [path, drive, runSearch])

  useEffect(() => { setEntries(null); setSelected(new Set()); setSelectMode(false); setFilter(''); setRenaming(null); setResults(null) }, [path, drive])
  useEffect(load, [load, version])
  useEffect(() => {
    const on = (e: Event) => { const x = (e as CustomEvent<{ drive: string; path: string }>).detail; if (x.drive === drive && x.path === path) load() }
    uploadEvents.addEventListener('committed', on)
    return () => uploadEvents.removeEventListener('committed', on)
  }, [path, drive, load])
  useEffect(() => { listRef.current?.focus({ preventScroll: true }) }, [path, drive])

  const shown = useMemo(() => {
    if (!results && !entries) return []
    const f = filter.trim().toLocaleLowerCase('tr-TR')
    const list = results
      ? results.entries.filter(e => showHidden || !e.hidden)
      : entries!.filter(e => (showHidden || !e.hidden) && (!f || e.name.toLocaleLowerCase('tr-TR').includes(f)))
    const dir = sort.asc ? 1 : -1
    return list.sort((a, b) => {
      if (foldersFirst && a.kind !== b.kind) return a.kind === 'Folder' ? -1 : 1 // folders first, like Explorer
      let c = 0
      if (sort.key === 'modified') c = a.modified.localeCompare(b.modified)
      else if (sort.key === 'size') c = (a.size ?? 0) - (b.size ?? 0)
      else if (sort.key === 'type') c = collator.compare(typeName(a), typeName(b))
      return (c || collator.compare(a.name, b.name)) * dir
    })
  }, [entries, filter, sort, results, showHidden, foldersFirst])

  const sel = shown.filter(e => selected.has(e.path))
  const single = sel.length === 1 ? sel[0] : null
  const previewable = shown.filter(e => e.kind === 'File' && previewKind(e.extension))

  // ---- actions ----------------------------------------------------------------------------
  const open = (e: FileEntry) => {
    if (e.problem) { toast(problemText(e.problem)); return }
    if (e.kind === 'File' && hasPlugin('quick') && !onDisk) void api.recent(e.path).catch(() => {})
    if (e.kind === 'Folder') return navigate(e.path)
    if (previewKind(e.extension)) return setViewer(e)
    window.location.href = api.contentUrl(e.path, true, d)
  }

  const download = (list: FileEntry[]) => {
    if (list.length === 0) return
    if (hasPlugin('archive') && (list.some(x => x.kind === 'Folder') || list.length > 3)) {
      // folders, or many files at once: one ZIP built on the fly, like a cloud drive
      const a = document.createElement('a')
      a.href = api.archiveUrl(list.map(x => x.path), list.length === 1 ? list[0].name : path ? nameOf(path) : (driveInfo?.name || 'DiskHaven'), d)
      document.body.appendChild(a); a.click(); a.remove()
      return
    }
    for (const e of list.filter(x => x.kind === 'File')) {
      const a = document.createElement('a')
      a.href = api.contentUrl(e.path, true, d)
      a.download = e.name
      document.body.appendChild(a)
      a.click()
      a.remove()
    }
  }

  // on a phone, a selection switches the bottom bar to selection mode; a new or renamed item only stays
  // selected there when the user was already selecting, otherwise the next tap would tick instead of open
  const markNew = (p: string) => setSelected(coarse() && !selectMode ? new Set() : new Set([p]))

  const newFolder = async () => {
    if (ro) return toast(t.readOnlyDrive)
    const name = uniqueName((entries ?? []).map(e => e.name), t.newFolder)
    try {
      const r = await api.createFolder(path, name, d)
      load()
      changed()
      markNew(r.path)
      setRenaming(r.path)
    } catch (e) { toastError(e) }
  }

  const commitRename = async (entry: FileEntry, value: string) => {
    setRenaming(null)
    const name = value.trim()
    if (!name || name === entry.name || ro) return
    // shown with the new name at once; the server confirms (or the list is reloaded on error)
    const guess = (parentOf(entry.path) ? parentOf(entry.path) + '/' : '') + name
    setEntries(list => list && list.map(x => x.path === entry.path ? { ...x, name, path: guess, extension: x.kind === 'File' ? (name.includes('.') ? name.slice(name.lastIndexOf('.') + 1).toLowerCase() : null) : null } : x))
    markNew(guess)
    try {
      const r = await api.rename(entry.path, name, d)
      markNew(r.path)
      load()
      changed()
    } catch (e) { toastError(e); load() }
  }

  const remove = async (list: FileEntry[]) => {
    if (list.length === 0) return
    if (ro) return toast(t.readOnlyDrive)
    const gone = new Set(list.map(e => e.path))
    setEntries(l => l && l.filter(x => !gone.has(x.path)))   // disappears at once; the bin move runs behind
    setSelected(new Set())
    setSelectMode(false)
    try {
      const r = await api.remove(list.map(e => e.path), d)
      load()
      changed()
      toast(t.movedToTrash(r.items.length), {
        action: {
          label: t.undo,
          run: async () => {
            try { for (const i of r.items) await api.restore(i.id, d) } catch (e) { toastError(e) }
            load()
            changed()
          },
        },
      })
    } catch (e) { toastError(e); load() }
  }

  const setHidden = async (list: FileEntry[], hidden: boolean) => {
    if (list.length === 0) return
    if (ro) return toast(t.readOnlyDrive)
    const paths = new Set(list.map(e => e.path))
    setEntries(l => l && l.map(x => paths.has(x.path) ? { ...x, hidden } : x))
    try { await api.setHidden([...paths], hidden, d); toast(t.hiddenDone(paths.size, hidden)); changed() }
    catch (e) { toastError(e); load() }
  }

  const compress = async (list: FileEntry[]) => {
    if (list.length === 0) return
    if (ro) return toast(t.readOnlyDrive)
    toast(t.compressing)
    try { const r = await api.compress(list.map(e => e.path), undefined, d); load(); changed(); setSelected(new Set([r.path])); setRenaming(r.path) }
    catch (e) { toastError(e) }
  }

  const extract = async (e: FileEntry) => {
    if (ro) return toast(t.readOnlyDrive)
    toast(t.extracting)
    try { const r = await api.extract(e.path, d); load(); changed(); setSelected(new Set([r.path])) }
    catch (err) { toastError(err) }
  }

  const copyPath = (list: FileEntry[]) => {
    const text = list.map(e => `"/${e.path}"`).join('\n')
    navigator.clipboard?.writeText(text).then(() => toast('Yol kopyalandı')).catch(() => {})
  }

  const toClipboard = (mode: Clipboard['mode'], list: FileEntry[]) => {
    if (list.length === 0) return
    if (mode === 'cut' && ro) return toast(t.readOnlyDrive)
    setClipboard({ mode, paths: list.map(e => e.path), drive })
    setSelectMode(false)
    if (coarse()) setSelected(new Set())
    toast(mode === 'copy' ? `${list.length} öğe kopyalandı` : `${list.length} öğe kesildi`)
  }

  const paste = async () => {
    if (!clipboard) return
    if (ro) return toast(t.readOnlyDrive)
    try {
      if (clipboard.drive !== drive || onDisk && clipboard.mode === 'copy') {
        // between drives (and any copy on a disk): verified copy in the background, progress at the bottom
        await startTransfer(clipboard.drive, clipboard.paths, drive, path, clipboard.mode === 'cut')
        if (clipboard.mode === 'cut') setClipboard(null)
        return
      }
      const r = clipboard.mode === 'copy' ? await api.copy(clipboard.paths, path, d) : await api.move(clipboard.paths, path, d)
      if (clipboard.mode === 'cut') setClipboard(null)
      setSelected(new Set(r.paths))
      load()
      changed()
    } catch (e) { toastError(e) }
  }

  const pickUpload = (folder: boolean) => {
    if (ro) return toast(t.readOnlyDrive)
    awake.prime()   // a tap: lets the phone keep its screen on later while the files upload
    ;(folder ? folderInput : fileInput).current?.click()
  }
  const onPicked = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files && !ro) void uploadFiles(path, filesFromInput(e.target.files), drive)
    e.target.value = ''
  }

  // ---- selection --------------------------------------------------------------------------
  const clickRow = (e: React.MouseEvent, entry: FileEntry) => {
    if (longPressed.current) { longPressed.current = false; return }
    if (coarse()) {
      if (!selecting) return open(entry)
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

  // long press = start selecting (like the phone's own Photos and Files apps); the ⋮ button opens the menu
  const touchStart = (_e: React.TouchEvent, entry: FileEntry) => {
    longPressed.current = false
    longPress.current = window.setTimeout(() => {
      longPressed.current = true
      navigator.vibrate?.(15)
      setSelectMode(true)
      setSelected(prev => new Set([...prev, entry.path]))
    }, 500)
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
    else if (k === 'Enter' && e.altKey && single) { handled(); void propertiesDialog(single, load) }
    else if (k === 'Enter' && single) { handled(); open(single) }
    else if (k === 'Delete') { handled(); void remove(sel) }
    else if (k === 'F2' && single && !ro) { handled(); setRenaming(single.path) }
    else if (k === 'F5') { handled(); load() }
    else if (k === 'Escape') { setSelected(new Set()); setMenu(null) }
    else if (mod && k.toLowerCase() === 'a') { handled(); setSelected(new Set(shown.map(x => x.path))) }
    else if (mod && e.shiftKey && k.toLowerCase() === 'c') { handled(); copyPath(sel) }
    else if (mod && k.toLowerCase() === 'c') { handled(); toClipboard('copy', sel) }
    else if (mod && k.toLowerCase() === 'x') { handled(); toClipboard('cut', sel) }
    else if (mod && k.toLowerCase() === 'v') { handled(); void paste() }
    else if (mod && e.shiftKey && k.toLowerCase() === 'n') { handled(); void newFolder() }
  }

  // ---- drag & drop ------------------------------------------------------------------------
  const dragStart = (e: React.DragEvent, entry: FileEntry) => {
    const paths = selected.has(entry.path) ? [...selected] : [entry.path]
    if (!selected.has(entry.path)) setSelected(new Set([entry.path]))
    e.dataTransfer.setData('application/x-diskhaven', JSON.stringify({ drive, paths }))
    e.dataTransfer.effectAllowed = 'copyMove'
  }

  const sortBy = (key: SortKey) => setSort(s => ({ key, asc: s.key === key ? !s.asc : true }))
  const SortMark = ({ k }: { k: SortKey }) => (sort.key === k ? (sort.asc ? <ArrowUp size={12} /> : <ArrowDown size={12} />) : null)

  const crumbs = path ? path.split('/') : []
  const rootName = onDisk ? (driveInfo?.name || t.disk) : t.thisNas
  const folderName = path ? nameOf(path) : rootName
  const space = onDisk ? (driveInfo && driveInfo.free != null ? { total: driveInfo.total, free: driveInfo.free } : null) : storage
  const selectedSize = sel.reduce((s, e) => s + (e.size ?? 0), 0)

  return (
    <div className="main-col" onClick={() => setMenu(null)}>
      <input ref={fileInput} type="file" multiple hidden onChange={onPicked} />
      <input ref={folderInput} type="file" hidden onChange={onPicked} {...{ webkitdirectory: '', directory: '' }} />

      <div className="commandbar" role="toolbar">
        <button className="cmd" disabled={ro} onClick={newFolder} title={`${t.newFolder} (Ctrl+Shift+N)`}><FolderPlus size={16} /> <span>{t.newFolder}</span></button>
        <button className="cmd" disabled={ro} onClick={() => pickUpload(false)}><Upload size={16} /> <span>{t.upload}</span></button>
        <button className="cmd hide-sm" disabled={ro} onClick={() => pickUpload(true)}><FolderUp size={16} /> <span>{t.uploadFolder}</span></button>
        <span className="cmd-sep" />
        <button className="cmd icon-only" disabled={!sel.length || ro} onClick={() => toClipboard('cut', sel)} title={`${t.cut} (Ctrl+X)`}><Scissors size={16} /></button>
        <button className="cmd icon-only" disabled={!sel.length} onClick={() => toClipboard('copy', sel)} title={`${t.copy} (Ctrl+C)`}><Copy size={16} /></button>
        <button className="cmd icon-only" disabled={!clipboard || ro} onClick={paste} title={`${t.paste} (Ctrl+V)`}><ClipboardPaste size={16} /></button>
        <button className="cmd icon-only" disabled={!single || ro} onClick={() => single && setRenaming(single.path)} title={`${t.rename} (F2)`}><Pencil size={16} /></button>
        <button className="cmd icon-only" disabled={!sel.length || ro} onClick={() => remove(sel)} title={`${t.delete} (Del)`}><Trash2 size={16} /></button>
        <button className="cmd icon-only" disabled={!sel.some(e => e.kind === 'File')} onClick={() => download(sel)} title={t.download}><Download size={16} /></button>
        {onDisk && !ro && <a className="cmd icon-only" href={`#/trash/${encodeURIComponent(drive)}`} title={t.diskTrash(rootName)}><Trash2 size={16} /> <span className="hide-sm">Çöp</span></a>}
        <span className="cmd-grow" />
        {view === 'icons' && (
          <label className="size-slider hide-sm" title={`${t.iconSize} (Ctrl + fare tekerleği)`}>
            <LayoutGrid size={12} />
            <input type="range" min={32} max={256} step={4} value={iconSize} onChange={e => setIconSize(Number(e.target.value))} aria-label={t.iconSize} />
            <LayoutGrid size={17} />
          </label>
        )}
        <button ref={sortBtn} className="cmd" onClick={e => { e.stopPropagation(); toggleSortMenu(e.currentTarget) }} title={t.sortBy} aria-expanded={!!sortMenu} aria-haspopup="menu">
          <ArrowDownUp size={16} /> <span className="hide-sm">{t.sortBy}</span> <ChevronDown size={13} />
        </button>
        <div className="view-pick">
          <button ref={viewBtn} className="cmd" onClick={e => { e.stopPropagation(); toggleViewMenu(e.currentTarget) }} title={t.viewMode} aria-expanded={!!viewMenu} aria-haspopup="menu">
            {viewIcon(view)} <span className="hide-sm">{t.viewMode}</span> <ChevronDown size={13} />
          </button>
        </div>
        <button className={`cmd icon-only hide-sm ${pane ? 'on' : ''}`} onClick={() => { setPane(!pane); writePref('hv.pane', pane ? '0' : '1') }} title={t.previewPane}><PanelRight size={16} /></button>
      </div>

      {/* phone only: folder tools in the title bar, next to the profile button (drawn into App's #title-tools) */}
      {toolSlot && createPortal(<>
        <button className="pt-btn" onClick={load} title={t.refresh} aria-label={t.refresh}><RefreshCw size={20} /></button>
        <button className="pt-btn" disabled={ro} onClick={newFolder} title={t.newFolder} aria-label={t.newFolder}><FolderPlus size={20} /></button>
        <button className={`pt-btn ${selecting ? 'on' : ''}`} disabled={shown.length === 0} onClick={() => (selecting ? endSelect() : setSelectMode(true))} title="Seç" aria-label="Seç" aria-pressed={selecting}><CheckSquare size={20} /></button>
        <button className="pt-btn" onClick={e => { e.stopPropagation(); toggleSortMenu(e.currentTarget) }} title={t.sortBy} aria-label={t.sortBy} aria-haspopup="menu"><ArrowDownUp size={20} /></button>
        <button className="pt-btn" onClick={e => { e.stopPropagation(); toggleViewMenu(e.currentTarget) }} title={t.viewMode} aria-label={t.viewMode} aria-haspopup="menu">
          {viewIcon(view, 20)}</button>
      </>, toolSlot)}

      {viewMenu && (
        <>
          <div className="menu-backdrop" onMouseDown={closeViewMenu} />
          <div className="menu view-menu" role="menu" style={{ top: viewMenu.top, bottom: viewMenu.bottom, right: viewMenu.right }} onClick={closeViewMenu}>
            {ICON_PRESETS.map(p => {
              const on = view === 'icons' && iconSize === p.size
              return <button key={p.size} className="menu-item" role="menuitemradio" aria-checked={on} onClick={() => { setIconSize(p.size); setView('icons') }}>
                <span className="menu-ic">{on ? <Check size={15} /> : <LayoutGrid size={p.glyph} />}</span>{p.label}</button>
            })}
            <div className="menu-sep" />
            {([['list', t.viewList], ['details', t.viewDetails], ['tiles', t.viewTiles], ['content', t.viewContent]] as const).map(([v, label]) =>
              <button key={v} className="menu-item" role="menuitemradio" aria-checked={view === v} onClick={() => setView(v)}>
                <span className="menu-ic">{view === v ? <Check size={15} /> : viewIcon(v)}</span>{label}</button>)}
            <div className="menu-sep" />
            <div className="menu-slider show-sm-flex"><LayoutGrid size={12} /><input type="range" min={32} max={256} step={4} value={iconSize}
              onClick={e => e.stopPropagation()} onChange={e => { setIconSize(Number(e.target.value)); if (view !== 'icons') setView('icons') }} aria-label={t.iconSize} /><LayoutGrid size={17} /></div>
            <button className="menu-item" onClick={() => setShowHidden(!showHidden)}><span className="menu-ic">{showHidden ? <Check size={15} /> : <Eye size={16} />}</span>{t.showHidden}</button>
          </div>
        </>
      )}

      {sortMenu && (
        <>
          <div className="menu-backdrop" onMouseDown={() => setSortMenu(null)} />
          <div className="menu view-menu" role="menu" style={{ top: sortMenu.top, bottom: sortMenu.bottom, right: sortMenu.right }} onClick={() => setSortMenu(null)}>
            <div className="menu-label">{t.sortBy}</div>
            {SORT_KEYS.map(([k, label]) =>
              <button key={k} className="menu-item" role="menuitemradio" aria-checked={sort.key === k}
                onClick={() => setSort(s => s.key === k ? s : { key: k, asc: k === 'name' || k === 'type' })}>
                <span className="menu-ic">{sort.key === k ? <Check size={15} /> : null}</span>{label}</button>)}
            <div className="menu-sep" />
            {([true, false] as const).map(asc =>
              <button key={String(asc)} className="menu-item" role="menuitemradio" aria-checked={sort.asc === asc} onClick={() => setSort(s => ({ ...s, asc }))}>
                <span className="menu-ic">{sort.asc === asc ? <Check size={15} /> : asc ? <ArrowUp size={15} /> : <ArrowDown size={15} />}</span>{sortDirLabel(sort.key, asc)}</button>)}
            <div className="menu-sep" />
            <button className="menu-item" role="menuitemcheckbox" aria-checked={foldersFirst} onClick={() => setFoldersFirst(!foldersFirst)}>
              <span className="menu-ic">{foldersFirst ? <Check size={15} /> : <FolderOpen size={15} />}</span>{t.foldersFirst}</button>
          </div>
        </>
      )}

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
          <div ref={crumbsRef} className={`crumbs ${crumbsCut ? 'cut-start' : ''}`} onClick={e => e.target === e.currentTarget && setEditingPath(true)}
            onScroll={e => setCrumbsCut(e.currentTarget.scrollLeft > 2)}>
            <button className={`crumb ${crumbs.length === 0 ? 'current' : ''}`} onClick={() => navigate('')}>{ro && <Lock size={12} />} {rootName}</button>
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
          <input type="search" enterKeyHint="search" value={filter} placeholder={t.searchIn(folderName)}
            onChange={e => { setFilter(e.target.value); if (!e.target.value) setResults(null) }}
            onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); void runSearch(filter) } if (e.key === 'Escape') { setFilter(''); setResults(null) } }} />
          {filter.trim() && !results && <button className="search-all" onClick={() => runSearch(filter)} title={t.searchAll}>{searching ? t.searching : t.searchAll}</button>}
        </label>
      </div>

      {ro && (
        <p className="info-strip ro-strip">
          <Lock size={14} /> <span>{!canWrite && !(onDisk && (driveInfo?.readOnly ?? true))
            ? (access === 'Read' ? 'Bu klasör seninle yalnız görmen için paylaşıldı.' : 'Burada yalnız seninle paylaşılanlar görünür.')
            : t.readOnlyStrip}</span>
          {driveInfo && isAdmin && <a className="link" href="#/disks">{t.changeOnDisks}</a>}
        </p>
      )}
      {results && (
        <p className="info-strip search-strip">
          <span>{t.searchResults(results.q, results.entries.length, results.truncated)}</span>
          <button className="link" onClick={() => { setResults(null); setFilter('') }}>{t.clearSearch}</button>
        </p>
      )}
      <div className="content">
        <div
          ref={listRef}
          className={`list-wrap ${dragOver ? 'dragover' : ''} ${selecting ? 'selecting' : ''}`}
          tabIndex={0}
          onKeyDown={onKey}
          style={{ '--tile': `${iconSize}px` } as React.CSSProperties}
          onWheel={e => {
            if (!(e.ctrlKey || e.metaKey)) return
            e.preventDefault()   // Ctrl + wheel resizes icons, like Explorer
            if (view !== 'icons') setView('icons')
            setIconSize(iconSize * (e.deltaY < 0 ? 1.12 : 1 / 1.12))
          }}
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
                  <Row key={e.path} entry={e} shared={sharedNames.has(e.name)} selected={selected.has(e.path)} selecting={selecting} cut={(clipboard?.mode === 'cut' && clipboard.paths.includes(e.path)) || e.hidden}
                    location={results ? parentOf(e.path) : undefined}
                    renaming={renaming === e.path} onRename={v => commitRename(e, v)} onCancelRename={() => setRenaming(null)}
                    onClick={ev => clickRow(ev, e)} onDoubleClick={() => { if (!coarse()) open(e) }} onContextMenu={ev => contextRow(ev, e)}
                    onDragStart={ev => dragStart(ev, e)} onDropItems={onDropItems}
                    onTouchStart={ev => touchStart(ev, e)} onTouchEnd={touchEnd}
                    onMore={ev => { ev.stopPropagation(); const r = (ev.currentTarget as HTMLElement).getBoundingClientRect(); contextRow({ clientX: r.left, clientY: r.bottom, preventDefault() {} }, e) }} />
                ))}
              </tbody>
            </table>
          )}

          {view === 'list' && shown.length > 0 && (
            <div className="list-view">
              {shown.map(e => (
                <div key={e.path} data-path={e.path} {...(e.kind === 'Folder' ? { 'data-folder': '' } : {})}
                  className={`li ${selected.has(e.path) ? 'sel' : ''} ${(clipboard?.mode === 'cut' && clipboard.paths.includes(e.path)) || e.hidden ? 'cut' : ''}`}
                  draggable onDragStart={ev => dragStart(ev, e)}
                  onDragOver={ev => { if (e.kind === 'Folder') { ev.preventDefault(); ev.currentTarget.classList.add('drop') } }}
                  onDragLeave={ev => ev.currentTarget.classList.remove('drop')}
                  onDrop={ev => { ev.currentTarget.classList.remove('drop'); if (e.kind === 'Folder') { ev.stopPropagation(); onDropItems(e.path, ev) } }}
                  onClick={ev => clickRow(ev, e)} onDoubleClick={() => { if (!coarse()) open(e) }} onContextMenu={ev => contextRow(ev, e)}
                  onTouchStart={ev => touchStart(ev, e)} onTouchEnd={touchEnd} onTouchMove={touchEnd}>
                  <FileIcon entry={e} />
                  {renaming === e.path
                    ? <RenameInput entry={e} onDone={v => commitRename(e, v)} onCancel={() => setRenaming(null)} />
                    : <span className="li-name" title={e.problem ? problemText(e.problem) : e.name}>{e.name}{e.problem && <ProblemMark p={e.problem} />}{sharedNames.has(e.name) && <SharedMark />}</span>}
                </div>
              ))}
            </div>
          )}

          {view === 'icons' && shown.length > 0 && (
            <div className="grid">
              {shown.map(e => (
                <div key={e.path} data-path={e.path} {...(e.kind === 'Folder' ? { 'data-folder': '' } : {})}
                  className={`tile ${selected.has(e.path) ? 'sel' : ''} ${(clipboard?.mode === 'cut' && clipboard.paths.includes(e.path)) || e.hidden ? 'cut' : ''}`}
                  draggable onDragStart={ev => dragStart(ev, e)}
                  onDragOver={ev => { if (e.kind === 'Folder') { ev.preventDefault(); ev.currentTarget.classList.add('drop') } }}
                  onDragLeave={ev => ev.currentTarget.classList.remove('drop')}
                  onDrop={ev => { ev.currentTarget.classList.remove('drop'); if (e.kind === 'Folder') { ev.stopPropagation(); onDropItems(e.path, ev) } }}
                  onClick={ev => clickRow(ev, e)} onDoubleClick={() => { if (!coarse()) open(e) }} onContextMenu={ev => contextRow(ev, e)}
                  onTouchStart={ev => touchStart(ev, e)} onTouchEnd={touchEnd} onTouchMove={touchEnd}>
                  <Thumb entry={e} size={iconSize} />
                  {renaming === e.path
                    ? <RenameInput entry={e} onDone={v => commitRename(e, v)} onCancel={() => setRenaming(null)} />
                    : <span className="tile-name" title={e.problem ? problemText(e.problem) : e.name}>{e.name}{e.problem && <ProblemMark p={e.problem} />}{sharedNames.has(e.name) && <SharedMark />}</span>}
                </div>
              ))}
            </div>
          )}

          {(view === 'tiles' || view === 'content') && shown.length > 0 && (
            <div className={view === 'tiles' ? 'tiles-view' : 'content-view'}>
              {shown.map(e => (
                <div key={e.path} data-path={e.path} {...(e.kind === 'Folder' ? { 'data-folder': '' } : {})}
                  className={`card ${selected.has(e.path) ? 'sel' : ''} ${(clipboard?.mode === 'cut' && clipboard.paths.includes(e.path)) || e.hidden ? 'cut' : ''}`}
                  draggable onDragStart={ev => dragStart(ev, e)}
                  onDragOver={ev => { if (e.kind === 'Folder') { ev.preventDefault(); ev.currentTarget.classList.add('drop') } }}
                  onDragLeave={ev => ev.currentTarget.classList.remove('drop')}
                  onDrop={ev => { ev.currentTarget.classList.remove('drop'); if (e.kind === 'Folder') { ev.stopPropagation(); onDropItems(e.path, ev) } }}
                  onClick={ev => clickRow(ev, e)} onDoubleClick={() => { if (!coarse()) open(e) }} onContextMenu={ev => contextRow(ev, e)}
                  onTouchStart={ev => touchStart(ev, e)} onTouchEnd={touchEnd} onTouchMove={touchEnd}>
                  <Thumb entry={e} size={view === 'tiles' ? 48 : 40} />
                  <div className="card-text">
                    {renaming === e.path
                      ? <RenameInput entry={e} onDone={v => commitRename(e, v)} onCancel={() => setRenaming(null)} />
                      : <span className="card-name" title={e.problem ? problemText(e.problem) : e.name}>{e.name}{e.problem && <ProblemMark p={e.problem} />}{sharedNames.has(e.name) && <SharedMark />}</span>}
                    <span className="card-meta">{typeName(e)}{results ? ` · ${parentOf(e.path) || rootName}` : ''}</span>
                    {view === 'tiles' && e.kind === 'File' && <span className="card-meta">{formatSize(e.size)}</span>}
                  </div>
                  {view === 'content' && (
                    <div className="card-side">
                      <span className="card-meta">{t.colModified}: {formatDate(e.modified)}</span>
                      {e.kind === 'File' && <span className="card-meta">{t.colSize}: {formatSize(e.size)}</span>}
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
        {pane && <PreviewPane entry={single} count={sel.length} />}
      </div>

      <footer className="statusbar hide-sm">
        <span>{t.items(shown.length)}</span>
        {sel.length > 0 && <span>{t.selected(sel.length)}{selectedSize > 0 ? ` · ${formatSize(selectedSize)}` : ''}</span>}
        <span className="grow" />
        {plugins.has('gauge') && <SystemGauge />}
        {space && <span className="hide-sm">{t.free(formatSize(space.free), formatSize(space.total))}</span>}
      </footer>

      <nav className="mobile-bar show-sm-flex" aria-label="İşlemler">
        {selecting ? (
          <>
            <div className="mb-head">
              <button className="mb-close" onClick={endSelect} aria-label="Seçimi bitir"><X size={20} /></button>
              <span className="mb-count">{sel.length ? `${t.selected(sel.length)}${selectedSize > 0 ? ` · ${formatSize(selectedSize)}` : ''}` : 'Öğelere dokunarak seç'}</span>
              <button className="link" onClick={() => setSelected(sel.length === shown.length ? new Set() : new Set(shown.map(x => x.path)))}>
                {sel.length === shown.length && shown.length > 0 ? 'Hiçbiri' : 'Tümü'}</button>
            </div>
            <div className="mb-actions">
              <button disabled={!sel.length} onClick={() => toClipboard('copy', sel)}><Copy size={22} /><span>{t.copy}</span></button>
              <button disabled={!sel.length || ro} onClick={() => toClipboard('cut', sel)}><Scissors size={22} /><span>Taşı</span></button>
              <button disabled={!sel.some(e => e.kind === 'File') && !(plugins.has('archive') && sel.length > 0)} onClick={() => download(sel)}><Download size={22} /><span>{t.download}</span></button>
              <button disabled={!sel.length || ro} className="danger" onClick={() => remove(sel)}><Trash2 size={22} /><span>{t.delete}</span></button>
              <button disabled={!sel.length} onClick={e => { e.stopPropagation(); const r = e.currentTarget.getBoundingClientRect(); setMenu({ x: r.right, y: r.top, onItem: true, above: true }) }}><MoreVertical size={22} /><span>Diğer</span></button>
            </div>
          </>
        ) : (
          <div className="mb-actions">
            <button className="primary" disabled={ro} onClick={() => pickUpload(false)}><Upload size={22} /><span>{t.upload}</span></button>
            <button onClick={() => history.back()}><ArrowLeft size={22} /><span>{t.back}</span></button>
            <button disabled={shown.length === 0} onClick={() => setSelectMode(true)}><CheckSquare size={22} /><span>Seç</span></button>
            <button className={`mb-transfers ${busy > 0 ? 'busy' : ''}`} disabled={!hasTransfers} onClick={() => transfersPanel.set(!transfersPanel.hidden)}>
              <ArrowUpDown size={22} />{busy > 0 && <b className="mb-badge">{busy}</b>}<span>İşlemler</span></button>
            <button ref={moreBtn} onClick={e => { e.stopPropagation(); const r = e.currentTarget.getBoundingClientRect(); setPhoneMore(m => m ? null : { x: r.right, y: r.top }) }}><MoreVertical size={22} /><span>Diğer</span></button>
          </div>
        )}
      </nav>

      {menu && (
        <ContextMenu above={menu.above} x={menu.x} y={menu.y} onClose={() => setMenu(null)} items={menu.onItem ? [
          single && { label: single.kind === 'Folder' ? t.open : previewKind(single.extension) ? t.open : t.download, icon: <FolderOpen size={16} />, run: () => open(single) },
          !ro && plugins.has('archive') && single?.extension === 'zip' && { label: t.extract, icon: <FolderInput size={16} />, run: () => extract(single) },
          (plugins.has('archive') || sel.every(e => e.kind === 'File')) && { label: sel.length === 1 && single?.kind === 'File' || !plugins.has('archive') ? t.download : t.downloadZip, icon: <Download size={16} />, run: () => download(sel) },
          'sep',
          !ro && { label: t.cut, icon: <Scissors size={16} />, hint: 'Ctrl+X', run: () => toClipboard('cut', sel) },
          { label: t.copy, icon: <Copy size={16} />, hint: 'Ctrl+C', run: () => toClipboard('copy', sel) },
          'sep',
          !ro && single && { label: t.rename, icon: <Pencil size={16} />, hint: 'F2', run: () => setRenaming(single.path) },
          canShare && single && { label: 'Kişilerle paylaş', icon: <Users size={16} />, run: () => void accessDialog(single, me, load) },
          plugins.has('sharing') && canWrite && single && { label: t.share, icon: <Link2 size={16} />, run: () => void import('./Sharing').then(m => m.shareDialog(single)) },
          plugins.has('quick') && single && (favorites.has(single.path)
            ? { label: 'Favorilerden çıkar', icon: <StarOff size={16} />, run: () => toggleFavorite(single) }
            : { label: 'Favorilere ekle', icon: <Star size={16} />, run: () => toggleFavorite(single) }),
          !ro && plugins.has('archive') && { label: t.compress, icon: <FileArchive size={16} />, run: () => compress(sel) },
          { label: t.copyPath, icon: <Copy size={16} />, hint: 'Ctrl+Shift+C', run: () => copyPath(sel) },
          !ro && (sel.every(e => e.hidden)
            ? { label: t.unhide, icon: <Eye size={16} />, run: () => setHidden(sel, false) }
            : { label: t.hide, icon: <EyeOff size={16} />, run: () => setHidden(sel, true) }),
          single && results && { label: t.openLocation, icon: <FolderOpen size={16} />, run: () => navigate(parentOf(single.path)) },
          galleryOn && single?.kind === 'Folder' && { label: 'Galeride aç', icon: <Images size={16} />, run: () => { location.href = `/galeri.html#album=${encodeURIComponent(single.path)}` } },
          galleryOn && single?.kind === 'Folder' && galleryAlbums && (galleryAlbums.has(single.path)
            ? { label: 'Galeriden çıkar', icon: <ImageOff size={16} />, run: () => void setGalleryAlbum(single.path, false) }
            : { label: 'Galeride albüm yap', icon: <ImagePlus size={16} />, run: () => void setGalleryAlbum(single.path, true) }),
          !ro && { label: t.delete, icon: <Trash2 size={16} />, hint: 'Del', run: () => remove(sel) },
          'sep',
          single && { label: t.properties, icon: <Info size={16} />, hint: 'Alt+Enter', run: () => void propertiesDialog(single, load) },
        ] : [
          !ro && { label: t.newFolder, icon: <FolderPlus size={16} />, hint: 'Ctrl+Shift+N', run: newFolder },
          !ro && clipboard && { label: t.paste, icon: <ClipboardPaste size={16} />, hint: 'Ctrl+V', run: paste },
          'sep',
          !ro && { label: t.uploadFiles, icon: <Upload size={16} />, run: () => pickUpload(false) },
          !ro && { label: t.uploadFolder, icon: <FolderUp size={16} />, run: () => pickUpload(true) },
          onDisk && !ro && { label: t.diskTrash(rootName), icon: <Trash2 size={16} />, run: () => { location.hash = `#/trash/${encodeURIComponent(drive)}` } },
          'sep',
          canShare && path !== '' && { label: 'Bu klasörü kişilerle paylaş', icon: <Users size={16} />,
            run: () => void accessDialog({ name: nameOf(path), path, kind: 'Folder', drive: d, extension: null }, me, load) },
          { label: t.refresh, icon: <RefreshCw size={16} />, hint: 'F5', run: load },
          { label: t.sortBy, icon: <ArrowDownUp size={16} />, run: () => setTimeout(() => toggleSortMenu(sortBtn.current), 0) },
          { label: t.selectAll, hint: 'Ctrl+A', run: () => setSelected(new Set(shown.map(x => x.path))) },
          { label: t.showHidden, icon: showHidden ? <Check size={15} /> : <Eye size={16} />, run: () => setShowHidden(!showHidden) },
        ]} />
      )}

      {phoneMore && (
        <ContextMenu above x={phoneMore.x} y={phoneMore.y} onClose={() => setPhoneMore(null)} items={[
          !ro && clipboard && { label: t.paste, icon: <ClipboardPaste size={16} />, run: paste },
          !ro && { label: t.newFolder, icon: <FolderPlus size={16} />, run: newFolder },
          { label: t.refresh, icon: <RefreshCw size={16} />, run: load },
          shown.length > 0 && { label: t.selectAll, icon: <CheckCheck size={16} />, run: () => { setSelectMode(true); setSelected(new Set(shown.map(x => x.path))) } },
          { label: t.sortBy, icon: <ArrowDownUp size={16} />, run: () => setTimeout(() => toggleSortMenu(moreBtn.current), 0) },
          { label: t.viewMode, icon: viewIcon(view), run: () => setTimeout(() => toggleViewMenu(moreBtn.current), 0) },
        ]} />
      )}

      {viewer && (
        <PreviewModal entry={viewer} onClose={() => setViewer(null)}
          position={previewable.length > 1 ? `${previewable.indexOf(viewer) + 1} / ${previewable.length}` : undefined}
          preload={[previewable[previewable.indexOf(viewer) - 1], previewable[previewable.indexOf(viewer) + 1]].filter(Boolean)}
          onPrev={previewable.indexOf(viewer) > 0 ? () => setViewer(previewable[previewable.indexOf(viewer) - 1]) : undefined}
          onNext={previewable.indexOf(viewer) < previewable.length - 1 ? () => setViewer(previewable[previewable.indexOf(viewer) + 1]) : undefined} />
      )}
    </div>
  )
}

interface RowProps {
  entry: FileEntry
  shared?: boolean
  selecting?: boolean
  location?: string
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
        {p.renaming ? <RenameInput entry={e} onDone={p.onRename} onCancel={p.onCancelRename} />
          : <span className="name-text" title={e.problem ? problemText(e.problem) : e.name}>{e.name}{e.problem && <ProblemMark p={e.problem} />}{p.shared && <SharedMark />}{p.location !== undefined && <small className="loc">/{p.location}</small>}</span>}
      </td>
      <td className="c-date">{formatDate(e.modified)}</td>
      <td className="c-type">{typeName(e)}</td>
      <td className="c-size" title={folder && e.files != null ? t.contents(e.files, e.folders ?? 0) : undefined}>
        {folder && e.size == null ? (e.drive ? '' : <span className="calc">{t.calculating}</span>) : formatSize(e.size)}
      </td>
      <td className="c-more">{p.selecting
        ? <span className="check" aria-hidden>{p.selected ? <CheckCircle2 size={22} /> : <Circle size={22} />}</span>
        : <button className="icon-btn" onClick={p.onMore} aria-label="Diğer"><MoreVertical size={16} /></button>}</td>
    </tr>
  )
}

export function RenameInput({ entry, onDone, onCancel }: { entry: FileEntry; onDone: (v: string) => void; onCancel: () => void }) {
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

function Thumb({ entry, size }: { entry: FileEntry; size: number }) {
  const [broken, setBroken] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const file = entry.kind === 'File'
  const small = file && hasThumb(entry.extension) && !broken
  const svg = file && entry.extension === 'svg' && !broken
  const [ref, near] = useNearScreen<HTMLImageElement>(small || svg)
  useEffect(() => { setBroken(false); setLoaded(false) }, [entry.path, entry.modified])
  if (small || svg) {
    // a small cached preview, asked for only while on screen; the original is never downloaded just to draw an icon
    const url = svg ? api.contentUrl(entry.path, false, entry.drive) : api.thumbUrl(entry.path, 's', entry.drive)
    const ext = (entry.extension ?? '').toLowerCase()
    const img = <img ref={ref} className={`thumb${loaded ? '' : ' loading'}${ext === 'pdf' ? ' page' : ''}`} src={near || loaded ? url : BLANK} alt="" decoding="async" draggable={false}
      onLoad={e => { if (e.currentTarget.src !== BLANK) setLoaded(true) }} onError={() => setBroken(true)} />
    // a video's frame carries a small play mark, a PDF's page a type label, so they are not taken for photos
    if (isVideoExt(ext) || ext === 'pdf')
      return <span className="thumb-wrap">{img}{loaded && (ext === 'pdf' ? <b className="thumb-tag">PDF</b> : <span className="thumb-play"><Play size={size >= 96 ? 16 : 11} fill="currentColor" /></span>)}</span>
    return img
  }
  return <span className="thumb icon"><FileIcon entry={entry} size={Math.round(size * 0.62)} /></span>
}

export type MenuItem = { label: string; icon?: React.ReactNode; hint?: string; run: () => void } | 'sep' | false | null | undefined

export function ContextMenu({ x, y, items, onClose, above }: { x: number; y: number; items: MenuItem[]; onClose: () => void; above?: boolean }) {
  const ref = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState({ left: x, top: y })
  useEffect(() => {
    const r = ref.current!.getBoundingClientRect()
    const top = above ? y - r.height - 6 : y
    setPos({ left: Math.max(4, Math.min(above ? x - r.width : x, innerWidth - r.width - 4)), top: Math.max(4, Math.min(top, innerHeight - r.height - 4)) })
    const close = () => onClose()
    window.addEventListener('blur', close)
    window.addEventListener('resize', close)
    return () => { window.removeEventListener('blur', close); window.removeEventListener('resize', close) }
  }, [x, y, onClose, above])
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


/** Why a listed item cannot be opened. */
function problemText(p: 'bad-name' | 'special') {
  return p === 'bad-name'
    ? 'Bu öğenin adı okunamıyor (eski bir Windows/Linux sisteminden kalma karakterler). Açılamaz ve kopyalanamaz; adını kendi bilgisayarında değiştir.'
    : 'Özel dosya (boru, soket ya da aygıt): içinde veri yok, açılamaz ve kopyalanmaz.'
}

/** A small "people" mark on items that are shared with someone (shown to admins and owners). */
function SharedMark() {
  return <Users size={12} className="shared-mark" aria-label="paylaşılıyor" />
}

function ProblemMark({ p }: { p: 'bad-name' | 'special' }) {
  return <span className="problem-mark" aria-label={problemText(p)}>{p === 'bad-name' ? ' ⚠ adı okunamıyor' : ' · özel dosya'}</span>
}
