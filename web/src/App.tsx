import { Link, Route, Routes } from 'react-router'
import DemoPage from './pages/DemoPage'
import DemosPage from './pages/DemosPage'

export default function App() {
  return (
    <div className="app">
      <header className="topbar">
        <Link to="/" className="brand">
          <span className="brand-mark">◆</span> Deadlock Clipper
        </Link>
      </header>
      <main className="content">
        <Routes>
          <Route path="/" element={<DemosPage />} />
          <Route path="/demos/:id" element={<DemoPage />} />
          <Route path="*" element={<p className="muted">Page not found.</p>} />
        </Routes>
      </main>
    </div>
  )
}
