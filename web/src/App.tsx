import { Link, NavLink, Route, Routes } from 'react-router'
import ClipsPage from './pages/ClipsPage'
import DemoPage from './pages/DemoPage'
import DemosPage from './pages/DemosPage'

export default function App() {
  return (
    <div className="app">
      <header className="topbar">
        <Link to="/" className="brand">
          <span className="brand-mark">◆</span> Deadlock Clipper
        </Link>
        <nav className="nav">
          <NavLink to="/" end>
            Demos
          </NavLink>
          <NavLink to="/clips">Clips</NavLink>
        </nav>
      </header>
      <main className="content">
        <Routes>
          <Route path="/" element={<DemosPage />} />
          <Route path="/demos/:id" element={<DemoPage />} />
          <Route path="/clips" element={<ClipsPage />} />
          <Route path="*" element={<p className="muted">Page not found.</p>} />
        </Routes>
      </main>
    </div>
  )
}
