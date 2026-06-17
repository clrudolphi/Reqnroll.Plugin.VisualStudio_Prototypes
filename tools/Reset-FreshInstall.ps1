<#
.SYNOPSIS
    Resets the Reqnroll install status to trigger a first-install Welcome dialog.
.DESCRIPTION
    Deletes the registry key so the extension thinks it has never been installed.
    The next time VS loads the extension, the Welcome dialog will appear after 7 seconds.
.PARAMETER Configuration
    'Debug' (default for VS Exp instance) or 'Release'
.EXAMPLE
    .\tools\Reset-FreshInstall.ps1
    .\tools\Reset-FreshInstall.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)

. "$PSScriptRoot\ReqnrollRegistry.ps1"

$path = Get-ReqnrollRegPath -Configuration $Configuration

if (Test-Path $path) {
    Remove-Item -Path $path -Recurse -Force
    Write-Host "Deleted registry key: $path" -ForegroundColor Green
    Write-Host "Welcome dialog will appear on next VS start." -ForegroundColor Green
} else {
    Write-Host "Registry key already absent: $path" -ForegroundColor Green
    Write-Host "Welcome dialog will appear on next VS start." -ForegroundColor Green
}
