# Enterprise Integration Hub

A portfolio-scale integration platform for synthetic Commerce Orders to ERP Orders. Phases 1–3 provide the .NET 10 foundation, hub configuration API, a separate PostgreSQL-backed Commerce/ERP simulator, HTTP adapters, and a durable first sync. Scheduling, richer retry and recovery controls, and the React UI arrive in later phases.

## Current architecture

- One ASP.NET Core process hosts the API and a background worker. The worker claims queued runs, checkpoints source pages, persists record outcomes, and reclaims interrupted runs after lease expiry.
- EF Core stores hub configuration and execution state in PostgreSQL. Local Compose uses PostgreSQL 17; the hosted demo will use Neon.
- Connector API keys are encrypted with AES-256-GCM using an environment-supplied key. API responses only report `secretSet`.
- The demo systems own a separate `demo` schema and expose paginated Commerce orders, idempotent ERP writes, and administrator-only fault controls. The worker accesses them through HTTP adapters.
- Authentication arrives in Phase 5. The API refuses to start outside `Development`; do not expose this environment publicly.

Phase 3 handles a clean run, permanent record validation errors, and replay of an interrupted write. A transient connector failure currently fails the run and preserves its checkpoint. Bounded retry schedules, cancellation, explicit run retry, full stale-worker fencing, and interval scheduling are Phase 4 work.

## Manual setup for local Phases 1–3

1. Install [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/) and enable the Linux container engine. Start Docker Desktop and wait until `docker info` succeeds. On Windows, its WSL 2 backend must be enabled during setup.
2. Clone or open this repository. No Neon or Render account is needed for Phase 3. Docker Compose builds the application inside an image, so a host .NET SDK is optional for this path.
3. From the repository root, run:

   ```powershell
   docker compose up --build -d
   docker compose ps
   ```

   Compose starts PostgreSQL, applies the hub and simulator migrations, then starts both services. The first image build downloads .NET and NuGet dependencies. Check `http://localhost:8080/api/v1/health/ready` and `http://localhost:8081/health/ready` for `{"status":"ready"}`.
4. Seed two synthetic connector configurations:

   ```powershell
   docker compose run --no-deps --rm hub seed-demo
   ```

   This command is idempotent. It adds `Demo Commerce` and `Demo ERP` connectors with local-only API keys. Then seed 125 synthetic source orders:

   ```powershell
   docker compose --profile seed run --rm demo-seed
   ```

   Both seed commands are safe to repeat.
5. List the seeded connectors:

   ```powershell
   Invoke-RestMethod http://localhost:8080/api/v1/connectors
   ```

   Responses include IDs and `secretSet: true`, never the API key. The generated OpenAPI JSON is at `http://localhost:8080/openapi/v1.json`.
6. Try the source and a transient fault:

   ```powershell
   $sourceHeaders = @{ 'X-Api-Key' = 'local-demo-key' }
   Invoke-RestMethod 'http://localhost:8081/commerce/orders?pageSize=50' -Headers $sourceHeaders
   $adminHeaders = @{ 'X-Api-Key' = 'local-demo-admin-key' }
   Invoke-RestMethod 'http://localhost:8081/__admin/faults' -Method Post -Headers $adminHeaders -ContentType 'application/json' -Body '{"target":"source","mode":"503","remaining":1}'
   try { Invoke-RestMethod 'http://localhost:8081/commerce/orders?pageSize=50' -Headers $sourceHeaders } catch { Write-Host 'Expected one-time 503 fault' }
   Invoke-RestMethod 'http://localhost:8081/commerce/orders?pageSize=50' -Headers $sourceHeaders
   ```

   The next source request returns 503; the following one succeeds. See [simulator contract](docs/simulator-contract.md) for destination writes and other faults.
7. Run the complete sync from PowerShell:

   ```powershell
   .\scripts\phase3-demo.ps1 -InjectMalformedRecord
   .\scripts\phase3-demo.ps1
   ```

   The script creates an enabled integration if needed, submits a manual run, waits for completion, and prints persisted counts and record errors. The first run should fetch 125 orders and show one `failed_permanent` record because the injected item lacks `total`. The second run should complete with 125 valid orders; previously written ERP orders are `unchanged`. The same script can be run again without duplicating ERP rows. Run details remain available at `/api/v1/runs/{runId}` after `docker compose restart hub`.

   To inspect records and attempts directly, use the run ID printed by the script:

   ```powershell
   Invoke-RestMethod "http://localhost:8080/api/v1/runs/$runId/records?status=failed_permanent"
   Invoke-RestMethod "http://localhost:8080/api/v1/integrations/$integrationId/runs"
   ```

   Set `$runId` and `$integrationId` to the values in the script output. Run and record lists accept `pageSize` (1–100) and an opaque `cursor` returned as `nextCursor`.

