import { useEffect, useState } from 'react'
import { Crown, Eye, Pencil, Users } from 'lucide-react'
import { api } from './api'
import { Modal, mount, toast, toastError } from './dialogs'
import { FileIcon } from './FileIcon'
import type { AccessLevel, FileEntry, Grant } from './types'

const rank: Record<AccessLevel, number> = { None: 0, Read: 1, Write: 2, Owner: 3 }
const EVERYONE = '*'

/** "Kişilerle paylaş": tick who may see and who may change an item (and, for an admin, who owns a folder). */
export function accessDialog(entry: Pick<FileEntry, 'name' | 'path' | 'kind' | 'drive' | 'extension'>, me: string, onChanged?: () => void) {
  return mount<void>(done => <AccessDialog entry={entry} me={me} onClose={() => { done(); onChanged?.() }} />)
}

function AccessDialog({ entry, me, onClose }: { entry: Pick<FileEntry, 'name' | 'path' | 'kind' | 'drive' | 'extension'>; me: string; onClose: () => void }) {
  const [people, setPeople] = useState<{ userName: string; isAdmin: boolean }[] | null>(null)
  const [own, setOwn] = useState<Grant[]>([])
  const [above, setAbove] = useState<Grant[]>([])
  const [canGiveOwner, setCanGiveOwner] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const folder = entry.kind === 'Folder'

  useEffect(() => {
    Promise.all([api.people(), api.access(entry.path, entry.drive)])
      .then(([p, a]) => { setPeople(p); setOwn(a.own); setAbove(a.above); setCanGiveOwner(a.canGiveOwner) })
      .catch(e => { toastError(e); onClose() })
  }, [entry.path, entry.drive])

  const levelOf = (who: string) => own.find(g => g.principal.toLowerCase() === who.toLowerCase())?.level ?? 'None'
  // the strongest permission that comes from a folder above, and where from
  const inherited = (who: string) => above
    .filter(g => g.principal === EVERYONE || g.principal.toLowerCase() === who.toLowerCase())
    .sort((a, b) => rank[b.level] - rank[a.level])[0]

  const set = async (who: string, level: AccessLevel) => {
    setBusy(who)
    try {
      const g = await api.setAccess(entry.path, who, level, entry.drive)
      setOwn(list => [...list.filter(x => x.principal.toLowerCase() !== who.toLowerCase()), ...(g ? [g] : [])])
      toast(level === 'None' ? 'Paylaşım kaldırıldı' : 'Paylaşım kaydedildi')
    } catch (e) { toastError(e) }
    setBusy(null)
  }

  // ticking "change" also ticks "see"; unticking "see" takes everything away
  const tick = (who: string, column: AccessLevel, on: boolean) => {
    const cur = levelOf(who)
    const next: AccessLevel = on ? (rank[column] > rank[cur] ? column : cur)
      : column === 'Read' ? 'None' : column === 'Write' ? 'Read' : 'Write'
    if (next !== cur) void set(who, next)
  }

  const users = (people ?? []).filter(p => !p.isAdmin && p.userName.toLowerCase() !== me.toLowerCase())
  const admins = (people ?? []).filter(p => p.isAdmin)
  const columns: [AccessLevel, string, React.ReactNode][] = [
    ['Read', 'Görebilir', <Eye size={14} />],
    ...(folder ? [['Write', 'Değiştirebilir', <Pencil size={14} />] as [AccessLevel, string, React.ReactNode]] : []),
    ...(folder && canGiveOwner ? [['Owner', 'Sahibi', <Crown size={14} />] as [AccessLevel, string, React.ReactNode]] : []),
  ]

  const row = (who: string, label: React.ReactNode, note?: string) => {
    const cur = levelOf(who)
    const from = inherited(who)
    return (
      <tr key={who}>
        <th scope="row">{label}{note && <small className="muted">{note}</small>}
          {from && rank[from.level] > rank[cur] && <small className="muted">üst klasörden: {levelName(from.level)} (/{from.path})</small>}</th>
        {columns.map(([lvl, name]) => {
          const byAbove = !!from && rank[from.level] >= rank[lvl]
          return (
            <td key={lvl}>
              <input type="checkbox" aria-label={`${typeof label === 'string' ? label : who}: ${name}`}
                checked={rank[cur] >= rank[lvl] || byAbove} disabled={busy !== null || (byAbove && rank[cur] < rank[lvl])}
                onChange={e => tick(who, lvl, e.target.checked)} />
            </td>
          )
        })}
      </tr>
    )
  }

  return (
    <Modal onClose={onClose}>
      <div className="access">
        <div className="props-head"><FileIcon entry={{ kind: entry.kind, extension: entry.extension ?? null }} size={32} /><b title={entry.name}>{entry.name}</b></div>
        <p className="modal-text small">
          {folder
            ? 'İçindekilerle birlikte paylaşılır. Paylaşılmayan kişi bu klasörü hiç görmez.'
            : 'Dosya yalnız görmek için paylaşılır. Paylaşılmayan kişi bu dosyayı hiç görmez.'}
        </p>
        {people === null ? <div className="loading"><span className="spinner" /></div> : (
          <table className="access-table">
            <thead><tr><th />{columns.map(([lvl, name, icon]) => <th key={lvl} scope="col"><span className="access-col">{icon}{name}</span></th>)}</tr></thead>
            <tbody>
              {row(EVERYONE, <span className="access-who"><Users size={15} /> Herkes</span>, 'tüm kullanıcılar, sonradan eklenenler de')}
              {users.map(u => row(u.userName, u.userName))}
            </tbody>
          </table>
        )}
        {people !== null && users.length === 0 && <p className="modal-text small">Henüz başka kullanıcı yok. Yönetim paneli → Kullanıcılar'dan ekleyebilirsin.</p>}
        {admins.length > 0 && <p className="modal-text small muted">Yöneticiler ({admins.map(a => a.userName).join(', ')}) her şeyi görür ve değiştirir.</p>}
        <div className="modal-actions"><button className="btn primary" onClick={onClose}>Tamam</button></div>
      </div>
    </Modal>
  )
}

export const levelName = (l: AccessLevel) => l === 'Owner' ? 'sahibi' : l === 'Write' ? 'görür ve değiştirir' : l === 'Read' ? 'görür' : 'yok'
