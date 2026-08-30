[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$requirements = @(Get-NightGateRuntimePackRequirements | ForEach-Object {
    "  - $($_.id) $($_.version)"
}) -join [Environment]::NewLine

throw @"
NightGate refuses to reconstruct or forge Microsoft runtime-pack nupkg files from
an installed shared framework. A formal release requires the official signed
packages, their exact nuspec identities, and a successful
'dotnet nuget verify --all'. Publish computes SHA-512 hashes and records them in
its release evidence and runtime-pack lock.

Place these official signed packages in work\nuget-feed:
$requirements

Optionally pass -RuntimePackSha512ManifestPath to Publish.ps1 (or set
NIGHTGATE_RUNTIME_PACK_SHA512_MANIFEST) when a separately trusted schemaVersion 1
SHA-512 manifest is available. A supplied manifest is matched strictly; it is not
required when the package signatures verify successfully.

This command is intentionally a non-writing safety entry point.
"@
