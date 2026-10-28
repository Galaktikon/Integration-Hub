# Phase 6 deployment and demo runbook

This runbook uses **two free Render web services**: one combined hub process runs the API, sync worker, and scheduler; the other runs only the synthetic Commerce and ERP simulator. One free Render static site serves the dashboard. One Neon Free project stores both applications' durable state in separate schemas. There is no separate worker service and no paid Render instance in [render.yaml](../render.yaml). No public demo URL or production secrets are in the repository. The public demo is complete only after the hosted checks below pass.

## 1. Prerequisites and free-tier behavior

1. Have this repository pushed to a Git provider that Render can access. Install and start Docker Desktop locally; the migration helper uses Docker, so a local .NET SDK is not required.
2. Use only synthetic orders and new hosted secrets. Do not use the local development credentials.
3. Keep Render on its **Free** compute plan for both web services and Neon on **Free**. Render static sites are free. Confirm the selected plan in each creation form before clicking Deploy. The three Render resources are listed in [render.yaml](../render.yaml), but the steps below create them manually through the Render dashboard.

Render Free web services spin down after 15 minutes without **inbound** traffic and wake on the next request, which can take about a minute. The hub's worker and scheduler therefore stop while it sleeps. Persisted work resumes when the hub wakes and any expired lease is reclaimable; scheduled runs and delayed retries are **not guaranteed to execute on time** while the service sleeps. Do not present this as an always-on scheduled service. Render's 750 Free instance hours are shared across the workspace each month, and Neon Free also has compute and storage limits. Monitor both usage pages; reaching limits can suspend the demo. Render can bill for excess outbound bandwidth or build minutes if a payment method is present, so check Render Billing and any spend limit if zero spend is required. [Render Free limits](https://render.com/docs/free), [Neon Free limits](https://neon.com/docs/introduction/plans).

## 2. Prepare Neon

1. In the Neon Console, create **one Free project**, choose PostgreSQL 17 if offered, and choose a region near the Render web-service region you will use. The default branch and database are sufficient; note the database name and owner role. Do not create a Render Postgres database. Use one Neon database with separate `hub` and `demo` schemas, created by the migrations.
2. In the Neon project's **Connect** dialog, select the default branch, default database, and owner role. Turn **Connection pooling off**. Record the direct host, database, and owner username privately. Neon gives a PostgreSQL URL; the migration helper needs the same values in the Npgsql key/value format shown below.
3. In **SQL Editor**, with that branch/database and owner role selected, run:

```sql
CREATE ROLE hub_runtime NOLOGIN;
CREATE ROLE demo_runtime NOLOGIN;
```

Create these roles **with SQL**, not the Console's **Add role** button: Console-created roles inherit `neon_superuser`, whereas SQL-created roles can be limited to the permissions below. `NOLOGIN` lets you set their passwords interactively before enabling login. Choose distinct, random passwords of at least 16 characters. From a local terminal with `psql`, connect to the **direct** host using `psql "host=<direct-neon-host> port=5432 dbname=<database> user=<owner-role> sslmode=require"`; it prompts for the owner password. If `psql` is not installed but Docker is, use `docker run --rm -it postgres:17 psql "host=<direct-neon-host> port=5432 dbname=<database> user=<owner-role> sslmode=require"`. At the `psql` prompt run `\password hub_runtime` and then `\password demo_runtime`, entering each new password twice. Then run `ALTER ROLE hub_runtime LOGIN;` and `ALTER ROLE demo_runtime LOGIN;`. Exit with `\q`. Store all passwords in a password manager. The runtime roles must not own tables or have schema creation privileges. [Neon role guidance](https://neon.com/docs/manage/roles).

4. From the repository root on the Docker machine, run:

```powershell
.\scripts\neon-migrate.ps1
```

The helper prompts privately for the hub and simulator migration connection strings, builds both images, runs both EF Core migrations, then clears its temporary environment variables. For **both prompts**, use the owner/migration role and the **direct, non-pooled** Neon host. Use Npgsql key/value syntax, not the `postgresql://` URL copied from Neon: `Host=<direct-neon-host>;Port=5432;Database=<database>;Username=<owner-role>;Password=<owner-password>;SSL Mode=Require;Maximum Pool Size=10`. It is fine for both migration prompts to use the same owner connection string. Do not put the string in a tracked file or shell command history. If a password contains a semicolon, quote it according to Npgsql connection-string syntax.

The simulator requires two distinct key values even during its migration command; the helper uses placeholders only for that command, and they never become hosted credentials.

5. After **both** migrations finish, use Neon SQL Editor with the same branch/database and owner role to grant runtime access:

```sql
GRANT USAGE ON SCHEMA hub TO hub_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA hub TO hub_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA hub TO hub_runtime;
GRANT USAGE ON SCHEMA demo TO demo_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA demo TO demo_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA demo TO demo_runtime;
```

6. In Neon **Connect**, enable **Connection pooling** to get the pooled host, identified by `-pooler`. Construct two **runtime** Npgsql strings with that host and the respective role passwords: `Host=<pooled-neon-host>;Port=5432;Database=<database>;Username=hub_runtime;Password=<hub-password>;SSL Mode=Require;Maximum Pool Size=10` and the same pattern with `demo_runtime`. Put only the hub string in the hub Render service and only the demo string in the demo service. Both runtime roles must be able to read their schema's `__EFMigrationsHistory` table for readiness. Reapply the table and sequence grants after future migrations. Keep migrations on the direct host. [Neon connection guidance](https://neon.com/docs/connect/connect-from-any-app).

## 3. Create the Render services manually

Use **New > Web Service** twice and **New > Static Site** once. Connect each to the same Git repository and the same deployed branch. Keep the **Root Directory** blank so the Dockerfiles can copy the whole repository. Choose a Render web-service region near Neon. Turn automatic deploys off if the form offers that choice; subsequent releases should follow the migration order. Render's Docker creation flow uses **Language: Docker** and a repo-relative **Dockerfile Path**. Build context is the repo root (default); if a context field appears, set it to `.`. Do not add a build or start command for either Docker service. [Render Docker setup](https://render.com/docs/docker), [Render service creation](https://render.com/docs/your-first-deploy).

First, run `./scripts/new-admin-credentials.ps1` locally. Enter a new admin password of at least 16 characters and save it in your password manager. The script prints `Security__AdminPasswordHash` and `Security__AdminTokenKey`; copy those values privately. Generate three more independent 32-byte base64 secrets in PowerShell, one at a time, and save each privately:

```powershell
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes); [Convert]::ToBase64String($bytes) }
finally { $rng.Dispose() }
```

Use one for `Security__EncryptionKey`, one for `Demo__ApiKey`, and one for `Demo__AdminKey`. Keep all three different. **Never replace `Security__EncryptionKey` after connectors have been saved** without also re-encrypting their secrets. Never put any of these values in Git or in the static site's variables.

### A. Synthetic demo web service

Create a web service named `integration-hub-demo` (or a unique equivalent) with **Language: Docker**, **Dockerfile Path: `./deploy/Dockerfile.demo`**, **Build Context: `.`**, and **Instance Type: Free**. Add these Environment variables before deploying (or use **Save only** and deploy after all values are set):

| Key | Value |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_URLS` | `http://+:10000` |
| `ConnectionStrings__Demo` | Pooled Neon runtime string for `demo_runtime` |
| `Demo__ApiKey` | First generated demo key |
| `Demo__AdminKey` | Second generated demo key, different from API key |

Set **Health Check Path** to `/health/ready` in the service's Settings. No pre-deploy command is needed because migrations were applied locally. Deploy and record its assigned `https://<simulator-host>` URL. Its `/health/ready` endpoint should return `{"status":"ready"}`.

### B. Combined hub API and worker web service

Create a second web service named `integration-hub-api` with **Language: Docker**, **Dockerfile Path: `./deploy/Dockerfile.hub`**, **Build Context: `.`**, and **Instance Type: Free**. This is the **only hub service**: the API, scheduler, and sync worker start together from this Docker image. Add:

| Key | Value |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_URLS` | `http://+:10000` |
| `ConnectionStrings__Hub` | Pooled Neon runtime string for `hub_runtime` |
| `Security__EncryptionKey` | Its generated 32-byte base64 key |
| `Security__AdminTokenKey` | Value printed by `new-admin-credentials.ps1` |
| `Security__AdminUsername` | A new admin username |
| `Security__AdminPasswordHash` | Value printed by `new-admin-credentials.ps1` |
| `Security__AllowedConnectorHosts` | Simulator hostname only, for example `integration-hub-demo.onrender.com` |
| `Security__FrontendOrigin` | Intended dashboard HTTPS origin, for example `https://integration-hub-dashboard.onrender.com` |
| `Demo__BaseUrl` | Actual simulator HTTPS origin, for example `https://integration-hub-demo.onrender.com` |
| `Demo__EnableFaultProxy` | `true` |
| `Demo__ApiKey` | Exactly the same API key as the demo service |
| `Demo__AdminKey` | Exactly the same admin key as the demo service |

Set **Health Check Path** to `/api/v1/health/ready`. Deploy and record its actual `https://<api-host>` URL. Confirm readiness. The dashboard hostname may change if the desired name is taken; correct `Security__FrontendOrigin` to the actual origin after creating the static site and **Save and deploy** the hub if necessary. The hostname fields have no trailing slash; `Security__AllowedConnectorHosts` has no `https://` prefix.

### C. Dashboard static site

Create **New > Static Site** from the same repository and branch. Name it `integration-hub-dashboard` (or a unique equivalent). Keep **Root Directory** blank. Set **Build Command** to `cd frontend/admin-dashboard && npm ci && npm run build` and **Publish Directory** to `frontend/admin-dashboard/dist`. Add `VITE_API_BASE_URL` with the hub's exact `https://<api-host>` origin, without a trailing slash. Deploy and record the assigned static-site URL. If the name differs from the hub's `Security__FrontendOrigin`, update that hub variable to the actual static-site origin and redeploy the hub. Any future API URL change requires rebuilding the static site because Vite embeds this value at build time. In the static site's Headers settings, optionally copy the three headers from [render.yaml](../render.yaml).

Render's environment-variable page supports **Environment > Add Environment Variable** and **Save and deploy** after creation. Keep each service's instance plan at **Free**. Render terminates public HTTPS; the API permits only the configured dashboard origin through CORS. Both health checks use database connectivity and migration state. [Render environment settings](https://render.com/docs/configure-environment-variables), [Render Free plans](https://render.com/docs/free).

Confirm these URLs return `{"status":"ready"}` before testing the dashboard; the first request after sleep may take about a minute:

```text
https://<simulator-host>/health/ready
https://<api-host>/api/v1/health/ready
```

## 4. Seed and configure the synthetic demo

The simulator's admin routes require `Demo__AdminKey`. In a private PowerShell terminal, seed it with the exact simulator URL and an interactive key prompt:

```powershell
$demoUrl = 'https://<simulator-host>'
$adminKey = Read-Host 'Demo admin key'
try { Invoke-RestMethod -Method Post -Uri "$demoUrl/__admin/seed" -Headers @{ 'X-Api-Key' = $adminKey } }
finally { Remove-Variable adminKey -ErrorAction SilentlyContinue }
```

This idempotently creates 125 synthetic Commerce orders. Do not place the admin key in the browser or static site.

Sign in to the dashboard with the new admin credentials. Add two enabled REST connectors:

- **Demo Commerce**, role **Source**, base URL `https://<simulator-host>/commerce`.
- **Demo ERP**, role **Destination**, base URL `https://<simulator-host>/erp`.

For both connectors, enter the simulator's generated `Demo__ApiKey` from Render, set page size to 100 and timeout to 15 seconds, then use **Test**. The key is encrypted before storage and is never returned by the API. Create an enabled integration with the default four mappings, then run it. Confirm 125 fetched records and 125 ERP rows created on the first clean run. A later run should report unchanged rows rather than duplicates.

## 5. Demonstrate recovery

1. In **Integrations**, select the demo flow, click **One 503**, then **Run now**. In **Run history**, show the run's persisted retry and eventual completion. The hosted fault proxy is protected by administrator login and accepts only bounded source 503 faults.
2. Click **Outage for retry demo**, start a new run, wait until the source attempt budget is exhausted, then use **Retry run** when the API allows it. Show that the same run continues from its persisted checkpoint.
3. For restart recovery, start a run and redeploy or restart only the combined API/worker service. The worker resumes after its lease expires. Use the record and attempt history to show the continuation. Avoid restarting during an external write solely for a public interview; the scripted local test covers that edge case reproducibly.
4. Record the run ID, status, counts, attempt history, and ERP row count as evidence. Do not claim a recovery scenario passed unless its observed result matches the expected outcome.

The simulator supports a protected reset of **synthetic destination rows and fault state**. Disable schedules and wait for active runs to finish first. Then send `POST https://<simulator-host>/__admin/reset` with the simulator admin key and `X-Confirm-Reset: reset-synthetic-demo`. It retains or reseeds the 125 source orders. Existing hub run history and integration definitions remain; create a new integration or use a fresh Neon branch for a fully clean presentation. This endpoint is not exposed through the dashboard.

## 6. Verify and release

From a clean checkout, run local Compose, seed, and the test commands in the [README](../README.md). Browser smoke tests live in `frontend/admin-dashboard/e2e`:

```powershell
cd frontend/admin-dashboard
npm ci
npx playwright install chromium
$env:E2E_ADMIN_PASSWORD = '<local-demo-password>'
npm run test:e2e
```

Set `E2E_MUTATE=1` only against a disposable, seeded local environment to run the create-and-launch test. Against the hosted site, set `E2E_BASE_URL`, `E2E_ADMIN_USERNAME`, and `E2E_ADMIN_PASSWORD` and leave `E2E_MUTATE` unset for a read-only sign-in/navigation smoke. CI runs .NET regression tests and both local browser smoke tests. It must be green on the deployed commit before calling the demo complete.

Review Render logs by correlation ID (`X-Correlation-ID` response header), run ID, and lease generation. The API emits JSON logs outside Development and instruments HTTP requests, durations, run claims, terminal statuses, and run duration with a .NET `Meter`. Authenticated `GET /api/v1/metrics` exposes aggregate run statuses, retry events, and uncertain attempts for the demo; the Meter can be connected to an exporter later. Liveness checks the process; readiness checks Neon connectivity and migration state. Neither health endpoint requires an admin token.

## Known limits and trade-offs

- The API, sync worker, and scheduler share one process by design. The database lease and fencing generation handle overlapping deploy instances and restart continuation. Render Free sleep pauses schedules, retries, and active background work; they continue only after an incoming request wakes the hub. This free deployment demonstrates recovery and manually triggered integrations, but does not provide timely unattended scheduling.
- The hosted demo uses synthetic REST contracts, not real vendor OAuth or webhooks. The simulator snapshot token is intentionally stable for the demo and does not prove behavior against an arbitrary vendor API.
- The admin account is a single operator identity with an eight-hour bearer token kept in browser memory. There is no multi-user RBAC, audit export, or self-service password reset.
- The fault proxy demonstrates bounded source 503 failures. The lost-response and stale-worker fencing cases are covered by local integration tests and `scripts/phase4-recovery.ps1` rather than exposed as public buttons.
- While the hub is awake, worker and scheduler polling may keep Neon compute active. Neon Free currently includes a limited monthly compute allowance and suspends compute when it is exhausted; review Neon usage after deployment.
- Production smoke is read-only by default. It verifies sign-in and navigation; manually confirm hosted failure, retry, reset, and restart behavior before sharing a public URL.
