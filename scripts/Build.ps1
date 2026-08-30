[CmdletBinding()]
param(
    [switch] $SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-NightGateRepoRoot
if (-not $SkipRestore) {
    & (Join-Path $PSScriptRoot 'Restore.ps1')
}

Initialize-NightGateBuildEnvironment
$dotnet = Resolve-NightGateDotNet
$arguments = @(
    'build', (Join-Path $root 'NightGate.slnx'),
    '--configuration', 'Release',
    '--no-restore'
) + (Get-NightGateBuildProperties) + @('-p:PlatformTarget=x64')
Invoke-NightGateChecked -Executable $dotnet -Arguments $arguments
