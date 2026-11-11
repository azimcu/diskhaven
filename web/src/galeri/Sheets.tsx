import { useEffect, useState, useSyncExternalStore } from 'react'
import { api } from '../api'
import type { FileEntry, TrashItem } from '../types'
import { isActive, uploads } from '../uploads'
import { albumTitle, gapi, nameOf, parentOf, type AlbumInfo } from './gapi'
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

/** A thin progress strip while photos go up from the phone. */
export function UploadStrip({ onFinished }: { onFinished: () => void }) {
  const snap = useSyncExternalStore(uploads.subscribe, uploads.getSnapshot)
  const active = snap.items.filter(i => isActive(i.status))
  const [wasActive, setWasActive] = useState(false)
  useEffect(() => {
    if (active.length > 0) setWasActive(true)
    else if (wasActive) {
      setWasActive(false)
      const failed = snap.items.filter(i => i.status === 'error').length
      if (failed) toast(`${failed} dosya yüklenemedi.`, true)
      else toast('Yükleme bitti.')
      uploads.clearFinished()
      onFinished()
    }
  }, [active.length]) // eslint-disable-line react-hooks/exhaustive-deps
  if (active.length === 0) return null
  const doneCount = snap.items.filter(i => i.status === 'done').length
  const progress = snap.items.length ? (doneCount + active.reduce((s, i) => s + (i.status === 'uploading' ? i.progress : 0), 0)) / snap.items.length : 0
  return (
    <div className="g-upload">
      <span>Yükleniyor: {doneCount}/{snap.items.length}</span>
      <div className="g-bar"><div style={{ width: `${Math.round(progress * 100)}%` }} /></div>
    </div>
  )
}
