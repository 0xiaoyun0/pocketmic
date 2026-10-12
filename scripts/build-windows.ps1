[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
$publishDir = Join-Path $repoRoot '.artifacts/windows'
Push-Location $repoRoot
try {
    & $Dotnet test tests/PocketMic.Tests/PocketMic.Tests.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & $Dotnet test tests/PocketMic.Windows.Tests/PocketMic.Windows.Tests.csproj -c Release --nologo --filter 'Category!=Network'
    if ($LASTEXITCODE -ne 0) { throw 'Windows safety tests failed.' }
    & $Dotnet publish desktop/PocketMic.Windows/PocketMic.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Allow-Firewall.ps1') -Destination $publishDir
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/QUICKSTART.zh-CN.md') -Destination (Join-Path $publishDir '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $publishDir
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishDir
    Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination $publishDir -Recurse -Force
    $files = Get-ChildItem -LiteralPath $publishDir -File | Where-Object Extension -NE '.pdb'
    Compress-Archive -LiteralPath (@($files.FullName) + @((Join-Path $publishDir 'licenses'))) -DestinationPath (Join-Path $repoRoot ".artifacts/PocketMic-$version-windows-x64.zip") -Force
} finally { Pop-Location }
