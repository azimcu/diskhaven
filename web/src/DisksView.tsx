import { useCallback, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import {
  Activity, AlertTriangle, CheckCircle2, ChevronUp, Clock, FolderOpen, FolderSync, HardDrive,
  History, Lock, Pencil, Play, Plus, RefreshCw, Settings2, ShieldCheck, Trash2, Unlock, Usb, X,
} from 'lucide-react'
import { api } from './api'
import { confirmDialog, promptDialog, toast, toastError } from './dialogs'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize } from './format'
import type {
  AfterSync, BackupBody, BackupJob, BackupRun, Disk, DriveInfo, FileEntry, SmartInfo, SyncReport,
} from './types'

const stateText = (d: Disk) =>
  d.state === 'off' ? (d.kind === 'usb' ? 'Kapalı · çıkarılabilir' : 'Görünmüyor') : d.power === 'standby' ? 'Uykuda'
    : d.state === 'mounted' ? (d.mountedReadOnly ? 'Açık · salt okunur bağlı' : 'Açık · yazılabilir bağlı') : 'Takılı · bağlı değil'

const systemText: Record<string, string> = {
  mounted: 'Sunucu bu diski kullanıyor',
  fstab: 'Sunucunun disk listesinde (fstab) kayıtlı',
  protected: 'Korunan diskler listesinde',
  'not-allowed': 'Bu tür diskler DiskHaven ayarlarında kapalı',
}

const afterText: Record<AfterSync, string> = {
  PowerOff: 'Diski kapat (kablo güvenle çıkarılabilir)',
  Standby: 'Diski uyut',
  StayOn: 'Açık bırak',
}

const phaseText: Record<string, string> = {
  prepare: 'Disk hazırlanıyor', plan: 'Değişiklikler hesaplanıyor', copy: 'Kopyalanıyor ve doğrulanıyor',
  'copy-hidden': 'Gizli klasör kopyalanıyor', approve: 'Onay bekliyor', cleanup: 'Temizleniyor',
  finish: 'Disk kapatılıyor', done: 'Bitti',
}

const driveName = (drives: DriveInfo[], id: string) =>
  id === '' ? 'Ana depolama' : drives.find(d => d.id === id)?.name ?? id

