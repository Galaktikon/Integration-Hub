# Enterprise Integration Hub

A portfolio-scale integration platform for synthetic Commerce Orders to ERP Orders. Phases 1–5 provide the .NET 10 foundation, PostgreSQL-backed sync and recovery, a database-backed scheduler, administrator authentication, and a React operations dashboard.

## Current architecture

- One ASP.NET Core process hosts the API, sync worker, and scheduler. The scheduler atomically handles due occurrences across competing processes; the sync worker checkpoints pages, schedules bounded retries, and reclaims interrupted runs.
- EF Core stores hub configuration and execution state in PostgreSQL. Local Compose uses PostgreSQL 17; the hosted demo will use Neon.
- Connector API keys are encrypted with AES-256-GCM using an environment-supplied key. API responses only report `secretSet`.
- The demo systems own a separate `demo` schema and expose paginated Commerce orders, idempotent ERP writes, and administrator-only fault controls. The worker accesses them through HTTP adapters.
- Admin sign-in issues an eight-hour signed bearer token. The dashboard keeps it in memory. Non-development hosting requires an administrator password hash and a separate signing key; the local example credentials are for development only.

Phase 4 added persisted retry times, attempt history, cancellation, explicit recovery, and stale-worker fencing. Phase 5 adds interval schedules, safe connector checks, a browser-based operator workflow, and protected admin routes. The destination uses a stable external identity, so replay after an uncertain response does not create another ERP row.

## Manual setup for local Phases 1–5

1. Install [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/) and enable the Linux container engine. Start Docker Desktop and wait until `docker info` succeeds. On Windows, its WSL 2 backend must be enabled during setup.
2. Clone or open this repository. No Neon or Render account is needed for Phase 5. Docker Compose builds the application and dashboard inside images, so a host .NET or Node.js installation is optional for this path.
3. From the repository root, run:

   ```powershell
   docker compose up --build -d
   docker compose ps
   ```

   Compose starts PostgreSQL, applies the hub and simulator migrations, then starts the API, simulator, and dashboard. The first image build downloads .NET, NuGet, and npm dependencies. Check `http://localhost:8080/api/v1/health/ready` and `http://localhost:8081/health/ready` for `{"status":"ready"}`.
4. Seed two synthetic connector configurations:

   ```powershell
   docker compose run --no-deps --rm hub seed-demo
   ```

   This command is idempotent. It adds `Demo Commerce` and `Demo ERP` connectors with local-only API keys. Then seed 125 synthetic source orders:

   ```powershell
   docker compose --profile seed run --rm demo-seed
   ```

   Both seed commands are safe to repeat.
