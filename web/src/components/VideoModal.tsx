import { useEffect, type ReactNode } from 'react'
import { videoUrl } from '../api'

/** A clip in a centred player; Escape or a click outside closes it. */
export default function VideoModal({ renderId, onClose, children }: { renderId: string; onClose: () => void; children?: ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])
  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <video src={videoUrl(renderId)} controls autoPlay />
        <div className="modal-bar">
          {children}
          <a className="button ghost sm" href={videoUrl(renderId)} download>
            Download
          </a>
          <button className="button ghost sm" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  )
}
