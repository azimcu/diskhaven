import { File, FileArchive, FileAudio, FileCode, FileImage, FileSpreadsheet, FileText, FileVideo, Folder, Presentation } from 'lucide-react'
import type { FileEntry } from './types'

const groups: [string[], typeof File, string][] = [
  [['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg', 'avif', 'heic', 'tif', 'tiff', 'raw'], FileImage, 'var(--ic-image)'],
  [['mp4', 'mkv', 'mov', 'avi', 'webm', 'm4v', 'wmv'], FileVideo, 'var(--ic-video)'],
  [['mp3', 'wav', 'flac', 'm4a', 'ogg', 'aac', 'opus', 'wma'], FileAudio, 'var(--ic-audio)'],
  [['zip', 'rar', '7z', 'tar', 'gz', 'xz', 'iso'], FileArchive, 'var(--ic-archive)'],
  [['xls', 'xlsx', 'csv', 'ods'], FileSpreadsheet, 'var(--ic-sheet)'],
  [['ppt', 'pptx', 'odp', 'key'], Presentation, 'var(--ic-slides)'],
  [['pdf', 'doc', 'docx', 'txt', 'md', 'rtf', 'odt', 'log'], FileText, 'var(--ic-doc)'],
  [['ts', 'tsx', 'js', 'cs', 'py', 'ino', 'c', 'cpp', 'h', 'html', 'css', 'json', 'xml', 'sh', 'yml', 'yaml', 'exe', 'msi'], FileCode, 'var(--ic-code)'],
]
const byExt = new Map<string, [typeof File, string]>()
for (const [exts, icon, color] of groups) for (const e of exts) byExt.set(e, [icon, color])

export function FileIcon({ entry, size = 18 }: { entry: Pick<FileEntry, 'kind' | 'extension'>; size?: number }) {
  if (entry.kind === 'Folder')
    return <Folder size={size} className="ic" style={{ color: 'var(--ic-folder)', fill: 'var(--ic-folder-fill)' }} strokeWidth={1.5} />
  const [Icon, color] = (entry.extension && byExt.get(entry.extension)) || [File, 'var(--ic-file)']
  return <Icon size={size} className="ic" style={{ color }} strokeWidth={1.5} />
}
