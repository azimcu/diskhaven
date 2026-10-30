import { lazy, Suspense, useCallback, useEffect, useState } from 'react'
import { KeyRound, LogOut, Menu as MenuIcon, Moon, ShieldCheck, Sun, UserCircle2 } from 'lucide-react'
import { effectiveTheme, onDeviceThemeChange, pickTheme, savedTheme, type Theme } from './theme'
import { api } from './api'
import { promptDialog, toast, toastError, Toasts } from './dialogs'
import { Explorer, type Clipboard } from './Explorer'
import { Login } from './Login'
import { t } from './strings'
import { Transfers } from './Transfers'
import { TrashView } from './TrashView'
import { loadPlugins, usePlugins } from './plugins'
import { Tree } from './Tree'
import type { AuthState } from './types'
import { uploadFiles } from './conflicts'
import { filesFromDrop } from './uploads'

// Plugin screens are separate chunks: a plugin that is not installed is never downloaded.
const DisksView = lazy(() => import('./DisksView').then(m => ({ default: m.DisksView })))
const SharesView = lazy(() => import('./Sharing').then(m => ({ default: m.SharesView })))
const AdminPanel = lazy(() => import('./Admin'))
const HideawayView = lazy(() => import('./Hideaway'))

// Location lives in the URL hash (#/files/Belgeler/Okul, #/trash) so browser back/forward and reload work.
type Special = 'trash' | 'disks' | 'shares' | 'users' | 'admin' | 'hideaway'
type Route = { view: 'files'; path: string } | { view: Special }

