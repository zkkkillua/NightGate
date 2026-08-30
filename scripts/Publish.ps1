[CmdletBinding()]
param(
    [switch] $SkipBuild,
    [switch] $ForcePrivateRuntimeFallback,
    [AllowEmptyString()] [string] $RuntimePackSha512ManifestPath,
    [AllowEmptyString()] [string] $PublishDirectory,
    [AllowEmptyString()] [string] $IsolatedArtifactsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-NightGateRepoRoot
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'Build.ps1')
}

Initialize-NightGateBuildEnvironment
$dotnet = Resolve-NightGateDotNet
$publishRoot = Resolve-NightGateRepoScopedDirectory `
    -Path $PublishDirectory `
    -DefaultRelativePath 'artifacts\publish'
$isolatedArtifactsRoot = Resolve-NightGateRepoScopedDirectory `
    -Path $IsolatedArtifactsDirectory `
    -DefaultRelativePath 'artifacts\isolated'
Remove-NightGateGeneratedDirectory -Path $publishRoot
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$projects = [ordered]@{
    Desktop = Join-Path $root 'src\NightGate.Desktop\NightGate.Desktop.csproj'
    Service = Join-Path $root 'src\NightGate.Service\NightGate.Service.csproj'
    NativeHost = Join-Path $root 'src\NightGate.NativeHost\NightGate.NativeHost.csproj'
}
$restoreProperties = Get-NightGateRestoreArguments
$buildProperties = Get-NightGateBuildProperties
$selfContainedRoot = Join-Path $publishRoot 'self-contained'
$attemptLog = Join-Path $publishRoot 'self-contained-attempt.log'
$attemptLines = [Collections.Generic.List[string]]::new()
$selfContained = -not $ForcePrivateRuntimeFallback
$runtimePackEvidence = @()
$fallbackReason = if ($ForcePrivateRuntimeFallback) {
    'Private-runtime fallback explicitly requested.'
}
else {
    ''
}

if ($selfContained) {
    # Formal release requires official signed win-x64 runtime packs.
    $runtimePackEvidence = @(Assert-NightGateOfficialRuntimePacks `
        -DotNetPath $dotnet `
        -ManifestPath $RuntimePackSha512ManifestPath)
}

function Invoke-CapturedDotNet {
    param([string[]] $Arguments)

    $output = & $dotnet @Arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    $script:attemptLines.Add("COMMAND: dotnet $($Arguments -join ' ')")
    $script:attemptLines.Add($output.TrimEnd())
    $script:attemptLines.Add("EXIT: $exitCode")
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Get-IsolatedArtifactsPath {
    param(
        [Parameter(Mandatory)] [string] $Lane,
        [Parameter(Mandatory)] [string] $ApplicationName
    )

    # The SDK's --artifacts-path assigns per-project BaseIntermediateOutputPath,
    # MSBuildProjectExtensionsPath, and BaseOutputPath without touching src/**/obj.
    return Join-Path $script:isolatedArtifactsRoot "$Lane\$ApplicationName"
}

if ($selfContained) {
    foreach ($entry in $projects.GetEnumerator()) {
        $isolatedArtifacts = Get-IsolatedArtifactsPath `
            -Lane 'self-contained' -ApplicationName $entry.Key
        $restoreArguments = @(
            'restore', $entry.Value,
            '--runtime', 'win-x64',
            '--artifacts-path', $isolatedArtifacts,
            '-p:SelfContained=true'
        ) + $restoreProperties
        $restoreResult = Invoke-CapturedDotNet -Arguments $restoreArguments
        if ($restoreResult.ExitCode -ne 0) {
            [IO.File]::WriteAllLines($attemptLog, $attemptLines)
            if (-not (Test-NightGateMissingRuntimePackFailure `
                -Output $restoreResult.Output)) {
                throw "Self-contained restore failed for a reason other than a missing runtime pack. See $attemptLog"
            }
            throw ("Self-contained restore could not resolve the required official " +
                "signed win-x64 runtime packs. Formal publishing does not fall back; " +
                "see $attemptLog")
        }

        $destination = Join-Path $selfContainedRoot $entry.Key
        $publishArguments = @(
            'publish', $entry.Value,
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--no-restore',
            '--artifacts-path', $isolatedArtifacts,
            '--output', $destination
        ) + $buildProperties
        $publishResult = Invoke-CapturedDotNet -Arguments $publishArguments
        if ($publishResult.ExitCode -ne 0) {
            [IO.File]::WriteAllLines($attemptLog, $attemptLines)
            throw "Self-contained publish failed after a successful restore. See $attemptLog"
        }
    }
}

[IO.File]::WriteAllLines($attemptLog, $attemptLines)

