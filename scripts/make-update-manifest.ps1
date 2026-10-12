[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
function Get-ReleaseAsset([string]$Name) {
    $path = Join-Path $repoRoot ".artifacts/$Name"
    $file = Get-Item -LiteralPath $path
    return @{ name = $Name; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); size = $file.Length }
}
$manifest = @{ schema = 1; version = $version; windows = Get-ReleaseAsset "PocketMic-$version-windows-x64.zip"; android = Get-ReleaseAsset "PocketMic-$version-android.apk" }
$path = Join-Path $repoRoot '.artifacts/update-manifest.json'
[IO.File]::WriteAllText($path, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$sums = @($manifest.windows, $manifest.android) | ForEach-Object { "$($_.sha256)  $($_.name)" }
$sums += "$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())  update-manifest.json"
[IO.File]::WriteAllLines((Join-Path $repoRoot '.artifacts/SHA256SUMS.txt'), $sums, [Text.UTF8Encoding]::new($false))
Write-Host 'Upload both packages, update-manifest.json and SHA256SUMS.txt to the matching GitHub Release.'
