import { useCallback, useEffect, useState } from 'react'
import { all, api, setToken, type Attempt, type Connector, type Integration, type RecordOutcome, type Run, type RunSummary, type Schedule } from './api'
import { OperationsView, RunDetails } from './DashboardViews'
import IntegrationWizard from './IntegrationWizard'

type Tab = 'overview' | 'integrations' | 'connectors' | 'runs'
const timestamp = (value?: string | null) => value ? new Date(value).toLocaleString() : '—'
const tone = (status: string) => status.includes('fail') || status === 'completed_with_errors' ? 'danger' : ['completed', 'created', 'enabled'].includes(status) ? 'success' : ['running', 'queued'].includes(status) ? 'active' : 'muted'

function Status({ value }: { value: string }) { return <span className={`status ${tone(value)}`}>{value.replaceAll('_', ' ')}</span> }

export default function App() {
  const [token, updateToken] = useState<string | null>(null)
  const [username, setUsername] = useState('admin')
  const [password, setPassword] = useState('')
  const [tab, setTab] = useState<Tab>('overview')
  const [connectors, setConnectors] = useState<Connector[]>([])
  const [integrations, setIntegrations] = useState<Integration[]>([])
  const [runs, setRuns] = useState<RunSummary[]>([])
  const [selectedIntegration, setSelectedIntegration] = useState<string | null>(null)
  const [selectedRun, setSelectedRun] = useState<string | null>(null)
  const [run, setRun] = useState<Run | null>(null)
  const [records, setRecords] = useState<RecordOutcome[]>([])
  const [attempts, setAttempts] = useState<Attempt[]>([])
  const [selectedRecord, setSelectedRecord] = useState<string | null>(null)
  const [schedule, setSchedule] = useState<Schedule | null>(null)
  const [scheduleEtag, setScheduleEtag] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [loading, setLoading] = useState(false)
  const [showConnectorEditor, setShowConnectorEditor] = useState(false)
  const [showIntegrationEditor, setShowIntegrationEditor] = useState(false)
  const [confirmAction, setConfirmAction] = useState<'retry' | 'cancel' | null>(null)
  const [connectorEdit, setConnectorEdit] = useState<Connector | null>(null)
  const [integrationEdit, setIntegrationEdit] = useState<Integration | null>(null)

  const refresh = useCallback(async () => {
    if (!token) return
    setLoading(true)
    try {
      const [c, i] = await Promise.all([all<Connector>('/connectors'), all<Integration>('/integrations')])
      setConnectors(c); setIntegrations(i)
      const histories = await Promise.all(i.map(x => all<RunSummary>(`/integrations/${x.id}/runs`)))
      setRuns(histories.flat().sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc)))
      setError('')
    } catch (e) { setError((e as Error).message) }
    finally { setLoading(false) }
  }, [token])

  useEffect(() => { void refresh() }, [refresh])
  useEffect(() => {
    if (!token) return
    const timer = window.setInterval(() => { void refresh() }, 10000)
    return () => window.clearInterval(timer)
  }, [token, refresh])
  useEffect(() => {
    if (!token || !selectedRun) return
    let active = true
    const load = async () => {
      try {
        const [details, outcomes] = await Promise.all([api<Run>(`/runs/${selectedRun}`), all<RecordOutcome>(`/runs/${selectedRun}/records`)])
        if (active) { setRun(details.value); setRecords(outcomes) }
      } catch (e) { if (active) setError((e as Error).message) }
    }
    void load()
    const timer = window.setInterval(() => { void load() }, 2000)
    return () => { active = false; window.clearInterval(timer) }
  }, [token, selectedRun])
  useEffect(() => {
    if (!selectedIntegration || !token) { setSchedule(null); return }
    void api<Schedule>(`/integrations/${selectedIntegration}/schedule`).then(x => {
      setSchedule(x.value); setScheduleEtag(x.etag)
    }).catch(() => { setSchedule(null); setScheduleEtag(null) })
  }, [selectedIntegration, token])
  useEffect(() => {
    if (!selectedRun || !selectedRecord) return
    void api<{ items: Attempt[] }>(`/runs/${selectedRun}/records/${selectedRecord}/attempts`)
      .then(x => setAttempts(x.value.items)).catch(e => setError((e as Error).message))
  }, [selectedRun, selectedRecord])

  async function action(work: () => Promise<void>, success: string) {
    setBusy(true); setError(''); setNotice('')
    try { await work(); setNotice(success); await refresh() }
    catch (e) { setError((e as Error).message) }
    finally { setBusy(false) }
  }

  async function login(event: React.FormEvent) {
    event.preventDefault(); setBusy(true); setError('')
    try {
      const result = await api<{ accessToken: string }>('/auth/login', 'POST', { username, password })
      setToken(result.value.accessToken); updateToken(result.value.accessToken); setPassword('')
    } catch (e) { setError((e as Error).message) }
    finally { setBusy(false) }
  }
  function logout() { setToken(null); updateToken(null); setRun(null); setRuns([]); setRecords([]); setTab('overview') }
  async function startRun(id: string) {
    await action(async () => {
      const created = await api<{ runId: string }>(`/integrations/${id}/runs`, 'POST')
      setSelectedRun(created.value.runId); setTab('runs')
    }, 'Run queued. Live progress is shown below.')
  }
  async function runAction(kind: 'retry' | 'cancel') {
    if (!run) return
    await action(async () => { await api(`/runs/${run.runId}/${kind}`, 'POST'); setRun((await api<Run>(`/runs/${run.runId}`)).value) },
      kind === 'retry' ? 'Recovery queued.' : 'Cancellation requested.')
    setConfirmAction(null)
  }
  async function saveSchedule(intervalSeconds: number, enabled: boolean) {
    if (!selectedIntegration) return
    await action(async () => {
      const currentEtag = schedule ? (await api<Schedule>(`/integrations/${selectedIntegration}/schedule`)).etag : null
      const result = await api<Schedule>(`/integrations/${selectedIntegration}/schedule`, 'PUT',
        { intervalSeconds, enabled }, currentEtag || scheduleEtag || undefined)
      setSchedule(result.value); setScheduleEtag(result.etag)
    }, 'Schedule saved.')
  }
  async function injectFault(remaining: number) {
    await action(async () => { await api('/demo/faults', 'POST', { target: 'source', mode: '503', remaining }) },
      remaining === 1 ? 'One source 503 is armed. Start a run to see automatic retry.' :
        `${remaining} source 503 responses are armed. Start a run, then retry it after its attempt budget is exhausted.`)
  }
  async function testConnector(id: string) {
    await action(async () => {
      const result = await api<{ healthy: boolean; code: string }>(`/connectors/${id}/test`, 'POST')
      if (!result.value.healthy) throw new Error(`Connection test failed: ${result.value.code.replaceAll('_', ' ')}`)
    }, 'Connector responded with the expected capabilities.')
  }

  if (!token) return <div className="login-shell"><div className="login-art"><div className="brand-mark">↗</div><p className="eyebrow">ENTERPRISE INTEGRATION HUB</p><h1>Every record.<br />A clear path forward.</h1><p>Operate reliable Commerce to ERP flows from one place.</p><div className="orbit orbit-one"/><div className="orbit orbit-two"/></div><form className="login-card" onSubmit={login}><p className="eyebrow">OPERATIONS ACCESS</p><h2>Sign in</h2><p className="subtle">Use your administrator credentials.</p><label>Username<input value={username} onChange={e => setUsername(e.target.value)} autoComplete="username" required /></label><label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" required /></label>{error && <p className="message error" role="alert">{error}</p>}<button className="primary wide" disabled={busy}>{busy ? 'Signing in…' : 'Sign in →'}</button><p className="fine-print">Session stays in this browser tab and expires after eight hours.</p></form></div>

  const selected = integrations.find(x => x.id === selectedIntegration)
  const openIntegration = (id: string) => { setSelectedIntegration(id); setTab('integrations') }
  const openRun = (id: string) => { setSelectedRun(id); setSelectedRecord(null); setAttempts([]); setTab('runs') }
  return <div className="app-shell">
    <aside className="sidebar"><div className="brand"><span className="brand-mark small">↗</span><div><strong>Integration Hub</strong><small>OPERATIONS CONSOLE</small></div></div><nav>{(['overview','integrations','connectors','runs'] as Tab[]).map((item, n) => <button key={item} className={tab === item ? 'nav active' : 'nav'} onClick={() => setTab(item)}><span aria-hidden="true">{['◫','⇄','⌘','◷'][n]}</span>{item === 'runs' ? 'Run history' : item[0].toUpperCase() + item.slice(1)}</button>)}</nav><div className="sidebar-bottom"><div className="environment"><span className="live-dot"/> Synthetic demo</div><button className="text-button" onClick={logout}>Sign out ↗</button></div></aside>
    <main className="main">{tab !== 'overview' && <header className="topbar"><div><span className="breadcrumb">WORKSPACE / {tab.toUpperCase()}</span><h1>{({ integrations: 'Integrations', connectors: 'Connectors', runs: 'Run history' } as Record<Exclude<Tab, 'overview'>, string>)[tab]}</h1></div><div className="top-actions"><button className="ghost" onClick={() => void refresh()} disabled={loading}>{loading ? 'Refreshing…' : '↻ Refresh'}</button><span className="avatar">AD</span></div></header>}
      {error && <div className="message error" role="alert">{error}<button onClick={() => setError('')}>×</button></div>}{notice && <div className="message notice" role="status">{notice}<button onClick={() => setNotice('')}>×</button></div>}
      {tab === 'overview' && <OperationsView integrations={integrations} connectors={connectors} runs={runs} openRun={openRun} openIntegration={openIntegration} newIntegration={() => { setIntegrationEdit(null); setShowIntegrationEditor(true) }} startRun={id => void startRun(id)} refresh={() => void refresh()} loading={loading} busy={busy} />}
      {tab === 'connectors' && <section className="panel"><div className="section-head"><div><p className="eyebrow">DATA ENDPOINTS</p><h2>Connectors</h2><p className="subtle">Approved source and destination REST connections.</p></div><button className="primary" onClick={() => { setConnectorEdit(null); setShowConnectorEditor(true) }}>+ Add connector</button></div>{connectors.length ? <div className="card-grid">{connectors.map(c => <article className="item-card" key={c.id}><div className="item-head"><span className="item-icon">{c.role === 'source' ? '↗' : '↙'}</span><Status value={c.enabled ? 'enabled' : 'disabled'}/></div><h3>{c.name}</h3><p>{c.role} · {c.kind}</p><code>{c.configuration.baseUrl}</code><div className="card-footer"><span>{c.secretSet ? '● Secret configured' : '○ Secret missing'}</span><span className="button-row"><button className="text-button" disabled={busy} onClick={() => void testConnector(c.id)}>Test</button><button className="text-button" onClick={() => { setConnectorEdit(c); setShowConnectorEditor(true) }}>Edit →</button></span></div></article>)}</div>: <Empty title="No connectors" text="Add a source and destination to get started."/>}</section>}
      {tab === 'integrations' && <div className="split-layout"><section className="panel"><div className="section-head"><div><p className="eyebrow">DATA FLOWS</p><h2>Integrations</h2></div><button className="primary" onClick={() => { setIntegrationEdit(null); setShowIntegrationEditor(true) }}>+ New integration</button></div>{integrations.length ? <div className="integration-list">{integrations.map(i => <button className={`integration-row ${selectedIntegration === i.id ? 'chosen' : ''}`} key={i.id} onClick={() => setSelectedIntegration(i.id)}><span className="item-icon">⇄</span><span><strong>{i.name}</strong><small>{connectors.find(c => c.id === i.sourceConnectorId)?.name || 'Source'} → {connectors.find(c => c.id === i.destinationConnectorId)?.name || 'Destination'}</small></span><Status value={i.enabled ? 'enabled' : 'disabled'}/></button>)}</div>: <Empty title="No integrations" text="Create an integration to define mapping and retry policy."/>}</section><section className="panel detail-panel">{selected ? <><div className="section-head"><div><p className="eyebrow">INTEGRATION DETAIL</p><h2>{selected.name}</h2></div><button className="ghost" onClick={() => { setIntegrationEdit(selected); setShowIntegrationEditor(true) }}>Edit</button></div><div className="detail-block"><span>STATUS</span><Status value={selected.enabled ? 'enabled' : 'disabled'}/></div><div className="detail-block"><span>RECORD TYPE</span><strong>Orders</strong></div><div className="detail-block"><span>FIELD MAPPINGS</span><strong>{selected.mapping.fields.length} fields</strong></div><div className="detail-block"><span>RETRY POLICY</span><strong>{selected.retryPolicy.maxAttempts} attempts · {selected.retryPolicy.maxRecoveries} recoveries</strong></div><div className="button-row"><button className="primary" disabled={!selected.enabled || busy} onClick={() => void startRun(selected.id)}>▶ Run now</button><button className="ghost" disabled={busy} onClick={() => void injectFault(1)}>One 503</button><button className="ghost" disabled={busy} onClick={() => void injectFault(4)}>Outage for retry demo</button></div><div className="schedule-box"><p className="eyebrow">AUTOMATION</p><h3>Interval schedule</h3><p>{schedule?.enabled ? `Next run ${timestamp(schedule.nextRunAtUtc)}` : 'Schedule is off.'}</p><ScheduleForm schedule={schedule} save={saveSchedule} busy={busy}/></div></>: <Empty title="Select an integration" text="Choose a flow to run it, schedule it, or edit its mapping."/>}</section></div>}
      {tab === 'runs' && <div className="run-workspace"><section className="panel run-list-panel"><div className="section-head"><div><h2>Run history</h2><p className="subtle">Select a run to see its progress and record outcomes.</p></div><span className="count-pill">{runs.length}</span></div>{runs.length ? <RunTable rows={runs} integrations={integrations} open={openRun} selected={selectedRun}/> : <Empty title="No runs yet" text="Start a sync from the Integrations view."/>}</section><section className="panel run-detail-panel">{run && run.runId === selectedRun ? <RunDetails run={run} records={records} attempts={attempts} selectedRecord={selectedRecord} selectRecord={id => { setSelectedRecord(id); setAttempts([]) }} integration={integrations.find(i => i.id === run.integrationId)} retry={() => setConfirmAction('retry')} cancel={() => setConfirmAction('cancel')} busy={busy} showIntegration={openIntegration}/> : <Empty title="Select a run" text="Inspect live progress, record results, and safe error details."/>}</section></div>}
    </main>
    {confirmAction && <div className="modal-backdrop" role="presentation"><div className="modal confirm-modal" role="dialog" aria-modal="true" aria-label={`${confirmAction} run`}><p className="eyebrow">CONFIRM ACTION</p><h2>{confirmAction === 'retry' ? 'Retry this run?' : 'Cancel this run?'}</h2><p className="subtle">{confirmAction === 'retry' ? 'The original configuration and completed records will be retained.' : 'In-flight writes may finish before the worker stops.'}</p><div className="modal-actions"><button className="ghost" onClick={() => setConfirmAction(null)}>Keep run</button><button className="primary" disabled={busy} onClick={() => void runAction(confirmAction)}>{busy ? 'Working…' : confirmAction === 'retry' ? 'Retry run' : 'Request cancellation'}</button></div></div></div>}
    {showConnectorEditor && <ConnectorEditor initial={connectorEdit} close={() => setShowConnectorEditor(false)} saved={() => { setShowConnectorEditor(false); void refresh() }} fail={setError}/>}
    {showIntegrationEditor && <IntegrationWizard initial={integrationEdit} connectors={connectors} close={() => setShowIntegrationEditor(false)} saved={() => { setShowIntegrationEditor(false); void refresh() }} fail={setError}/>}
  </div>
}

