import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { filesFromInput, uploads } from '../uploads'
import { albumTitle, gapi, nameOf, parentOf, type AlbumInfo, type Media } from './gapi'
import { Grid, useMedia } from './Grid'
import { openSettings, openTrash, pickAlbum, UploadStrip } from './Sheets'
import { confirmSheet, Icon, promptSheet, pushBack, toast, toastError } from './ui'
import { Viewer } from './Viewer'

type Tab = 'photos' | 'albums'
type Layer = { kind: 'album'; path: string } | { kind: 'viewer'; items: Media[]; index: number; more: () => void; album: string | null }

/**
 * The gallery app. Back gestures follow the browser history: opening an album or a photo pushes an entry,
 * Android "back" (or the iPhone swipe in the browser) closes it, exactly like a native gallery.
 */
export function Galeri({ user, onLogout }: { user: string; onLogout: () => void }) {
  const [tab, setTab] = useState<Tab>(() => (localStorage.getItem('galeri.tab') as Tab) || 'photos')
  const [layers, setLayers] = useState<Layer[]>([])
  const [version, setVersion] = useState(0)
  const [menu, setMenu] = useState(false)
  const refresh = useCallback(() => setVersion(v => v + 1), [])

  useEffect(() => { try { localStorage.setItem('galeri.tab', tab) } catch { /* private mode */ } }, [tab])

  // first visit after start-up: make sure the index is fresh, then redraw
  useEffect(() => { gapi.scan().then(refresh).catch(() => {}) }, [refresh])

  const closers = useRef<(() => void)[]>([])
  const push = (l: Layer) => {
    setLayers(s => [...s, l])
    closers.current.push(pushBack(() => { closers.current.pop(); setLayers(s => s.slice(0, -1)) }))
  }
  // closing the top layer from a button: same as the back gesture
  const pop = () => { const c = closers.current.pop(); setLayers(s => s.slice(0, -1)); c?.() }

  /** After a change: forget removed paths at once, rescan, redraw. */
  const changed = useCallback(async (removed: string[] = []) => {
    try {
      if (removed.length) await gapi.forget(removed)
      await gapi.scan()
    } catch { /* the periodic scan catches up */ }
    refresh()
  }, [refresh])

  const del = useCallback(async (paths: string[]) => {
    if (!paths.length) return false
    if (!await confirmSheet(paths.length === 1 ? 'Bu öğe silinsin mi?' : `${paths.length} öğe silinsin mi?`,
      'Çöp kutusuna taşınır; oradan geri yükleyebilirsin.', 'Sil', true)) return false
    try {
      await api.remove(paths)
      toast(paths.length === 1 ? 'Çöp kutusuna taşındı.' : `${paths.length} öğe çöp kutusuna taşındı.`)
      await changed(paths)
      return true
    } catch (e) { toastError(e); return false }
  }, [changed])

  const moveCopy = useCallback(async (paths: string[], copy: boolean) => {
    if (!paths.length) return false
    const from = parentOf(paths[0])
    const dest = await pickAlbum(copy ? `${paths.length} öğeyi kopyala` : `${paths.length} öğeyi taşı`, copy ? undefined : from)
    if (dest === null) return false
    try {
      if (copy) await api.copy(paths, dest)
      else await api.move(paths, dest)
      toast(`${paths.length} öğe ${copy ? 'kopyalandı' : 'taşındı'}: ${albumTitle(dest)}`)
      await changed(copy ? [] : paths)
      return true
    } catch (e) { toastError(e); return false }
  }, [changed])

  const top = layers[layers.length - 1]
  const albumLayer = [...layers].reverse().find(l => l.kind === 'album') as Extract<Layer, { kind: 'album' }> | undefined

  return (
    <div className="g-app">
      <div className={`g-screen ${albumLayer ? 'covered' : ''}`}>
        <header className="g-top">
          <h1>{tab === 'photos' ? 'Fotoğraflar' : 'Albümler'}</h1>
          <button className="g-icon-btn" onClick={() => setMenu(true)} aria-label="Menü"><Icon name="more" /></button>
        </header>
        {tab === 'photos'
          ? <MediaScreen album={null} version={version} onOpen={(items, index, more) => push({ kind: 'viewer', items, index, more, album: null })}
              del={del} moveCopy={moveCopy} empty="Henüz fotoğraf yok. Bir albüme fotoğraf yükleyerek başla." />
          : <AlbumList version={version} onOpen={a => push({ kind: 'album', path: a.path })} onChanged={() => changed()} />}
        <nav className="g-tabs">
          <button className={tab === 'photos' ? 'on' : ''} onClick={() => setTab('photos')}><Icon name="photos" /><span>Fotoğraflar</span></button>
          <button className={tab === 'albums' ? 'on' : ''} onClick={() => setTab('albums')}><Icon name="albums" /><span>Albümler</span></button>
        </nav>
      </div>

      {albumLayer && (
        <div className="g-screen g-layer">
          <AlbumScreen key={albumLayer.path} path={albumLayer.path} version={version} onBack={pop}
            onOpen={(items, index, more) => push({ kind: 'viewer', items, index, more, album: albumLayer.path })}
            onRenamed={async (np) => { await changed(); setLayers(s => s.map(l => l.kind === 'album' && l.path === albumLayer.path ? { ...l, path: np } : l)) }}
            del={del} moveCopy={moveCopy} changed={changed} />
        </div>
      )}

      {top?.kind === 'viewer' && (
        <ViewerLayer layer={top} version={version} onClose={pop} del={del} moveCopy={moveCopy} />
      )}

      {menu && (
        <div className="g-scrim clear" onClick={() => setMenu(false)}>
          <div className="g-menu" onClick={e => e.stopPropagation()}>
            <button onClick={async () => { setMenu(false); if (await openSettings()) changed() }}><Icon name="settings" size={20} />Galeri ayarları</button>
            <button onClick={async () => { setMenu(false); if (await openTrash()) changed() }}><Icon name="trash" size={20} />Çöp kutusu</button>
            <button onClick={() => { setMenu(false); toast('Yenileniyor…'); changed() }}><Icon name="restore" size={20} />Yenile</button>
            <a href="/" onClick={() => setMenu(false)}><Icon name="folder" size={20} />Dosyalar (DiskHaven)</a>
            <button onClick={async () => { setMenu(false); try { await api.logout() } catch { /* already out */ } onLogout() }}>
              <Icon name="close" size={20} />Çıkış ({user})
            </button>
          </div>
        </div>
      )}
    </div>
  )
}

