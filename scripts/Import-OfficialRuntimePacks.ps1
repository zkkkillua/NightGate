[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $SourceDirectory = (Join-Path $HOME 'Downloads'),
    [AllowEmptyString()] [string] $FeedDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Initialize-NightGateBuildEnvironment
$dotnet = Resolve-NightGateDotNet
$source = [IO.Path]::GetFullPath($SourceDirectory)
$feed = if ([string]::IsNullOrWhiteSpace($FeedDirectory)) {
    Join-Path (Get-NightGateRepoRoot) 'work\nuget-feed'
}
else {
    [IO.Path]::GetFullPath($FeedDirectory)
}
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Runtime-pack download directory is unavailable: $source"
}

$verified = [Collections.Generic.List[object]]::new()
$missing = [Collections.Generic.List[string]]::new()
foreach ($requirement in Get-NightGateRuntimePackRequirements) {
    $canonicalName = "$($requirement.id).$($requirement.version).nupkg"
    $stem = [regex]::Escape("$($requirement.id).$($requirement.version)")
    $candidates = @(Get-ChildItem -LiteralPath $source -File |
        Where-Object {
            $_.Name -match "(?i)^${stem}(?: \(\d+\))?\.nupkg$"
        } |
        Sort-Object LastWriteTimeUtc -Descending)
    $accepted = $null
    $rejections = [Collections.Generic.List[string]]::new()
    foreach ($candidate in $candidates) {
        try {
            $identity = Get-NightGateNuGetPackageIdentity -Path $candidate.FullName
            if ([string]$identity.id -cne [string]$requirement.id -or
                [string]$identity.version -cne [string]$requirement.version) {
                throw "nuspec identity is $($identity.id) $($identity.version)"
            }

            $signatureOutput = & $dotnet 'nuget' 'verify' `
                $candidate.FullName '--all' '--verbosity' 'normal' 2>&1 |
                Out-String
            if ($LASTEXITCODE -ne 0) {
                throw "NuGet signature verification failed. $signatureOutput"
            }

            $accepted = [pscustomobject][ordered]@{
                id = [string]$requirement.id
                version = [string]$requirement.version
                sourcePath = $candidate.FullName
                destinationPath = Join-Path $feed $canonicalName
                sha512 = Get-NightGateSha512 -Path $candidate.FullName
            }
            break
        }
        catch {
            $rejections.Add("$($candidate.Name): $($_.Exception.Message)")
        }
    }

    if ($null -eq $accepted) {
        $detail = if ($rejections.Count -eq 0) {
            'not downloaded'
        }
        else {
            $rejections -join '; '
        }
        $missing.Add("$canonicalName ($detail)")
    }
    else {
        $verified.Add($accepted)
    }
}

if ($missing.Count -ne 0) {
    throw ("The following official signed runtime packs are unavailable or invalid:`n  - " +
        ($missing -join "`n  - "))
}

if ($WhatIfPreference) {
    foreach ($item in $verified) {
        Write-Host "Would import $($item.sourcePath) -> $($item.destinationPath)"
    }
    return
}

New-Item -ItemType Directory -Path $feed -Force | Out-Null
foreach ($item in $verified) {
    if ($PSCmdlet.ShouldProcess($item.destinationPath, 'Import verified runtime pack')) {
        Copy-Item -LiteralPath $item.sourcePath `
            -Destination $item.destinationPath -Force
    }
}

$evidence = @(Assert-NightGateOfficialRuntimePacks `
    -DotNetPath $dotnet `
    -FeedPath $feed)
$evidence | ConvertTo-Json -Depth 4
