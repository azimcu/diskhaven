import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  ChevronRight, ClipboardPaste, Copy, Download, FolderOpen, FolderPlus, FolderUp, KeyRound, LayoutGrid, List, Lock,
  Pencil, Scissors, Settings, Trash2, Upload,
} from 'lucide-react'
import { api, ApiError } from './api'
import { confirmDialog, Modal, mount, toast, toastError } from './dialogs'
import { ContextMenu, RenameInput, type MenuItem } from './Explorer'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize, previewKind, typeName, uniqueName } from './format'
import { loadPlugins } from './plugins'
import { PreviewModal } from './Preview'
import type { FileEntry, HideawayState } from './types'
import { filesFromDrop, filesFromInput, uploadEvents, uploads } from './uploads'

/**
 * The hidden folder: its own tree, separate from the NAS, encrypted on the server. Until it is unlocked nothing
 * about its content is loaded. Locking (button, sign-out, 10 minutes idle) drops the key on the server.
 */
export default function HideawayView() {
  const [state, setState] = useState<HideawayState | null>(null)
  const refresh = useCallback(() => api.hideaway.state().then(setState).catch(toastError), [])
  useEffect(() => { void refresh() }, [refresh])
  if (!state) return <div className="main-col"><div className="loading"><span className="spinner" /></div></div>
  if (!state.setUp) return <SetupWizard onDone={refresh} />
  if (!state.unlocked) return <Unlock label={state.label} onDone={refresh} />
  return <VaultExplorer state={state} onLocked={refresh} />
}

// ---- first use ------------------------------------------------------------------------------
function SetupWizard({ onDone }: { onDone: () => void }) {
  const [pw, setPw] = useState(''); const [pw2, setPw2] = useState('')
  const [decoyOn, setDecoyOn] = useState(false); const [decoy, setDecoy] = useState('')
  const [busy, setBusy] = useState(false); const [key, setKey] = useState<string | null>(null); const [saved, setSaved] = useState(false)
  const err = pw && pw.length < 8 ? 'Parola en az 8 karakter olmalı.' : pw2 && pw !== pw2 ? 'Parolalar aynı değil.'
    : decoyOn && decoy && decoy === pw ? 'Yem parolası asıl paroladan farklı olmalı.' : decoyOn && decoy && decoy.length < 8 ? 'Yem parolası en az 8 karakter olmalı.' : null
  const ready = pw.length >= 8 && pw === pw2 && !err && (!decoyOn || decoy.length >= 8)
  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); if (!ready) return
    setBusy(true)
    try { setKey((await api.hideaway.setup(pw, decoyOn ? decoy : null)).recoveryKey) } catch (x) { toastError(x) }
    setBusy(false)
  }
  if (key) return (
    <div className="main-col hw-center">
      <div className="hw-card">
        <KeyRound size={28} className="hw-ic" />
        <h2>Kurtarma anahtarın</h2>
        <p>Parolanı unutursan ya da dosyalara bir yedek diskten ulaşman gerekirse bu anahtar kasayı açar. <b>Sadece bir kez gösteriliyor.</b> Bir yere yaz ya da dosya olarak sakla; bu sunucuda saklama.</p>
        <code className="hw-key">{key}</code>
        <div className="hw-row">
          <button className="btn" onClick={() => navigator.clipboard?.writeText(key).then(() => toast('Anahtar kopyalandı'))}><Copy size={15} /> Kopyala</button>
          <button className="btn" onClick={() => {
            const a = document.createElement('a')
            a.href = URL.createObjectURL(new Blob([`DiskHaven kurtarma anahtarı\n\n${key}\n\nKurtarma: diskhaven-recover <yedek diskte .diskhaven-data/hideaway> <çıkış klasörü>\n`], { type: 'text/plain' }))
            a.download = 'kurtarma-anahtari.txt'; a.click(); URL.revokeObjectURL(a.href)
          }}><Download size={15} /> Dosya olarak indir</button>
        </div>
        <label className="check"><input type="checkbox" checked={saved} onChange={e => setSaved(e.target.checked)} /> Anahtarı güvenli bir yere kaydettim</label>
        <button className="btn primary wide" disabled={!saved} onClick={onDone}>Kasayı aç</button>
      </div>
    </div>
  )
  return (
    <div className="main-col hw-center">
      <form className="hw-card" onSubmit={submit}>
        <Lock size={28} className="hw-ic" />
        <h2>Gizli klasörü kur</h2>
        <p>Burası dosyalarının geri kalanından tamamen ayrı, şifreli bir alan. Normal klasör ağacından girilemez. Kurulumdan sonra menüdeki adını dikkat çekmeyen bir şeyle değiştirebilirsin.</p>
        <label className="field"><span>Parola</span><input type="password" value={pw} onChange={e => setPw(e.target.value)} autoComplete="new-password" autoFocus /></label>
        <label className="field"><span>Parola (tekrar)</span><input type="password" value={pw2} onChange={e => setPw2(e.target.value)} autoComplete="new-password" /></label>
        <label className="check"><input type="checkbox" checked={decoyOn} onChange={e => setDecoyOn(e.target.checked)} /> Yem parolası da ekle</label>
        {decoyOn && <>
          <p className="muted small">Yem parolası girilince ayrı, sahte bir kasa açılır; asıl kasan görünmez ve var olduğu anlaşılmaz. İçine sıradan dosyalar koy.</p>
          <label className="field"><span>Yem parolası</span><input type="password" value={decoy} onChange={e => setDecoy(e.target.value)} autoComplete="new-password" /></label>
        </>}
        {err && <p className="error small">{err}</p>}
        <button className="btn primary wide" disabled={!ready || busy}>{busy ? 'Kuruluyor…' : 'Kur'}</button>
      </form>
    </div>
  )
}

