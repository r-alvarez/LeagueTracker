import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
// Self-hosted variable font - bundled into wwwroot, no CDN requests.
import '@fontsource-variable/inter/index.css'
import './index.css'
import App from './App.tsx'
import { account, bootAccount } from './account'

bootAccount().then(() => {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <BrowserRouter basename={account.basename}>
        <App />
      </BrowserRouter>
    </StrictMode>,
  )
}).catch(err => {
  document.getElementById('root')!.textContent = `LeagueTracker could not load its accounts: ${err}`
})
