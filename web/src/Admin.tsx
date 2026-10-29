import { useEffect, useMemo, useState } from 'react'
import { Activity, Blocks, Check, Download, Gauge, HardDrive, Puzzle, Search, Trash2, Users } from 'lucide-react'
import { api } from './api'
import { confirmDialog, toast, toastError } from './dialogs'
import { formatDate, formatSize } from './format'
import { loadPlugins, usePlugins } from './plugins'
import { UsersView } from './Sharing'
import type { ActivityItem, AdminOverview, PluginInfo } from './types'

type Tab = 'overview' | 'plugins' | 'users' | 'activity'

/** Admin panel: overview of the server, plugin store, users and the activity log. */
export default function AdminPanel({ me }: { me: string }) {
  const [tab, setTab] = useState<Tab>(() => (location.hash.split('/')[2] as Tab) || 'overview')
  const { has } = usePlugins()
  const go = (t: Tab) => { setTab(t); history.replaceState(null, '', `#/admin/${t}`) }
  const tabs: [Tab, string, React.ReactNode][] = [
    ['overview', 'Genel bakış', <Gauge size={16} />],
    ['plugins', 'Eklenti mağazası', <Puzzle size={16} />],
    ['users', 'Kullanıcılar', <Users size={16} />],
    ...(has('activity') ? [['activity', 'Etkinlik günlüğü', <Activity size={16} />] as [Tab, string, React.ReactNode]] : []),
  ]
  return (
    <div className="main-col admin">
      <div className="admin-tabs" role="tablist">
        {tabs.map(([id, label, icon]) => (
          <button key={id} role="tab" aria-selected={tab === id} className={tab === id ? 'on' : ''} onClick={() => go(id)}>{icon}<span>{label}</span></button>
        ))}
      </div>
      <div className="admin-body">
        {tab === 'overview' && <Overview />}
        {tab === 'plugins' && <Store />}
        {tab === 'users' && <UsersView me={me} />}
        {tab === 'activity' && <ActivityLog />}
      </div>
    </div>
  )
}

function Overview() {
  const [o, setO] = useState<AdminOverview | null>(null)
  useEffect(() => {
    let alive = true
    const load = () => api.adminOverview().then(v => alive && setO(v)).catch(() => {})
    load()
    const t = window.setInterval(load, 4000)
    return () => { alive = false; window.clearInterval(t) }
  }, [])
  if (!o) return <div className="loading"><span className="spinner" /></div>
  const used = o.storage.total - o.storage.free
  const s = o.system
  const mem = s.memUsed != null && s.memTotal ? s.memUsed / s.memTotal : null
  const uptime = Math.max(0, (Date.now() - Date.parse(o.startedAt)) / 3600000)
  return (
    <div className="ov-grid">
      <Card title="Depolama" icon={<HardDrive size={16} />}>
        <div className="big">{formatSize(used)} <small>/ {formatSize(o.storage.total)}</small></div>
        <Bar value={used / o.storage.total} />
        <p>{formatSize(o.storage.free)} boş</p>
      </Card>
      <Card title="İşlemci" icon={<Gauge size={16} />}>
        <div className="big">%{Math.round(s.cpu ?? 0)}</div>
        <Bar value={(s.cpu ?? 0) / 100} />
        <p>{s.temperature != null ? `${s.temperature.toLocaleString('tr-TR')} °C` : 'Sıcaklık bilgisi yok'}</p>
      </Card>
      <Card title="Bellek" icon={<Gauge size={16} />}>
        <div className="big">{mem != null ? `%${Math.round(mem * 100)}` : '–'}</div>
        {mem != null && <Bar value={mem} />}
        <p>{formatSize(s.memUsed)} / {formatSize(s.memTotal)}</p>
      </Card>
      <Card title="Disk etkinliği" icon={<Download size={16} />}>
        <div className="big small-big">↓ {formatSize(s.readPerSec ?? 0)}/sn</div>
        <div className="big small-big">↑ {formatSize(s.writePerSec ?? 0)}/sn</div>
      </Card>
      <Card title="Hesaplar ve içerik" icon={<Users size={16} />}>
        <dl className="ov-dl">
          <dt>Kullanıcı</dt><dd>{o.users}</dd>
          <dt>Paylaşım bağlantısı</dt><dd>{o.shares}</dd>
          <dt>Çöp kutusunda</dt><dd>{o.trash} öğe</dd>
          <dt>Kurulu eklenti</dt><dd>{o.plugins}</dd>
        </dl>
      </Card>
      <Card title="Sistem" icon={<Blocks size={16} />}>
        <dl className="ov-dl">
          <dt>DiskHaven</dt><dd>v{o.version}{o.demo ? ' (demo)' : ''}</dd>
          <dt>Çalışma süresi</dt><dd>{uptime < 48 ? `${uptime.toFixed(1)} saat` : `${Math.round(uptime / 24)} gün`}</dd>
          <dt>Çalışma ortamı</dt><dd>{o.dotnet}</dd>
          <dt>İşletim sistemi</dt><dd title={o.os}>{o.os.split(' ').slice(0, 3).join(' ')}</dd>
        </dl>
      </Card>
    </div>
  )
}

function Card({ title, icon, children }: { title: string; icon: React.ReactNode; children: React.ReactNode }) {
  return <section className="ov-card"><h3>{icon}{title}</h3>{children}</section>
}
const Bar = ({ value }: { value: number }) => <span className={`bar ${value > 0.9 ? 'full' : ''}`}><i style={{ width: `${Math.min(100, Math.round(value * 100))}%` }} /></span>