function parseHash(): Route {
  const h = decodeURIComponent(location.hash.replace(/^#\/?/, ''))
  if (h === 'trash' || h === 'disks' || h === 'shares' || h === 'hideaway') return { view: h }
  if (h === 'users' || h.startsWith('admin')) return { view: 'admin' }
  return { view: 'files', path: h.startsWith('files') ? h.slice(5).replace(/^\/+|\/+$/g, '') : '' }
}

const hashFor = (path: string) => `#/files${path ? '/' + path.split('/').map(encodeURIComponent).join('/') : ''}`

export function App() {
  const [auth, setAuth] = useState<AuthState | null>(null)
  const [picked, setPicked] = useState<Theme | null>(savedTheme)
  const [, redraw] = useState(0)
  useEffect(() => onDeviceThemeChange(() => redraw(n => n + 1)), [])
  const theme = effectiveTheme(picked)
  const [route, setRoute] = useState<Route>(parseHash)
  const [version, setVersion] = useState(0)
  const [clipboard, setClipboard] = useState<Clipboard | null>(null)
  const [storage, setStorage] = useState<{ total: number; free: number } | null>(null)
  const [drawer, setDrawer] = useState(false)
  const [userMenu, setUserMenu] = useState(false)

  useEffect(() => {
    api.authState().then(setAuth).catch(() => setAuth({ setupRequired: false, user: null }))
    const onUnauthorized = () => setAuth(a => (a ? { ...a, user: null } : a))
    window.addEventListener('diskhaven:unauthorized', onUnauthorized)
    const onHash = () => { setRoute(parseHash()); setDrawer(false) }
    window.addEventListener('hashchange', onHash)
    return () => { window.removeEventListener('diskhaven:unauthorized', onUnauthorized); window.removeEventListener('hashchange', onHash) }
  }, [])

  const changed = useCallback(() => {
    setVersion(v => v + 1)
    api.storage().then(setStorage).catch(() => {})
  }, [])
  useEffect(() => { if (auth?.user) { changed(); void loadPlugins() } }, [auth?.user, changed])
  const plugins = usePlugins()

  const navigate = useCallback((path: string) => { location.hash = hashFor(path) }, [])

  /** Drop onto a folder: items dragged inside DiskHaven are moved (Ctrl = copy), files from the computer are uploaded. */
  const onDropItems = useCallback(async (dest: string, e: React.DragEvent) => {
    e.preventDefault()
    const internal = e.dataTransfer.getData('application/x-diskhaven')
    if (internal) {
      const paths: string[] = JSON.parse(internal)
      const copy = e.ctrlKey || e.altKey
      const sources = paths.filter(p => p !== dest)
      if (sources.length === 0) return
      try {
        const r = copy ? await api.copy(sources, dest) : await api.move(sources, dest)
        toast(`${r.paths.length} öğe ${copy ? 'kopyalandı' : 'taşındı'}`)
      } catch (err) { toastError(err) }
      changed()
      return
    }
    if (e.dataTransfer.types.includes('Files')) await uploadFiles(dest, await filesFromDrop(e.dataTransfer))
  }, [changed])

  const changePassword = async () => {
    setUserMenu(false)
    const current = await promptDialog('Mevcut parola', '', 'password')
    if (!current) return
    const next = await promptDialog('Yeni parola (en az 10 karakter)', '', 'password')
    if (!next) return
    try { await api.changePassword(current, next); toast('Parola değiştirildi') } catch (e) { toastError(e) }
  }

  const logout = async () => {
    setUserMenu(false)
    await api.logout().catch(() => {})
    setAuth(a => (a ? { ...a, user: null } : a))
  }

  if (!auth) return <div className="loading full"><span className="spinner" /></div>
  if (!auth.user) return <><Login setup={auth.setupRequired} demo={auth.demo} onDone={() => api.authState().then(setAuth).catch(() => {})} /><Toasts /></>

  return (
    <div className="app">
      {auth.demo && <div className="demo-bar" role="note">Demo: değişiklikler her saat başı sıfırlanır · dosya başına en fazla 20 MB · disk ve hesap ayarları kapalı</div>}
      <header className="titlebar">
        <button className="icon-btn show-sm" onClick={() => setDrawer(d => !d)} aria-label="Klasörler"><MenuIcon size={18} /></button>
        <img src="/icon.svg" width={20} height={20} alt="" />
        <span className="brand">{t.appName}</span>
        <span className="grow" />
        <div className="theme-switch" role="group" aria-label="Tema">
          <button className={theme === 'light' ? 'on' : ''} aria-pressed={theme === 'light'} onClick={() => setPicked(pickTheme('light'))} title="Açık tema"><Sun size={15} /></button>
          <button className={theme === 'dark' ? 'on' : ''} aria-pressed={theme === 'dark'} onClick={() => setPicked(pickTheme('dark'))} title="Koyu tema"><Moon size={15} /></button>
        </div>
        <div className="user">
          <button className="user-btn" onClick={() => setUserMenu(m => !m)}><UserCircle2 size={18} /> <span className="hide-sm">{auth.user}</span></button>
          {userMenu && (
            <>
              <div className="menu-backdrop" onMouseDown={() => setUserMenu(false)} />
              <div className="menu user-menu">
                {auth.isAdmin && <button className="menu-item" onClick={() => { setUserMenu(false); location.hash = '#/admin' }}><span className="menu-ic"><ShieldCheck size={16} /></span>Yönetim paneli</button>}
                <button className="menu-item" onClick={changePassword}><span className="menu-ic"><KeyRound size={16} /></span>{t.changePassword}</button>
                <button className="menu-item" onClick={logout}><span className="menu-ic"><LogOut size={16} /></span>{t.logout}</button>
              </div>
            </>
          )}
        </div>
      </header>
      <div className="body">
        <div className={`side ${drawer ? 'open' : ''}`}>
          <Tree current={route.view === 'files' ? route.path : null} special={route.view === 'files' ? null : route.view}
            version={version} onOpen={navigate} onOpenSpecial={v => { location.hash = `#/${v}` }}
            isAdmin={!!auth.isAdmin} onDropItems={onDropItems} storage={storage} />
        </div>
        {drawer && <div className="side-backdrop" onClick={() => setDrawer(false)} />}
        <Suspense fallback={<div className="main-col"><div className="loading"><span className="spinner" /></div></div>}>
        {route.view === 'files'
          ? <Explorer path={route.path} version={version} clipboard={clipboard} setClipboard={setClipboard}
              navigate={navigate} changed={changed} onDropItems={onDropItems} storage={storage} />
          : route.view === 'trash' ? <TrashView onChanged={changed} />
          : route.view === 'shares' && plugins.has('sharing') ? <SharesView />
          : route.view === 'hideaway' && plugins.has('vault') ? <HideawayView />
          : route.view === 'admin' && auth.isAdmin ? <AdminPanel me={auth.user} />
          : route.view === 'disks' && auth.isAdmin && plugins.has('disks') ? <DisksView onChanged={changed} />
          : plugins.loaded ? <div className="main-col"><p className="empty-note">Bu bölüm kurulu değil.</p></div> : null}
        </Suspense>
      </div>
      <Transfers />
      <Toasts />
    </div>
  )
}