/** The viewer keeps its own copy of the list so a deletion inside it updates in place. */
function ViewerLayer({ layer, version, onClose, del, moveCopy }: {
  layer: Extract<Layer, { kind: 'viewer' }>; version: number; onClose: () => void
  del: (p: string[]) => Promise<boolean>; moveCopy: (p: string[], copy: boolean) => Promise<boolean>
}) {
  const [items, setItems] = useState(layer.items)
  const first = useRef(true)
  // more pages arrive through the grid underneath; keep in step without losing local removals
  useEffect(() => { if (first.current) { first.current = false; return } setItems(prev => layer.items.length > prev.length ? layer.items : prev) }, [layer.items, version])
  return (
    <Viewer items={items} start={layer.index} onClose={onClose} onNeedMore={layer.more}
      onDelete={async m => { const ok = await del([m.path]); if (ok) setItems(l => l.filter(x => x.path !== m.path)); return ok }}
      onMove={async (m, copy) => { const ok = await moveCopy([m.path], copy); if (ok && !copy) setItems(l => l.filter(x => x.path !== m.path)); return ok }} />
  )
}

// ---- photo grid with selection ---------------------------------------------------------------------------------
function MediaScreen({ album, version, onOpen, del, moveCopy, empty, header }: {
  album: string | null; version: number
  onOpen: (items: Media[], index: number, more: () => void) => void
  del: (p: string[]) => Promise<boolean>; moveCopy: (p: string[], copy: boolean) => Promise<boolean>
  empty: string; header?: (selecting: boolean, bar: React.ReactNode) => React.ReactNode
}) {
  const { items, total, more } = useMedia(album, version)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const anchor = useRef<string | null>(null)
  const selecting = selected.size > 0

  useEffect(() => { setSelected(new Set()) }, [album, version])

  // Android back while selecting clears the selection instead of leaving the screen
  useEffect(() => {
    if (!selecting) return
    return pushBack(() => setSelected(new Set()))
  }, [selecting])

  const toggle = (path: string, range?: boolean) => setSelected(s => {
    const n = new Set(s)
    if (range && anchor.current) {
      const a = items.findIndex(i => i.path === anchor.current), b = items.findIndex(i => i.path === path)
      if (a >= 0 && b >= 0) { for (let i = Math.min(a, b); i <= Math.max(a, b); i++) n.add(items[i].path); return n }
    }
    if (n.has(path)) n.delete(path); else n.add(path)
    anchor.current = path
    return n
  })
  const paths = [...selected]

  const bar = selecting && (
    <div className="g-selbar">
      <button className="g-icon-btn" onClick={() => setSelected(new Set())} aria-label="Seçimi bırak"><Icon name="close" /></button>
      <b>{selected.size} seçildi</b>
      <button className="g-icon-btn" onClick={() => setSelected(new Set(items.map(i => i.path)))} aria-label="Tümünü seç"><Icon name="selectAll" /></button>
    </div>
  )

  return (
    <>
      {header ? header(selecting, bar) : bar}
      <main className="g-scroll">
        <Grid items={items} total={total} more={more} selected={selected} selecting={selecting}
          onOpen={i => onOpen(items, i, more)} onToggle={toggle} empty={empty} />
      </main>
      {selecting && (
        <div className="g-actions">
          <button onClick={() => moveCopy(paths, false)}><Icon name="move" /><span>Taşı</span></button>
          <button onClick={() => moveCopy(paths, true)}><Icon name="copy" /><span>Kopyala</span></button>
          {paths.length === 1 && <a href={gapi.original(paths[0], true)} download><Icon name="download" /><span>İndir</span></a>}
          {paths.length > 1 && <a href={api.archiveUrl(paths, 'Galeri')}><Icon name="download" /><span>ZIP indir</span></a>}
          <button onClick={() => del(paths)}><Icon name="trash" /><span>Sil</span></button>
        </div>
      )}
    </>
  )
}

