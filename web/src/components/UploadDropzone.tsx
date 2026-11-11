import { useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { uploadDemo } from '../api'

interface Upload {
  name: string
  progress: number
  error?: string
}

export default function UploadDropzone() {
  const queryClient = useQueryClient()
  const input = useRef<HTMLInputElement>(null)
  const [dragging, setDragging] = useState(false)
  const [uploads, setUploads] = useState<Upload[]>([])

  const update = (name: string, patch: Partial<Upload>) =>
    setUploads((us) => us.map((u) => (u.name === name ? { ...u, ...patch } : u)))

  async function handleFiles(files: FileList | null) {
    if (!files) return
    for (const file of Array.from(files)) {
      if (!file.name.toLowerCase().endsWith('.dem')) {
        setUploads((us) => [...us, { name: file.name, progress: 0, error: 'Not a .dem file' }])
        continue
      }
      setUploads((us) => [...us.filter((u) => u.name !== file.name), { name: file.name, progress: 0 }])
      try {
        await uploadDemo(file, (p) => update(file.name, { progress: p }))
        setUploads((us) => us.filter((u) => u.name !== file.name))
        queryClient.invalidateQueries({ queryKey: ['demos'] })
      } catch (e) {
        update(file.name, { error: (e as Error).message })
      }
    }
  }

  return (
    <div>
      <div
        className={`dropzone ${dragging ? 'dragging' : ''}`}
        onClick={() => input.current?.click()}
        onDragOver={(e) => {
          e.preventDefault()
          setDragging(true)
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault()
          setDragging(false)
          handleFiles(e.dataTransfer.files)
        }}
      >
        <strong>Drop .dem files here</strong>
        <span className="muted">or click to choose</span>
        <input
          ref={input}
          type="file"
          accept=".dem"
          multiple
          hidden
          onChange={(e) => {
            handleFiles(e.target.files)
            e.target.value = ''
          }}
        />
      </div>
      {uploads.map((u) => (
        <div key={u.name} className="upload-row">
          <span className="mono">{u.name}</span>
          {u.error ? (
            <span className="error">{u.error}</span>
          ) : (
            <progress value={u.progress} max={1} />
          )}
        </div>
      ))}
    </div>
  )
}
