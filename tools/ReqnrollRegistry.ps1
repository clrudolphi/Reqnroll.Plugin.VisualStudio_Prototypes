<#
.SYNOPSIS
    Shared helpers for Reqnroll IDE Support registry scenarios.
.DESCRIPTION
    Dot-source this file from scenario scripts:
        . .\tools\ReqnrollRegistry.ps1
#>

function Get-ReqnrollRegPath {
    param(
        [ValidateSet('Debug','Release')]
        [string]$Configuration = 'Debug'
    )
    $base = 'HKCU:\Software\Reqnroll\VSLSP'
    if ($Configuration -eq 'Debug') {
        return "$base\Debug"
    }
    return $base
}

function Decode-Date($epoch) {
    $magicDate = [DateTime]'2024-01-12'
    if ($null -eq $epoch -or $epoch -le 0) {
        return 'N/A'
    }
    return $magicDate.AddDays($epoch).ToString('yyyy-MM-dd')
}

function Get-ReqnrollInstallStatus {
    param(
        [ValidateSet('Debug','Release')]
        [string]$Configuration = 'Debug'
    )
    $path = Get-ReqnrollRegPath -Configuration $Configuration
    if (-not (Test-Path $path)) {
        Write-Host "Registry key '$path' does not exist - no install recorded." -ForegroundColor Yellow
        return
    }
    $props = Get-ItemProperty $path
    $status = [PSCustomObject]@{
        Path              = $path
        Version           = $props.'version.vs2022'
        InstallDateEpoch  = $props.'installDate.vs2022'
        LastUsedDateEpoch = $props.'lastUsedDate'
        UsageDays         = $props.'usageDays'
        UserLevel         = $props.'userLevel'
    }

    $installDate = Decode-Date $status.InstallDateEpoch
    $lastUsedDate = Decode-Date $status.LastUsedDateEpoch

    Write-Host "=== Reqnroll Installation Status ($Configuration) ===" -ForegroundColor Cyan
    $status | Format-List
    Write-Host "InstallDate:  $installDate" -ForegroundColor Gray
    Write-Host "LastUsedDate: $lastUsedDate" -ForegroundColor Gray
}
