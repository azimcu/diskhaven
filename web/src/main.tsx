import { lazy, StrictMode, Suspense } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import './styles.css'
import { savedTheme } from './theme'

// a theme the user picked overrides the device setting; otherwise CSS follows prefers-color-scheme
const picked = savedTheme()
if (picked) document.documentElement.setAttribute('data-theme', picked)

// /s/<token> is a share link opened by someone without an account
const share = location.pathname.match(/^\/s\/([A-Za-z0-9_-]{20,64})\/?$/)

const PublicShare = lazy(() => import('./Sharing').then(m => ({ default: m.PublicShare })))

createRoot(document.getElementById('root')!).render(
  <StrictMode>{share ? <Suspense fallback={null}><PublicShare token={share[1]} /></Suspense> : <App />}</StrictMode>)
