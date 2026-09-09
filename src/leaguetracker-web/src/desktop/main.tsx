import { createRoot } from 'react-dom/client'
import { MemoryRouter } from 'react-router-dom'
import '@fontsource-variable/inter/index.css'
import '../index.css'
import './review.css'
import DesktopApp from './DesktopApp'
import { invoke } from './bridge'
import { reviewHost } from '../reviewHost'

invoke<{ artPrefix: string; last: boolean; version: string }>('bootstrap').then(bootstrap => {
  reviewHost.desktop = true
  reviewHost.artPrefix = bootstrap.artPrefix
  reviewHost.openYouTube = url => { void invoke('openYouTube', { url }).catch(() => undefined) }
  createRoot(document.getElementById('root')!).render(<MemoryRouter><DesktopApp openLast={bootstrap.last} /></MemoryRouter>)
}).catch(error => { document.getElementById('root')!.textContent = String(error) })
