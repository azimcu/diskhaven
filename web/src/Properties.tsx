import { useEffect, useState } from 'react'
import { api } from './api'
import { confirmDialog, Modal, mount, toast, toastError } from './dialogs'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize, parentOf, typeName } from './format'
import { t } from './strings'
import { usePlugins } from './plugins'
import type { FileEntry, FileVersion, ItemProperties } from './types'

const bytes = (n: number) => `${formatSize(n)} (${n.toLocaleString('tr-TR')} bayt)`

/** Explorer's "Properties" (Alt+Enter): General tab with type, location, size, dates and the Hidden attribute. */
export function propertiesDialog(entry: FileEntry, onChanged: () => void) {
  return mount<void>(done => <PropertiesDialog entry={entry} onClose={() => { done(); onChanged() }} />)
}

function PropertiesDialog({ entry, onClose }: { entry: FileEntry; onClose: () => void }) {
  const [p, setP] = useState<ItemProperties | null>(null)
  const [hidden, setHidden] = useState(entry.hidden)
  const [sha, setSha] = useState<string | null>(null)
  const [hashing, setHashing] = useState(false)
  const { has } = usePlugins()
  useEffect(() => { api.properties(entry.path).then(r => { setP(r); setHidden(r.entry.hidden) }).catch(toastError) }, [entry.path])

  const e = p?.entry ?? entry
  const folder = e.kind === 'Folder'
  const dotName = e.name.startsWith('.')
  const toggleHidden = async (v: boolean) => {
    setHidden(v)   // the checkbox answers at once; the server follows
    try { await api.setHidden([e.path], v) } catch (err) { setHidden(!v); toastError(err) }
  }
  const hash = async () => {
    setHashing(true)
    try { setSha((await api.checksum(e.path)).sha256) } catch (err) { toastError(err) }
    setHashing(false)
  }

  return (
    <Modal onClose={onClose}>
      <div className="props">
        <div className="props-head"><FileIcon entry={e} size={36} /><b title={e.name}>{e.name}</b></div>
        <dl>
          <dt>Tür</dt><dd>{typeName(e)}</dd>
          <dt>Konum</dt><dd>/{parentOf(e.path)}</dd>
          <dt>Boyut</dt><dd>{e.size == null ? (folder ? t.calculating : '') : bytes(e.size)}</dd>
          {!folder && p?.sizeOnDisk != null && <><dt>Diskteki boyutu</dt><dd>{bytes(p.sizeOnDisk)}</dd></>}
          {folder && e.files != null && <><dt>İçerik</dt><dd>{t.contents(e.files, e.folders ?? 0)}</dd></>}
        </dl>
        <hr />
        <dl>
          {p?.created && <><dt>Oluşturma</dt><dd>{formatDate(p.created)}</dd></>}
          <dt>Değiştirme</dt><dd>{formatDate(e.modified)}</dd>
          {p && <><dt>Erişim</dt><dd>{formatDate(p.accessed)}</dd></>}
        </dl>
        <hr />
        <dl>
          <dt>Öznitelikler</dt>
          <dd>
            <label className="check"><input type="checkbox" checked={hidden} disabled={dotName} onChange={ev => toggleHidden(ev.target.checked)} /> Gizli</label>
            {dotName && <small className="muted"> (adı noktayla başladığı için her zaman gizli)</small>}
          </dd>
          {!folder && <>
            <dt>SHA-256</dt>
            <dd>{sha ? <code className="hash" onClick={() => navigator.clipboard?.writeText(sha)} title="Kopyalamak için tıkla">{sha}</code>
              : <button className="btn small" disabled={hashing} onClick={hash}>{hashing ? 'Hesaplanıyor…' : 'Hesapla'}</button>}</dd>
          </>}
        </dl>
        {!folder && has('versions') && <Versions path={e.path} onRestored={onClose} />}
        <div className="modal-actions"><button className="btn primary" autoFocus onClick={onClose}>Tamam</button></div>
      </div>
    </Modal>
  )
}

/** "Previous versions": earlier states kept when the file was replaced by an upload. */
function Versions({ path, onRestored }: { path: string; onRestored: () => void }) {
  const [list, setList] = useState<FileVersion[] | null>(null)
  useEffect(() => { api.versions(path).then(setList).catch(() => setList([])) }, [path])
  const restore = async (v: FileVersion) => {
    if (!(await confirmDialog(`${formatDate(v.savedAt)} tarihli sürüm geri yüklensin mi? Şu anki hâli de önceki sürümlere eklenir, hiçbir şey kaybolmaz.`, 'Geri yükle'))) return
    try { await api.restoreVersion(path, v.id); toast('Önceki sürüm geri yüklendi'); onRestored() } catch (err) { toastError(err) }
  }
  return (
    <>
      <hr />
      <p className="props-sub">Önceki sürümler</p>
      {list === null ? <span className="spinner" />
        : list.length === 0 ? <p className="muted small">Önceki sürüm yok. Bu dosya bir yüklemeyle değiştirildiğinde eski hâli burada 30 gün saklanır.</p>
        : (
          <ul className="versions">
            {list.map(v => (
              <li key={v.id}>
                <span>{formatDate(v.savedAt)}<small>{formatSize(v.size)}</small></span>
                <a className="btn small" href={api.versionUrl(path, v.id)} download>İndir</a>
                <button className="btn small" onClick={() => restore(v)}>Geri yükle</button>
              </li>
            ))}
          </ul>
        )}
    </>
  )
}
