import { useEffect, useState } from 'react'
import { ArrowLeft, Check, Copy, Download, Inbox, KeyRound, Link2, Lock, Trash2, Upload, UserPlus, X } from 'lucide-react'
import { api } from './api'
import { confirmDialog, Modal, mount, promptDialog, toast, toastError, Toasts } from './dialogs'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize, nameOf, previewKind } from './format'
import { t } from './strings'
import type { FileEntry, PublicItem, Share, ShareMode, UserInfo } from './types'

const DAYS = [1, 7, 30, null] as const

/** Copies to the clipboard. On plain http (home network) the clipboard API is missing, so fall back to execCommand. */
async function copyText(text: string, quiet = false) {
  try {
    if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(text)
    else {
      const area = document.createElement('textarea')
      area.value = text; area.setAttribute('readonly', ''); area.style.position = 'fixed'; area.style.opacity = '0'
      document.body.appendChild(area); area.select(); area.setSelectionRange(0, text.length)
      const ok = document.execCommand('copy'); area.remove()
      if (!ok) throw new Error('copy failed')
    }
    toast(t.linkCopied)
  } catch { if (!quiet) toast('Kopyalanamadı; bağlantıyı seçip elle kopyala', { error: true }) }
}

// ---- "Share" dialog from the explorer -------------------------------------------------------
function ShareBody({ entry, done }: { entry: FileEntry; done: () => void }) {
  const [days, setDays] = useState<number | null>(7)
  const [mode, setMode] = useState<ShareMode>('View')
  const [password, setPassword] = useState('')
  const [share, setShare] = useState<Share | null>(null)
  const [busy, setBusy] = useState(false)
  const folder = entry.kind === 'Folder'
  const create = async () => {
    setBusy(true)
    try { const s = await api.createShare(entry.path, days, mode, password || null); setShare(s); void copyText(api.shareUrl(s.token), true) }
    catch (e) { toastError(e) }
    setBusy(false)
  }
  return (
    <Modal onClose={done}>
      <p className="modal-title">{t.shareTitle(entry.name)}</p>
      <p className="modal-text small">{mode === 'UploadOnly' ? t.shareDropInfo : t.shareInfo}</p>
      {share ? (
        <>
          <div className="share-link">
            <input className="input" readOnly value={api.shareUrl(share.token)} onFocus={e => e.currentTarget.select()} />
            <button className="btn primary" onClick={() => copyText(api.shareUrl(share.token))}><Copy size={15} /> {t.copyLink}</button>
          </div>
          {share.hasPassword && <p className="modal-text small"><Lock size={13} /> {t.sharePasswordHint}</p>}
        </>
      ) : (
        <div className="share-form">
          {folder && (
            <div className="segmented" role="radiogroup" aria-label={t.share}>
              {(['View', 'UploadOnly'] as const).map(m => (
                <button key={m} role="radio" aria-checked={mode === m} className={mode === m ? 'on' : ''} onClick={() => setMode(m)}>
                  {m === 'View' ? <><Link2 size={14} /> {t.shareModeView}</> : <><Inbox size={14} /> {t.shareModeDrop}</>}
                </button>
              ))}
            </div>
          )}
          <div className="segmented" role="radiogroup" aria-label={t.shareExpiry}>
            {DAYS.map(d => (
              <button key={String(d)} role="radio" aria-checked={days === d} className={days === d ? 'on' : ''} onClick={() => setDays(d)}>{t.shareDays(d)}</button>
            ))}
          </div>
          <label className="share-pass"><Lock size={14} />
            <input className="input" type="password" autoComplete="new-password" value={password} onChange={e => setPassword(e.target.value)} placeholder={t.sharePassword} />
          </label>
        </div>
      )}
      <div className="modal-actions">
        <button className="btn" onClick={done}>{t.close}</button>
        {!share && <button className="btn primary" disabled={busy} onClick={create}><Link2 size={15} /> {t.createLink}</button>}
      </div>
    </Modal>
  )
}

export const shareDialog = (entry: FileEntry) => mount<void>(done => <ShareBody entry={entry} done={() => done()} />)

