param(
    [string]$HubUrl = 'http://localhost:8080',
    [string]$SimulatorUrl = 'http://localhost:8081',
    [switch]$InjectMalformedRecord,
    [int]$WaitSeconds = 120
)

$ErrorActionPreference = 'Stop'
$HubUrl = $HubUrl.TrimEnd('/')
$SimulatorUrl = $SimulatorUrl.TrimEnd('/')
$connectors = (Invoke-RestMethod "$HubUrl/api/v1/connectors?pageSize=100").items
$source = $connectors | Where-Object { $_.name -eq 'Demo Commerce' -and $_.role -eq 'source' } | Select-Object -First 1
$destination = $connectors | Where-Object { $_.name -eq 'Demo ERP' -and $_.role -eq 'destination' } | Select-Object -First 1
if (-not $source -or -not $destination) {
    throw 'Demo connectors are missing. Run: docker compose run --no-deps --rm hub seed-demo'
}

$integrations = (Invoke-RestMethod "$HubUrl/api/v1/integrations?pageSize=100").items
$integration = $integrations | Where-Object {
    $_.name -eq 'Commerce Orders to ERP Orders' -and $_.sourceConnectorId -eq $source.id -and
    $_.destinationConnectorId -eq $destination.id -and $_.enabled
} | Select-Object -First 1
if (-not $integration) {
    $body = @{
        name = 'Commerce Orders to ERP Orders'
        sourceConnectorId = $source.id
        destinationConnectorId = $destination.id
        entityType = 'orders'
        enabled = $true
        mapping = @{
            schemaVersion = 1
            fields = @(
                @{ source = 'orderNumber'; destination = 'externalOrderId'; required = $true; transform = 'trim' }
                @{ source = 'customer.email'; destination = 'customerEmail'; required = $true; transform = 'trim' }
                @{ source = 'total'; destination = 'amount'; required = $true; transform = 'decimal' }
                @{ source = 'placedAt'; destination = 'placedAtUtc'; required = $true; transform = 'utcDate' }
            )
        }
        validation = @{ schemaVersion = 1; amountMinimum = 0 }
        retryPolicy = @{ maxAttempts = 4; baseDelaySeconds = 2; maxDelaySeconds = 60; maxRecoveries = 2 }
    } | ConvertTo-Json -Depth 8
    $integration = Invoke-RestMethod "$HubUrl/api/v1/integrations" -Method Post -ContentType 'application/json' -Body $body
}

if ($InjectMalformedRecord) {
    $headers = @{ 'X-Api-Key' = 'local-demo-admin-key' }
    $fault = @{ target = 'source'; mode = 'malformedRecord'; remaining = 1 } | ConvertTo-Json
    Invoke-RestMethod "$SimulatorUrl/__admin/faults" -Method Post -Headers $headers -ContentType 'application/json' -Body $fault | Out-Null
}

$queued = Invoke-RestMethod "$HubUrl/api/v1/integrations/$($integration.id)/runs" -Method Post
Write-Host "Queued run $($queued.runId)"
$deadline = (Get-Date).AddSeconds($WaitSeconds)
do {
    Start-Sleep -Seconds 1
    $run = Invoke-RestMethod "$HubUrl/api/v1/runs/$($queued.runId)"
} while ($run.status -in @('queued', 'running') -and (Get-Date) -lt $deadline)

$run | ConvertTo-Json -Depth 6
if ($run.status -in @('queued', 'running')) { throw 'The run did not finish before the wait deadline.' }
if ($run.counts.failedPermanent -gt 0) {
    Invoke-RestMethod "$HubUrl/api/v1/runs/$($queued.runId)/records?status=failed_permanent" |
        ConvertTo-Json -Depth 6
}
if ($run.status -notin @('completed', 'completed_with_errors')) { throw "Run ended as $($run.status)." }
