# Phase 2 simulator HTTP contract

The simulator is a separate ASP.NET Core service. It owns the `demo` PostgreSQL schema; the hub communicates with it through HTTP. Every `/commerce` and `/erp` request requires `X-Api-Key`. Every `/__admin` request requires a different `X-Api-Key` set by `Demo__AdminKey`. The local keys in Compose are development examples only.

Its generated OpenAPI document is available at `/openapi/v1.json`.

## Commerce source

`GET /commerce/orders?pageSize=50&cursor=...&snapshot=seed-v1` returns JSON `{ "items": [...], "snapshot": "seed-v1", "nextCursor": "..." }`. `pageSize` defaults to 100 and may be 1–500. Omit `cursor` and `snapshot` on the first request. Pass the returned snapshot on later pages. The cursor is opaque; `nextCursor: null` ends pagination. An empty page has `items: []` and `nextCursor: null`. Invalid cursors, page sizes, or snapshots return 400. Missing or incorrect API keys return 401.

Each source item has stable `id`, `orderNumber`, `customer.email`, `total`, and `placedAt` fields. The idempotent seed creates 125 synthetic orders with IDs `commerce-0001` through `commerce-0125`. The source dataset is immutable after seeding, so `seed-v1` identifies one stable snapshot. `malformedRecord` fault replaces the first item of one page with an item missing `total`, preserving the rest of the page for record-level validation in Phase 3.

## ERP destination

`PUT /erp/orders/{integrationId}/{sourceRecordId}` requires `X-Api-Key`, `Idempotency-Key: {integrationId:N}:{sourceRecordId}`, and JSON:

```json
{ "externalOrderId": "ORD-0001", "customerEmail": "customer0001@example.test", "amount": 11.25, "placedAtUtc": "2026-01-01T12:01:00Z" }
```

The first write returns 201 with `{ "destinationOrderId": "...", "outcome": "created" }`. An identical repeat returns 200 `unchanged`; changed fields return 200 `updated`, keeping the same destination ID. PostgreSQL uniquely indexes `(integrationId, sourceRecordId)` and the idempotency key. Invalid order data or key returns 400. `lostResponse` commits the write before delaying the response, exercising safe retry after an apparent timeout.

## Fault controls

`POST /__admin/faults` accepts `{ "target": "source|destination", "mode": "429|503|timeout|invalidJson|malformedRecord|lostResponse", "remaining": 1, "delayMilliseconds": 30000 }`. The fault is consumed by the next matching request, up to `remaining` requests. `malformedRecord` works only on the source; `lostResponse` only on the destination. `DELETE /__admin/faults` clears a pending fault. These controls are in memory and reset on simulator restart. `POST /__admin/seed` adds the 125 source orders if absent. `GET /__admin/orders/count` returns the ERP row count.

`POST /__admin/reset` requires the admin API key and `X-Confirm-Reset: reset-synthetic-demo`. It deletes only synthetic ERP destination rows, clears pending faults, and ensures the 125 source orders exist. Disable schedules and wait for active runs before using it. Hub integration definitions and run history are retained.

The REST adapters enforce a 2 MiB response limit, a configured timeout of 1–60 seconds, cancellation, page bounds, and a stable idempotency key. They classify 429/5xx/network errors as transient; 401/403 as authentication failures; other 4xx as permanent; malformed JSON/shape as malformed responses; and cancellation by timeout as a timeout. Use `RestHttpClientFactory.Create()` when constructing adapters so redirects cannot forward API keys to another host. Integration configuration still applies the approved-host check in the hub.
