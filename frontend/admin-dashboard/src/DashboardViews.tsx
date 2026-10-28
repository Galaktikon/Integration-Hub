import { useState } from 'react'
import type { Attempt, Connector, Integration, RecordOutcome, Run, RunSummary } from './api'

const timestamp = (value?: string | null) => value ? new Date(value).toLocaleString() : '—'
const needsAttention = (status: string) => status === 'failed' || status === 'completed_with_errors'
const isActive = (status: string) => status === 'queued' || status === 'running'

function Status({ value }: { value: string }) {
  const tone = needsAttention(value) || value.includes('failed') ? 'danger' : isActive(value) ? 'active' : value === 'completed' || value === 'created' || value === 'enabled' ? 'success' : 'muted'
  return <span className={`status ${tone}`}>{value.replaceAll('_', ' ')}</span>
}

export function OperationsView({ integrations, connectors, runs, openRun, openIntegration, newIntegration, startRun, refresh, loading, busy }: {
  integrations: Integration[]; connectors: Connector[]; runs: RunSummary[]
  openRun: (id: string) => void; openIntegration: (id: string) => void; newIntegration: () => void; startRun: (id: string) => void; refresh: () => void; loading: boolean; busy: boolean
}) {
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState<'all' | 'attention' | 'active' | 'disabled'>('all')
  const [showAllAttention, setShowAllAttention] = useState(false)
  const latest = new Map<string, RunSummary>()
  for (const run of runs) if (!latest.has(run.integrationId)) latest.set(run.integrationId, run)
  const attention = integrations.filter(i => needsAttention(latest.get(i.id)?.status || ''))
  const active = integrations.filter(i => isActive(latest.get(i.id)?.status || ''))
  const healthy = integrations.filter(i => i.enabled && latest.get(i.id)?.status === 'completed')
  const visible = integrations.filter(i => {
    const run = latest.get(i.id)
    const matches = `${i.name} ${connectors.find(c => c.id === i.sourceConnectorId)?.name || ''} ${connectors.find(c => c.id === i.destinationConnectorId)?.name || ''}`.toLowerCase().includes(query.toLowerCase())
    return matches && (filter === 'all' || filter === 'attention' && needsAttention(run?.status || '') || filter === 'active' && isActive(run?.status || '') || filter === 'disabled' && !i.enabled)
  })
  const connectorName = (id: string) => connectors.find(c => c.id === id)?.name || 'Unknown connector'
  return <div className="operations-view">
    <div className="page-heading"><div><h1>Operations</h1><p>Monitor integrations, track runs, and resolve issues.</p></div><div className="button-row"><button className="ghost" onClick={refresh} disabled={loading}>{loading ? 'Refreshing…' : '↻ Refresh'}</button><button className="primary" onClick={newIntegration}>+ New integration</button></div></div>
    <div className="summary-grid">
      <button className="summary-card attention" onClick={() => setFilter('attention')}><strong>{attention.length}</strong><span>Flows need attention</span><b aria-hidden>→</b></button>
      <button className="summary-card running" onClick={() => setFilter('active')}><strong>{active.length}</strong><span>Running or queued</span><b aria-hidden>→</b></button>
      <button className="summary-card healthy" onClick={() => setFilter('all')}><strong>{healthy.length}</strong><span>Latest run completed</span><b aria-hidden>→</b></button>
    </div>
    <div className="operations-grid"><div className="operations-main">
      {attention.length > 0 && <section className="panel attention-panel"><div className="section-head"><div><h2>Needs attention</h2><p className="subtle">Latest run for each affected integration</p></div><span className="count-pill">{attention.length}</span></div><div className="attention-list">{(showAllAttention ? attention : attention.slice(0, 4)).map(i => { const r = latest.get(i.id)!; return <div className="attention-item" key={i.id}><div className="attention-icon" aria-hidden>!</div><div><strong>{i.name}</strong><p>{r.status === 'failed' ? 'Latest run failed' : 'Latest run completed with errors'} · {timestamp(r.createdAtUtc)}</p></div><button className="ghost" onClick={() => openRun(r.runId)}>View run</button></div> })}</div>{attention.length > 4 && <button className="text-button attention-toggle" onClick={() => setShowAllAttention(!showAllAttention)}>{showAllAttention ? 'Show fewer' : `Show all ${attention.length}`}</button>}</section>}
      <section className="panel integration-table-panel"><div className="section-head"><div><h2>All integrations</h2><p className="subtle">Choose a flow to view its configuration or run it now.</p></div><span className="count-pill">{integrations.length}</span></div><div className="table-tools"><label className="search-label">Search integrations<input value={query} onChange={e => setQuery(e.target.value)} placeholder="Name or connector" /></label><label className="filter-label">Show<select value={filter} onChange={e => setFilter(e.target.value as typeof filter)}><option value="all">All</option><option value="attention">Needs attention</option><option value="active">Running</option><option value="disabled">Disabled</option></select></label></div>{integrations.length ? <div className="table-scroll"><table className="data-table"><thead><tr><th>Integration</th><th>Route</th><th>Latest run</th><th>Last started</th><th>Actions</th></tr></thead><tbody>{visible.map(i => { const r = latest.get(i.id); return <tr key={i.id}><td><button className="link-button" onClick={() => openIntegration(i.id)}>{i.name}</button>{!i.enabled && <span className="inline-muted">Disabled</span>}</td><td>{connectorName(i.sourceConnectorId)} → {connectorName(i.destinationConnectorId)}</td><td>{r ? <Status value={r.status}/> : <span className="inline-muted">Not run yet</span>}</td><td>{timestamp(r?.createdAtUtc)}</td><td><button className="ghost compact" disabled={!i.enabled || busy} onClick={() => startRun(i.id)}>Run now</button><button className="text-button" onClick={() => openIntegration(i.id)}>Details</button></td></tr> })}</tbody></table>{visible.length === 0 && <p className="table-empty">No integrations match this view.</p>}</div> : <div className="empty"><h3>No integrations yet</h3><p>Create a flow after adding source and destination connectors.</p><button className="primary" onClick={newIntegration}>New integration</button></div>}</section>
    </div><aside className="panel activity-panel"><div className="section-head"><h2>Recent activity</h2><span className="count-pill">{runs.length}</span></div>{runs.length ? <div className="activity-list">{runs.slice(0, 8).map(r => <button key={r.runId} className="activity-item" onClick={() => openRun(r.runId)}><Status value={r.status}/><strong>{integrations.find(i => i.id === r.integrationId)?.name || 'Integration'}</strong><small>{timestamp(r.createdAtUtc)} · {r.triggerType}</small></button>)}</div> : <p className="subtle">Runs will appear here after the first sync.</p>}</aside></div>
  </div>
}

