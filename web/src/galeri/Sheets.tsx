import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { api } from '../api'
import type { FileEntry, TrashItem } from '../types'
import { isActive, isDone, uploads, type UploadStatus } from '../uploads'
import { albumTitle, gapi, nameOf, parentOf, type AlbumInfo, type TreeFolder } from './gapi'
import { Icon, promptSheet, sheet, toast, toastError } from './ui'

/** Picks the album (folder) to move or copy into: an existing album, a new album, or any folder. */
export function pickAlbum(title: string, exclude?: string) {
  return sheet<string | null>(done => <AlbumPicker title={title} exclude={exclude} done={done} />, null)
}

function AlbumPicker({ title, exclude, done }: { title: string; exclude?: string; done: (v: string | null) => void }) {
  const [albums, setAlbums] = useState<AlbumInfo[] | null>(null)
  const [parent, setParent] = useState('DCIM')
  const [browse, setBrowse] = useState<string | null>(null)
  useEffect(() => {
    gapi.albums().then(r => { setAlbums(r.albums); setParent(r.newAlbumParent) }).catch(e => { toastError(e); setAlbums([]) })
  }, [])

  const newAlbum = async () => {
    const name = await promptSheet('Yeni albüm', '', 'Oluştur')
    if (!name) return
    try {
      // the album's parent folder may not exist yet (a fresh server has no DCIM): create it on the way
      let at = ''
      for (const part of parent.split('/').filter(Boolean)) {
        const listing = await api.list(at)
        if (!listing.entries.some(e => e.kind === 'Folder' && e.name.toLowerCase() === part.toLowerCase()))
          await api.createFolder(at, part)
        at = at ? `${at}/${part}` : part
      }
      const r = await api.createFolder(at, name)
      await gapi.select([r.path])   // a new album is an album at once
      done(r.path)
    } catch (e) { toastError(e) }
  }

  if (browse !== null) return <FolderBrowser start={browse} onPick={done} onBack={() => setBrowse(null)} />

  return (
    <div className="g-scrim" onClick={() => done(null)}>
      <div className="g-bottom-sheet tall" onClick={e => e.stopPropagation()}>
        <div className="g-handle" />
        <h2>{title}</h2>
        <div className="g-pick-list">
          <button className="g-pick-row" onClick={newAlbum}>
            <span className="g-pick-icon accent"><Icon name="add" /></span><span>Yeni albüm</span>
          </button>
          <button className="g-pick-row" onClick={() => setBrowse('')}>
            <span className="g-pick-icon"><Icon name="folder" /></span><span>Başka bir klasör seç…</span>
          </button>
          {albums === null && <div className="g-center"><div className="g-spinner" /></div>}
          {albums?.filter(a => a.path !== exclude).map(a => (
            <button key={a.path} className="g-pick-row" onClick={() => done(a.path)}>
              <img src={gapi.thumb(a.cover)} alt="" loading="lazy" />
              <span><b>{a.name}</b><small>{a.count} öğe · /{a.path}</small></span>
            </button>
          ))}
        </div>
      </div>
    </div>
  )
}

function FolderBrowser({ start, onPick, onBack }: { start: string; onPick: (p: string) => void; onBack: () => void }) {
  const [path, setPath] = useState(start)
  const [folders, setFolders] = useState<FileEntry[] | null>(null)
  useEffect(() => {
    setFolders(null)
    api.list(path).then(r => setFolders(r.entries.filter(e => e.kind === 'Folder' && !e.hidden))).catch(e => { toastError(e); setFolders([]) })
  }, [path])
  const make = async () => {
    const name = await promptSheet('Yeni klasör', '', 'Oluştur')
    if (!name) return
    try { const r = await api.createFolder(path, name); setPath(r.path) } catch (e) { toastError(e) }
  }
  return (
    <div className="g-scrim" onClick={onBack}>
      <div className="g-bottom-sheet tall" onClick={e => e.stopPropagation()}>
        <div className="g-handle" />
        <div className="g-sheet-head">
          <button className="g-icon-btn" onClick={() => (path ? setPath(parentOf(path)) : onBack())} aria-label="Geri"><Icon name="back" /></button>
          <h2>{path ? nameOf(path) : 'Tüm klasörler'}</h2>
          <button className="g-icon-btn" onClick={make} aria-label="Yeni klasör"><Icon name="add" /></button>
        </div>
        <p className="g-crumb">/{path}</p>
        <div className="g-pick-list">
          {folders === null && <div className="g-center"><div className="g-spinner" /></div>}
          {folders?.length === 0 && <p className="g-muted g-pad">Alt klasör yok.</p>}
          {folders?.map(f => (
            <button key={f.path} className="g-pick-row" onClick={() => setPath(f.path)}>
              <span className="g-pick-icon"><Icon name="folder" /></span><span>{f.name}</span><Icon name="chevron" />
            </button>
          ))}
        </div>
        <button className="g-fill-btn" onClick={() => onPick(path)}>Buraya: /{path}</button>
      </div>
    </div>
  )
}

