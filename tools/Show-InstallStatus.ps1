<#
.SYNOPSIS
    Shows the current Reqnroll VSLSP installation status from the registry.
.PARAMETER Configuration
    'Debug' (default for VS Exp instance) or 'Release'
.EXAMPLE
    .\tools\Show-InstallStatus.ps1
    .\tools\Show-InstallStatus.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)

. "$PSScriptRoot\ReqnrollRegistry.ps1"

Get-ReqnrollInstallStatus -Configuration $Configuration