export function RunDetails({ run, records, attempts, selectedRecord, selectRecord, integration, retry, cancel, busy, showIntegration }: {
  run: Run; records: RecordOutcome[]; attempts: Attempt[]; selectedRecord: string | null; selectRecord: (id: string | null) => void
  integration?: Integration; retry: () => void; cancel: () => void; busy: boolean; showIntegration: (id: string) => void
}) {
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState('all')
  const chosen = records.find(r => r.recordId === selectedRecord)
  const visible = records.filter(r => (filter === 'all' || r.status === filter) && r.sourceRecordId.toLowerCase().includes(query.toLowerCase()))
  const problemCount = run.counts.failedPermanent + run.counts.failedRetryable
  const summary = problemCount ? `${problemCount} record${problemCount === 1 ? '' : 's'} ${run.counts.failedRetryable ? 'need another attempt or review' : 'failed permanently'}.` : run.status === 'completed' ? 'All fetched records were processed.' : run.status === 'failed' ? 'This run stopped before it could complete.' : 'This run is in progress.'
  const nextStep = run.nextSourceAttemptAtUtc ? `The source will be tried again at ${timestamp(run.nextSourceAttemptAtUtc)}.` : run.allowedActions.includes('retry') ? 'Automatic recovery is exhausted. Review the error, then retry this run.' : isActive(run.status) && run.counts.failedRetryable ? 'Retryable records will be attempted automatically while the run is active.' : run.status === 'completed_with_errors' ? 'Review the failed records and their attempt history.' : run.status === 'completed' ? 'No action is needed.' : run.status === 'failed' ? 'Review the error and connector settings.' : 'Progress updates automatically.'
  return <div className="run-details">
    <div className="run-title"><div><p className="eyebrow">RUN DETAILS · {run.triggerType.toUpperCase()}</p><h2>{integration?.name || 'Integration'}</h2></div><div className="button-row">{run.allowedActions.includes('retry') && <button className="primary" disabled={busy} onClick={retry}>Retry run</button>}{run.allowedActions.includes('cancel') && <button className="ghost" disabled={busy} onClick={cancel}>Cancel run</button>}</div></div>
    <div className="run-meta"><Status value={run.status}/><span>Started {timestamp(run.startedAtUtc)}</span>{run.finishedAtUtc && <span>Finished {timestamp(run.finishedAtUtc)}</span>}</div>
    <p className="run-summary">{summary}</p>
    {run.sourceRetries.length > 0 && <div className="message notice" role="status">
      {run.sourceRetries.map((retry, index) => <p key={`${retry.occurredAtUtc}-${index}`}>
        {retry.code === 'service_unavailable' ? 'Source returned 503' : `Source request failed (${retry.code.replaceAll('_', ' ')})`}; automatic retry {retry.attempt} scheduled at {timestamp(retry.occurredAtUtc)}.
      </p>)}
    </div>}
    <div className="next-step"><strong>Next step</strong><p>{nextStep}</p>{integration && <button className="text-button" onClick={() => showIntegration(integration.id)}>View integration settings →</button>}</div>
    {run.lastErrorCode && <div className="message error">{run.lastErrorCategory}: {run.lastErrorCode}</div>}
    <section className="run-progress"><div className="section-head"><h3>Run progress</h3><span>{run.counts.fetched} fetched</span></div><div className="progress"><div style={{ width: `${run.counts.fetched ? Math.min(100, (run.counts.fetched - run.counts.pending) / run.counts.fetched * 100) : 0}%` }}/></div><div className="run-counts">{(['created','updated','unchanged','failedRetryable','failedPermanent','pending'] as const).map(key => <div key={key}><strong>{run.counts[key]}</strong><span>{key.replace(/([A-Z])/g, ' $1')}</span></div>)}</div></section>
    <section className="record-section"><div className="section-head"><div><h3>Record outcomes</h3><p className="subtle">Select a record to inspect its attempts and safe error details.</p></div><span className="count-pill">{records.length}</span></div><div className="table-tools"><label className="search-label">Search record ID<input value={query} onChange={e => setQuery(e.target.value)} placeholder="Source record ID" /></label><label className="filter-label">Outcome<select value={filter} onChange={e => setFilter(e.target.value)}><option value="all">All outcomes</option>{[...new Set(records.map(r => r.status))].map(s => <option key={s} value={s}>{s.replaceAll('_', ' ')}</option>)}</select></label></div>{records.length ? <div className="table-scroll"><table className="data-table"><thead><tr><th>Source record ID</th><th>Outcome</th><th>Attempts</th><th>Last error</th></tr></thead><tbody>{visible.map(r => <tr key={r.recordId} className={selectedRecord === r.recordId ? 'selected-row' : ''} onClick={() => selectRecord(r.recordId)}><td><button className="link-button" onClick={() => selectRecord(r.recordId)}>{r.sourceRecordId}</button></td><td><Status value={r.status}/></td><td>{r.attemptCount}</td><td>{r.safeErrorMessage || r.errorCode || '—'}</td></tr>)}</tbody></table>{visible.length === 0 && <p className="table-empty">No records match this view.</p>}</div> : <p className="subtle">Records appear as source pages are checkpointed.</p>}
    {chosen && <div className="record-inspector"><div className="section-head"><div><p className="eyebrow">RECORD INSPECTOR</p><h3>{chosen.sourceRecordId}</h3></div><button className="text-button" onClick={() => selectRecord(null)}>Close</button></div><div className="run-meta"><Status value={chosen.status}/><span>{chosen.attemptCount} attempts</span>{chosen.nextAttemptAtUtc && <span>Next attempt {timestamp(chosen.nextAttemptAtUtc)}</span>}</div>{chosen.safeErrorMessage && <p className="message error">{chosen.safeErrorMessage}</p>}<h4>Attempt history</h4>{attempts.length ? attempts.map(a => <div className="attempt-row" key={a.attemptNumber}><strong>Attempt {a.attemptNumber}</strong><span>{timestamp(a.startedAtUtc)}</span><Status value={a.outcome}/><span>{a.httpStatus || a.errorCode || '—'}</span></div>) : <p className="subtle">No attempts recorded yet.</p>}</div>}</section>
  </div>
}
