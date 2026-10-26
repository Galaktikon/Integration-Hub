import { useState } from 'react'
import { api, type Connector, type Field, type Integration } from './api'

const blankFields: Field[] = [
  { source: 'orderNumber', destination: 'externalOrderId', required: true, transform: 'trim' },
  { source: 'customer.email', destination: 'customerEmail', required: true, transform: 'trim' },
  { source: 'total', destination: 'amount', required: true, transform: 'decimal' },
  { source: 'placedAt', destination: 'placedAtUtc', required: true, transform: 'utcDate' },
]

export default function IntegrationWizard({ initial, connectors, close, saved, fail }: {
  initial: Integration | null; connectors: Connector[]; close: () => void; saved: () => void; fail: (value: string) => void
}) {
  const [step, setStep] = useState(0)
  const [name, setName] = useState(initial?.name || '')
  const [sourceConnectorId, setSource] = useState(initial?.sourceConnectorId || '')
  const [destinationConnectorId, setDestination] = useState(initial?.destinationConnectorId || '')
  const [fields, setFields] = useState<Field[]>(initial?.mapping.fields || blankFields)
  const [amountMinimum, setMinimum] = useState(initial?.validation.amountMinimum ?? 0)
  const [maxAttempts, setAttempts] = useState(initial?.retryPolicy.maxAttempts ?? 4)
  const [baseDelaySeconds, setBaseDelay] = useState(initial?.retryPolicy.baseDelaySeconds ?? 2)
  const [maxDelaySeconds, setMaxDelay] = useState(initial?.retryPolicy.maxDelaySeconds ?? 60)
  const [maxRecoveries, setRecoveries] = useState(initial?.retryPolicy.maxRecoveries ?? 2)
  const [enabled, setEnabled] = useState(initial?.enabled ?? false)
  const [busy, setBusy] = useState(false)
  const [localError, setLocalError] = useState('')
  const source = connectors.find(c => c.id === sourceConnectorId)
  const destination = connectors.find(c => c.id === destinationConnectorId)
  const mapped = fields.filter(f => f.source.trim()).length
  const stepErrors = [
    [!name.trim() && 'Enter an integration name.', !sourceConnectorId && 'Choose a source connector.', !destinationConnectorId && 'Choose a destination connector.', sourceConnectorId === destinationConnectorId && !!sourceConnectorId && 'Source and destination must differ.'].filter(Boolean),
    [fields.some(f => !f.source.trim()) && 'Map every source field.', new Set(fields.map(f => f.destination)).size !== fields.length && 'Destination fields must be unique.'].filter(Boolean),
    [amountMinimum < 0 && 'Minimum amount must be zero or greater.', maxAttempts < 1 && 'Allow at least one attempt.', baseDelaySeconds < 1 && 'Base delay must be at least one second.', maxDelaySeconds < baseDelaySeconds && 'Maximum delay must be at least the base delay.', maxRecoveries < 0 && 'Manual recoveries cannot be negative.'].filter(Boolean),
  ]
  function changeField(index: number, change: Partial<Field>) { setFields(old => old.map((value, i) => i === index ? { ...value, ...change } : value)) }
  function continueStep() {
    if (stepErrors[step].length) { setLocalError(String(stepErrors[step][0])); return }
    setLocalError(''); setStep(step + 1)
  }
  async function submit(e: React.FormEvent) {
    e.preventDefault()
    if (step !== 2) { continueStep(); return }
    const problem = stepErrors.flat()[0]
    if (problem) { setLocalError(String(problem)); return }
    setBusy(true); setLocalError('')
    try {
      const body = { name, sourceConnectorId, destinationConnectorId, entityType: 'orders', mapping: { schemaVersion: 1, fields }, validation: { schemaVersion: 1, amountMinimum }, retryPolicy: { maxAttempts, baseDelaySeconds, maxDelaySeconds, maxRecoveries }, enabled }
      if (initial) { const current = await api<Integration>(`/integrations/${initial.id}`); await api(`/integrations/${initial.id}`, 'PUT', body, current.etag || undefined) }
      else await api('/integrations', 'POST', body)
      saved()
    } catch (err) { const message = (err as Error).message; setLocalError(message); fail(message) }
    finally { setBusy(false) }
  }
  return <div className="wizard-backdrop" role="dialog" aria-modal="true" aria-label={initial ? 'Edit integration' : 'New integration'}><form className="wizard" onSubmit={submit} noValidate>
    <header className="wizard-header"><div><p className="eyebrow">INTEGRATION SETUP</p><h1>{initial ? 'Edit integration' : 'New integration'}</h1><p>Configure how Commerce orders move to the ERP.</p></div><button type="button" className="ghost" onClick={close}>Close</button></header>
    <div className="wizard-steps" aria-label="Setup steps">{['Connectors', 'Field mapping', 'Validation & recovery'].map((label, index) => <button type="button" key={label} className={`wizard-step ${step === index ? 'current' : ''} ${step > index ? 'complete' : ''}`} onClick={() => { if (index <= step) { setStep(index); setLocalError('') } }} aria-current={step === index ? 'step' : undefined}><span>{step > index ? '✓' : index + 1}</span>{label}</button>)}</div>
    <div className="wizard-layout"><div className="wizard-content panel">
      {step === 0 && <><h2>Choose your connectors</h2><p className="subtle">Name the flow, then select its enabled source and destination.</p><div className="form-grid"><label className="span-two">Integration name<input value={name} maxLength={160} onChange={e => setName(e.target.value)} placeholder="Commerce Orders → ERP" required /></label><label>Source connector<select value={sourceConnectorId} onChange={e => setSource(e.target.value)} required><option value="">Select source</option>{connectors.filter(c => c.role === 'source' && (c.enabled || c.id === sourceConnectorId)).map(c => <option key={c.id} value={c.id}>{c.name}{!c.enabled ? ' (disabled)' : ''}</option>)}</select></label><label>Destination connector<select value={destinationConnectorId} onChange={e => setDestination(e.target.value)} required><option value="">Select destination</option>{connectors.filter(c => c.role === 'destination' && (c.enabled || c.id === destinationConnectorId)).map(c => <option key={c.id} value={c.id}>{c.name}{!c.enabled ? ' (disabled)' : ''}</option>)}</select></label></div>{(!connectors.some(c => c.role === 'source' && c.enabled) || !connectors.some(c => c.role === 'destination' && c.enabled)) && <p className="message notice">You need an enabled source and destination connector before saving this integration.</p>}</>}
      {step === 1 && <><h2>Map required ERP fields</h2><p className="subtle">Enter the source field path that should populate each ERP field. Choose a transform only when needed.</p><div className={`mapping-summary ${mapped === fields.length ? 'complete' : ''}`}>{mapped} of {fields.length} fields mapped</div><div className="mapping-table"><div className="mapping-header"><span>ERP field</span><span>Commerce field</span><span>Transform</span></div>{fields.map((field, index) => <div className="mapping-item" key={`${field.destination}-${index}`}><div><strong>{field.destination}</strong>{field.required && <small>Required</small>}</div><label><span className="sr-only">Source field for {field.destination}</span><input value={field.source} onChange={e => changeField(index, { source: e.target.value })} placeholder="Source field path" required={field.required} /></label><label><span className="sr-only">Transform for {field.destination}</span><select value={field.transform || ''} onChange={e => changeField(index, { transform: e.target.value || null })}><option value="">None</option><option value="trim">Trim</option><option value="decimal">Decimal</option><option value="utcDate">UTC date</option></select></label></div>)}</div></>}
      {step === 2 && <><h2>Validation & recovery</h2><p className="subtle">Set the order validation threshold and how the worker should retry temporary failures.</p><div className="form-grid"><label>Minimum amount<input type="number" min="0" step="0.01" value={amountMinimum} onChange={e => setMinimum(Number(e.target.value))} /></label><label>Max attempts<input type="number" min="1" max="10" value={maxAttempts} onChange={e => setAttempts(Number(e.target.value))} /></label><label>Base delay (seconds)<input type="number" min="1" max="300" value={baseDelaySeconds} onChange={e => setBaseDelay(Number(e.target.value))} /></label><label>Max delay (seconds)<input type="number" min="1" max="3600" value={maxDelaySeconds} onChange={e => setMaxDelay(Number(e.target.value))} /></label><label>Manual recoveries<input type="number" min="0" max="10" value={maxRecoveries} onChange={e => setRecoveries(Number(e.target.value))} /></label><label className="check"><input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} /> Enabled after saving</label></div><p className="subtle">Schedules are configured from the integration details after saving.</p></>}
      {localError && <p className="message error" role="alert">{localError}</p>}
    </div><aside className="wizard-summary panel"><h3>Integration summary</h3><dl><div><dt>Name</dt><dd>{name || 'Not named yet'}</dd></div><div><dt>Source</dt><dd>{source?.name || 'Select a source'}</dd></div><div><dt>Destination</dt><dd>{destination?.name || 'Select a destination'}</dd></div><div><dt>Field mappings</dt><dd>{mapped} of {fields.length} complete</dd></div><div><dt>Status after saving</dt><dd>{enabled ? 'Enabled' : 'Disabled'}</dd></div></dl></aside></div>
    <footer className="wizard-footer"><button type="button" className="ghost" onClick={() => { if (step === 0) close(); else { setStep(step - 1); setLocalError('') } }}>{step === 0 ? 'Cancel' : 'Back'}</button><button className="primary" disabled={busy}>{busy ? 'Saving…' : step === 2 ? 'Save integration' : step === 0 ? 'Continue to field mapping →' : 'Continue to validation →'}</button></footer>
  </form></div>
}
