# API contracts

Status: Phase 0 contract, 2026-10-26. All hub routes begin `/api/v1`, use JSON with `camelCase` names, UUID identifiers, UTC ISO 8601 timestamps, and `application/problem+json` for API errors. The OpenAPI document generated during implementation must follow this specification.

## Authentication and general behavior

All connector, integration, schedule, run, and record routes require an authenticated administrator. The health routes are unauthenticated and contain no secret data. Hosted requests use HTTPS. Connector credentials are accepted only on create/update and are never returned. List responses use `items` and `nextCursor`; the cursor is opaque. Default page size is 25, maximum 100. Invalid or expired cursors return `400 invalid_cursor`.

Mutable resources expose an `ETag`; `PUT` requires `If-Match` and returns `412 precondition_failed` on a stale version. Run creation uses the PostgreSQL unique constraint as its final overlap guard. The API does not execute synchronization inline.

## Endpoints

| Method | Path | Success | Purpose |
| --- | --- | --- | --- |
| GET | `/connectors` | 200 | List safe connector summaries. |
| POST | `/connectors` | 201 | Create a supported REST connector. |
| GET | `/connectors/{id}` | 200 | Read non-secret configuration. |
| PUT | `/connectors/{id}` | 200 | Replace configuration or rotate a secret. |
| DELETE | `/connectors/{id}` | 204 | Delete a connector not used by any integration. Requires `If-Match`. |
| POST | `/connectors/{id}/test` | 200 | Validate approved endpoint and authentication with a bounded request. |
| GET | `/integrations` | 200 | List integrations. |
| POST | `/integrations` | 201 | Create a draft integration. |
| GET | `/integrations/{id}` | 200 | Read integration details. |
| PUT | `/integrations/{id}` | 200 | Replace editable configuration. |
| DELETE | `/integrations/{id}` | 204 | Delete an integration without run history. Requires `If-Match`. |
| POST | `/integrations/{id}/validate` | 200 | Validate connector capabilities, mapping, and policy. |
| GET | `/integrations/{id}/schedule` | 200 | Read its schedule; 404 if absent. |
| PUT | `/integrations/{id}/schedule` | 200 | Create or replace an interval schedule. |
| DELETE | `/integrations/{id}/schedule` | 204 | Disable and remove its schedule. |
| POST | `/integrations/{id}/runs` | 202 | Commit a manual run request. |
| GET | `/integrations/{id}/runs` | 200 | Paginated run history. |
| GET | `/runs/{runId}` | 200 | Run state, counters, and allowed actions. |
| GET | `/runs/{runId}/records` | 200 | Paginated record outcomes; filter by status. |
| GET | `/runs/{runId}/records/{recordId}/attempts` | 200 | Attempt history without raw payloads. |
| POST | `/runs/{runId}/retry` | 202 | Requeue an eligible failed run with the same snapshot. |
| POST | `/runs/{runId}/cancel` | 202 | Request cancellation of queued or running work. |
| GET | `/health/live` | 200 | Process liveness. |
| GET | `/health/ready` | 200 or 503 | Database and schema readiness. |

`POST /runs/{runId}/cancel` is a request: in-flight destination writes may finish, and committed results are retained. A queued run may transition directly to `cancelled` in the request transaction. A running run sets `cancellation_requested_at_utc`, records a `cancellation_requested` event, and becomes `cancelled` when the worker observes it.

## Connector contract

`POST /connectors` request:

```json
{
  "name": "Demo Commerce",
  "kind": "restOrdersV1",
  "role": "source",
  "configuration": {
    "schemaVersion": 1,
    "baseUrl": "https://demo-systems.example.com/commerce",
    "authMode": "apiKeyHeader",
    "pageSize": 100,
    "timeoutSeconds": 15
  },
  "secret": "supplied-once",
  "enabled": true
}
```

`201` response includes an `id`, the non-secret fields, and `secretSet: true`; it never includes `secret`, `encryptedSecret`, or authorization headers. `PUT` may omit `secret` to retain the current secret. `baseUrl` must match an approved host and pass outbound-address checks. Unsupported kinds, roles, auth modes, and out-of-range limits return `422 invalid_configuration`.

## Integration and mapping contract

`POST /integrations` request:

```json
{
  "name": "Commerce Orders to ERP Orders",
  "sourceConnectorId": "11111111-1111-4111-8111-111111111111",
  "destinationConnectorId": "22222222-2222-4222-8222-222222222222",
  "entityType": "orders",
  "mapping": {
    "schemaVersion": 1,
    "fields": [
      { "source": "orderNumber", "destination": "externalOrderId", "required": true },
      { "source": "customer.email", "destination": "customerEmail", "required": true, "transform": "trim" },
      { "source": "total", "destination": "amount", "required": true, "transform": "decimal" },
      { "source": "placedAt", "destination": "placedAtUtc", "required": true, "transform": "utcDate" }
    ]
  },
  "validation": { "schemaVersion": 1, "amountMinimum": 0 },
  "retryPolicy": { "maxAttempts": 4, "baseDelaySeconds": 2, "maxDelaySeconds": 60, "maxRecoveries": 2 },
  "enabled": false
}
```

