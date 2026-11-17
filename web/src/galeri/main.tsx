import { StrictMode, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { api } from '../api'
import { Galeri } from './Galeri'
import { Toasts } from './ui'
import './galeri.css'

type State = { kind: 'loading' } | { kind: 'login'; setup: boolean } | { kind: 'in'; user: string }

function Root() {
  const [state, setState] = useState<State>({ kind: 'loading' })
  const check = () => api.authState()
    .then(s => setState(s.user ? { kind: 'in', user: s.user } : { kind: 'login', setup: s.setupRequired }))
    .catch(() => setState({ kind: 'login', setup: false }))
  useEffect(() => {
    check()
    const out = () => setState({ kind: 'login', setup: false })
    window.addEventListener('diskhaven:unauthorized', out)
    return () => window.removeEventListener('diskhaven:unauthorized', out)
  }, [])
  if (state.kind === 'loading') return <div className="g-center full"><div className="g-spinner" /></div>
  if (state.kind === 'login') return <SignIn setup={state.setup} onDone={user => setState({ kind: 'in', user })} />
  return <Galeri user={state.user} onLogout={() => setState({ kind: 'login', setup: false })} />
}

function SignIn({ setup, onDone }: { setup: boolean; onDone: (u: string) => void }) {
  const [user, setUser] = useState('')
  const [pass, setPass] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  if (setup) return (
    <main className="g-login"><div className="g-login-card">
      <img src="/galeri-icon.svg" width={72} height={72} alt="" />
      <h1>DiskHaven Gallery</h1><p>Önce DiskHaven'da yönetici hesabını oluştur.</p>
      <a className="g-fill-btn" href="/">DiskHaven'ı aç</a>
    </div></main>
  )
  return (
    <main className="g-login">
      <form className="g-login-card" onSubmit={async e => {
        e.preventDefault(); setBusy(true); setError(null)
        try { const r = await api.login(user, pass); onDone(r.user) } catch (err) { setError(err instanceof Error ? err.message : String(err)) } finally { setBusy(false) }
      }}>
        <img src="/galeri-icon.svg" width={72} height={72} alt="" />
        <h1>DiskHaven Gallery</h1>
        <p>DiskHaven hesabınla giriş yap.</p>
        <input className="g-input" placeholder="Kullanıcı adı" value={user} onChange={e => setUser(e.target.value)} autoComplete="username" autoCapitalize="none" required />
        <input className="g-input" placeholder="Parola" type="password" value={pass} onChange={e => setPass(e.target.value)} autoComplete="current-password" required />
        {error && <p className="g-error" role="alert">{error}</p>}
        <button className="g-fill-btn" disabled={busy}>Giriş yap</button>
      </form>
    </main>
  )
}

createRoot(document.getElementById('root')!).render(<StrictMode><Root /><Toasts /></StrictMode>)