// ---- list of links ---------------------------------------------------------------------------
export function SharesView() {
  const [list, setList] = useState<Share[] | null>(null)
  const load = () => api.shares().then(setList).catch(toastError)
  useEffect(() => { void load() }, [])
  const revoke = async (s: Share) => {
    try { await api.revokeShare(s.id); toast('Bağlantı kaldırıldı') } catch (e) { toastError(e) }
    void load()
  }
  return (
    <div className="main-col">
      <div className="addressbar"><div className="crumbs"><span className="crumb current">{t.shares}</span></div></div>
      <p className="info-strip">{t.shareInfo}</p>
      <div className="list-wrap">
        {list === null && <div className="loading"><span className="spinner" /></div>}
        {list?.length === 0 && <p className="empty-note">{t.noShares}</p>}
        {list && list.length > 0 && (
          <ul className="card-list">
            {list.map(s => (
              <li key={s.id} className="card-row">
                <FileIcon entry={{ kind: s.kind, extension: s.kind === 'File' ? s.path.split('.').pop()?.toLowerCase() ?? null : null }} />
                <div className="card-main">
                  <b title={s.path}>{nameOf(s.path)}</b>
                  <small>
                    {s.mode === 'UploadOnly' && <span className="tag"><Inbox size={12} /> {t.dropBox}</span>}
                    {s.hasPassword && <span className="tag"><Lock size={12} /> {t.passwordProtected}</span>}
                    /{s.path} · {t.expires(s.expiresAt)} · {s.mode === 'UploadOnly' ? t.uploadsIn(s.uploads) : t.downloads(s.downloads)}
                  </small>
                </div>
                <button className="icon-btn" title={t.copyLink} onClick={() => copyText(api.shareUrl(s.token))}><Copy size={16} /></button>
                <button className="icon-btn" title={t.revoke} onClick={() => revoke(s)}><Trash2 size={16} /></button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  )
}

// ---- users (admin) ---------------------------------------------------------------------------
export function UsersView({ me }: { me: string }) {
  const [list, setList] = useState<UserInfo[] | null>(null)
  const load = () => api.users().then(setList).catch(toastError)
  useEffect(() => { void load() }, [])
  const add = async () => {
    const name = await promptDialog('Kullanıcı adı (ör. esim)')
    if (!name) return
    const pass = await promptDialog('Parola (en az 10 karakter)', '', 'password')
    if (!pass) return
    try { await api.addUser(name, pass, false); toast(`${name} eklendi`) } catch (e) { toastError(e) }
    void load()
  }
  const reset = async (u: UserInfo) => {
    const pass = await promptDialog(`${u.userName} için yeni parola`, '', 'password')
    if (!pass) return
    try { await api.resetPassword(u.userName, pass); toast('Parola değiştirildi; bu kişinin diğer cihazlarında oturum kapandı') } catch (e) { toastError(e) }
  }
  const remove = async (u: UserInfo) => {
    if (!(await confirmDialog(t.confirmRemoveUser(u.userName), t.removeUser, true))) return
    try { await api.removeUser(u.userName) } catch (e) { toastError(e) }
    void load()
  }
  return (
    <div className="main-col">
      <div className="commandbar"><button className="cmd" onClick={add}><UserPlus size={16} /> {t.addUser}</button></div>
      <div className="addressbar"><div className="crumbs"><span className="crumb current">{t.users}</span></div></div>
      <p className="info-strip">{t.usersInfo}</p>
      <div className="list-wrap">
        {list === null && <div className="loading"><span className="spinner" /></div>}
        <ul className="card-list">
          {list?.map(u => (
            <li key={u.userName} className="card-row">
              <div className="card-main">
                <b>{u.userName}{u.userName === me ? ' (sen)' : ''}</b>
                <small>{u.isAdmin ? t.admin : t.member} · {formatDate(u.createdAt)}</small>
              </div>
              {u.userName !== me && <>
                <button className="icon-btn" title={t.resetPassword} onClick={() => reset(u)}><KeyRound size={16} /></button>
                <button className="icon-btn" title={t.removeUser} onClick={() => remove(u)}><Trash2 size={16} /></button>
              </>}
            </li>
          ))}
        </ul>
      </div>
    </div>
  )
}

// ---- public page for someone who opened a link (no login) -------------------------------------
export function PublicShare({ token }: { token: string }) {
  const [path, setPath] = useState('')
  const [item, setItem] = useState<PublicItem | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [round, setRound] = useState(0)   // bumped after the password was accepted
  useEffect(() => {
    setItem(null)
    api.publicItem(token, path).then(i => { setItem(i); setError(null) }).catch(e => setError(e.message))
  }, [token, path, round])

  const open = item && !item.locked ? item : null
  const file = open?.kind === 'File'
  const kind = file ? previewKind(open!.name.split('.').pop()?.toLowerCase() ?? null) : null
  return (
    <div className="public">
      <header className="titlebar">
        <img src="/icon.svg" width={20} height={20} alt="" />
        <span className="brand">{t.appName}</span>
        <span className="grow" />
        {open && open.mode === 'View' && <small className="muted">{t.sharedBy(open.by)}</small>}
      </header>
      <main className="public-body">
        {error && <p className="empty-note error">{error}</p>}
        {!item && !error && <div className="loading"><span className="spinner" /></div>}
        {item?.locked && <Unlock token={token} done={() => setRound(r => r + 1)} />}
        {open?.mode === 'UploadOnly' && <DropBox token={token} by={open.by} expiresAt={open.expiresAt} />}
        {open && open.mode === 'View' && (
          <>
            <div className="public-head">
              {path && <button className="icon-btn" onClick={() => setPath(path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')} aria-label={t.back}><ArrowLeft size={16} /></button>}
              <h1>{open.name}</h1>
              {file && <a className="btn primary" href={api.publicContentUrl(token, path, true)}><Download size={15} /> {t.download} ({formatSize(open.size)})</a>}
            </div>
            {file && kind === 'image' && <img className="public-preview" src={api.publicContentUrl(token, path)} alt={open.name} />}
            {file && kind === 'video' && <video className="public-preview" src={api.publicContentUrl(token, path)} controls playsInline />}
            {file && kind === 'audio' && <audio src={api.publicContentUrl(token, path)} controls />}
            {open.entries && (
              <ul className="card-list">
                {open.entries.length === 0 && <p className="empty-note">{t.emptyFolder}</p>}
                {open.entries.map(e => (
                  <li key={e.path} className="card-row clickable" onClick={() => setPath(e.path)}>
                    <FileIcon entry={e} />
                    <div className="card-main"><b>{e.name}</b><small>{e.kind === 'Folder' ? t.folderType : formatSize(e.size)} · {formatDate(e.modified)}</small></div>
                    {e.kind === 'File' && <a className="icon-btn" href={api.publicContentUrl(token, e.path, true)} onClick={ev => ev.stopPropagation()} aria-label={t.download}><Download size={16} /></a>}
                  </li>
                ))}
              </ul>
            )}
            {open.expiresAt && <p className="muted small">{t.expires(open.expiresAt)}</p>}
          </>
        )}
      </main>
      <Toasts />
    </div>
  )
}

function Unlock({ token, done }: { token: string; done: () => void }) {
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    try { await api.unlockShare(token, password); done() } catch (err) { toastError(err); setBusy(false) }
  }
  return (
    <form className="public-lock" onSubmit={submit}>
      <Lock size={28} />
      <h1>{t.lockedTitle}</h1>
      <p className="muted">{t.lockedText}</p>
      <input className="input" type="password" autoFocus autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} aria-label={t.lockedTitle} />
      <button className="btn primary" disabled={busy || !password}>{t.open}</button>
    </form>
  )
}

type Drop = { id: number; file: File; sent: number; state: 'wait' | 'busy' | 'done' | 'fail'; error?: string }

/** Drop box: files go one after another; the visitor sees only what they sent in this visit. */
function DropBox({ token, by, expiresAt }: { token: string; by: string; expiresAt: string | null }) {
  const [drops, setDrops] = useState<Drop[]>([])
  const [over, setOver] = useState(false)
  const set = (id: number, change: Partial<Drop>) => setDrops(list => list.map(d => d.id === id ? { ...d, ...change } : d))
  // one sender at a time, so a slow connection is not split across many files
  useEffect(() => {
    if (drops.some(d => d.state === 'busy')) return
    const next = drops.find(d => d.state === 'wait')
    if (!next) return
    set(next.id, { state: 'busy' })
    api.dropFile(token, next.file, sent => set(next.id, { sent }))
      .then(() => set(next.id, { state: 'done', sent: next.file.size }))
      .catch(e => set(next.id, { state: 'fail', error: e.message }))
  }, [drops, token])
  useEffect(() => {
    const busy = drops.some(d => d.state === 'busy' || d.state === 'wait')
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault() }
    if (busy) window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [drops])
  const add = (files: FileList | null) => {
    if (!files?.length) return
    const now = Date.now()
    setDrops(list => [...list, ...Array.from(files).map((file, i) => ({ id: now + i, file, sent: 0, state: 'wait' as const }))])
  }
  return (
    <div className="dropbox">
      <h1>{t.dropTitle(by)}</h1>
      <label
        className={`drop-zone ${over ? 'over' : ''}`}
        onDragOver={e => { e.preventDefault(); setOver(true) }}
        onDragLeave={() => setOver(false)}
        onDrop={e => { e.preventDefault(); setOver(false); add(e.dataTransfer.files) }}
      >
        <Upload size={30} />
        <span>{t.dropText}</span>
        <span className="btn primary">{t.dropPick}</span>
        <input type="file" multiple hidden onChange={e => { add(e.currentTarget.files); e.currentTarget.value = '' }} />
      </label>
      {drops.length > 0 && (
        <ul className="card-list">
          {drops.map(d => (
            <li key={d.id} className="card-row">
              <FileIcon entry={{ kind: 'File', extension: d.file.name.split('.').pop()?.toLowerCase() ?? null }} />
              <div className="card-main">
                <b>{d.file.name}</b>
                <small>
                  {d.state === 'done' ? <><Check size={12} /> {t.dropSent} · {formatSize(d.file.size)}</>
                    : d.state === 'fail' ? <span className="danger-text"><X size={12} /> {t.dropFailed}: {d.error}</span>
                    : `${formatSize(d.sent)} / ${formatSize(d.file.size)}`}
                </small>
                {(d.state === 'busy' || d.state === 'wait') && <span className="bar"><i style={{ width: `${d.file.size ? Math.round(d.sent / d.file.size * 100) : 0}%` }} /></span>}
              </div>
            </li>
          ))}
        </ul>
      )}
      {expiresAt && <p className="muted small">{t.expires(expiresAt)}</p>}
    </div>
  )
}