/** Which albums show in the gallery, which are pinned, and where new albums go. */
export function openSettings() {
  return sheet<boolean>(done => <Settings done={done} />, false)
}

function Settings({ done }: { done: (changed: boolean) => void }) {
  const [albums, setAlbums] = useState<AlbumInfo[] | null>(null)
  const [parent, setParent] = useState('')
  const [changed, setChanged] = useState(false)
  useEffect(() => { gapi.albums().then(r => { setAlbums(r.albums); setParent(r.newAlbumParent) }).catch(toastError) }, [])

  const save = async (next: AlbumInfo[], newParent = parent) => {
    setAlbums(next)
    setChanged(true)
    try {
      await gapi.settings({
        hidden: next.filter(a => a.hidden).map(a => a.path),
        pinned: next.filter(a => a.pinned).map(a => a.path),
        newAlbumParent: newParent,
      })
    } catch (e) { toastError(e) }
  }
  const pickParent = async () => {
    const p = await pickAlbum('Yeni albümler nereye açılsın?')
    if (p === null) return
    setParent(p)
    if (albums) save(albums, p)
  }

  return (
    <div className="g-scrim" onClick={() => done(changed)}>
      <div className="g-bottom-sheet tall" onClick={e => e.stopPropagation()}>
        <div className="g-handle" />
        <h2>Galeri ayarları</h2>
        <button className="g-pick-row" onClick={pickParent}>
          <span className="g-pick-icon"><Icon name="folder" /></span>
          <span><b>Yeni albüm konumu</b><small>/{parent || '(ana klasör)'}</small></span>
        </button>
        <h3 className="g-sub">Albümler — galeride görünsün mü?</h3>
        <p className="g-muted g-pad">Kapalı albümler "Fotoğraflar"da ve albüm listesinde görünmez; dosyalara dokunulmaz. Raptiye ile sabitlenen albümler en üstte durur.</p>
        <div className="g-pick-list">
          {albums === null && <div className="g-center"><div className="g-spinner" /></div>}
          {albums?.map(a => (
            <div key={a.path} className="g-pick-row static">
              <img src={gapi.thumb(a.cover)} alt="" loading="lazy" />
              <span><b>{a.name}</b><small>{a.count} öğe · /{a.path}</small></span>
              <button className={`g-icon-btn ${a.pinned ? 'accent' : 'dim'}`} aria-label="Sabitle"
                onClick={() => save(albums.map(x => x.path === a.path ? { ...x, pinned: !x.pinned } : x))}><Icon name="pin" size={20} /></button>
              <label className="g-switch">
                <input type="checkbox" checked={!a.hidden} onChange={() => save(albums.map(x => x.path === a.path ? { ...x, hidden: !x.hidden } : x))} />
                <span />
              </label>
            </div>
          ))}
        </div>
        <button className="g-fill-btn" onClick={() => done(changed)}>Bitti</button>
      </div>
    </div>
  )
}

/** The DiskHaven recycle bin, showing what was deleted (from the gallery or anywhere else). */
export function openTrash() {
  return sheet<boolean>(done => <Trash done={done} />, false)
}

