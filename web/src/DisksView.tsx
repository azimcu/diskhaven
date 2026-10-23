import { useCallback, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { Activity, AlertTriangle, CheckCircle2, Clock, FolderSync, HardDrive, History, RefreshCw, Settings2, Usb, X } from 'lucide-react'
import { api } from './api'
import { confirmDialog, promptDialog, toast, toastError } from './dialogs'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize } from './format'
import type { AfterSync, Disk, FileEntry, SmartInfo, SyncJob, SyncPlanResult, SyncReport } from './types'

const stateText = (d: Disk) =>
  d.state === 'off' ? 'Kapalı · çıkarılabilir' : d.power === 'standby' ? 'Uykuda' : d.state === 'mounted' ? 'Açık · kullanımda' : 'Açık'

const afterText: Record<AfterSync, string> = {
  PowerOff: 'Diski kapat (kablo güvenle çıkarılabilir)',
  Standby: 'Diski uyut',
  StayOn: 'Açık bırak',
}

const phaseText: Record<string, string> = {
  prepare: 'Disk hazırlanıyor', plan: 'Değişiklikler hesaplanıyor', copy: 'Kopyalanıyor ve doğrulanıyor',
  cleanup: 'Temizleniyor', finish: 'Disk kapatılıyor', done: 'Bitti',
}

/** "This PC"-like page: every disk with its state, on/off switch, assigned folders and sync. */
export function DisksView({ onChanged }: { onChanged: () => void }) {
  const [data, setData] = useState<{ agent: boolean; disks: Disk[]; job: SyncJob | null } | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(() => api.disks().then(setData).catch(toastError), [])
  useEffect(() => {
    void load()
    const running = data?.job?.status === 'Running'
    const timer = setInterval(load, running ? 1000 : 4000)
    return () => clearInterval(timer)
  }, [load, data?.job?.status])

  const act = async (d: Disk, fn: () => Promise<unknown>, done?: string) => {
    setBusy(d.id)
    try { await fn(); if (done) toast(done) } catch (e) { toastError(e) }
    setBusy(null)
    void load()
    onChanged()
  }

  const toggle = (d: Disk) =>
    d.state === 'off'
      ? act(d, () => api.diskAction(d.id, 'power-on'), `${d.name} açıldı`)
      : act(d, () => api.diskAction(d.id, 'power-off'), `${d.name} kapatıldı, kabloyu çıkarabilirsin`)

  const adopt = async (d: Disk) => {
    const name = await promptDialog('Yedek diskin adı (ör. "Yedek 2TB")', d.label || d.model || 'Yedek disk')
    if (!name) return
    await act(d, () => api.adoptDisk(d.id, name), `${name} artık HomeVault yedek diski. Diskteki mevcut dosyalara dokunulmadı.`)
  }

  const sync = async (d: Disk) => {
    setBusy(d.id)
    let plan: SyncPlanResult
    try { plan = await api.planSync(d.id) } catch (e) { setBusy(null); return toastError(e) }
    setBusy(null)
    void load()
    if (!(await planDialog(d, plan))) return
    try { await api.startSync(d.id) } catch (e) { toastError(e) }
    void load()
  }

  if (!data) return <div className="main-col"><div className="loading"><span className="spinner" /></div></div>
  const job = data.job

  return (
    <div className="main-col">
      <div className="commandbar">
        <button className="cmd" onClick={load}><RefreshCw size={16} /> <span>Yenile</span></button>
      </div>
      <div className="addressbar"><div className="crumbs"><span className="crumb current">Diskler</span></div></div>
      <div className="list-wrap disks-page">
        {!data.agent && <p className="empty-note error">Disk servisi çalışmıyor. Sunucuda <code>homevault-diskd</code> servisini başlat.</p>}
        {job && (job.status === 'Running' || Date.now() - new Date(job.started).getTime() < 10 * 60_000) && <JobCard job={job} />}
        {data.agent && data.disks.length === 0 && <p className="empty-note">Takılı bir USB disk yok.</p>}
        <div className="disk-grid">
          {data.disks.map(d => (
            <DiskCard key={d.id} disk={d} busy={busy === d.id || (job?.status === 'Running' && job.fsId === d.id)}
              onToggle={() => toggle(d)} onAdopt={() => adopt(d)} onSync={() => sync(d)}
              onSettings={async () => { if (await settingsDialog(d)) void load() }}
              onHistory={() => historyDialog(d)} />
          ))}
        </div>
      </div>
    </div>
  )
}