5. Open [http://localhost:5173](http://localhost:5173) and sign in with username `admin` and password `local-demo-password`. These credentials are confined to the local development configuration. In **Connectors**, confirm that `Demo Commerce` and `Demo ERP` have secrets configured, and use **Test** to check each endpoint. In **Integrations**, create an enabled Commerce-to-ERP flow using those connectors; the default four field mappings are valid. Select the flow and click **Run now**. **Runs** polls real execution state and lets you inspect records and attempt history. Use **One 503** before a run to see automatic retry. For manual recovery, use **Outage for retry demo**, start a run, wait for it to fail after four source attempts, then click **Retry run**; the original run and snapshot continue. The **Interval schedule** control can enable a five-minute or longer schedule; disable it after a demo if you do not want recurring local runs.

6. For direct API calls, sign in first and send the returned token:

   ```powershell
   $login = Invoke-RestMethod http://localhost:8080/api/v1/auth/login -Method Post -ContentType 'application/json' -Body '{"username":"admin","password":"local-demo-password"}'
   $hubHeaders = @{ Authorization = "Bearer $($login.accessToken)" }
   Invoke-RestMethod http://localhost:8080/api/v1/connectors -Headers $hubHeaders
   ```

   Responses include IDs and `secretSet: true`, never the API key. Send `$hubHeaders` on other hub API requests, including the generated OpenAPI JSON at `http://localhost:8080/openapi/v1.json`.
7. Try the source and a transient fault directly on the simulator:

   ```powershell
   $sourceHeaders = @{ 'X-Api-Key' = 'local-demo-key' }
   Invoke-RestMethod 'http://localhost:8081/commerce/orders?pageSize=50' -Headers $sourceHeaders
   $adminHeaders = @{ 'X-Api-Key' = 'local-demo-admin-key' }
   Invoke-RestMethod 'http://localhost:8081/__admin/faults' -Method Post -Headers $adminHeaders -ContentType 'application/json' -Body '{"target":"source","mode":"503","remaining":1}'
   try { Invoke-RestMethod 'http://localhost:8081/commerce/orders?pageSize=50' -Headers $sourceHeaders } catch { Write-Host 'Expected one-time 503 fault' }
   Invoke-RestMethod 'http://localhost:8081/commerce/orders?pageSize=50' -Headers $sourceHeaders
   ```

   The next source request returns 503; the following one succeeds. See [simulator contract](docs/simulator-contract.md) for destination writes and other faults.
8. Run the complete sync from PowerShell:

   ```powershell
   .\scripts\phase3-demo.ps1 -InjectMalformedRecord
   .\scripts\phase3-demo.ps1
   ```

   The script creates an enabled integration if needed, submits a manual run, waits for completion, and prints persisted counts and record errors. The first run should fetch 125 orders and show one `failed_permanent` record because the injected item lacks `total`. The second run should complete with 125 valid orders; previously written ERP orders are `unchanged`. The same script can be run again without duplicating ERP rows. Run details remain available at `/api/v1/runs/{runId}` after `docker compose restart hub`.

   To inspect records and attempts directly, use the run ID printed by the script:

   ```powershell
   Invoke-RestMethod "http://localhost:8080/api/v1/runs/$runId/records?status=failed_permanent" -Headers $hubHeaders
   Invoke-RestMethod "http://localhost:8080/api/v1/integrations/$integrationId/runs" -Headers $hubHeaders
   ```

   Set `$runId` and `$integrationId` to the values in the script output. Run and record lists accept `pageSize` (1–100) and an opaque `cursor` returned as `nextCursor`.

9. Reproduce the Phase 4 lost-response and restart recovery demonstration:

   ```powershell
   .\scripts\phase4-recovery.ps1
   ```

   This script creates a fresh integration, injects one destination response loss after the ERP write commits, restarts the hub while the request is pending, then verifies a completed 125-record run. It checks that exactly 125 ERP rows were added and that the interrupted attempt was marked `uncertain`. Run it from a terminal with access to Docker Desktop. It uses the seeded demo connectors and orders from step 4.

   To exercise automatic source retry separately, inject one source `503` immediately before submitting a run with `.\scripts\phase3-demo.ps1`, or use the browser's **One 503** action. The worker persists `nextSourceAttemptAtUtc`, releases the lease until the due time, and resumes without operator input. A failed run with `allowedActions` containing `retry` can be requeued in the dashboard or with `Invoke-RestMethod "http://localhost:8080/api/v1/runs/$runId/retry" -Method Post -Headers $hubHeaders`. A running cancellation takes effect after the current external call returns or is cancelled.

   The worker enforces a one-hour execution deadline, 1,000 pages, 50,000 records, 64 KiB per source item, and 2 MiB per source page. Connector timeouts and configured attempt budgets further bound external work.

If port 5432, 5173, 8080, or 8081 is already in use, change only the left side of the matching `ports` entry in `compose.yaml`. If you change port 8080, update the dashboard's `VITE_API_BASE_URL` build argument, the hub's `Security__FrontendOrigin`, and browser and PowerShell URLs. Container-to-container addresses stay unchanged. To stop the stack, run `docker compose down`; omit `-v` to retain local database data.

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

Create `integration_hub_test` only once; `createdb` reports that it already exists on later runs. The integration tests migrate their own schemas and use this separate database so the live Compose worker cannot claim test runs. CI applies both migrations. Clear the environment variable afterward with `Remove-Item Env:INTEGRATION_TEST_DATABASE`. To run only unit tests without PostgreSQL, use `dotnet test --filter 'FullyQualifiedName!~ConfigurationApiTests&FullyQualifiedName!~DemoSystemsTests&FullyQualifiedName!~SyncPipelineTests&FullyQualifiedName!~ReliabilityTests&FullyQualifiedName!~ScheduleTests'`. To build the dashboard on the host, run `npm ci` and `npm run build` in `frontend/admin-dashboard`.

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

No hosted services need to be created for Phases 1–5. Before Phase 6 deployment you will need a Neon project and a paid, always-on Render web service for the combined API, worker, and scheduler, plus a Render static site and simulator service. Configure separate migration and runtime database roles, stable encryption and token keys, a PBKDF2 administrator password hash, approved connector hosts, HTTPS frontend origin, and protected simulator credentials before public access. Run `.\scripts\new-admin-credentials.ps1` in PowerShell to generate the password hash and a fresh signing key, then store its output as private Render environment variables. Set `Security__AdminUsername` separately. The development credentials in this repository must never be used for hosted deployment.

## Design documents

- [Requirements](docs/requirements.md)
- [Architecture](docs/architecture.md)
- [Data model](docs/data-model.md)
- [API contracts](docs/api-contracts.md)
- [ADR 0001](docs/adr/0001-modular-monolith.md)
