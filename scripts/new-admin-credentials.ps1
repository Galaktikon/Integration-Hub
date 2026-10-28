$ErrorActionPreference = 'Stop'
$password = Read-Host 'New admin password' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ($plain.Length -lt 16) { throw 'Use at least 16 characters for the administrator password.' }
    $salt = New-Object byte[] 16
    $tokenKey = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($salt)
        $rng.GetBytes($tokenKey)
    } finally {
        $rng.Dispose()
    }
    $deriver = [Security.Cryptography.Rfc2898DeriveBytes]::new(
        $plain, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $hash = $deriver.GetBytes(32)
    } finally {
        $deriver.Dispose()
    }
    Write-Host "Security__AdminPasswordHash=$([Convert]::ToBase64String($salt)):$([Convert]::ToBase64String($hash))"
    Write-Host "Security__AdminTokenKey=$([Convert]::ToBase64String($tokenKey))"
    Write-Host 'Store these as private environment variables. Never commit them.'
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    Remove-Variable plain -ErrorAction SilentlyContinue
}