if ($selfContained) {
    $mode = [ordered]@{
        mode = 'genuine-self-contained'
        runtimeIdentifier = 'win-x64'
        sdkVersion = $script:NightGateExpectedSdk
        verified = $false
        fallbackReason = $null
        bundledPrivateRuntime = $false
        requiresInstalledDotNet = $false
        frameworkDependentBinaries = $false
        launchBinding = 'self-contained-apphost'
        releaseEligible = $true
        runtimePacks = $runtimePackEvidence
    }
}
else {
    if (Test-Path -LiteralPath $selfContainedRoot) {
        Remove-NightGateGeneratedDirectory -Path $selfContainedRoot
    }
    $privateRoot = Join-Path $publishRoot 'private-runtime-fallback'
    New-Item -ItemType Directory -Path $privateRoot -Force | Out-Null

    foreach ($entry in $projects.GetEnumerator()) {
        $isolatedArtifacts = Get-IsolatedArtifactsPath `
            -Lane 'private-runtime-fallback' -ApplicationName $entry.Key
        $restoreArguments = @(
            'restore', $entry.Value,
            '--artifacts-path', $isolatedArtifacts,
            '-p:SelfContained=false'
        ) + $restoreProperties
        Invoke-NightGateChecked -Executable $dotnet -Arguments $restoreArguments

        $destination = Join-Path $privateRoot ("apps\{0}" -f $entry.Key)
        $publishArguments = @(
            'publish', $entry.Value,
            '--configuration', 'Release',
            '--self-contained', 'false',
            '--no-restore',
            '--artifacts-path', $isolatedArtifacts,
            '--output', $destination,
            '-p:PlatformTarget=x64',
            '-p:UseAppHost=true',
            '-p:AppHostDotNetSearch=AppRelative',
            '-p:AppHostRelativeDotNet=..\..\runtime'
        ) + $buildProperties
        Invoke-NightGateChecked -Executable $dotnet -Arguments $publishArguments
    }

    $runtimeRoot = Split-Path -Parent $dotnet
    $runtimeDestination = Join-Path $privateRoot 'runtime'
    New-Item -ItemType Directory -Path $runtimeDestination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $runtimeRoot 'dotnet.exe') `
        -Destination $runtimeDestination

    $runtimeParts = @(
        "host\fxr\$script:NightGateRuntimeVersion",
        "shared\Microsoft.NETCore.App\$script:NightGateRuntimeVersion",
        "shared\Microsoft.WindowsDesktop.App\$script:NightGateRuntimeVersion"
    )
    foreach ($relativePath in $runtimeParts) {
        $source = Join-Path $runtimeRoot $relativePath
        if (-not (Test-Path -LiteralPath $source -PathType Container)) {
            throw "Required private runtime component is unavailable: $source"
        }
        $destination = Join-Path $runtimeDestination $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force |
            Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Recurse
    }

    $mode = [ordered]@{
        mode = 'private-runtime-fallback'
        runtimeIdentifier = 'win-x64'
        sdkVersion = $script:NightGateExpectedSdk
        runtimeVersion = $script:NightGateRuntimeVersion
        verified = $false
        fallbackReason = $fallbackReason
        bundledPrivateRuntime = $true
        requiresInstalledDotNet = $false
        frameworkDependentBinaries = $true
        launchBinding = 'app-relative-only'
        releaseEligible = $false
        diagnosticOnly = $true
        components = @(
            'dotnet.exe',
            "host/fxr/$script:NightGateRuntimeVersion",
            "shared/Microsoft.NETCore.App/$script:NightGateRuntimeVersion",
            "shared/Microsoft.WindowsDesktop.App/$script:NightGateRuntimeVersion"
        )
    }
}

if ($selfContained) {
    $runtimePackLockPath = Join-Path $publishRoot 'runtime-packs.sha512.json'
    $runtimePackLock = [ordered]@{
        schemaVersion = 1
        packages = @($runtimePackEvidence | ForEach-Object {
            [ordered]@{
                id = $_.id
                version = $_.version
                sha512 = $_.sha512
                signatureVerified = [bool]$_.signatureVerified
                trustedManifestMatched = [bool]$_.trustedManifestMatched
            }
        })
    }
    [IO.File]::WriteAllText(
        $runtimePackLockPath,
        ($runtimePackLock | ConvertTo-Json -Depth 6) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

$servicePublishDirectory = if ($selfContained) {
    Join-Path $selfContainedRoot 'Service'
}
else {
    Join-Path $privateRoot 'apps\Service'
}
$serviceTemplate = Join-Path $root 'src\NightGate.Service\appsettings.sample.json'
$serviceConfiguration = Join-Path $servicePublishDirectory 'appsettings.json'
Copy-Item -LiteralPath $serviceTemplate -Destination $serviceConfiguration -Force
$serviceConfigurationText = Get-Content -LiteralPath $serviceConfiguration `
    -Raw -Encoding UTF8
if ($serviceConfigurationText -notmatch '__CONFIGURED_WINDOWS_USER_SID__' -or
    $serviceConfigurationText -match 'S-1-5-21-\d') {
    throw 'Published service configuration must contain only the target-install SID placeholder.'
}

$modePath = Join-Path $publishRoot '.publish-mode.json'
$modeJson = $mode | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText(
    $modePath,
    $modeJson + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
Write-Host "Publish mode: $($mode.mode)"
Write-Host "Publish evidence: $modePath"
