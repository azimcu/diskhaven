import { useState } from 'react'
import { api } from './api'
import { t } from './strings'

export function Login({ setup, demo, onDone }: { setup: boolean; demo?: { userName: string; password: string } | null; onDone: (user: string) => void }) {
  const [userName, setUserName] = useState(demo?.userName ?? '')
  const [password, setPassword] = useState(demo?.password ?? '')
  const [password2, setPassword2] = useState('')
  const [token, setToken] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    if (setup && password !== password2) return setError('Parolalar aynı değil.')
    setBusy(true)
    try {
      const r = setup ? await api.setup(token, userName, password) : await api.login(userName, password)
      onDone(r.user)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="login">
      <form className="login-card" onSubmit={submit}>
        <img src="/icon.svg" width={56} height={56} alt="" />
        <h1>{t.appName}</h1>
        <p className="muted">{setup ? 'İlk kurulum: yönetici hesabını oluştur.' : 'Devam etmek için giriş yap.'}</p>
        {demo && !setup && (
          <p className="demo-note">Bu bir demo. Bilgiler hazır yazıldı: <b>{demo.userName}</b> / <b>{demo.password}</b>.
            Dosya yükleyip silebilirsin; her şey her saat başı sıfırlanır.</p>
        )}
        {setup && (
          <label>Kurulum kodu
            <input className="input" value={token} onChange={e => setToken(e.target.value)} autoComplete="off" required />
            <small className="muted">Sunucudaki <code>config/setup-token.txt</code> dosyasında yazıyor.</small>
          </label>
        )}
        <label>Kullanıcı adı
          <input className="input" value={userName} onChange={e => setUserName(e.target.value)} autoComplete="username" autoCapitalize="none" required />
        </label>
        <label>Parola
          <input className="input" type="password" value={password} onChange={e => setPassword(e.target.value)}
            autoComplete={setup ? 'new-password' : 'current-password'} required minLength={setup ? 10 : undefined} />
        </label>
        {setup && (
          <label>Parola (tekrar)
            <input className="input" type="password" value={password2} onChange={e => setPassword2(e.target.value)} autoComplete="new-password" required />
          </label>
        )}
        {error && <p className="form-error" role="alert">{error}</p>}
        <button className="btn primary wide" disabled={busy}>{setup ? 'Hesabı oluştur' : 'Giriş yap'}</button>
      </form>
    </main>
  )
}
