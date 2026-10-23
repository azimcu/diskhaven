import { useEffect, useState } from 'react'
import { RotateCcw, Trash2 } from 'lucide-react'
import { api } from './api'
import { confirmDialog, toast, toastError } from './dialogs'
import { FileIcon } from './FileIcon'
import { formatDate, formatSize, parentOf } from './format'
import { t } from './strings'
import type { TrashItem } from './types'

export function TrashView({ onChanged }: { onChanged: () => void }) {
  const [items, setItems] = useState<TrashItem[] | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())

  const load = () => api.trash().then(setItems).catch(toastError)
  useEffect(() => { void load() }, [])

  const restore = async (ids: string[]) => {
    try {
      for (const id of ids) await api.restore(id)
      toast(ids.length === 1 ? 'Geri yüklendi' : `${ids.length} öğe geri yüklendi`)
    } catch (e) { toastError(e) }
    setSelected(new Set())
    onChanged()
    void load()
  }

  const purge = async (ids: string[]) => {
    const name = ids.length === 1 ? items!.find(i => i.id === ids[0])!.name : `${ids.length} öğe`
    if (!(await confirmDialog(t.confirmPurge(name), t.deleteForever, true))) return
    try { for (const id of ids) await api.purge(id) } catch (e) { toastError(e) }
    setSelected(new Set())
    void load()
  }

  const empty = async () => {
    if (!(await confirmDialog(t.confirmEmptyTrash, t.emptyTrash, true))) return
    try { await api.emptyTrash() } catch (e) { toastError(e) }
    void load()
  }

  const toggle = (id: string, additive: boolean) =>
    setSelected(prev => {
      const next = new Set(additive ? prev : [])
      if (prev.has(id) && additive) next.delete(id)
      else next.add(id)
      return next
    })

  const ids = [...selected]
  return (
    <div className="main-col">
      <div className="commandbar">
        <button className="cmd" disabled={ids.length === 0} onClick={() => restore(ids)}><RotateCcw size={16} /> {t.restore}</button>
        <button className="cmd" disabled={ids.length === 0} onClick={() => purge(ids)}><Trash2 size={16} /> {t.deleteForever}</button>
        <span className="cmd-sep" />
        <button className="cmd" disabled={!items?.length} onClick={empty}>{t.emptyTrash}</button>
      </div>
      <div className="addressbar"><div className="crumbs"><span className="crumb current">{t.trash}</span></div></div>
      <p className="info-strip">{t.trashInfo}</p>
      <div className="list-wrap">
        {items && items.length === 0 && <p className="empty-note">{t.trashEmpty}</p>}
        {items && items.length > 0 && (
          <table className="details">
            <thead>
              <tr><th>{t.colName}</th><th className="c-date">{t.colOriginal}</th><th className="c-date">{t.colDeleted}</th><th className="c-size">{t.colSize}</th></tr>
            </thead>
            <tbody>
              {items.map(i => (
                <tr key={i.id} className={selected.has(i.id) ? 'sel' : ''}
                  onClick={e => toggle(i.id, e.ctrlKey || e.metaKey)}
                  onDoubleClick={() => restore([i.id])}>
                  <td className="c-name"><FileIcon entry={{ kind: i.kind, extension: i.name.includes('.') ? i.name.split('.').pop()!.toLowerCase() : null }} /><span className="name-text">{i.name}</span></td>
                  <td className="c-date">{t.thisNas}{parentOf(i.originalPath) ? ` › ${parentOf(i.originalPath).replaceAll('/', ' › ')}` : ''}</td>
                  <td className="c-date">{formatDate(i.deletedAt)}</td>
                  <td className="c-size">{formatSize(i.size)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
      <footer className="statusbar"><span>{items ? t.items(items.length) : ''}</span>{ids.length > 0 && <span>{t.selected(ids.length)}</span>}</footer>
    </div>
  )
}
