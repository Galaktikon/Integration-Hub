# Enterprise Integration Hub

A portfolio-scale integration platform for moving synthetic commerce orders into a synthetic ERP system. The project demonstrates durable execution, safe retries, idempotent writes, restart recovery, and operator diagnostics.

## Phase 0 decisions

- Backend: C# on .NET 10 LTS, ASP.NET Core, EF Core, and Npgsql.
- Frontend: React and TypeScript, built as static assets.
- State: PostgreSQL locally; Neon PostgreSQL for the hosted demo.
- Runtime: one ASP.NET Core process hosts the HTTP API, scheduler, and background worker. PostgreSQL coordinates work even when deployments briefly overlap.
- Hosting: Render static site for the UI, Render web service for the API and worker, and a separately deployed HTTP simulator for the synthetic source and destination.
- Delivery: at-least-once processing with destination-enforced idempotency. No distributed exactly-once claim.

The current repository contains the Phase 0 specification. Application code and runnable Docker Compose services begin in Phase 1.

## Design documents

| Document | Purpose |
| --- | --- |
| [Requirements](docs/requirements.md) | MVP scope, use cases, and acceptance checks |
| [Architecture](docs/architecture.md) | Components, data flow, recovery, security, and deployment |
| [Data model](docs/data-model.md) | Tables, constraints, indexes, and state transitions |
| [API contracts](docs/api-contracts.md) | Versioned endpoints, request shapes, responses, and errors |
| [ADR 0001](docs/adr/0001-modular-monolith.md) | Reason for a combined API and worker modular monolith |

## Planned development workflow

The intended local environment uses Docker Compose for the hub, simulator, frontend, and PostgreSQL. Phase 1 will add the solution, containers, EF Core migrations, seed command, and CI. The quick start will then be:

```text
docker compose up --build
dotnet test
```

Migrations will run as an explicit deployment step, not from every application instance at startup. Seed and reset commands will be limited to development and demo data. The hosted Render service will use a paid always-on web instance so its scheduler and worker can run without an incoming request.

## Conventions

- UTC timestamps in storage and API responses; `camelCase` JSON; UUID identifiers; `/api/v1` routes.
- Domain rules have no HTTP, EF Core, or UI dependencies. Connector adapters own external HTTP and error translation.
- Database migrations are versioned and reviewed with the application change that needs them.
- Connector credentials, database URLs, and encryption keys come from environment secrets and never enter Git or ordinary API responses.
- Critical behavior is tested against PostgreSQL and the HTTP simulator, especially duplicate writes, concurrent claims, and crash recovery.

## Status

Phase 0 specification. No application has been built or deployed yet.