function Store() {
  const [list, setList] = useState<PluginInfo[] | null>(null)
  const [q, setQ] = useState('')
  const [cat, setCat] = useState('')
  useEffect(() => { api.adminPlugins().then(setList).catch(toastError) }, [])
  const cats = useMemo(() => [...new Set((list ?? []).map(p => p.category))], [list])
  const toggle = async (p: PluginInfo) => {
    if (p.installed && !(await confirmDialog(`"${p.name}" kaldırılsın mı? Verileri silinmez; yeniden kurunca her şey geri gelir.`, 'Kaldır'))) return
    setList(l => l && l.map(x => x.id === p.id ? { ...x, installed: !p.installed } : x))   // the button answers at once
    try { await api.setPlugin(p.id, !p.installed); await loadPlugins(); toast(p.installed ? `${p.name} kaldırıldı` : `${p.name} kuruldu`) }
    catch (e) { toastError(e); setList(l => l && l.map(x => x.id === p.id ? { ...x, installed: p.installed } : x)) }
  }
  if (!list) return <div className="loading"><span className="spinner" /></div>
  const f = q.trim().toLocaleLowerCase('tr-TR')
  const shown = list.filter(p => (!cat || p.category === cat) && (!f || `${p.name} ${p.description}`.toLocaleLowerCase('tr-TR').includes(f)))
  return (
    <div className="store">
      <div className="store-top">
        <p>DiskHaven küçük bir çekirdekle gelir; ihtiyacın olan özellikleri buradan kurarsın. Kaldırılan eklentinin kodu tarayıcıya hiç yüklenmez, sunucuda da kapanır. Verileri silinmez.</p>
        <label className="search"><Search size={14} /><input value={q} onChange={e => setQ(e.target.value)} placeholder="Eklenti ara" /></label>
        <div className="chips">
          <button className={!cat ? 'on' : ''} onClick={() => setCat('')}>Tümü</button>
          {cats.map(c => <button key={c} className={cat === c ? 'on' : ''} onClick={() => setCat(c)}>{c}</button>)}
        </div>
      </div>
      <div className="store-grid">
        {shown.map(p => (
          <article key={p.id} className={`plugin ${p.installed ? 'installed' : ''}`}>
            <header><span className="plugin-cat">{p.category}</span><span className="plugin-ver">v{p.version}</span></header>
            <h3>{p.name}</h3>
            <p>{p.description}</p>
            <ul>{p.features.map(x => <li key={x}>{x}</li>)}</ul>
            <button className={`btn ${p.installed ? '' : 'primary'}`} onClick={() => toggle(p)}>
              {p.installed ? <><Check size={15} /> Kurulu · Kaldır</> : 'Kur'}
            </button>
          </article>
        ))}
      </div>
    </div>
  )
}

const ACTIONS: Record<string, string> = {
  upload: 'Yükledi', folder: 'Klasör oluşturdu', rename: 'Yeniden adlandırdı', move: 'Taşıdı', copy: 'Kopyaladı', delete: 'Sildi',
  restore: 'Geri yükledi', purge: 'Kalıcı sildi', hide: 'Gizledi / gösterdi', compress: 'Sıkıştırdı', extract: 'Ayıkladı', share: 'Paylaştı', unshare: 'Paylaşımı kaldırdı',
}

function ActivityLog() {
  const [items, setItems] = useState<ActivityItem[] | null>(null)
  const [q, setQ] = useState('')
  const [user, setUser] = useState('')
  const [action, setAction] = useState('')
  useEffect(() => {
    const t = window.setTimeout(() => api.activity(q, user, action).then(setItems).catch(toastError), 250)
    return () => window.clearTimeout(t)
  }, [q, user, action])
  const users = useMemo(() => [...new Set((items ?? []).map(i => i.user))], [items])
  return (
    <div className="activity">
      <div className="activity-filters">
        <label className="search"><Search size={14} /><input value={q} onChange={e => setQ(e.target.value)} placeholder="Dosya ya da klasör adı" /></label>
        <select value={user} onChange={e => setUser(e.target.value)}><option value="">Tüm kullanıcılar</option>{users.map(u => <option key={u}>{u}</option>)}</select>
        <select value={action} onChange={e => setAction(e.target.value)}><option value="">Tüm işlemler</option>{Object.entries(ACTIONS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select>
      </div>
      {!items ? <div className="loading"><span className="spinner" /></div>
        : items.length === 0 ? <p className="empty-note">Henüz kayıt yok.</p>
        : (
          <table className="details activity-table">
            <thead><tr><th>Zaman</th><th>Kullanıcı</th><th>İşlem</th><th>Öğe</th></tr></thead>
            <tbody>
              {items.map((i, n) => (
                <tr key={n}>
                  <td className="c-date">{formatDate(i.at)}</td>
                  <td>{i.user}</td>
                  <td>{i.action === 'delete' || i.action === 'purge' ? <span className="danger-text"><Trash2 size={13} /> {ACTIONS[i.action]}</span> : ACTIONS[i.action] ?? i.action}</td>
                  <td className="act-paths" title={i.paths.join('\n')}>{i.paths.slice(0, 3).map(p => `/${p}`).join(', ')}{i.paths.length > 3 ? ` +${i.paths.length - 3}` : ''} {i.detail && <small>{i.detail}</small>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
    </div>
  )
}
