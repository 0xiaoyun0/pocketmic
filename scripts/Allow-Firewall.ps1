#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$ruleName = 'PocketMic-Local-Audio'
if (-not (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name $ruleName -DisplayName 'PocketMic local audio' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 47655 -Profile Private -RemoteAddress LocalSubnet | Out-Null
}
Write-Host 'PocketMic: TCP 47655 is allowed for local-subnet devices on PRIVATE networks only.'
Write-Host 'Undo: Remove-NetFirewallRule -Name PocketMic-Local-Audio'
