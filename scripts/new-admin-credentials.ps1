$ErrorActionPreference = 'Stop'
$password = Read-Host 'New admin password' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ($plain.Length -lt 16) { throw 'Use at least 16 characters for the administrator password.' }
    $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        $plain, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $tokenKey = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    Write-Host "Security__AdminPasswordHash=$([Convert]::ToBase64String($salt)):$([Convert]::ToBase64String($hash))"
    Write-Host "Security__AdminTokenKey=$([Convert]::ToBase64String($tokenKey))"
    Write-Host 'Store these as private environment variables. Never commit them.'
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    Remove-Variable plain -ErrorAction SilentlyContinue
}