function DiskCard({ disk: d, busy, onToggle, onAdopt, onSync, onSettings, onHistory }: {
  disk: Disk; busy: boolean; onToggle: () => void; onAdopt: () => void; onSync: () => void; onSettings: () => void; onHistory: () => void
}) {
  const health = d.state !== 'off' ? (
    <button className="btn" onClick={() => healthDialog(d)} title="Disk sağlığı"><Activity size={16} /> Sağlık</button>
  ) : null
  const used = d.usage ? d.usage.total - d.usage.free : null
  const pct = d.usage && d.usage.total ? (used! / d.usage.total) * 100 : 0
  const on = d.state !== 'off'
  return (
    <section className={`disk-card ${on ? '' : 'off'}`}>
      <header className="disk-head">
        {d.kind === 'usb' ? <Usb size={28} className="ic disk-ic" /> : <HardDrive size={28} className="ic disk-ic" />}
        <div className="disk-title">
          <strong>{d.name}</strong>
          <span className="muted">{[d.label !== d.name && d.label, d.fsType.toUpperCase(), formatSize(d.size), d.kind === 'virtual' ? 'sanal test diski' : d.model].filter(Boolean).join(' · ')}</span>
        </div>
        <button className={`switch ${on ? 'on' : ''}`} role="switch" aria-checked={on} disabled={busy} onClick={onToggle}
          title={on ? 'Diski kapat (güvenle çıkar)' : 'Diski aç'}>
          <span className="knob" />
        </button>
      </header>
      <div className="disk-state">
        <span className={`dot ${on ? (d.power === 'standby' ? 'sleep' : 'on') : ''}`} /> {busy ? 'İşlem sürüyor…' : stateText(d)}
      </div>
      {d.usage && (
        <div className="disk-usage">
          <div className={`bar big ${pct > 90 ? 'warn' : ''}`}><div style={{ width: `${pct}%` }} /></div>
          <span className="muted">{formatSize(d.usage.free)} boş / {formatSize(d.usage.total)}</span>
        </div>
      )}
      {d.foreign && <p className="disk-warn"><AlertTriangle size={16} /> Bu diskteki kimlik dosyası ayarlardaki diskle uyuşmuyor. Eşitleme yapılmaz.</p>}

      {!d.adopted ? (
        <div className="disk-actions">
          <p className="muted small">Bu disk henüz yedek diski değil. Yedek diski yaptığında içindeki dosyalara dokunulmaz; sadece seçtiğin klasörler eşitlenir.</p>
          <div className="disk-actions row">
            <button className="btn primary" disabled={busy} onClick={onAdopt}>Yedek diski yap</button>
            {health}
          </div>
        </div>
      ) : (
        <>
          <div className="disk-folders">
            {d.folders.length === 0
              ? <span className="muted small">Henüz klasör atanmadı.</span>
              : d.folders.map(f => <span key={f} className="chip"><FileIcon entry={{ kind: 'Folder', extension: null }} size={14} /> {f}</span>)}
          </div>
          <div className="disk-meta">
            {d.lastSync ? (
              <span className={d.lastSync.success ? 'ok-text' : 'err-text'}>
                {d.lastSync.success ? <CheckCircle2 size={14} /> : <AlertTriangle size={14} />}{' '}
                Son eşitleme {formatDate(d.lastSync.finished)}
                {d.lastSync.success ? ` · ${d.lastSync.copied + d.lastSync.updated} dosya, tümü doğrulandı` : ` · ${d.lastSync.message ?? 'başarısız'}`}
              </span>
            ) : <span className="muted">Hiç eşitlenmedi</span>}
            {d.schedule?.enabled && <span className="muted"><Clock size={14} /> Her gün {d.schedule.time}</span>}
          </div>
          <div className="disk-actions row">
            <button className="btn primary" disabled={busy || d.folders.length === 0} onClick={onSync}><FolderSync size={16} /> Eşitle</button>
            <button className="btn" onClick={onSettings}><Settings2 size={16} /> Ayarlar</button>
            <button className="btn" onClick={onHistory}><History size={16} /> Geçmiş</button>
            {health}
          </div>
        </>
      )}
    </section>
  )
}