/** "This PC"-like page: disks, their state, and the backup jobs that mirror folders between them. */
export function DisksView({ onChanged }: { onChanged: () => void }) {
  const [data, setData] = useState<{ agent: boolean; demo?: boolean; disks: Disk[] } | null>(null)
  const [backups, setBackups] = useState<{ jobs: BackupJob[]; job: BackupRun | null }>({ jobs: [], job: null })
  const [drives, setDrives] = useState<DriveInfo[]>([])
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const d = await api.disks()
      setData(d)
      if (d.demo) return
      const [b, dr] = await Promise.all([
        api.backups().catch(() => ({ jobs: [] as BackupJob[], job: null })),
        api.drives().catch(() => [] as DriveInfo[]),
      ])
      setBackups(b)
      setDrives(dr)
    } catch (e) { toastError(e) }
  }, [])

  useEffect(() => {
    void load()
    const timer = setInterval(load, backups.job?.status === 'Running' ? 1000 : 4000)
    return () => clearInterval(timer)
  }, [load, backups.job?.status])

  const act = async (key: string, fn: () => Promise<unknown>, done?: string) => {
    setBusy(key)
    try { await fn(); if (done) toast(done) } catch (e) { toastError(e) }
    setBusy(null)
    void load()
    onChanged()
  }

  const setReadOnly = async (d: Disk, readOnly: boolean) => {
    if (!readOnly && !(await confirmDialog(
      `${d.name} yazılabilir yapılacak. DiskHaven bu diske dosya yazabilecek, silebilecek ve taşıyabilecek (silinenler diskin kendi çöp kutusunda 30 gün durur). Diskteki mevcut dosyalara şimdi hiçbir şey yapılmaz. Emin misin?`,
      'Yazılabilir yap'))) return
    await act(d.id, () => api.setReadOnly(d.id, readOnly), readOnly ? `${d.name} artık salt okunur` : `${d.name} artık yazılabilir`)
  }

  const browse = async (d: Disk) => {
    setBusy(d.id)
    try { await api.openDrive(d.id); onChanged(); location.hash = `#/d/${encodeURIComponent(d.id)}` } catch (e) { toastError(e) }
    setBusy(null)
  }

  const toggle = (d: Disk) =>
    d.state === 'off'
      ? act(d.id, () => api.diskAction(d.id, 'power-on'), `${d.name} açıldı`)
      : act(d.id, () => api.diskAction(d.id, 'power-off'), `${d.name} kapatıldı, kabloyu çıkarabilirsin`)

  const adopt = async (d: Disk) => {
    const name = await promptDialog('Yedek diskin adı (ör. "Yedek 2TB")', d.label || d.model || 'Yedek disk')
    if (!name) return
    await act(d.id, () => api.adoptDisk(d.id, name), `${name} artık DiskHaven yedek diski. Diskteki mevcut dosyalara dokunulmadı.`)
  }

  const runBackup = (job: BackupJob, approve = false) =>
    act(job.id, () => api.runBackup(job.id, approve), approve ? 'Onay verildi, yedek başladı' : 'Yedek başladı')

  const removeBackup = async (job: BackupJob) => {
    if (!(await confirmDialog(
      `"${job.name}" yedek işi silinecek. Disklerdeki dosyalar SİLİNMEZ; sadece bu iş ve geçmişi kaldırılır.`,
      'Yedeği sil'))) return
    await act(job.id, () => api.deleteBackup(job.id), 'Yedek işi silindi')
  }

  if (!data) return <div className="main-col"><div className="loading"><span className="spinner" /></div></div>
  const job = backups.job

  return (
    <div className="main-col">
      <div className="commandbar">
        <button className="cmd" onClick={load}><RefreshCw size={16} /> <span>Yenile</span></button>
      </div>
      <div className="addressbar"><div className="crumbs"><span className="crumb current">Diskler</span></div></div>
      <div className="list-wrap disks-page">
        {data.demo && <p className="empty-note">Demoda gerçek disk yok. Gerçek kurulumda USB diskler burada görünür: bağla, ayır, uyut, SMART sağlığına bak ve yedek işleri kur.</p>}
        {!data.agent && !data.demo && <p className="empty-note error">Disk servisi çalışmıyor. Sunucuda <code>diskhaven-diskd</code> servisini başlat.</p>}
        {job && (job.status === 'Running' || job.status === 'AwaitingApproval' || Date.now() - new Date(job.started).getTime() < 10 * 60_000) &&
          <JobCard job={job} onApprove={() => backups.jobs.find(j => j.id === job.jobId) && runBackup(backups.jobs.find(j => j.id === job.jobId)!, true)} />}
        {data.agent && data.disks.filter(d => !d.system).length === 0 && <p className="empty-note">DiskHaven'in kullanabileceği bir disk takılı değil.</p>}
        <p className="muted small disks-note"><Lock size={13} /> Yeni takılan her disk <b>salt okunur</b> kaydedilir: DiskHaven o diske, sen yazılabilir yapana kadar tek bayt yazmaz (Linux da diski salt okunur bağlar).</p>
        <div className="disk-grid">
          {data.disks.filter(d => !d.system).map(d => (
            <DiskCard key={d.id} disk={d} busy={busy === d.id}
              backupCount={backups.jobs.filter(j => j.targetDrive === d.id || j.sourceDrive === d.id).length}
              onToggle={() => toggle(d)} onAdopt={() => adopt(d)}
              onSettings={async () => { if (await settingsDialog(d)) void load() }}
              onReadOnly={ro => setReadOnly(d, ro)} onBrowse={() => browse(d)} />
          ))}
        </div>

        {!data.demo && (
          <>
            <h3 className="section-h"><FolderSync size={16} /> Yedekler</h3>
            <p className="muted small">
              Her yedek <b>tek yönlüdür</b>: seçtiğin klasörler kaynak diskten hedef diske yansıtılır; kaynak hiç değişmez.
              Hedefte değişen ya da silinen dosyalar önce <b>hedef diskin kendi çöp kutusuna</b> gider, 30 gün sonra silinir.
              Hedefteki dosyaların yarısından fazlası silinecekse iş durur ve onayını bekler.
            </p>
            <div className="disk-actions row">
              <button className="btn primary" onClick={async () => { if (await backupDialog(null, drives)) void load() }}>
                <Plus size={16} /> Yedek ekle
              </button>
            </div>
            {backups.jobs.length === 0
              ? <p className="empty-note">Henüz yedek işi yok. "Yedek ekle" ile ilk yedeğini kur.</p>
              : (
                <div className="disk-grid">
                  {backups.jobs.map(j => (
                    <BackupCard key={j.id} job={j} drives={drives} busy={busy === j.id || job?.status === 'Running' && job.jobId === j.id}
                      onRun={() => runBackup(j)} onApprove={() => runBackup(j, true)} onDelete={() => removeBackup(j)}
                      onEdit={async () => { if (await backupDialog(j, drives)) void load() }}
                      onHistory={() => historyDialog(j, drives)} />
                  ))}
                </div>
              )}
          </>
        )}

        {data.disks.some(d => d.system) && (
          <>
            <h3 className="section-h"><ShieldCheck size={16} /> Sistem diskleri · DiskHaven dokunmaz</h3>
            <p className="muted small">Sunucunun kendi kullandığı diskler (sistem, fotoğraflar, sunucu yedeği). Sadece gösterilir: bağlama, ayırma, uyutma, yazma yapılmaz. Sağlık bilgisi okunabilir.</p>
            <div className="disk-grid">
              {data.disks.filter(d => d.system).map(d => <SystemCard key={d.id} disk={d} />)}
            </div>
          </>
        )}
      </div>
    </div>
  )
}

