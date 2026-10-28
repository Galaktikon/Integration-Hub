export type Connector = {
  id: string; name: string; kind: string; role: 'source' | 'destination'; enabled: boolean; secretSet: boolean
  configuration: { schemaVersion: number; baseUrl: string; authMode: string; pageSize: number; timeoutSeconds: number }
}
export type Field = { source: string; destination: string; required: boolean; transform: string | null }
export type Integration = {
  id: string; name: string; sourceConnectorId: string; destinationConnectorId: string; entityType: string; enabled: boolean
  mapping: { schemaVersion: number; fields: Field[] }
  validation: { schemaVersion: number; amountMinimum: number }
  retryPolicy: { maxAttempts: number; baseDelaySeconds: number; maxDelaySeconds: number; maxRecoveries: number }
}
export type Schedule = { id: string; intervalSeconds: number; enabled: boolean; nextRunAtUtc: string; lastHandledAtUtc: string | null }
export type RunSummary = { runId: string; integrationId: string; status: string; triggerType: string; createdAtUtc: string }
export type Run = RunSummary & {
  counts: { fetched: number; created: number; updated: number; unchanged: number; failedPermanent: number; failedRetryable: number; pending: number }
  sourceComplete: boolean; allowedActions: string[]; lastErrorCode: string | null; lastErrorCategory: string | null
  nextSourceAttemptAtUtc: string | null; startedAtUtc: string | null; finishedAtUtc: string | null
  sourceRetries: { occurredAtUtc: string; code: string; attempt: number }[]
}
export type RecordOutcome = { recordId: string; sourceRecordId: string; status: string; attemptCount: number; errorCategory: string | null; errorCode: string | null; safeErrorMessage: string | null; nextAttemptAtUtc: string | null }
export type Attempt = { attemptNumber: number; outcome: string; httpStatus: number | null; errorCode: string | null; startedAtUtc: string; durationMs: number | null }
export type Page<T> = { items: T[]; nextCursor: string | null }

const base = (import.meta.env.VITE_API_BASE_URL || 'http://localhost:8080').replace(/\/$/, '')
let accessToken: string | null = null
export const setToken = (token: string | null) => { accessToken = token }

export async function api<T>(path: string, method = 'GET', body?: unknown, etag?: string): Promise<{ value: T; etag: string | null }> {
  const response = await fetch(`${base}/api/v1${path}`, {
    method,
    headers: {
      ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      ...(etag ? { 'If-Match': etag } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}))
    const details = Array.isArray(problem.errors) ? problem.errors.map((x: { message: string }) => x.message).join(' ') : ''
    throw new Error(details || problem.detail || problem.title || `Request failed (${response.status})`)
  }
  const content = await response.text()
  return { value: content ? JSON.parse(content) as T : undefined as T, etag: response.headers.get('ETag') }
}

export async function all<T>(path: string): Promise<T[]> {
  const rows: T[] = []
  let cursor: string | null = null
  do {
    const query: string = `${path}${path.includes('?') ? '&' : '?'}pageSize=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`
    const page: Page<T> = (await api<Page<T>>(query)).value
    rows.push(...page.items)
    cursor = page.nextCursor
  } while (cursor)
  return rows
}
