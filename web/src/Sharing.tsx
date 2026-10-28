import { useEffect, useState } from 'react'
import { ArrowLeft, Copy, Download, KeyRound, Link2, Trash2, UserPlus } from 'lucide-react'
import { api } from './api'
import { confirmDialog, Modal, mount, promptDialog, toast, toastError, Toasts } from './dialogs'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize, nameOf, previewKind } from './format'
import { t } from './strings'
import type { FileEntry, PublicItem, Share, UserInfo } from './types'

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
  const [share, setShare] = useState<Share | null>(null)
  const [busy, setBusy] = useState(false)
  const create = async () => {
    setBusy(true)
    try { const s = await api.createShare(entry.path, days); setShare(s); void copyText(api.shareUrl(s.token), true) }
    catch (e) { toastError(e) }
    setBusy(false)
  }
  return (
    <Modal onClose={done}>
      <p className="modal-title">{t.shareTitle(entry.name)}</p>
      <p className="modal-text small">{t.shareInfo}</p>
      {share ? (
        <div className="share-link">
          <input className="input" readOnly value={api.shareUrl(share.token)} onFocus={e => e.currentTarget.select()} />
          <button className="btn primary" onClick={() => copyText(api.shareUrl(share.token))}><Copy size={15} /> {t.copyLink}</button>
        </div>
      ) : (
        <div className="segmented" role="radiogroup" aria-label={t.shareExpiry}>
          {DAYS.map(d => (
            <button key={String(d)} role="radio" aria-checked={days === d} className={days === d ? 'on' : ''} onClick={() => setDays(d)}>{t.shareDays(d)}</button>
          ))}
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
                  <small>/{s.path} · {t.expires(s.expiresAt)} · {t.downloads(s.downloads)}</small>
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
  useEffect(() => {
    setItem(null)
    api.publicItem(token, path).then(i => { setItem(i); setError(null) }).catch(e => setError(e.message))
  }, [token, path])

  const file = item?.kind === 'File'
  const kind = file ? previewKind(item!.name.split('.').pop()?.toLowerCase() ?? null) : null
  return (
    <div className="public">
      <header className="titlebar">
        <img src="/icon.svg" width={20} height={20} alt="" />
        <span className="brand">{t.appName}</span>
        <span className="grow" />
        {item && <small className="muted">{t.sharedBy(item.by)}</small>}
      </header>
      <main className="public-body">
        {error && <p className="empty-note error">{error}</p>}
        {!item && !error && <div className="loading"><span className="spinner" /></div>}
        {item && (
          <>
            <div className="public-head">
              {path && <button className="icon-btn" onClick={() => setPath(path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')} aria-label={t.back}><ArrowLeft size={16} /></button>}
              <h1>{item.name}</h1>
              {file && <a className="btn primary" href={api.publicContentUrl(token, path, true)}><Download size={15} /> {t.download} ({formatSize(item.size)})</a>}
            </div>
            {file && kind === 'image' && <img className="public-preview" src={api.publicContentUrl(token, path)} alt={item.name} />}
            {file && kind === 'video' && <video className="public-preview" src={api.publicContentUrl(token, path)} controls playsInline />}
            {file && kind === 'audio' && <audio src={api.publicContentUrl(token, path)} controls />}
            {item.entries && (
              <ul className="card-list">
                {item.entries.length === 0 && <p className="empty-note">{t.emptyFolder}</p>}
                {item.entries.map(e => (
                  <li key={e.path} className="card-row clickable" onClick={() => setPath(e.path)}>
                    <FileIcon entry={e} />
                    <div className="card-main"><b>{e.name}</b><small>{e.kind === 'Folder' ? t.folderType : formatSize(e.size)} · {formatDate(e.modified)}</small></div>
                    {e.kind === 'File' && <a className="icon-btn" href={api.publicContentUrl(token, e.path, true)} onClick={ev => ev.stopPropagation()} aria-label={t.download}><Download size={16} /></a>}
                  </li>
                ))}
              </ul>
            )}
            {item.expiresAt && <p className="muted small">{t.expires(item.expiresAt)}</p>}
          </>
        )}
      </main>
      <Toasts />
    </div>
  )
}