const busText: Record<string, string> = { usb: 'USB', sata: 'SATA (kasa içi)', nvme: 'NVMe', virtual: 'sanal test diski' }

function SystemCard({ disk: d }: { disk: Disk }) {
  return (
    <section className="disk-card system">
      <header className="disk-head">
        <ShieldCheck size={28} className="ic disk-ic" />
        <div className="disk-title">
          <strong>{d.systemMount ?? (d.name || 'Sistem diski')}</strong>
          <span className="muted">{[d.label, d.fsType && d.fsType.toUpperCase(), formatSize(d.size), busText[d.kind], d.model].filter(Boolean).join(' · ')}</span>
        </div>
      </header>
      <div className="disk-state"><Lock size={14} /> {systemText[d.systemReason ?? ''] ?? 'Sistem diski'} · dokunulmaz</div>
      {d.kind !== 'virtual' && <div className="disk-actions row"><button className="btn" onClick={() => healthDialog(d)}><Activity size={16} /> Sağlık</button></div>}
    </section>
  )
}

function DiskCard({ disk: d, busy, backupCount, onToggle, onAdopt, onSettings, onReadOnly, onBrowse }: {
  disk: Disk; busy: boolean; backupCount: number; onToggle: () => void; onAdopt: () => void
  onSettings: () => void; onReadOnly: (readOnly: boolean) => void; onBrowse: () => void
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
          <span className="muted">{[d.label !== d.name && d.label, d.fsType.toUpperCase(), formatSize(d.size), busText[d.kind], d.kind !== 'virtual' && d.model].filter(Boolean).join(' · ')}</span>
        </div>
        {d.kind !== 'sata' && (
          <button className={`switch ${on ? 'on' : ''}`} role="switch" aria-checked={on} disabled={busy} onClick={onToggle}
            title={on ? 'Diski kapat (güvenle çıkar)' : 'Diski aç'}>
            <span className="knob" />
          </button>
        )}
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
      <div className={`ro-row ${d.readOnly ? 'ro' : 'rw'}`}>
        {d.readOnly ? <Lock size={16} /> : <Unlock size={16} />}
        <span className="grow">{d.readOnly ? <><b>Salt okunur</b> · DiskHaven bu diske hiçbir şey yazmaz</> : <><b>Yazılabilir</b> · DiskHaven bu diske yazabilir</>}</span>
        <button className="btn small" disabled={busy || d.state === 'off'} onClick={() => onReadOnly(!d.readOnly)}>
          {d.readOnly ? 'Yazılabilir yap' : 'Salt okunur yap'}
        </button>
      </div>
      {on && <div className="disk-actions row"><button className="btn" disabled={busy} onClick={onBrowse}><FolderOpen size={16} /> Gözat</button></div>}
      {d.foreign && <p className="disk-warn"><AlertTriangle size={16} /> Bu diskteki kimlik dosyası ayarlardaki diskle uyuşmuyor. Yedek yazılmaz.</p>}

      {!d.adopted ? (
        <div className="disk-actions">
          <p className="muted small">{d.readOnly
            ? 'Yedek diski yapmak için önce diski yazılabilir yap. Salt okunur bir disk yine de gezilebilir ve başka bir diske kopyalanabilir.'
            : 'Bu disk henüz yedek diski değil. Yedek diski yaptığında içindeki dosyalara dokunulmaz; sadece yedek işlerinin seçtiği klasörler yansıtılır.'}</p>
          <div className="disk-actions row">
            <button className="btn primary" disabled={busy || d.readOnly} onClick={onAdopt}>Yedek diski yap</button>
            {health}
          </div>
        </div>
      ) : (
        <>
          <div className="disk-meta">
            <span className="muted"><FolderSync size={14} /> {backupCount === 0 ? 'Bu diskte yedek işi yok' : `${backupCount} yedek işinde kullanılıyor`}</span>
          </div>
          <div className="disk-actions row">
            <button className="btn" onClick={onSettings}><Settings2 size={16} /> Ayarlar</button>
            {health}
          </div>
        </>
      )}
    </section>
  )
}