function JobCard({ job }: { job: SyncJob }) {
  const p = job.progress
  const pct = p && p.bytesTotal > 0 ? (p.bytesDone / p.bytesTotal) * 100 : p && p.filesTotal ? (p.filesDone / p.filesTotal) * 100 : 0
  const r = job.report
  return (
    <section className={`job-card ${job.status.toLowerCase()}`}>
      <header>
        {job.status === 'Running' ? <span className="spinner" /> : job.status === 'Done' ? <CheckCircle2 size={20} className="ic ok" /> : <AlertTriangle size={20} className="ic err" />}
        <strong>{job.diskName}{job.scheduled ? ' · zamanlanmış eşitleme' : ''}</strong>
        {job.status === 'Running' && <button className="btn" onClick={() => api.cancelSync()}><X size={14} /> Durdur</button>}
      </header>
      {job.status === 'Running' && (
        <>
          <div className="muted small">{phaseText[job.phase] ?? job.phase}{p?.current ? ` · ${p.current}` : ''}</div>
          {p && <div className="bar big"><div style={{ width: `${pct}%` }} /></div>}
          {p && <div className="muted small">{p.filesDone}/{p.filesTotal} işlem · {formatSize(p.bytesDone)} / {formatSize(p.bytesTotal)}</div>}
        </>
      )}
      {r && (
        <p className="small">
          {r.canceled ? 'Durduruldu. ' : ''}
          {r.copied} yeni, {r.updated} değişen dosya kopyalandı{r.moved > 0 ? `, ${r.moved} dosya taşındı (tekrar kopyalanmadı)` : ''} · <b>{r.verified} dosya diskten geri okunup doğrulandı</b> · {r.deleted} öğe diskteki çöp kutusuna taşındı
          {r.errors.length > 0 && <span className="err-text"> · {r.errors.length} hata</span>}
          {r.skipped.length > 0 && <span className="muted"> · {r.skipped.length} atlandı</span>}
        </p>
      )}
      {job.error && <p className="err-text small">{job.error}</p>}
      {r && r.errors.length > 0 && <ul className="small err-list">{r.errors.slice(0, 20).map(e => <li key={e.path}><b>{e.path}</b>: {e.message}</li>)}</ul>}
    </section>
  )
}

// ---- dialogs ------------------------------------------------------------------------------
function mountDialog<T>(render: (done: (v: T) => void) => React.ReactNode): Promise<T> {
  return new Promise(resolve => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    root.render(render(v => { root.unmount(); host.remove(); resolve(v) }))
  })
}

const kindText: Record<string, string> = { Move: 'Taşınacak', Copy: 'Yeni', Update: 'Değişti', Delete: 'Silinecek', DeleteFolder: 'Silinecek', FixCase: 'Ad düzeltme' }

function planDialog(d: Disk, plan: SyncPlanResult): Promise<boolean> {
  const s = plan.summary
  const fits = s.bytes <= plan.free
  return mountDialog<boolean>(done => (
    <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && done(false)}>
      <div className="modal wide">
        <h3 className="modal-h">{d.name} ile eşitle</h3>
        {s.copy + s.update + s.delete + s.move + s.createFolders === 0 ? (
          <p className="ok-text">Her şey zaten eşit, değişiklik yok.</p>
        ) : (
          <>
            <div className="plan-stats">
              <div><b>{s.copy}</b><span>yeni dosya</span></div>
              {s.move > 0 && <div><b>{s.move}</b><span>taşınacak</span></div>}
              <div><b>{s.update}</b><span>değişen</span></div>
              <div><b>{s.delete}</b><span>silinecek*</span></div>
              <div><b>{formatSize(s.bytes)}</b><span>yazılacak</span></div>
            </div>
            <p className={`small ${fits ? 'muted' : 'err-text'}`}>Diskte {formatSize(plan.free)} boş.{!fits && ' Yer yetmiyor!'}</p>
            <ul className="plan-list">
              {plan.actions.map((a, i) => <li key={i}><span className={`tag ${a.kind}`}>{kindText[a.kind] ?? a.kind}</span> {a.kind === 'Move' ? `${a.targetPath} → ${a.path}` : a.path}</li>)}
            </ul>
            <p className="muted small">* Silinen ve değişen dosyaların eski hâli diskte <code>.homevault-trash</code> klasöründe 30 gün saklanır.</p>
          </>
        )}
        {plan.skipped.length > 0 && <p className="small err-text">{plan.skipped.length} öğe atlanacak: {plan.skipped.slice(0, 3).join(', ')}</p>}
        <p className="muted small">Sonra: {afterText[d.after ?? 'PowerOff']}.</p>
        <div className="modal-actions">
          {s.copy + s.update + s.delete + s.move + s.createFolders === 0 ? (
            <button className="btn primary" onClick={() => done(false)} autoFocus>Tamam</button>
          ) : (
            <>
              <button className="btn" onClick={() => done(false)}>Vazgeç</button>
              <button className="btn primary" disabled={!fits} onClick={() => done(true)} autoFocus>Eşitlemeyi başlat</button>
            </>
          )}
        </div>
      </div>
    </div>
  ))
}