function Trash({ done }: { done: (changed: boolean) => void }) {
  const [items, setItems] = useState<TrashItem[] | null>(null)
  const [changed, setChanged] = useState(false)
  const load = () => api.trash().then(setItems).catch(e => { toastError(e); setItems([]) })
  useEffect(() => { load() }, [])
  const restore = async (t: TrashItem) => {
    try { await api.restore(t.id); setChanged(true); toast(`Geri yüklendi: ${t.name}`); load() } catch (e) { toastError(e) }
  }
  const purge = async (t: TrashItem) => {
    try { await api.purge(t.id); load() } catch (e) { toastError(e) }
  }
  return (
    <div className="g-scrim" onClick={() => done(changed)}>
      <div className="g-bottom-sheet tall" onClick={e => e.stopPropagation()}>
        <div className="g-handle" />
        <h2>Çöp kutusu</h2>
        <p className="g-muted g-pad">Silinenler burada bekler; geri yükleyince eski klasörüne döner.</p>
        <div className="g-pick-list">
          {items === null && <div className="g-center"><div className="g-spinner" /></div>}
          {items?.length === 0 && <p className="g-muted g-pad">Çöp kutusu boş.</p>}
          {items?.map(t => (
            <div key={t.id} className="g-pick-row static">
              <span className="g-pick-icon"><Icon name={t.kind === 'Folder' ? 'folder' : 'photos'} /></span>
              <span><b>{t.name}</b><small>/{albumTitle(parentOf(t.originalPath)) === 'Ana klasör' ? '' : parentOf(t.originalPath)} · {new Date(t.deletedAt).toLocaleDateString('tr-TR')}</small></span>
              <button className="g-icon-btn accent" onClick={() => restore(t)} aria-label="Geri yükle"><Icon name="restore" size={20} /></button>
              <button className="g-icon-btn dim" onClick={() => purge(t)} aria-label="Kalıcı sil"><Icon name="close" size={20} /></button>
            </div>
          ))}
        </div>
        <button className="g-fill-btn" onClick={() => done(changed)}>Kapat</button>
      </div>
    </div>
  )
}

/** A thin progress strip while photos go up from the phone. Tapping it opens the live details. */
export function UploadStrip({ onFinished }: { onFinished: () => void }) {
  const snap = useSyncExternalStore(uploads.subscribe, uploads.getSnapshot)
  const active = snap.items.filter(i => isActive(i.status))
  const failed = snap.items.filter(i => i.status === 'error')
  const [wasActive, setWasActive] = useState(false)
  useEffect(() => {
    if (active.length > 0) setWasActive(true)
    else if (wasActive) {
      setWasActive(false)
      const sent = snap.items.filter(i => i.status === 'done').length
      const there = snap.items.filter(i => i.status === 'skipped').length
      if (failed.length) toast(`${failed.length} dosya yüklenemedi. Ayrıntı için alttaki çubuğa dokun.`, true)
      else if (sent || there) toast(`Sorunsuz yüklendi: ${sent} dosya, parmak izleri doğrulandı${there ? ` (${there} dosya zaten vardı)` : ''}.`)
      uploads.clearFinished()
      onFinished()
    }
  }, [active.length]) // eslint-disable-line react-hooks/exhaustive-deps
  if (active.length === 0 && failed.length === 0) return null
  const doneCount = snap.items.filter(i => isDone(i.status)).length
  const progress = snap.items.length ? (doneCount + active.reduce((s, i) => s + (i.status === 'uploading' ? i.progress : 0), 0)) / snap.items.length : 0
  return (
    <button className={`g-upload${failed.length && !active.length ? ' bad' : ''}`} onClick={() => void openUploads()} aria-label="Yükleme ayrıntıları">
      <span>{active.length === 0 ? `${failed.length} dosya yüklenemedi · dokun`
        : `${active.some(i => i.status === 'waiting') ? 'Bağlantı bekleniyor' : 'Yükleniyor'}: ${doneCount}/${snap.items.length}`}</span>
      <div className="g-bar"><div style={{ width: `${Math.round(progress * 100)}%` }} /></div>
    </button>
  )
}