function BackupCard({ job, drives, busy, onRun, onApprove, onDelete, onEdit, onHistory }: {
  job: BackupJob; drives: DriveInfo[]; busy: boolean
  onRun: () => void; onApprove: () => void; onDelete: () => void; onEdit: () => void; onHistory: () => void
}) {
  const r = job.lastRun
  const waiting = r?.awaitingApproval === true
  return (
    <section className={`disk-card ${busy ? 'busy' : ''}`}>
      <header className="disk-head">
        <FolderSync size={28} className="ic disk-ic" />
        <div className="disk-title">
          <strong>{job.name}</strong>
          <span className="muted">
            {driveName(drives, job.sourceDrive)} · {job.sourceFolders.join(', ')} → {driveName(drives, job.targetDrive)}
            {job.targetFolder ? `/${job.targetFolder}` : ' (kök)'}
          </span>
        </div>
      </header>
      <div className="disk-meta">
        {job.enabled ? <span className="muted"><Clock size={14} /> Her gün {job.time}</span> : <span className="muted">Elle çalıştırılır</span>}
        <span className="muted">· Yedek yönü: tek yön</span>
      </div>
      {r ? (
        <p className={`small ${waiting ? 'err-text' : r.success ? '' : 'err-text'}`}>
          {waiting ? <AlertTriangle size={14} /> : r.success ? <CheckCircle2 size={14} /> : <AlertTriangle size={14} />}{' '}
          Son çalışma {formatDate(r.finished)}
          {waiting ? ' · silme onayı bekliyor'
            : r.success ? ` · ${r.copied + r.updated} dosya yazıldı, ${r.deleted} öğe hedefin çöpüne gitti`
            : ` · ${r.message ?? (r.canceled ? 'durduruldu' : 'başarısız')}`}
          {r.errors > 0 && <span className="err-text"> · {r.errors} hata</span>}
        </p>
      ) : <p className="muted small">Henüz çalıştırılmadı.</p>}
      {waiting && r?.message && <p className="muted small">{r.message}</p>}
      <div className="disk-actions row">
        {waiting
          ? <button className="btn primary" disabled={busy} onClick={onApprove}><CheckCircle2 size={16} /> Onayla ve çalıştır</button>
          : <button className="btn primary" disabled={busy} onClick={onRun}><Play size={16} /> Şimdi çalıştır</button>}
        <button className="btn" disabled={busy} onClick={onEdit}><Pencil size={16} /> Düzenle</button>
        <button className="btn" onClick={onHistory}><History size={16} /> Geçmiş</button>
        <button className="btn link-danger" disabled={busy} onClick={onDelete}><Trash2 size={16} /> Sil</button>
      </div>
    </section>
  )
}

