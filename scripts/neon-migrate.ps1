$ErrorActionPreference = 'Stop'
$priorHub = [Environment]::GetEnvironmentVariable('ConnectionStrings__Hub', 'Process')
$priorDemo = [Environment]::GetEnvironmentVariable('ConnectionStrings__Demo', 'Process')

function Set-SecretEnvironmentVariable([string]$Name, [string]$Prompt) {
    $secret = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
    try {
        $value = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        if ($value -match '^\s*postgres(?:ql)?://') {
            throw "$Prompt must use Npgsql key/value syntax: Host=<direct-host>;Port=5432;Database=<database>;Username=<owner>;Password=<password>;SSL Mode=Require. Neon postgresql:// URLs are not accepted."
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "$Prompt cannot be empty."
        }
        [Environment]::SetEnvironmentVariable($Name, $value, 'Process')
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

try {
    Set-SecretEnvironmentVariable 'ConnectionStrings__Hub' 'Direct Neon hub migration connection string'
    Set-SecretEnvironmentVariable 'ConnectionStrings__Demo' 'Direct Neon simulator migration connection string'

    docker build -f deploy/Dockerfile.hub -t integration-hub-api:migrate .
    if ($LASTEXITCODE -ne 0) { throw 'Hub migration image build failed.' }
    docker build -f deploy/Dockerfile.demo -t integration-hub-demo:migrate .
    if ($LASTEXITCODE -ne 0) { throw 'Simulator migration image build failed.' }

    docker run --rm --env ConnectionStrings__Hub integration-hub-api:migrate migrate
    if ($LASTEXITCODE -ne 0) { throw 'Hub migration failed.' }
    docker run --rm --env ConnectionStrings__Demo --env Demo__ApiKey=unused-migration-key --env Demo__AdminKey=other-unused-migration-key integration-hub-demo:migrate migrate
    if ($LASTEXITCODE -ne 0) { throw 'Simulator migration failed.' }
    Write-Host 'Both migrations completed. Apply runtime role grants in Neon before deploying.'
} finally {
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Hub', $priorHub, 'Process')
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Demo', $priorDemo, 'Process')
}
