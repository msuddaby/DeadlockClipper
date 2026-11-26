import { Route, Routes } from 'react-router'
import Layout from './components/Layout'
import ClipsPage from './pages/ClipsPage'
import HomePage from './pages/HomePage'
import MatchPage from './pages/MatchPage'
import MatchesPage from './pages/MatchesPage'
import SettingsPage from './pages/SettingsPage'

export default function App() {
  return (
    <Layout>
      <Routes>
        <Route path="/" element={<HomePage />} />
        <Route path="/matches" element={<MatchesPage />} />
        <Route path="/demos/:id" element={<MatchPage />} />
        <Route path="/clips" element={<ClipsPage />} />
        <Route path="/settings" element={<SettingsPage />} />
        <Route path="*" element={<p className="muted">Page not found.</p>} />
      </Routes>
    </Layout>
  )
}
