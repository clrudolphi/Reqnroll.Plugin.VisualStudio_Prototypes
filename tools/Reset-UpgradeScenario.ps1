<#
.SYNOPSIS
    Sets the stored extension version to an older value to trigger the Upgrade dialog.
.DESCRIPTION
    Writes an older version string into the registry so the extension thinks
    a newer version was installed since last run. The Upgrade dialog showing
    the changelog will appear after 7 seconds.
.PARAMETER OldVersion
    The version string to store (default: '0.0.1.0').
.PARAMETER Configuration
    'Debug' (default for VS Exp instance) or 'Release'
.EXAMPLE
    .\tools\Reset-UpgradeScenario.ps1
    .\tools\Reset-UpgradeScenario.ps1 -OldVersion '2025.1.0'
#>
param(
    [string]$OldVersion = '0.0.1.0',
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)

. "$PSScriptRoot\ReqnrollRegistry.ps1"

$path = Get-ReqnrollRegPath -Configuration $Configuration

if (-not (Test-Path $path)) {
    $null = New-Item -Path $path -Force -ErrorAction Stop
}

$magicDate = [DateTime]'2024-01-12'
$today = (Get-Date).Date
$todayEpoch = [int]($today - $magicDate).TotalDays

Set-ItemProperty -Path $path -Name 'version.vs2022'    -Value $OldVersion -Type String
Set-ItemProperty -Path $path -Name 'installDate.vs2022' -Value $todayEpoch -Type DWord
Set-ItemProperty -Path $path -Name 'lastUsedDate'       -Value $todayEpoch -Type DWord
Set-ItemProperty -Path $path -Name 'usageDays'          -Value 10          -Type DWord
Set-ItemProperty -Path $path -Name 'userLevel'          -Value 0           -Type DWord

Write-Host "Stored OldVersion = '$OldVersion' at $path" -ForegroundColor Green
Write-Host "Upgrade dialog will appear on next VS start." -ForegroundColor Green
