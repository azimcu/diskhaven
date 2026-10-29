import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import { PublicShare } from './Sharing'
import './styles.css'
import { applyTheme, savedTheme } from './theme'

applyTheme(savedTheme())

// /s/<token> is a share link opened by someone without an account
const share = location.pathname.match(/^\/s\/([A-Za-z0-9_-]{20,64})\/?$/)

createRoot(document.getElementById('root')!).render(<StrictMode>{share ? <PublicShare token={share[1]} /> : <App />}</StrictMode>)
