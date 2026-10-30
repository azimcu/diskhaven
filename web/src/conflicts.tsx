import { api } from './api'
import { Modal, mount } from './dialogs'
import { hasPlugin } from './plugins'
import { uploads } from './uploads'

type Picked = { file: File; relativeName: string }
type Choice = 'replace' | 'both' | 'skip' | null

/**
 * Queues files for upload like Windows' "Replace or skip files": when files dropped straight into a folder
 * have the name of a file already there, ask once for all of them. Files inside dropped folders and uploads
 * into the hidden folder are never asked about (they keep both, as before).
 */
export async function uploadFiles(destination: string, files: Picked[]) {
  if (files.length === 0) return
  let taken = new Set<string>()
  if (!destination.startsWith('hideaway:')) {
    try {
      const listing = await api.list(destination)
      taken = new Set(listing.entries.filter(e => e.kind === 'File').map(e => e.name.toLocaleLowerCase('tr-TR')))
    } catch { /* cannot list: just upload, the server keeps both */ }
  }
  const clashes = files.filter(f => !f.relativeName.includes('/') && taken.has(f.relativeName.toLocaleLowerCase('tr-TR')))
  if (clashes.length === 0) return uploads.add(destination, files)

  const choice = await mount<Choice>(done => <ReplaceDialog names={clashes.map(c => c.relativeName)} total={files.length} done={done} />)
  if (choice === null) return
  const clashing = new Set(clashes)
  uploads.add(destination, files
    .filter(f => choice !== 'skip' || !clashing.has(f))
    .map(f => ({ ...f, replace: choice === 'replace' && clashing.has(f) })))
}

function ReplaceDialog({ names, total, done }: { names: string[]; total: number; done: (c: Choice) => void }) {
  const versions = hasPlugin('versions')
  return (
    <Modal onClose={() => done(null)}>
      <p className="modal-title">{names.length === 1 ? `"${names[0]}" bu klasörde zaten var` : `${names.length} dosya bu klasörde zaten var`}</p>
      {names.length > 1 && (
        <ul className="clash-list">
          {names.slice(0, 5).map(n => <li key={n}>{n}</li>)}
          {names.length > 5 && <li>ve {names.length - 5} dosya daha</li>}
        </ul>
      )}
      <p className="modal-text small">
        {versions
          ? 'Değiştirirsen eski hâli kaybolmaz: Özellikler → Önceki sürümler bölümünden geri alabilirsin.'
          : 'Değiştirirsen eski dosyalar Çöp Kutusu\'na taşınır.'}
      </p>
      <div className="clash-actions">
        <button className="btn primary" autoFocus onClick={() => done('replace')}>{names.length === 1 ? 'Dosyayı değiştir' : 'Dosyaları değiştir'}</button>
        <button className="btn" onClick={() => done('both')}>İkisini de tut</button>
        <button className="btn" onClick={() => done('skip')}>{names.length === total ? 'Yüklemeyi atla' : `Bu ${names.length === 1 ? 'dosyayı' : 'dosyaları'} atla`}</button>
        <button className="btn" onClick={() => done(null)}>İptal</button>
      </div>
    </Modal>
  )
}
