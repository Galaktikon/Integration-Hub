# Five-minute synthetic demo

Use a seeded local stack or the protected hosted demo described in the [deployment runbook](phase6-deployment.md). All orders and endpoints are synthetic. Keep schedules disabled unless scheduling is the feature being shown.

1. **Show the operations view.** Sign in, point out the current attention list, latest-run status per integration, and recent activity. Open an integration to show its source, destination, mapping count, retry policy, and schedule.
2. **Show configuration.** In **Connectors**, test the Commerce and ERP connections. Open **New integration**, choose those two connectors, review the four field mappings, then review validation and retry settings. The wizard saves only after the final step.
3. **Run the happy path.** Use **Run now** on an enabled integration. In **Run history**, show the fetched, created, updated, unchanged, failed, and pending counts; select a record to show attempt history. A clean first run against a new integration fetches 125 orders and creates 125 ERP rows. A second run against the same integration leaves those ERP rows unchanged.
4. **Show recovery.** Select the integration, click **One 503**, then start a new run. Show the persisted retry and completion. For manual recovery, arm **Outage for retry demo**, start a run, wait for failure, then use **Retry run** when offered. The API decides which actions are allowed; the UI does not force a retry on an ineligible run.
5. **Explain restart safety.** The worker checkpoints source pages and record attempts in PostgreSQL. A new worker instance reclaims an expired lease and continues the same run; a fencing generation rejects writes from an old worker. Use `scripts/phase4-recovery.ps1` locally to reproduce a lost destination response and worker restart. The script checks that the 125 ERP rows are not duplicated.

Use the [operations](screenshots/operations.png), [integration setup](screenshots/integration-setup.png), and [run details](screenshots/run-details.png) screenshots when a live environment is unavailable. The [architecture](architecture.md), [API contracts](api-contracts.md), and [data model](data-model.md) explain the design choices behind the demonstration.

## Expected evidence

| Scenario | Evidence |
| --- | --- |
| First clean run | 125 fetched and 125 created for a new integration |
| Repeat run | No extra ERP rows; unchanged outcomes |
| One source 503 | Retry event and eventual completion |
| Exhausted source attempts | Failed run and `Retry run` action when allowed |
| Restart after uncertain destination write | An `uncertain` attempt and exactly 125 destination rows |

The public hosted demonstration remains pending until Neon and Render are configured and the hosted scenarios are observed. The screenshot data is from a local synthetic environment.
