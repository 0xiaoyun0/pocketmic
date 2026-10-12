[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$classDir = Join-Path $repoRoot '.artifacts/interop-java'
New-Item -ItemType Directory -Path $classDir -Force | Out-Null
$javac = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin/javac.exe' } else { 'javac' }
Push-Location $repoRoot
try {
    & $javac -encoding UTF-8 -d $classDir android/app/src/main/java/app/pocketmic/Pairing.java android/app/src/main/java/app/pocketmic/Wire.java android/app/src/main/java/app/pocketmic/PinnedTls.java tests/interop/InteropClient.java
    if ($LASTEXITCODE -ne 0) { throw 'Java interop client failed to compile.' }
    & $Dotnet run --project tests/PocketMic.Interop -c Release -- $classDir
    if ($LASTEXITCODE -ne 0) { throw 'Java / Windows interoperability test failed.' }
} finally { Pop-Location }