Fields have dotted paths with ASCII letters, numbers, and underscores; array traversal and expressions are unsupported in version 1. Destination fields must be unique. Transforms are `trim`, `decimal`, or `utcDate`. The API checks required destination fields and connector roles before enabling. `POST /integrations/{id}/validate` returns `{"valid":true,"issues":[]}` or `{"valid":false,"issues":[{"path":"mapping.fields[2]","code":"unsupported_transform","message":"..."}]}`. A structurally invalid request still returns `422`.

`PUT /integrations/{id}/schedule` accepts `{"intervalSeconds":900,"enabled":true}` and returns `nextRunAtUtc`. The minimum interval is 300 seconds. Schedules use UTC and are best effort; a due occurrence is skipped and recorded if another run is active.

## Manual run and retry

`POST /integrations/{id}/runs` has an empty body. A successful response is `202 Accepted` with a `Location: /api/v1/runs/{runId}` header:

```json
{
  "runId": "33333333-3333-4333-8333-333333333333",
  "integrationId": "44444444-4444-4444-8444-444444444444",
  "status": "queued",
  "triggerType": "manual",
  "createdAtUtc": "2026-10-26T18:00:00Z"
}
```

It returns `409 active_run_exists` if the integration already has a queued or running run, `409 integration_disabled` if disabled, and `422 invalid_configuration` if validation fails. It never returns `202` before the run transaction commits.

`POST /runs/{runId}/retry` has an empty body. It is allowed only for a `failed` run with a retryable last error and remaining recovery budget. It preserves the same run ID, configuration and source snapshot, successful record states, and attempt history; it increments `recoveryGeneration` and changes the run to `queued`. Its `202` response has the same `Location` and includes `runId`, `status`, and `recoveryGeneration`. Concurrent retry requests yield one `202` and one `409 run_not_retryable`. For permanent data errors, the administrator corrects data or mapping and starts a new manual run.

## Run response and count semantics

`GET /runs/{runId}` example:

```json
{
  "runId": "33333333-3333-4333-8333-333333333333",
  "integrationId": "44444444-4444-4444-8444-444444444444",
  "status": "completed_with_errors",
  "triggerType": "manual",
  "recoveryGeneration": 0,
  "counts": {
    "fetched": 100,
    "created": 91,
    "updated": 4,
    "unchanged": 2,
    "failedPermanent": 3,
    "failedRetryable": 0,
    "pending": 0
  },
  "sourceComplete": true,
  "allowedActions": [],
  "createdAtUtc": "2026-10-26T18:00:00Z",
  "startedAtUtc": "2026-10-26T18:00:01Z",
  "finishedAtUtc": "2026-10-26T18:00:18Z"
}
```

Counts derive from distinct `sync_record` states, not attempts. `pending` aggregates records in `pending`, `processing`, or `retry_wait`; `fetched` equals the sum of all outcome counts shown, including `pending`. During a failed or running execution, `pending` may be positive and `sourceComplete` may be false. The response never claims a total source-record count before pagination completes. `allowedActions` may contain `retry` or `cancel` according to server-side state and authorization.

`GET /runs/{runId}/records?status=failed_permanent&pageSize=25` returns `items` with record ID, source ID, status, attempt count, error category/code/safe message, destination ID, and timestamps. It does not return `source_payload_jsonb`. The attempts route returns attempt number, recovery generation, outcome, HTTP status, duration, and safe error code.

## Error envelope

Use RFC 9457 Problem Details with a stable extension `code`, a `traceId`, and `errors` for field validation. Example:

```json
{
  "type": "https://integration-hub.example/problems/active-run-exists",
  "title": "An active run already exists",
  "status": 409,
  "detail": "This integration already has a queued or running run.",
  "code": "active_run_exists",
  "traceId": "00-example-trace-id"
}
```

| Status | Stable code | Meaning |
| --- | --- | --- |
| 400 | `invalid_cursor`, `malformed_request` | Request syntax or pagination cursor is invalid. |
| 401 | `authentication_required` | No valid admin identity. |
| 403 | `forbidden` | Identity lacks the required role. |
| 404 | `not_found` | Resource does not exist or is not visible. |
| 409 | `active_run_exists`, `integration_disabled`, `run_not_retryable` | State conflicts with action. |
| 412 | `precondition_failed` | `If-Match` version is stale. |
| 428 | `precondition_required` | `If-Match` is missing on an update or delete. |
| 422 | `invalid_configuration` | Supported request shape violates configuration rules. |
| 503 | `dependency_unavailable` | Required storage or downstream dependency is unavailable. |

Error `detail` and record `safeErrorMessage` never include secrets, connection strings, raw payloads, or stack traces. Unexpected failures receive a trace ID and are logged with restricted details.

## Demo systems HTTP contract

The independent simulator exposes `GET /commerce/orders?cursor=...&pageSize=...&snapshot=...` returning `items`, `snapshot`, and `nextCursor`; and `PUT /erp/orders/{integrationId}/{sourceRecordId}` with a stable `Idempotency-Key` and canonical order JSON. It returns `201 created`, `200 updated`, or `200 unchanged` with a destination order ID and outcome field. The same identity cannot create a second ERP row. Fault injection and source seeding are administrator-only demo routes specified in the [Phase 2 simulator contract](simulator-contract.md).