function SettingsBody({ d, done }: { d: Disk; done: (v: boolean) => void }) {
  const [folders, setFolders] = useState<FileEntry[] | null>(null)
  const [chosen, setChosen] = useState(new Set(d.folders))
  const [name, setName] = useState(d.name)
  const [enabled, setEnabled] = useState(d.schedule?.enabled ?? false)
  const [time, setTime] = useState(d.schedule?.time ?? '03:00')
  const [after, setAfter] = useState<AfterSync>(d.after ?? 'PowerOff')
  const [saving, setSaving] = useState(false)
  useEffect(() => { api.list('').then(l => setFolders(l.entries.filter(e => e.kind === 'Folder' && !e.hidden))).catch(toastError) }, [])

  const total = (folders ?? []).filter(f => chosen.has(f.path)).reduce((s, f) => s + (f.size ?? 0), 0)
  const capacity = d.usage?.total ?? d.size
  const save = async () => {
    setSaving(true)
    try {
      await api.updateDisk(d.id, { name, folders: [...chosen], schedule: { enabled, time, lastRun: null }, after })
      toast('Ayarlar kaydedildi')
      done(true)
    } catch (e) { toastError(e) }
    setSaving(false)
  }
  const forget = async () => {
    if (!(await confirmDialog(`${d.name} yedek diski listesinden çıkarılacak. Diskteki dosyalar SİLİNMEZ.`, 'Listeden çıkar'))) return
    try { await api.forgetDisk(d.id); done(true) } catch (e) { toastError(e) }
  }
  return (
    <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && done(false)}>
      <div className="modal wide">
        <h3 className="modal-h">{d.name} · ayarlar</h3>
        <label className="field">Adı<input className="input" value={name} onChange={e => setName(e.target.value)} /></label>
        <div className="field">
          <span>Bu diske yedeklenecek klasörler</span>
          {!folders ? <span className="spinner" /> : (
            <ul className="check-list">
              {folders.map(f => (
                <li key={f.path}>
                  <label>
                    <input type="checkbox" checked={chosen.has(f.path)} onChange={e => setChosen(prev => { const n = new Set(prev); if (e.target.checked) n.add(f.path); else n.delete(f.path); return n })} />
                    <FileIcon entry={f} size={16} /> <span className="grow">{f.name}</span> <span className="muted">{formatSize(f.size)}</span>
                  </label>
                </li>
              ))}
            </ul>
          )}
          <span className={`small ${total > capacity ? 'err-text' : 'muted'}`}>Seçilen: {formatSize(total)} / disk {formatSize(capacity)}{total > capacity ? ' — sığmaz!' : ''}</span>
        </div>
        <div className="field">
          <label className="inline"><input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} /> Her gün otomatik eşitle, saat</label>
          <input className="input narrow" type="time" value={time} disabled={!enabled} onChange={e => setTime(e.target.value)} />
        </div>
        <label className="field">Eşitleme bitince
          <select className="input" value={after} onChange={e => setAfter(e.target.value as AfterSync)}>
            {Object.entries(afterText).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
        </label>
        <div className="modal-actions">
          <button className="btn link-danger" onClick={forget}>Listeden çıkar</button>
          <span className="grow" />
          <button className="btn" onClick={() => done(false)}>Vazgeç</button>
          <button className="btn primary" disabled={saving} onClick={save}>Kaydet</button>
        </div>
      </div>
    </div>
  )
}