const stageText: Record<UploadStatus, string> = {
  queued: 'Sırada', hashing: 'Parmak izi alınıyor', uploading: 'Yükleniyor', verifying: 'Sunucuda doğrulanıyor',
  waiting: 'Bağlantı bekleniyor', done: 'Yüklendi, doğrulandı', skipped: 'Zaten vardı', error: 'Yüklenemedi', canceled: 'Durduruldu',
}

/** Live details of the upload, file by file; swipe down (or tap outside) to close and carry on. */
export function openUploads() {
  return sheet<void>(done => <UploadDetails done={done} />, undefined)
}

function UploadDetails({ done }: { done: () => void }) {
  const snap = useSyncExternalStore(uploads.subscribe, uploads.getSnapshot)
  const [drag, setDrag] = useState(0)
  const start = useRef<number | null>(null)
  const order = (s: UploadStatus) => s === 'error' ? 0 : isActive(s) ? (s === 'queued' ? 2 : 1) : 3
  const list = [...snap.items].sort((a, b) => order(a.status) - order(b.status) || a.id - b.id)
  const count = (f: (s: UploadStatus) => boolean) => snap.items.filter(i => f(i.status)).length
  const ok = count(s => s === 'done'), there = count(s => s === 'skipped'), bad = count(s => s === 'error'), left = count(isActive)
  return (
    <div className="g-scrim" onClick={() => done()}>
      <div className="g-bottom-sheet tall" style={drag ? { transform: `translateY(${drag}px)`, transition: 'none' } : undefined}
        onClick={e => e.stopPropagation()}
        onTouchStart={e => { start.current = (e.currentTarget.querySelector('.g-pick-list')?.scrollTop ?? 0) > 0 ? null : e.touches[0].clientY }}
        onTouchMove={e => { if (start.current !== null) setDrag(Math.max(0, e.touches[0].clientY - start.current)) }}
        onTouchEnd={() => { if (drag > 90) done(); else setDrag(0); start.current = null }}>
        <div className="g-handle" />
        <h2>Yükleme</h2>
        <p className="g-muted g-pad">
          {left ? `${left} dosya sürüyor · ` : ''}{ok} yüklendi ve doğrulandı{there ? ` · ${there} zaten vardı` : ''}{bad ? ` · ${bad} yüklenemedi` : ''}.
          {left > 0 && ' Ekran açık kalırsa en hızlısı; başka uygulamaya geçersen dönünce kaldığı yerden sürer.'}
        </p>
        {snap.items.length === 0 && <p className="g-muted g-pad">Yüklenen dosya yok.</p>}
        <div className="g-pick-list">
          {list.map(i => (
            <div key={i.id} className={`g-pick-row static g-up-row ${i.status}`}>
              <span className="g-pick-icon"><Icon name={i.status === 'error' ? 'close' : isDone(i.status) ? 'check' : 'upload'} /></span>
              <span><b>{i.relativeName}</b>
                <small>{stageText[i.status]}{(i.status === 'uploading' || i.status === 'hashing') ? ` · %${Math.round(i.progress * 100)}` : ''}{i.error ? ` · ${i.error}` : ''}</small>
                {(i.status === 'uploading' || i.status === 'hashing') && <div className="g-bar"><div style={{ width: `${Math.round(i.progress * 100)}%` }} /></div>}
              </span>
              {i.status === 'error' && <button className="g-icon-btn accent" onClick={() => uploads.retry(i.id)} aria-label="Tekrar dene"><Icon name="restore" size={20} /></button>}
              {isActive(i.status) && <button className="g-icon-btn dim" onClick={() => uploads.cancel(i.id)} aria-label="Durdur"><Icon name="close" size={20} /></button>}
            </div>
          ))}
        </div>
        {bad > 0 && !left
          ? <button className="g-fill-btn" onClick={() => { uploads.clearFinished(); for (const i of snap.items) if (i.status === 'error') uploads.cancel(i.id); uploads.clearFinished(); done() }}>Hataları temizle ve kapat</button>
          : <button className="g-fill-btn" onClick={() => done()}>Kapat</button>}
      </div>
    </div>
  )
}