function JobCard({ job, onApprove }: { job: BackupRun; onApprove: () => void }) {
  const p = job.progress
  const pct = p && p.bytesTotal > 0 ? (p.bytesDone / p.bytesTotal) * 100 : p && p.filesTotal ? (p.filesDone / p.filesTotal) * 100 : 0
  const r = job.report
  const icon = job.status === 'Running' || job.status === 'AwaitingApproval'
    ? <span className="spinner" />
    : job.status === 'Done' ? <CheckCircle2 size={20} className="ic ok" /> : <AlertTriangle size={20} className="ic err" />
  return (
    <section className={`job-card ${job.status.toLowerCase()}`}>
      <header>
        {icon}
        <strong>{job.jobName}{job.scheduled ? ' · zamanlanmış yedek' : ''}</strong>
        {job.status === 'Running' && <button className="btn" onClick={() => api.cancelBackup()}><X size={14} /> Durdur</button>}
        {job.status === 'AwaitingApproval' && <button className="btn primary" onClick={onApprove}><CheckCircle2 size={14} /> Onayla</button>}
      </header>
      {job.status === 'Running' && (
        <>
          <div className="muted small">{phaseText[job.phase] ?? job.phase}{p?.current ? ` · ${p.current}` : ''}</div>
          {p && <div className="bar big"><div style={{ width: `${pct}%` }} /></div>}
          {p && <div className="muted small">{p.filesDone}/{p.filesTotal} işlem · {formatSize(p.bytesDone)} / {formatSize(p.bytesTotal)}</div>}
        </>
      )}
      {job.status === 'AwaitingApproval' && <p className="small err-text">{job.message}</p>}
      {r && (
        <p className="small">
          {r.canceled ? 'Durduruldu. ' : ''}
          {r.copied} yeni, {r.updated} değişen dosya kopyalandı{r.moved > 0 ? `, ${r.moved} dosya taşındı (tekrar kopyalanmadı)` : ''} · <b>{r.verified} dosya diskten geri okunup doğrulandı</b> · {r.deleted} öğe hedefin çöp kutusuna taşındı
          {r.errors.length > 0 && <span className="err-text"> · {r.errors.length} hata</span>}
          {r.skipped.length > 0 && <span className="muted"> · {r.skipped.length} atlandı</span>}
        </p>
      )}
      {job.error && <p className="err-text small">{job.error}</p>}
      {r && r.renamed && r.renamed.length > 0 && <p className="muted small">{r.renamed.length} dosya hedef diskte farklı adla saklandı: {r.renamed.slice(0, 3).join(', ')}</p>}
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

/** Top-level folders of a drive (main storage when the id is ""), with a way to step into subfolders. */
function FolderPicker({ drive, chosen, onChange }: { drive: string; chosen: string[]; onChange: (folders: string[]) => void }) {
  const [path, setPath] = useState('')
  const [entries, setEntries] = useState<FileEntry[] | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    setEntries(null)
    setError('')
    api.list(path, drive || undefined)
      .then(l => setEntries(l.entries.filter(e => e.kind === 'Folder' && !e.hidden)))
      .catch(e => { setError(e instanceof Error ? e.message : String(e)); setEntries([]) })
  }, [path, drive])
  const toggle = (p: string) => onChange(chosen.includes(p) ? chosen.filter(f => f !== p) : [...chosen, p])
  const up = path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : ''
  return (
    <div className="folder-picker">
      <div className="muted small folder-path">
        <button className="btn small" onClick={() => setPath('')}>Kök</button>
        {path && <button className="btn small" onClick={() => setPath(up)}><ChevronUp size={12} /> Yukarı</button>}
        <span>/{path}</span>
      </div>
      {error && <p className="err-text small">{error}</p>}
      {!entries ? <span className="spinner" /> : entries.length === 0 && !error ? <p className="muted small">Burada klasör yok.</p> : (
        <ul className="check-list">
          {entries.map(f => (
            <li key={f.path}>
              <label>
                <input type="checkbox" checked={chosen.includes(f.path)} onChange={() => toggle(f.path)} />
                <FileIcon entry={f} size={16} /> <span className="grow">{f.name}</span>
              </label>
              <button className="btn small" onClick={() => setPath(f.path)}>Aç</button>
            </li>
          ))}
        </ul>
      )}
      {chosen.length > 0 && (
        <div className="disk-folders">
          {chosen.map(f => <span key={f} className="chip">{f} <button className="chip-x" onClick={() => toggle(f)}><X size={12} /></button></span>)}
        </div>
      )}
    </div>
  )
}