// ---- albums ------------------------------------------------------------------------------------------------------
function AlbumList({ version, onOpen, onChanged }: { version: number; onOpen: (a: AlbumInfo) => void; onChanged: () => void }) {
  const [albums, setAlbums] = useState<AlbumInfo[] | null>(null)
  const [showHidden, setShowHidden] = useState(false)
  useEffect(() => { gapi.albums().then(r => setAlbums(r.albums)).catch(e => { toastError(e); setAlbums([]) }) }, [version])

  const create = async () => {
    const dest = await pickAlbum('Yeni albüm')
    if (dest !== null) { toast(`Albüm hazır: ${albumTitle(dest)}. Fotoğrafları içine taşıyabilir ya da yükleyebilirsin.`); onChanged() }
  }

  if (albums === null) return <main className="g-scroll"><div className="g-center"><div className="g-spinner" /></div></main>
  const visible = albums.filter(a => !a.hidden)
  const hidden = albums.filter(a => a.hidden)
  return (
    <main className="g-scroll">
      <div className="g-albums">
        {visible.map(a => <AlbumCard key={a.path} a={a} onOpen={() => onOpen(a)} />)}
        <button className="g-album new" onClick={create}>
          <div className="g-album-cover"><Icon name="add" size={40} /></div>
          <b>Yeni albüm</b>
        </button>
      </div>
      {albums.length === 0 && <p className="g-muted g-pad">Henüz albüm yok. İçinde fotoğraf olan her klasör burada albüm olarak görünür.</p>}
      {hidden.length > 0 && (
        <>
          <button className="g-hidden-toggle" onClick={() => setShowHidden(s => !s)}>
            <Icon name={showHidden ? 'show' : 'hide'} size={20} /> Gizli albümler ({hidden.length})
          </button>
          {showHidden && <div className="g-albums dim">{hidden.map(a => <AlbumCard key={a.path} a={a} onOpen={() => onOpen(a)} />)}</div>}
        </>
      )}
    </main>
  )
}

function AlbumCard({ a, onOpen }: { a: AlbumInfo; onOpen: () => void }) {
  return (
    <button className="g-album" onClick={onOpen}>
      <div className="g-album-cover"><img src={gapi.thumb(a.cover)} alt="" loading="lazy" onError={e => { e.currentTarget.style.visibility = 'hidden' }} />
        {a.pinned && <span className="g-pin"><Icon name="pin" size={14} /></span>}</div>
      <b>{a.name}</b>
      <small>{a.count}</small>
    </button>
  )
}