/** "Albümleri seç": every folder with photos or videos as a tree; a ticked folder is an album. Layered: a folder shows only
 *  its own photos, never its subfolders' (each subfolder is ticked on its own). Ticks are saved at once. */
export function chooseAlbums() {
  return sheet<boolean>(done => <AlbumTree done={done} />, false)
}

function AlbumTree({ done }: { done: (changed: boolean) => void }) {
  const [folders, setFolders] = useState<TreeFolder[] | null>(null)
  const [open, setOpen] = useState<Set<string>>(new Set(['']))
  const [changed, setChanged] = useState(false)
  useEffect(() => {
    gapi.tree().then(r => {
      setFolders(r.folders)
      // open the way down to every ticked folder, so the current albums are visible
      const o = new Set([''])
      for (const f of r.folders) if (f.selected) for (let p = parentOf(f.path); p; p = parentOf(p)) o.add(p)
      setOpen(o)
    }).catch(e => { toastError(e); setFolders([]) })
  }, [])

  const children = (path: string) => (folders ?? []).filter(f => f.path !== '' && parentOf(f.path) === path)
    .sort((a, b) => a.path.localeCompare(b.path, 'tr'))
  const below = (path: string) => (folders ?? []).filter(f => f.path !== path && (path === '' || f.path.startsWith(path + '/')))

  const tick = async (f: TreeFolder) => {
    const on = !f.selected
    setFolders(l => l && l.map(x => x.path === f.path ? { ...x, selected: on } : x))
    setChanged(true)
    try { await gapi.select(on ? [f.path] : [], on ? [] : [f.path]) }
    catch (e) { toastError(e); setFolders(l => l && l.map(x => x.path === f.path ? { ...x, selected: !on } : x)) }
  }

  const row = (f: TreeFolder, depth: number): React.ReactNode => {
    const kids = children(f.path)
    const isOpen = open.has(f.path)
    const inside = below(f.path).filter(x => x.selected).length
    return (
      <div key={f.path || '/'}>
        <div className="g-tree-row" style={{ paddingLeft: 8 + depth * 18 }}>
          <button className={`g-tree-arrow ${kids.length ? '' : 'none'} ${isOpen ? 'open' : ''}`} aria-label={isOpen ? 'Kapat' : 'Aç'} disabled={!kids.length}
            onClick={() => setOpen(o => { const n = new Set(o); if (n.has(f.path)) n.delete(f.path); else n.add(f.path); return n })}>
            <Icon name="chevron" size={20} /></button>
          <label className={`g-tree-label ${f.count === 0 ? 'empty' : ''}`}>
            <input type="checkbox" checked={f.selected} disabled={f.count === 0} onChange={() => tick(f)} />
            <span className="g-tree-name"><b>{albumTitle(f.path)}</b>
              <small>{f.count ? `${f.count} öğe` : 'kendi içinde fotoğraf yok'}{!isOpen && inside ? ` · içinde ${inside} albüm` : ''}</small></span>
          </label>
        </div>
        {isOpen && kids.map(k => row(k, depth + 1))}
      </div>
    )
  }

  const root = folders?.find(f => f.path === '')
  return (
    <div className="g-scrim" onClick={() => done(changed)}>
      <div className="g-bottom-sheet tall" onClick={e => e.stopPropagation()}>
        <div className="g-handle" />
        <h2>Albümleri seç</h2>
        <p className="g-muted g-sheet-note">Tik koyduğun klasör galeride albüm olur. Albüm yalnız kendi içindeki fotoğrafları gösterir; alt klasörleri ayrıca seçersin.</p>
        <div className="g-tree">
          {folders === null && <div className="g-center"><div className="g-spinner" /></div>}
          {folders?.length === 0 && <p className="g-muted g-pad">Henüz fotoğraf ya da video olan bir klasör yok.</p>}
          {root && row(root, 0)}
        </div>
        <div className="g-dialog-actions"><button className="g-text-btn accent" onClick={() => done(changed)}>Tamam</button></div>
      </div>
    </div>
  )
}