function BackupBody({ job, drives, done }: {
  job: BackupJob | null; drives: DriveInfo[]; done: (v: boolean) => void
}) {
  const [name, setName] = useState(job?.name ?? '')
  const [sourceDrive, setSourceDrive] = useState(job?.sourceDrive ?? '')
  const [folders, setFolders] = useState<string[]>(job?.sourceFolders ?? [])
  const [targetDrive, setTargetDrive] = useState(job?.targetDrive ?? '')
  const [targetFolder, setTargetFolder] = useState(job?.targetFolder ?? '')
  const [enabled, setEnabled] = useState(job?.enabled ?? false)
  const [time, setTime] = useState(job?.time ?? '03:00')
  const [threshold, setThreshold] = useState(job?.deleteThresholdPercent ?? 50)
  const [adopt, setAdopt] = useState(job?.adoptExisting ?? false)
  const [saving, setSaving] = useState(false)
  const [targets, setTargets] = useState<FileEntry[]>([])

  const diskChoices = drives.filter(d => d.kind !== 'main' && !d.system)
  useEffect(() => {
    if (!targetDrive) { setTargets([]); return }
    api.list('', targetDrive).then(l => setTargets(l.entries.filter(e => e.kind === 'Folder' && !e.hidden))).catch(() => setTargets([]))
  }, [targetDrive])

  const body: BackupBody = {
    name: name.trim(), sourceDrive, sourceFolders: folders, targetDrive, targetFolder: targetFolder.trim(),
    enabled, time, deleteThresholdPercent: threshold, adoptExisting: adopt,
  }
  const sameDrive = sourceDrive === targetDrive
  const save = async () => {
    if (!body.name) return toast('Yedeğe bir ad ver')
    if (body.sourceFolders.length === 0) return toast('Yedeklenecek en az bir klasör seç')
    if (!body.targetDrive) return toast('Hedef disk seç')
    if (sameDrive) return toast('Kaynak ve hedef aynı disk olamaz')
    setSaving(true)
    try {
      if (job) await api.updateBackup(job.id, body)
      else await api.createBackup(body)
      toast(job ? 'Yedek güncellendi' : 'Yedek eklendi')
      done(true)
    } catch (e) { toastError(e) }
    setSaving(false)
  }
  return (
    <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && done(false)}>
      <div className="modal wide">
        <h3 className="modal-h">{job ? `${job.name} · yedeği düzenle` : 'Yeni yedek'}</h3>
        <label className="field">Yedeğin adı<input className="input" value={name} onChange={e => setName(e.target.value)} placeholder="ör. Belgeler → Yedek 2TB" /></label>

        <label className="field">Kaynak
          <select className="input" value={sourceDrive} onChange={e => { setSourceDrive(e.target.value); setFolders([]) }}>
            <option value="">Ana depolama</option>
            {diskChoices.map(d => <option key={d.id} value={d.id}>{d.name}{d.readOnly ? ' (salt okunur — yedek için sorun değil)' : ''}</option>)}
          </select>
        </label>
        <div className="field">
          <span>Yedeklenecek klasörler</span>
          <FolderPicker drive={sourceDrive} chosen={folders} onChange={setFolders} />
          <span className="muted small">Klasörün içine girip alt klasörleri de seçebilirsin. Seçilen bir klasörün altındakiler zaten onunla birlikte yedeklenir.</span>
        </div>

        <label className="field">Hedef disk
          <select className="input" value={targetDrive} onChange={e => setTargetDrive(e.target.value)}>
            <option value="">Seç…</option>
            {diskChoices.map(d => <option key={d.id} value={d.id}>{d.name}{d.readOnly ? ' (salt okunur — yedek yazılamaz)' : ''}</option>)}
          </select>
        </label>
        <label className="field">Hedef klasör
          <input className="input" list="backup-target-folders" value={targetFolder} onChange={e => setTargetFolder(e.target.value)}
            placeholder="boş = diskin kökü" />
          <datalist id="backup-target-folders">
            {targets.map(f => <option key={f.path} value={f.path} />)}
          </datalist>
          <span className="muted small">Yoksa ilk çalıştırmada oluşturulur. Bir klasör yalnızca bir yedeğe ait olabilir.</span>
        </label>
        <label className="inline"><input type="checkbox" checked={adopt} onChange={e => setAdopt(e.target.checked)} /> Hedef klasördeki mevcut dosyaları bu yedeğe sahiplen</label>
        <p className="muted small">Hedef klasör boş değilse ve daha önce bir yedeğe ait değilse bu seçenek olmadan hiçbir şey yazılmaz. Sahiplenirsen hedef, kaynağın aynası olur; kaynakta olmayan dosyalar hedefin çöp kutusuna gider.</p>

        <div className="field">
          <label className="inline"><input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} /> Her gün otomatik çalıştır, saat</label>
          <input className="input narrow" type="time" value={time} disabled={!enabled} onChange={e => setTime(e.target.value)} />
        </div>
        <label className="field">Hedefte en fazla yüzde kaç silinebilir (onay sormadan)
          <input className="input narrow" type="number" min={0} max={100} value={threshold}
            onChange={e => setThreshold(Math.max(0, Math.min(100, Number(e.target.value))))} />
        </label>
        <p className="muted small">Bu oran aşılırsa yedek hiçbir şey silmez, durur ve onayını bekler.</p>

        <div className="modal-actions">
          <button className="btn" onClick={() => done(false)}>Vazgeç</button>
          <button className="btn primary" disabled={saving} onClick={save}>Kaydet</button>
        </div>
      </div>
    </div>
  )
}

