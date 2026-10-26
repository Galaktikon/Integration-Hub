param(
    [string]$HubUrl = 'http://localhost:8080',
    [string]$SimulatorUrl = 'http://localhost:8081',
    [string]$AdminKey = 'local-demo-admin-key',
    [int]$WaitSeconds = 180
)

$ErrorActionPreference = 'Stop'
$HubUrl = $HubUrl.TrimEnd('/')
$SimulatorUrl = $SimulatorUrl.TrimEnd('/')
$adminHeaders = @{ 'X-Api-Key' = $AdminKey }
$connectors = (Invoke-RestMethod "$HubUrl/api/v1/connectors?pageSize=100").items
$source = $connectors | Where-Object { $_.name -eq 'Demo Commerce' -and $_.role -eq 'source' } | Select-Object -First 1
$destination = $connectors | Where-Object { $_.name -eq 'Demo ERP' -and $_.role -eq 'destination' } | Select-Object -First 1
if (-not $source -or -not $destination) {
    throw 'Demo connectors are missing. Run: docker compose run --no-deps --rm hub seed-demo'
}

$body = @{
    name = "Recovery demo $([guid]::NewGuid().ToString('N').Substring(0, 8))"
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
    retryPolicy = @{ maxAttempts = 4; baseDelaySeconds = 2; maxDelaySeconds = 10; maxRecoveries = 2 }
} | ConvertTo-Json -Depth 8
$integration = Invoke-RestMethod "$HubUrl/api/v1/integrations" -Method Post -ContentType 'application/json' -Body $body
$before = (Invoke-RestMethod "$SimulatorUrl/__admin/orders/count" -Headers $adminHeaders).count
$fault = @{ target = 'destination'; mode = 'lostResponse'; remaining = 1; delayMilliseconds = 30000 } | ConvertTo-Json
Invoke-RestMethod "$SimulatorUrl/__admin/faults" -Method Post -Headers $adminHeaders -ContentType 'application/json' -Body $fault | Out-Null
$queued = Invoke-RestMethod "$HubUrl/api/v1/integrations/$($integration.id)/runs" -Method Post
Write-Host "Queued $($queued.runId); waiting for a committed ERP write before restarting the hub."

$deadline = (Get-Date).AddSeconds($WaitSeconds)
do {
    Start-Sleep -Milliseconds 250
    $count = (Invoke-RestMethod "$SimulatorUrl/__admin/orders/count" -Headers $adminHeaders).count
} while ($count -le $before -and (Get-Date) -lt $deadline)
if ($count -le $before) { throw 'No destination write committed before the wait deadline.' }

$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    docker compose restart hub | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose could not restart the hub.' }
} finally { Pop-Location }

$run = $null
do {
    Start-Sleep -Seconds 1
    try { $run = Invoke-RestMethod "$HubUrl/api/v1/runs/$($queued.runId)" } catch { continue }
} while (($null -eq $run -or $run.status -in @('queued', 'running')) -and (Get-Date) -lt $deadline)
if ($null -eq $run -or $run.status -in @('queued', 'running')) { throw 'The run did not finish before the wait deadline.' }
$after = (Invoke-RestMethod "$SimulatorUrl/__admin/orders/count" -Headers $adminHeaders).count
$records = @()
$cursor = $null
do {
    $url = "$HubUrl/api/v1/runs/$($queued.runId)/records?pageSize=100"
    if ($cursor) { $url += "&cursor=$([uri]::EscapeDataString($cursor))" }
    $page = Invoke-RestMethod $url
    $records += $page.items
    $cursor = $page.nextCursor
} while ($cursor)
$replayed = @($records | Where-Object { $_.attemptCount -gt 1 })
if ($replayed.Count -ne 1) { throw "Expected one replayed record; found $($replayed.Count)." }
$attempts = (Invoke-RestMethod "$HubUrl/api/v1/runs/$($queued.runId)/records/$($replayed[0].recordId)/attempts").items
if ('uncertain' -notin @($attempts | ForEach-Object { $_.outcome })) { throw 'The interrupted attempt was not marked uncertain.' }
if ($run.status -ne 'completed' -or $run.counts.fetched -ne 125 -or $run.counts.pending -ne 0) {
    throw "Unexpected run result: $($run | ConvertTo-Json -Depth 5)"
}
if ($after - $before -ne 125) { throw "Expected exactly 125 new ERP rows; found $($after - $before)." }

[pscustomobject]@{
    runId = $queued.runId
    status = $run.status
    fetched = $run.counts.fetched
    created = $run.counts.created
    unchanged = $run.counts.unchanged
    uncertainAttempts = @($attempts | Where-Object { $_.outcome -eq 'uncertain' }).Count
    newDestinationRows = $after - $before
} | ConvertTo-Json