const settingsDialog = (d: Disk) => mountDialog<boolean>(done => <SettingsBody d={d} done={done} />)

function HistoryBody({ d, done }: { d: Disk; done: () => void }) {
  const [list, setList] = useState<SyncReport[] | null>(null)
  useEffect(() => { api.syncHistory(d.id).then(setList).catch(toastError) }, [d.id])
  return (
    <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && done()}>
      <div className="modal wide">
        <h3 className="modal-h">{d.name} · eşitleme geçmişi</h3>
        {!list ? <span className="spinner" /> : list.length === 0 ? <p className="muted">Henüz eşitleme yok.</p> : (
          <table className="details history">
            <thead><tr><th>Tarih</th><th>Sonuç</th><th className="c-size">Yazılan</th></tr></thead>
            <tbody>
              {list.map(r => (
                <tr key={r.started}>
                  <td>{formatDate(r.started)}</td>
                  <td className={r.success ? 'ok-text' : 'err-text'}>
                    {r.canceled ? 'Durduruldu' : r.success ? 'Başarılı' : `${r.errors.length} hata`} · {r.copied}+{r.updated} kopya, {r.verified} doğrulandı, {r.deleted} silindi
                  </td>
                  <td className="c-size">{formatSize(r.bytesCopied)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        <div className="modal-actions"><button className="btn" onClick={done}>Kapat</button></div>
      </div>
    </div>
  )
}

const historyDialog = (d: Disk) => mountDialog<void>(done => <HistoryBody d={d} done={() => done()} />)



function HealthBody({ d, done }: { d: Disk; done: () => void }) {
  const [info, setInfo] = useState<SmartInfo | null>(null)
  useEffect(() => { api.smart(d.id).then(setInfo).catch(e => { toastError(e); done() }) }, [d.id, done])
  const bad = !!info && (info.passed === false || (info.pending ?? 0) > 0 || (info.reallocated ?? 0) > 0 || (info.uncorrectable ?? 0) > 0)
  return (
    <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && done()}>
      <div className="modal wide">
        <h3 className="modal-h">{d.name} · disk sağlığı</h3>
        {!info ? <span className="spinner" /> : !info.available ? (
          <p className="muted">{d.kind === 'virtual' ? 'Bu bir sanal test diski, sağlık bilgisi yok.' : 'Bu USB kutusu disk sağlık bilgisini vermiyor. Bazı kutular bunu desteklemez.'}</p>
        ) : (
          <>
            <p className={bad ? 'err-text' : 'ok-text'}>
              {bad ? <AlertTriangle size={16} /> : <CheckCircle2 size={16} />}{' '}
              {bad ? 'Dikkat: disk hata belirtileri gösteriyor. Önemli dosyaların başka bir yerde de kopyası olsun.' : 'Disk sağlıklı görünüyor.'}
            </p>
            <dl className="pv-meta health">
              <dt>Genel sağlık testi</dt><dd>{info.passed == null ? '—' : info.passed ? 'Geçti' : 'BAŞARISIZ'}</dd>
              <dt>Sıcaklık</dt><dd>{info.temperature != null ? `${info.temperature} °C` : '—'}</dd>
              <dt>Çalışma süresi</dt><dd>{info.powerOnHours != null ? `${info.powerOnHours.toLocaleString('tr-TR')} saat (~${Math.round(info.powerOnHours / 24 / 30)} ay)` : '—'}</dd>
              <dt>Yer değiştiren sektör</dt><dd>{info.reallocated ?? '—'}</dd>
              <dt>Bekleyen bozuk sektör</dt><dd>{info.pending ?? '—'}</dd>
              <dt>Düzeltilemeyen hata</dt><dd>{info.uncorrectable ?? '—'}</dd>
            </dl>
            <p className="muted small">Sıfırdan büyük sektör değerleri diskin yıprandığını gösterir. Artıyorsa diski değiştirmeyi düşün.</p>
          </>
        )}
        <div className="modal-actions"><button className="btn" onClick={done}>Kapat</button></div>
      </div>
    </div>
  )
}

const healthDialog = (d: Disk) => mountDialog<void>(done => <HealthBody d={d} done={() => done()} />)
