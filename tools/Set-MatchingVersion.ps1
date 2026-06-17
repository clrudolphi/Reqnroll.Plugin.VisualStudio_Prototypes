<#
.SYNOPSIS
    Sets the stored version to match the current extension version so no dialog appears.
.DESCRIPTION
    Writes the specified CurrentVersion so the extension sees no version change
    and skips both the Welcome and Upgrade dialogs.
    Useful as a baseline before testing other flows.
.PARAMETER CurrentVersion
    The version string to match the extension's IVersionProvider.GetExtensionVersion().
.PARAMETER Configuration
    'Debug' (default for VS Exp instance) or 'Release'
.EXAMPLE
    .\tools\Set-MatchingVersion.ps1
    .\tools\Set-MatchingVersion.ps1 -CurrentVersion '2026.1.0'
#>
param(
    [string]$CurrentVersion = '1.0.0.0',
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

Set-ItemProperty -Path $path -Name 'version.vs2022'    -Value $CurrentVersion -Type String
Set-ItemProperty -Path $path -Name 'installDate.vs2022' -Value $todayEpoch    -Type DWord
Set-ItemProperty -Path $path -Name 'lastUsedDate'       -Value $todayEpoch    -Type DWord
Set-ItemProperty -Path $path -Name 'usageDays'          -Value 50             -Type DWord
Set-ItemProperty -Path $path -Name 'userLevel'          -Value 1              -Type DWord

Write-Host "Stored version = '$CurrentVersion' at $path" -ForegroundColor Green
Write-Host "No dialog will appear on next VS start (version matches)." -ForegroundColor Green