function Empty({ title, text }: { title: string; text: string }) { return <div className="empty"><span>◇</span><h3>{title}</h3><p>{text}</p></div> }
function RunTable({ rows, integrations, open, selected }: { rows: RunSummary[]; integrations: Integration[]; open: (id: string) => void; selected?: string | null }) { return <div className="run-table">{rows.map(r => <button key={r.runId} className={`run-row ${selected === r.runId ? 'chosen' : ''}`} onClick={() => open(r.runId)}><span><strong>{integrations.find(i => i.id === r.integrationId)?.name || 'Integration'}</strong><small>{timestamp(r.createdAtUtc)} · {r.triggerType}</small></span><Status value={r.status}/><span className="arrow">↗</span></button>)}</div> }
function ScheduleForm({ schedule, save, busy }: { schedule: Schedule | null; save: (interval: number, enabled: boolean) => void; busy: boolean }) { const [minutes, setMinutes] = useState(15); useEffect(() => { if (schedule) setMinutes(schedule.intervalSeconds / 60) }, [schedule]); return <div className="schedule-controls"><label>Every <input type="number" min="5" max="43200" value={minutes} onChange={e => setMinutes(Number(e.target.value))}/> minutes</label><button className="ghost" disabled={busy || minutes < 5 || minutes > 43200} onClick={() => save(minutes * 60, !schedule?.enabled)}>{schedule?.enabled ? 'Disable' : 'Enable'}</button>{schedule?.enabled && <button className="text-button" disabled={busy || minutes < 5 || minutes > 43200} onClick={() => save(minutes * 60, true)}>Update interval</button>}</div> }