const backupDialog = (job: BackupJob | null, drives: DriveInfo[]) =>
  mountDialog<boolean>(done => <BackupBody job={job} drives={drives} done={done} />)

function SettingsBody({ d, done }: { d: Disk; done: (v: boolean) => void }) {
  const [name, setName] = useState(d.name)
  const [after, setAfter] = useState<AfterSync>(d.after ?? 'PowerOff')
  const [saving, setSaving] = useState(false)
  const save = async () => {
    setSaving(true)
    try {
      await api.updateDisk(d.id, { name, after })
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
        <label className="field">Yedek bitince
          <select className="input" value={after} onChange={e => setAfter(e.target.value as AfterSync)}>
            {Object.entries(afterText).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
        </label>
        <p className="muted small">Hangi klasörlerin yedekleneceği artık "Yedekler" bölümünde ayarlanır; bir diskte birden çok yedek işi olabilir.</p>
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

function HistoryBody({ job, drives, done }: { job: BackupJob; drives: DriveInfo[]; done: () => void }) {
  const [list, setList] = useState<SyncReport[] | null>(null)
  useEffect(() => { api.backupHistory(job.id).then(setList).catch(toastError) }, [job.id])
  return (
    <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && done()}>
      <div className="modal wide">
        <h3 className="modal-h">{job.name} · yedek geçmişi</h3>
        <p className="muted small">{driveName(drives, job.sourceDrive)} → {driveName(drives, job.targetDrive)}{job.targetFolder ? `/${job.targetFolder}` : ''}</p>
        {!list ? <span className="spinner" /> : list.length === 0 ? <p className="muted">Henüz çalıştırılmadı.</p> : (
          <table className="details history">
            <thead><tr><th>Tarih</th><th>Sonuç</th><th className="c-size">Yazılan</th></tr></thead>
            <tbody>
              {list.map(r => (
                <tr key={r.started}>
                  <td>{formatDate(r.started)}</td>
                  <td className={r.success ? 'ok-text' : 'err-text'}>
                    {r.canceled ? 'Durduruldu' : r.success ? 'Başarılı' : `${r.errors.length} hata`} · {r.copied}+{r.updated} kopya, {r.verified} doğrulandı, {r.deleted} hedefin çöpüne
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

const historyDialog = (job: BackupJob, drives: DriveInfo[]) => mountDialog<void>(done => <HistoryBody job={job} drives={drives} done={() => done()} />)

function HealthBody({ d, done }: { d: Disk; done: () => void }) {
  const [info, setInfo] = useState<SmartInfo | null>(null)
  useEffect(() => { api.smart(d.id).then(setInfo).catch(e => { toastError(e); done() }) }, [d.id, done])
  const bad = !!info && (info.passed === false || (info.pending ?? 0) > 0 || (info.reallocated ?? 0) > 0 || (info.uncorrectable ?? 0) > 0)
  return (
    <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && done()}>
      <div className="modal wide">
        <h3 className="modal-h">{d.name} · disk sağlığı</h3>
        {!info ? <span className="spinner" /> : !info.available ? (
          <p className="muted">{d.kind === 'virtual' ? 'Bu bir sanal test diski, sağlık bilgisi yok.' : d.kind === 'usb' ? 'Bu USB kutusu disk sağlık bilgisini vermiyor. Bazı kutular bunu desteklemez.' : 'Disk sağlık bilgisi okunamadı (disk uykuda olabilir).'}</p>
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