function Unlock({ label, onDone }: { label: string; onDone: () => void }) {
  const [pw, setPw] = useState(''); const [busy, setBusy] = useState(false); const [err, setErr] = useState<string | null>(null)
  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setBusy(true); setErr(null)
    try { await api.hideaway.unlock(pw); onDone() }
    catch (x) { setErr(x instanceof ApiError && x.status === 401 ? 'Parola yanlış.' : x instanceof Error ? x.message : String(x)) }
    setBusy(false)
  }
  return (
    <div className="main-col hw-center">
      <form className="hw-card" onSubmit={submit}>
        <Lock size={26} className="hw-ic" />
        <h2>{label}</h2>
        <p className="muted">Bu alan korumalı. Devam etmek için parolayı gir.</p>
        <label className="field"><span>Parola ya da kurtarma anahtarı</span><input type="password" value={pw} onChange={e => setPw(e.target.value)} autoFocus autoComplete="current-password" /></label>
        {err && <p className="error small">{err}</p>}
        <button className="btn primary wide" disabled={!pw || busy}>{busy ? 'Açılıyor…' : 'Aç'}</button>
      </form>
    </div>
  )
}

// ---- unlocked ---------------------------------------------------------------------------------
const withSrc = (e: FileEntry): FileEntry => ({ ...e, src: api.hideaway.contentUrl(e.path), downloadSrc: api.hideaway.contentUrl(e.path, true) })

