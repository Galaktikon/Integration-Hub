# ADR 0001: Combined API and worker modular monolith

Date: 2026-10-26

Status: Accepted; hosting plan updated for the Phase 6 free demo on 2026-10-27

## Context

The integration hub needs responsive HTTP administration, durable scheduled and manual work, restart recovery, and a practical portfolio deployment. The first release has one business domain and a small number of connectors. Render hosts the static UI and hub; Neon hosts PostgreSQL.

## Decision

Build one .NET 10 ASP.NET Core application with clearly separated API, application, domain, connector, infrastructure, and worker modules. Host the HTTP API, database-backed scheduler, and `BackgroundService` worker in one Render Free web service. Keep the synthetic source and destination behind a second Render Free web service. PostgreSQL stores all work and coordinates claims, leases, fencing generations, retries, and schedules.

## Why

One deployable hub is simpler to build, operate, and explain at this scale. The worker remains asynchronous from API requests, and the database is the durable boundary. Module boundaries keep the source/destination adapters replaceable. The design supports separating the worker into another process later because it owns no process-local durable queue or state.

## Consequences

- CPU or memory pressure from synchronization can affect API latency. Start with sequential record processing, bounded pages and requests, and measure before adding concurrency.
- Deployments briefly run old and new instances together. PostgreSQL ownership and fencing are required even with one configured replica.
- Render Free sleep pauses the worker, scheduler, and retries until an incoming request wakes the combined hub. The free hosted demo therefore supports manual runs and restart recovery but does not promise timely unattended scheduling. This is a known demo limitation.
- Startup reclaims only expired leases; it never assumes that a prior destination write failed. It repeats uncertain writes with the same idempotency key.
- A separate worker service remains a future option if volume or latency measurements justify it, without changing API contracts or run state.

## Alternatives considered

A separate API and worker deployment provides stronger resource isolation but adds a service and coordination overhead to the initial portfolio release. An in-memory queue is insufficient because it would lose work on restart. An always-on plan would provide timely background scheduling, but would make this hosted demo paid.
