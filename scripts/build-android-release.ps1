[CmdletBinding()]
param([string]$JavaHome = $env:JAVA_HOME)
$ErrorActionPreference = 'Stop'
if (-not $JavaHome) { throw 'Set JAVA_HOME to JDK 17 or pass -JavaHome.' }
$repoRoot = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
$keyDir = Join-Path $env:LOCALAPPDATA 'PocketMicBuild'
New-Item -ItemType Directory -Path $keyDir -Force | Out-Null
$keystorePath = Join-Path $keyDir 'release.jks'
$passwordPath = Join-Path $keyDir 'signing-password.dpapi'
$previousJava = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $JavaHome
    if (-not (Test-Path -LiteralPath $passwordPath)) {
        if (Test-Path -LiteralPath $keystorePath) { throw 'Keystore exists but password file is missing. Restore the original password file.' }
        $passwordBytes = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($passwordBytes); $rng.Dispose()
        $newPassword = [Convert]::ToBase64String($passwordBytes)
        ConvertTo-SecureString -String $newPassword -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -LiteralPath $passwordPath
    }
    $securePassword = Get-Content -LiteralPath $passwordPath | ConvertTo-SecureString
    $env:POCKETMIC_STORE_PASSWORD = [Net.NetworkCredential]::new('', $securePassword).Password
    $env:POCKETMIC_KEYSTORE = $keystorePath
    if (-not (Test-Path -LiteralPath $keystorePath)) {
        & (Join-Path $JavaHome 'bin/keytool.exe') -genkeypair -keystore $keystorePath -storetype JKS -alias pocketmic -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=PocketMic, OU=Open Source, O=PocketMic, C=CN' -storepass:env POCKETMIC_STORE_PASSWORD -keypass:env POCKETMIC_STORE_PASSWORD
        if ($LASTEXITCODE -ne 0) { throw 'Signing key generation failed.' }
    }
    Push-Location (Join-Path $repoRoot 'android')
    try {
        & .\gradlew.bat :app:testDebugUnitTest :app:lintDebug :app:assembleRelease --no-daemon
        if ($LASTEXITCODE -ne 0) { throw 'Android validation or build failed.' }
    } finally { Pop-Location }
    New-Item -ItemType Directory -Path (Join-Path $repoRoot '.artifacts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'android/app/build/outputs/apk/release/app-release.apk') -Destination (Join-Path $repoRoot ".artifacts/PocketMic-$version-android.apk")
    Write-Host 'Release APK built. Back up the signing key and its DPAPI password file in %LOCALAPPDATA%\PocketMicBuild. Never commit these files.'
} finally {
    $env:POCKETMIC_STORE_PASSWORD = $null
    $env:POCKETMIC_KEYSTORE = $null
    $env:JAVA_HOME = $previousJava
}