function VaultExplorer({ state, onLocked }: { state: HideawayState; onLocked: () => void }) {
  const [folder, setFolder] = useState('')
  const [crumbs, setCrumbs] = useState<{ id: string; name: string }[]>([])
  const [entries, setEntries] = useState<FileEntry[] | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [renaming, setRenaming] = useState<string | null>(null)
  const [menu, setMenu] = useState<{ x: number; y: number; onItem: boolean } | null>(null)
  const [clip, setClip] = useState<{ mode: 'copy' | 'cut'; ids: string[] } | null>(null)
  const [viewer, setViewer] = useState<FileEntry | null>(null)
  const [view, setView] = useState<'details' | 'icons'>('details')
  const [dragOver, setDragOver] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null), folderInput = useRef<HTMLInputElement>(null)

  const locked = useCallback((e: unknown) => {
    if (e instanceof ApiError && e.status === 423) { onLocked(); return true }
    return false
  }, [onLocked])
  const fail = useCallback((e: unknown) => { if (!locked(e)) toastError(e) }, [locked])

  const load = useCallback(() => {
    api.hideaway.list(folder).then(r => { setEntries(r.entries.map(withSrc)); setCrumbs(r.path) }).catch(fail)
  }, [folder, fail])
  useEffect(() => { setEntries(null); setSelected(new Set()); load() }, [load])
  const dest = `hideaway:${folder}`
  useEffect(() => {
    const on = (e: Event) => { if ((e as CustomEvent).detail === dest) load() }
    uploadEvents.addEventListener('committed', on)
    return () => uploadEvents.removeEventListener('committed', on)
  }, [dest, load])

  // locks itself after the idle time even if the tab stays open (the server does too)
  useEffect(() => {
    let timer = 0
    const reset = () => { window.clearTimeout(timer); timer = window.setTimeout(() => { void api.hideaway.lock().finally(onLocked) }, state.idleMinutes * 60000) }
    const evs = ['pointerdown', 'keydown', 'wheel'] as const
    evs.forEach(ev => window.addEventListener(ev, reset, { passive: true }))
    reset()
    return () => { window.clearTimeout(timer); evs.forEach(ev => window.removeEventListener(ev, reset)) }
  }, [state.idleMinutes, onLocked])

  const shown = useMemo(() => entries ?? [], [entries])
  const sel = shown.filter(e => selected.has(e.path))
  const single = sel.length === 1 ? sel[0] : null
  const previewable = shown.filter(e => e.kind === 'File' && previewKind(e.extension))

  const open = (e: FileEntry) => {
    if (e.kind === 'Folder') return setFolder(e.path)
    if (previewKind(e.extension)) return setViewer(e)
    location.href = e.downloadSrc!
  }
  const download = (list: FileEntry[]) => {
    if (!list.length) return
    const a = document.createElement('a')
    a.href = list.length === 1 && list[0].kind === 'File' ? list[0].downloadSrc! : api.hideaway.archiveUrl(list.map(x => x.path))
    document.body.appendChild(a); a.click(); a.remove()
  }
  const newFolder = async () => {
    try {
      const r = await api.hideaway.createFolder(folder, uniqueName(shown.map(e => e.name), 'Yeni klasör'))
      setEntries(l => [...(l ?? []), withSrc(r)]); setSelected(new Set([r.path])); setRenaming(r.path)
    } catch (e) { fail(e) }
  }
  const rename = async (e: FileEntry, v: string) => {
    setRenaming(null); const name = v.trim(); if (!name || name === e.name) return
    setEntries(l => l && l.map(x => x.path === e.path ? { ...x, name } : x))
    try { await api.hideaway.rename(e.path, name); load() } catch (x) { fail(x); load() }
  }
  const remove = async (list: FileEntry[]) => {
    if (!list.length || !(await confirmDialog(`${list.length} öğe kalıcı olarak silinsin mi? Gizli klasörün çöp kutusu yok.`, 'Sil', true))) return
    const gone = new Set(list.map(e => e.path))
    setEntries(l => l && l.filter(x => !gone.has(x.path))); setSelected(new Set())
    try { await api.hideaway.remove([...gone]) } catch (x) { fail(x); load() }
  }
  const paste = async () => {
    if (!clip) return
    try { clip.mode === 'copy' ? await api.hideaway.copy(clip.ids, folder) : await api.hideaway.move(clip.ids, folder); if (clip.mode === 'cut') setClip(null); load() }
    catch (x) { fail(x) }
  }
  const lock = async () => { await api.hideaway.lock().catch(() => {}); onLocked() }
  const pick = (dir: boolean) => (dir ? folderInput : fileInput).current?.click()
  const onPicked = (e: React.ChangeEvent<HTMLInputElement>) => { if (e.target.files) uploads.add(dest, filesFromInput(e.target.files)); e.target.value = '' }

  const click = (ev: React.MouseEvent, e: FileEntry) => {
    if (ev.ctrlKey || ev.metaKey) setSelected(p => { const n = new Set(p); if (n.has(e.path)) n.delete(e.path); else n.add(e.path); return n })
    else setSelected(new Set([e.path]))
  }
  const ctx = (ev: React.MouseEvent, e: FileEntry | null) => {
    ev.preventDefault(); ev.stopPropagation()
    if (e && !selected.has(e.path)) setSelected(new Set([e.path]))
    if (!e) setSelected(new Set())
    setMenu({ x: ev.clientX, y: ev.clientY, onItem: !!e })
  }
  const onKey = (e: React.KeyboardEvent) => {
    if ((e.target as HTMLElement).tagName === 'INPUT') return
    const mod = e.ctrlKey || e.metaKey, k = e.key.toLowerCase()
    if (e.key === 'Delete') void remove(sel)
    else if (e.key === 'F2' && single) setRenaming(single.path)
    else if (e.key === 'Enter' && single) open(single)
    else if (e.key === 'Backspace' && crumbs.length) setFolder(crumbs.length > 1 ? crumbs[crumbs.length - 2].id : '')
    else if (mod && k === 'c' && sel.length) setClip({ mode: 'copy', ids: sel.map(x => x.path) })
    else if (mod && k === 'x' && sel.length) setClip({ mode: 'cut', ids: sel.map(x => x.path) })
    else if (mod && k === 'v') void paste()
    else if (mod && k === 'a') { e.preventDefault(); setSelected(new Set(shown.map(x => x.path))) }
    else return
    e.preventDefault()
  }

  const items: MenuItem[] = menu?.onItem ? [
    single && { label: 'Aç', icon: <FolderOpen size={16} />, run: () => open(single) },
    { label: sel.length === 1 && single?.kind === 'File' ? 'İndir' : 'ZIP olarak indir', icon: <Download size={16} />, run: () => download(sel) },
    'sep',
    { label: 'Kes', icon: <Scissors size={16} />, hint: 'Ctrl+X', run: () => setClip({ mode: 'cut', ids: sel.map(x => x.path) }) },
    { label: 'Kopyala', icon: <Copy size={16} />, hint: 'Ctrl+C', run: () => setClip({ mode: 'copy', ids: sel.map(x => x.path) }) },
    'sep',
    single && { label: 'Yeniden adlandır', icon: <Pencil size={16} />, hint: 'F2', run: () => setRenaming(single.path) },
    { label: 'Sil', icon: <Trash2 size={16} />, hint: 'Del', run: () => remove(sel) },
  ] : [
    { label: 'Yeni klasör', icon: <FolderPlus size={16} />, run: newFolder },
    clip && { label: 'Yapıştır', icon: <ClipboardPaste size={16} />, hint: 'Ctrl+V', run: paste },
    'sep',
    { label: 'Dosya yükle', icon: <Upload size={16} />, run: () => pick(false) },
    { label: 'Klasör yükle', icon: <FolderUp size={16} />, run: () => pick(true) },
  ]

  return (
    <div className="main-col" onClick={() => setMenu(null)}>
      <input ref={fileInput} type="file" multiple hidden onChange={onPicked} />
      <input ref={folderInput} type="file" hidden onChange={onPicked} {...{ webkitdirectory: '', directory: '' }} />
      <div className="commandbar" role="toolbar">
        <button className="cmd" onClick={newFolder}><FolderPlus size={16} /> <span>Yeni klasör</span></button>
        <button className="cmd" onClick={() => pick(false)}><Upload size={16} /> <span>Yükle</span></button>
        <button className="cmd hide-sm" onClick={() => pick(true)}><FolderUp size={16} /> <span>Klasör yükle</span></button>
        <span className="cmd-sep" />
        <button className="cmd icon-only" disabled={!sel.length} onClick={() => setClip({ mode: 'cut', ids: sel.map(x => x.path) })} title="Kes"><Scissors size={16} /></button>
        <button className="cmd icon-only" disabled={!sel.length} onClick={() => setClip({ mode: 'copy', ids: sel.map(x => x.path) })} title="Kopyala"><Copy size={16} /></button>
        <button className="cmd icon-only" disabled={!clip} onClick={paste} title="Yapıştır"><ClipboardPaste size={16} /></button>
        <button className="cmd icon-only" disabled={!single} onClick={() => single && setRenaming(single.path)} title="Yeniden adlandır"><Pencil size={16} /></button>
        <button className="cmd icon-only" disabled={!sel.length} onClick={() => remove(sel)} title="Sil"><Trash2 size={16} /></button>
        <button className="cmd icon-only" disabled={!sel.length} onClick={() => download(sel)} title="İndir"><Download size={16} /></button>
        <span className="cmd-grow" />
        <button className={`cmd icon-only ${view === 'details' ? 'on' : ''}`} onClick={() => setView('details')} title="Ayrıntılar"><List size={16} /></button>
        <button className={`cmd icon-only ${view === 'icons' ? 'on' : ''}`} onClick={() => setView('icons')} title="Simgeler"><LayoutGrid size={16} /></button>
        <button className="cmd icon-only" onClick={() => void settingsDialog(state, onLocked)} title="Ayarlar"><Settings size={16} /></button>
        <button className="cmd" onClick={lock} title="Kilitle"><Lock size={16} /> <span>Kilitle</span></button>
      </div>
      <div className="addressbar">
        <div className="crumbs">
          <button className={`crumb ${crumbs.length === 0 ? 'current' : ''}`} onClick={() => setFolder('')}>{state.label}</button>
          {crumbs.map((c, i) => (
            <span key={c.id} className="crumb-wrap"><ChevronRight size={14} className="crumb-sep" />
              <button className={`crumb ${i === crumbs.length - 1 ? 'current' : ''}`} onClick={() => setFolder(c.id)}>{c.name}</button></span>
          ))}
        </div>
      </div>
      <div className="content">
        <div className={`list-wrap ${dragOver ? 'dragover' : ''}`} tabIndex={0} onKeyDown={onKey}
          onClick={e => { if (e.target === e.currentTarget) setSelected(new Set()) }}
          onContextMenu={e => { if (!(e.target as HTMLElement).closest('[data-path]')) ctx(e, null) }}
          onDragOver={e => { if (e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragOver(true) } }}
          onDragLeave={e => { if (e.currentTarget === e.target) setDragOver(false) }}
          onDrop={async e => { e.preventDefault(); setDragOver(false); if (e.dataTransfer.types.includes('Files')) uploads.add(dest, await filesFromDrop(e.dataTransfer)) }}>
          {entries === null && <div className="loading"><span className="spinner" /></div>}
          {entries && shown.length === 0 && <p className="empty-note">Bu klasör boş.<br /><small>Dosyaları buraya sürükle ya da Yükle'yi kullan.</small></p>}
          {view === 'details' && shown.length > 0 && (
            <table className="details" role="grid">
              <thead><tr><th>Ad</th><th className="c-date">Değiştirme tarihi</th><th className="c-type">Tür</th><th className="c-size">Boyut</th></tr></thead>
              <tbody>
                {shown.map(e => (
                  <tr key={e.path} data-path={e.path} className={`${selected.has(e.path) ? 'sel' : ''} ${clip?.mode === 'cut' && clip.ids.includes(e.path) ? 'cut' : ''}`}
                    onClick={ev => click(ev, e)} onDoubleClick={() => open(e)} onContextMenu={ev => ctx(ev, e)}>
                    <td className="c-name"><FileIcon entry={e} />
                      {renaming === e.path ? <RenameInput entry={e} onDone={v => rename(e, v)} onCancel={() => setRenaming(null)} /> : <span className="name-text">{e.name}</span>}</td>
                    <td className="c-date">{formatDate(e.modified)}</td>
                    <td className="c-type">{typeName(e)}</td>
                    <td className="c-size">{formatSize(e.size)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {view === 'icons' && shown.length > 0 && (
            <div className="grid">
              {shown.map(e => (
                <div key={e.path} data-path={e.path} className={`tile ${selected.has(e.path) ? 'sel' : ''}`}
                  onClick={ev => click(ev, e)} onDoubleClick={() => open(e)} onContextMenu={ev => ctx(ev, e)}>
                  {e.kind === 'File' && previewKind(e.extension) === 'image'
                    ? <img className="thumb" src={e.src} alt="" loading="lazy" decoding="async" />
                    : <span className="thumb icon"><FileIcon entry={e} size={50} /></span>}
                  {renaming === e.path ? <RenameInput entry={e} onDone={v => rename(e, v)} onCancel={() => setRenaming(null)} /> : <span className="tile-name">{e.name}</span>}
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
      <footer className="statusbar"><span>{shown.length} öğe</span>{sel.length > 0 && <span>{sel.length} seçili</span>}<span className="grow" /><span><Lock size={11} /> {state.idleMinutes} dk işlem olmazsa kilitlenir</span></footer>
      {menu && <ContextMenu x={menu.x} y={menu.y} items={items} onClose={() => setMenu(null)} />}
      {viewer && <PreviewModal entry={viewer} onClose={() => setViewer(null)}
        onPrev={previewable.indexOf(viewer) > 0 ? () => setViewer(previewable[previewable.indexOf(viewer) - 1]) : undefined}
        onNext={previewable.indexOf(viewer) < previewable.length - 1 ? () => setViewer(previewable[previewable.indexOf(viewer) + 1]) : undefined} />}
    </div>
  )
}

// ---- settings -------------------------------------------------------------------------------
function settingsDialog(state: HideawayState, onLocked: () => void) {
  return mount<void>(done => <SettingsBody state={state} done={done} onLocked={onLocked} />)
}

function SettingsBody({ state, done, onLocked }: { state: HideawayState; done: () => void; onLocked: () => void }) {
  const [label, setLabel] = useState(state.label)
  const [cur, setCur] = useState(''); const [next, setNext] = useState(''); const [decoy, setDecoy] = useState('')
  const saveLabel = async () => {
    try { await api.hideaway.label(label); await loadPlugins(); toast('Menü adı değiştirildi') }
    catch (e) { if (e instanceof ApiError && e.status === 423) { done(); onLocked() } else toastError(e) }
  }
  const changePw = async () => {
    try { await api.hideaway.password(cur, next); setNext(''); toast('Parola değiştirildi') } catch (e) { toastError(e) }
  }
  const setDecoyPw = async () => {
    try { await api.hideaway.decoy(decoy, cur); setDecoy(''); toast('Yem parolası ayarlandı') } catch (e) { toastError(e) }
  }
  return (
    <Modal onClose={done}>
      <div className="hw-settings">
        <h3>Menüdeki adı</h3>
        <p className="muted small">Sol menüde bu adla, sıradan bir bölüm gibi görünür.</p>
        <div className="hw-row"><input className="text" value={label} maxLength={40} onChange={e => setLabel(e.target.value)} /><button className="btn primary" onClick={saveLabel}>Kaydet</button></div>
        <div className="chips">{state.suggestions.map(s => <button key={s} onClick={() => setLabel(s)}>{s}</button>)}</div>

        <h3>Parola</h3>
        <label className="field"><span>Şu anki parola</span><input type="password" value={cur} onChange={e => setCur(e.target.value)} autoComplete="current-password" /></label>
        <div className="hw-row"><input className="text" type="password" placeholder="Yeni parola" value={next} onChange={e => setNext(e.target.value)} autoComplete="new-password" />
          <button className="btn" disabled={!cur || next.length < 8} onClick={changePw}>Değiştir</button></div>

        <h3>Yem parolası</h3>
        <p className="muted small">Bu parola girilince ayrı, sahte bir kasa açılır; bu kasa görünmez. Yeniden ayarlarsan eski sahte kasa silinir. (Şu anki parolayı yukarıya yaz.)</p>
        <div className="hw-row"><input className="text" type="password" placeholder="Yem parolası" value={decoy} onChange={e => setDecoy(e.target.value)} autoComplete="new-password" />
          <button className="btn" disabled={!cur || decoy.length < 8} onClick={setDecoyPw}>Ayarla</button></div>

        <h3>Yedekler ve kurtarma</h3>
        <p className="muted small">Yedek disklere eşitlenirken bu kasanın şifreli hali de diske gider (<code>.diskhaven-data/hideaway</code>). Sunucu olmasa bile kurtarma aracıyla, parola ya da kurtarma anahtarıyla herhangi bir bilgisayarda açılıp normal klasörlere çıkarılabilir.</p>
        <a className="btn" href="/api/hideaway/recovery-tool" download><Download size={15} /> Kurtarma aracını indir</a>
        <div className="modal-actions"><button className="btn primary" onClick={done}>Kapat</button></div>
      </div>
    </Modal>
  )
}