function ConnectorEditor({ initial, close, saved, fail }: { initial: Connector | null; close: () => void; saved: () => void; fail: (value: string) => void }) {
  const [name, setName] = useState(initial?.name || '')
  const [role, setRole] = useState<'source' | 'destination'>(initial?.role || 'source')
  const [baseUrl, setBaseUrl] = useState(initial?.configuration.baseUrl || '')
  const [pageSize, setPageSize] = useState(initial?.configuration.pageSize || 100)
  const [timeoutSeconds, setTimeoutSeconds] = useState(initial?.configuration.timeoutSeconds || 15)
  const [secret, setSecret] = useState('')
  const [enabled, setEnabled] = useState(initial?.enabled ?? true)
  const [busy, setBusy] = useState(false)
  async function submit(e: React.FormEvent) { e.preventDefault(); setBusy(true); try { const body = { name, kind: 'restOrdersV1', role, configuration: { schemaVersion: 1, baseUrl, authMode: 'apiKeyHeader', pageSize, timeoutSeconds }, secret: secret || (initial ? null : ''), enabled }; if (initial) { const current = await api<Connector>(`/connectors/${initial.id}`); await api(`/connectors/${initial.id}`, 'PUT', body, current.etag || undefined) } else await api('/connectors', 'POST', body); saved() } catch (err) { fail((err as Error).message) } finally { setBusy(false) } }
  return <div className="modal-backdrop" onMouseDown={close}><form className="modal" onMouseDown={e => e.stopPropagation()} onSubmit={submit}><div className="modal-head"><div><p className="eyebrow">CONNECTOR CONFIGURATION</p><h2>{initial ? 'Edit connector' : 'Add connector'}</h2></div><button type="button" className="close" onClick={close}>×</button></div><div className="form-grid"><label>Name<input value={name} maxLength={160} onChange={e => setName(e.target.value)} required /></label><label>Role<select value={role} onChange={e => setRole(e.target.value as 'source'|'destination')}><option value="source">Source</option><option value="destination">Destination</option></select></label><label className="span-two">Base URL<input type="url" value={baseUrl} placeholder="http://demo-systems:8081/commerce" onChange={e => setBaseUrl(e.target.value)} required /></label><label>Page size<input type="number" min="1" max="500" value={pageSize} onChange={e => setPageSize(Number(e.target.value))} required /></label><label>Timeout (seconds)<input type="number" min="1" max="60" value={timeoutSeconds} onChange={e => setTimeoutSeconds(Number(e.target.value))} required /></label><label className="span-two">API key<input type="password" value={secret} onChange={e => setSecret(e.target.value)} required={!initial} placeholder={initial ? 'Leave blank to keep existing secret' : 'Enter API key'} /><small>Secrets are encrypted and never returned by the API.</small></label><label className="check span-two"><input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)}/> Enabled</label></div><div className="modal-actions"><button type="button" className="ghost" onClick={close}>Cancel</button><button className="primary" disabled={busy}>{busy ? 'Saving…' : 'Save connector'}</button></div></form></div>
}

