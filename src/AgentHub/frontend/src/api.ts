type ApiError = Error & { status?: number }

const TOKEN = typeof window !== 'undefined' ? window.__AGENTHUB_TOKEN__ || '' : ''

export const WRITABLE = !!TOKEN

export async function api<T = unknown>(path: string, opts: RequestInit = {}): Promise<T> {
  const headers = new Headers(opts.headers)
  if (!headers.has('Content-Type')) headers.set('Content-Type', 'application/json; charset=utf-8')
  if (TOKEN) headers.set('X-AgentHub-Token', TOKEN)
  const resp = await fetch(path, { ...opts, headers })
  const ct = resp.headers.get('content-type') || ''
  const body = ct.includes('json') ? await resp.json().catch(() => ({})) : await resp.text()
  if (!resp.ok) {
    const err: ApiError = new Error((body && (body as { error?: string }).error) || `HTTP ${resp.status}`)
    err.status = resp.status
    throw err
  }
  return body as T
}

export const get = <T = unknown>(path: string, init?: RequestInit) => api<T>(path, init)
export const post = <T = unknown>(path: string, body?: unknown) =>
  api<T>(path, { method: 'POST', body: JSON.stringify(body ?? {}) })
export const put = <T = unknown>(path: string, body?: unknown) =>
  api<T>(path, { method: 'PUT', body: JSON.stringify(body ?? {}) })
export const del = <T = unknown>(path: string) => api<T>(path, { method: 'DELETE' })
