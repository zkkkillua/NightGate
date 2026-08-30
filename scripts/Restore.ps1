[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Initialize-NightGateBuildEnvironment
$dotnet = Resolve-NightGateDotNet
$solution = Join-Path (Get-NightGateRepoRoot) 'NightGate.slnx'
$arguments = @('restore', $solution) + (Get-NightGateRestoreArguments)
Invoke-NightGateChecked -Executable $dotnet -Arguments $arguments