function AlbumScreen({ path, version, onBack, onOpen, onRenamed, del, moveCopy, changed }: {
  path: string; version: number; onBack: () => void
  onOpen: (items: Media[], index: number, more: () => void) => void
  onRenamed: (newPath: string) => void
  del: (p: string[]) => Promise<boolean>; moveCopy: (p: string[], copy: boolean) => Promise<boolean>
  changed: (removed?: string[]) => Promise<void>
}) {
  const [info, setInfo] = useState<AlbumInfo | null>(null)
  const [menu, setMenu] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  useEffect(() => { gapi.albums().then(r => setInfo(r.albums.find(a => a.path === path) ?? null)).catch(() => {}) }, [path, version])

  const setFlag = async (key: 'hidden' | 'pinned', on: boolean) => {
    setMenu(false)
    try {
      const r = await gapi.albums()
      const list = r.albums.filter(a => a[key] && a.path !== path).map(a => a.path)
      await gapi.settings({ [key]: on ? (key === 'pinned' ? [path, ...list] : [...list, path]) : list })
      toast(key === 'hidden' ? (on ? 'Albüm gizlendi.' : 'Albüm yeniden görünüyor.') : (on ? 'Albüm sabitlendi.' : 'Sabitleme kaldırıldı.'))
      changed()
    } catch (e) { toastError(e) }
  }
  const rename = async () => {
    setMenu(false)
    if (path === '') return
    const name = await promptSheet('Albümü yeniden adlandır', nameOf(path), 'Kaydet')
    if (!name || name === nameOf(path)) return
    try {
      const r = await api.rename(path, name)
      // keep its hidden / pinned state under the new name
      const s = await gapi.albums()
      await gapi.settings({
        hidden: s.albums.filter(a => a.hidden).map(a => a.path === path ? r.path : a.path),
        pinned: s.albums.filter(a => a.pinned).map(a => a.path === path ? r.path : a.path),
      })
      onRenamed(r.path)
    } catch (e) { toastError(e) }
  }
  const remove = async () => {
    setMenu(false)
    if (path === '') return
    if (!await confirmSheet('Albüm silinsin mi?', `"${nameOf(path)}" klasörü ve içindeki her şey çöp kutusuna taşınır.`, 'Albümü sil', true)) return
    try { await api.remove([path]); await changed([path]); onBack() } catch (e) { toastError(e) }
  }

  return (
    <>
      <MediaScreen album={path} version={version} onOpen={onOpen} del={del} moveCopy={moveCopy}
        empty="Bu albüm boş. Sağ alttaki düğmeyle telefondan fotoğraf ekleyebilirsin."
        header={(selecting, bar) => selecting ? bar : (
          <header className="g-top">
            <button className="g-icon-btn" onClick={onBack} aria-label="Geri"><Icon name="back" /></button>
            <div className="g-top-title"><h1>{albumTitle(path)}</h1>{info && <small>{info.count} öğe{info.hidden ? ' · gizli' : ''}</small>}</div>
            <button className="g-icon-btn" onClick={() => setMenu(true)} aria-label="Albüm menüsü"><Icon name="more" /></button>
          </header>
        )} />
      <button className="g-fab" onClick={() => input.current?.click()} aria-label="Fotoğraf ekle"><Icon name="upload" /></button>
      <input ref={input} type="file" accept="image/*,video/*" multiple hidden
        onChange={e => { if (e.target.files?.length) { uploads.add(path, filesFromInput(e.target.files)); toast(`${e.target.files.length} dosya yükleniyor…`) } e.target.value = '' }} />
      <UploadStrip onFinished={() => changed()} />
      {menu && (
        <div className="g-scrim clear" onClick={() => setMenu(false)}>
          <div className="g-menu" onClick={e => e.stopPropagation()}>
            <button onClick={() => { setMenu(false); input.current?.click() }}><Icon name="upload" size={20} />Fotoğraf / video ekle</button>
            <button onClick={() => setFlag('pinned', !info?.pinned)}><Icon name="pin" size={20} />{info?.pinned ? 'Sabitlemeyi kaldır' : 'Albümü sabitle'}</button>
            <button onClick={() => setFlag('hidden', !info?.hidden)}><Icon name={info?.hidden ? 'show' : 'hide'} size={20} />{info?.hidden ? 'Albümü göster' : 'Albümü gizle'}</button>
            {path !== '' && <button onClick={rename}><Icon name="rename" size={20} />Yeniden adlandır</button>}
            {path !== '' && <button className="danger" onClick={remove}><Icon name="trash" size={20} />Albümü sil</button>}
            <p className="g-menu-note">Konum: /{path}</p>
          </div>
        </div>
      )}
    </>
  )
}
