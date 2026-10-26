# Requirements and MVP scope

Status: Phase 0 baseline, 2026-10-26. This document defines the first release and its acceptance checks.

## Goal

An administrator can configure a synthetic Commerce Orders to ERP Orders integration, run a paginated sync, inspect outcomes, inject failures, and recover without duplicate destination orders or lost execution state.

## Required capabilities

| ID | Requirement | Acceptance evidence |
| --- | --- | --- |
| R1 | Configure one REST source and one REST destination per integration, with supported authentication and pagination settings. | Invalid configuration is rejected; secret values are never returned. |
| R2 | Map nested source fields to destination fields using a versioned declarative schema and a small fixed transform set. | Mapping validates before a run; a run stores an immutable configuration snapshot. |
| R3 | Fetch all pages of synthetic orders, validate each record, and upsert valid orders. | At least 100 orders sync; malformed records have record-level errors. |
| R4 | Queue manual runs durably and execute them outside the HTTP request in the combined API and worker process. | Run creation returns a committed run ID immediately; work survives process restart. |
| R5 | Persist page checkpoints, record work, attempts, errors, and final results in PostgreSQL. | Run history and record details remain available after restart. |
| R6 | Retry transient failures with bounded backoff; keep permanent data failures terminal for that run. | A 503 or timeout recovers; malformed data is not retried blindly. |
| R7 | Use stable destination identity and idempotency keys across attempts and runs. | A committed write followed by a lost response produces one destination order. |
| R8 | Allow at most one active run per integration and prevent stale workers from committing after lease loss. | Concurrent manual and scheduled triggers cannot create overlapping active runs. |
| R9 | Provide a React operations UI for configuration, manual runs, progress, record errors, and eligible recovery actions. | A reviewer completes the demo from the browser. |
| R10 | Secure administrative actions and connector destinations in a hosted demo. | Unauthenticated requests cannot mutate state; outbound hosts are allowlisted. |
| R11 | Provide migrations, CI, structured logs, health checks, and reproducible tests. | Clean checkout setup, tests, and failure demo follow documented commands. |

## Quality and operational requirements

- The API never waits for a full synchronization. `202 Accepted` means the run request committed in PostgreSQL.
- Processing is at least once. The destination must turn repeated writes for the same integration and source ID into one logical order.
- On graceful shutdown the worker stops claiming new work. After a crash or forced stop, a successor reclaims expired leases and resumes durable work.
- A failure in one record does not prevent unrelated valid records from completing. A source-wide error may fail the run after its retry budget is exhausted.
- Counters count distinct logical records, not HTTP attempts. Final counts reconcile with persisted record outcomes.
- HTTP timeouts, page size, payload size, attempt count, and run duration have configured upper bounds.
- Secrets and raw customer payloads do not appear in normal API responses or logs. The demo uses synthetic data only.
- Admin actions require authentication and HTTPS in the hosted environment. The UI origin is the only permitted browser CORS origin.

## Primary demo journeys

1. Create or edit a connector and integration; validation catches an unsupported mapping before execution.
2. Run a multi-page order sync; observe created, updated or unchanged, and failed record outcomes.
3. Inject destination 503 or lost response; observe persisted attempts and an eventual safe retry.
4. Submit malformed source data; inspect a permanent record error while valid orders finish.
5. Restart the application during a run; observe lease recovery and completion without duplicate ERP orders.
6. Submit simultaneous run requests or overlap a scheduled trigger; observe one accepted run and one conflict or skipped schedule occurrence.
7. Use the UI to retry eligible failed work or start a fresh run after correcting data or configuration.

## Fixed MVP boundaries

- One business entity: orders. One source connector and one destination connector per integration.
- REST/JSON adapters with API-key authentication for the simulator. Connector configuration is declarative, with approved hosts only in the hosted demo.
- Sequential record processing initially. No throughput or production-scale claim until measured.
- Single organization and one protected administrator role.
- Database-backed scheduling is included for the hosted demo, with a minimum interval and no promise of exact-to-the-second execution.
- The simulator is a separate HTTP application. It owns its source and destination data and exposes fault controls only to an administrator.

## Explicit exclusions

Real vendor accounts, OAuth refresh, webhooks, multi-tenancy, user-authored scripts, visual mapping, multi-destination fan-out, distributed queues, Kafka, Kubernetes, and exactly-once delivery are outside the MVP.

## Release acceptance

The release is complete when a second person can follow the README, configure the demo in the browser, sync at least 100 orders, reproduce transient and permanent failures, restart during work, retry safely, and explain the result from run history. CI and hosted smoke tests must pass. The public demo must be protected and use only synthetic data.

## Traceability

| Journey | Primary design | Persisted state | API operation |
| --- | --- | --- | --- |
| Configure | Architecture: configuration and adapters | `connector`, `integration`, `schedule` | Connector and integration CRUD/validate |
| Sync | Architecture: worker pipeline | `sync_run`, `sync_record`, `sync_attempt` | Create run, get run and records |
| Recover transient failure | Architecture: retry and lease recovery | `next_attempt_at_utc`, lease, attempts | Get run; retry failed run if terminal |
| Diagnose invalid record | Architecture: validation | Error code and safe message on `sync_record` | Get run records |
| Avoid overlap | Architecture: atomic claim | Partial unique run index and fencing generation | Create run, 409 conflict |
| Schedule | Architecture: DB scheduler | `integration_schedule` | Schedule CRUD |