If port 5432, 8080, or 8081 is already in use, change only the left side of the matching `ports` entry in `compose.yaml`. If you change port 8080, use the new host port in browser and PowerShell requests. Container-to-container addresses stay unchanged. To stop the stack, run `docker compose down`; omit `-v` to retain local database data.

## Build and test without running the hub in Docker

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) if you want to run `dotnet` commands on the host. Confirm `dotnet --version` reports `10.0.403` or a newer .NET 10 feature band; the runtime alone is insufficient. With Docker Desktop running and the Compose PostgreSQL service started:

```powershell
dotnet tool restore
dotnet restore IntegrationHub.sln
dotnet build IntegrationHub.sln --no-restore
docker compose exec -T postgres createdb -U hub integration_hub_test
$env:INTEGRATION_TEST_DATABASE = 'Host=localhost;Port=5432;Database=integration_hub_test;Username=hub;Password=local_only_password'
dotnet test IntegrationHub.sln --no-build
```

Create `integration_hub_test` only once; `createdb` reports that it already exists on later runs. The integration tests migrate their own schemas and use this separate database so the live Compose worker cannot claim test runs. CI applies both migrations. Clear the environment variable afterward with `Remove-Item Env:INTEGRATION_TEST_DATABASE`. To run only unit tests without PostgreSQL, use `dotnet test --filter 'FullyQualifiedName!~ConfigurationApiTests&FullyQualifiedName!~DemoSystemsTests&FullyQualifiedName!~SyncPipelineTests'`.

To run services directly on Windows, start PostgreSQL with `docker compose up -d postgres`, migrate each project with `dotnet run --project src/IntegrationHub.Api -- migrate` and `dotnet run --project src/IntegrationHub.DemoSystems -- migrate`, seed with `dotnet run --project src/IntegrationHub.DemoSystems -- seed`, then start both projects. Development settings contain only local example credentials. Override them with environment variables for any nonlocal environment.

## Configuration and migrations

`ConnectionStrings__Hub` and `ConnectionStrings__Demo` are PostgreSQL connection strings. `Security__EncryptionKey` is a base64-encoded 32-byte AES key; keep it stable across restarts or existing encrypted connector secrets cannot be read. `Security__AllowedConnectorHosts` is a comma-separated list of approved connector hostnames. The local example allows `demo-systems` and `localhost`. `Demo__BaseUrl` supplies the simulator base URL used by the hub seed command. The simulator requires distinct `Demo__ApiKey` and `Demo__AdminKey` values.

Migrations run as an explicit command, not on API startup:

```powershell
dotnet run --project src/IntegrationHub.Api -- migrate
```

For a future schema change, edit `HubDbContext`, then run:

```powershell
dotnet tool restore
dotnet tool run dotnet-ef migrations add DescriptiveName --project src/IntegrationHub.Infrastructure --startup-project src/IntegrationHub.Infrastructure --output-dir Migrations
dotnet tool run dotnet-ef migrations has-pending-model-changes --project src/IntegrationHub.Infrastructure --startup-project src/IntegrationHub.Infrastructure
```

CI migrates a fresh PostgreSQL database, checks for model drift, builds, and tests. Do not run `EnsureCreated` or edit database tables by hand.

## Hosted setup later

No hosted services need to be created for Phases 1–3. Before Phase 6 deployment you will need a Neon project and a paid, always-on Render web service for the combined API and worker, plus a Render static site and simulator service. A migration role, runtime database role, stable encryption key, approved connector hosts, HTTPS origin, and administrator authentication must be configured before public access. The current API blocks non-development hosting because authentication is not yet present.

## Design documents

- [Requirements](docs/requirements.md)
- [Architecture](docs/architecture.md)
- [Data model](docs/data-model.md)
- [API contracts](docs/api-contracts.md)
- [ADR 0001](docs/adr/0001-modular-monolith.md)
