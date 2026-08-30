Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:NightGateExpectedSdk = '10.0.301'
$script:NightGateRuntimeVersion = '10.0.9'
$script:NightGateRepoRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))

function Get-NightGateRepoRoot {
    return $script:NightGateRepoRoot
}

function Resolve-NightGateRepoScopedDirectory {
    param(
        [AllowEmptyString()] [string] $Path,
        [Parameter(Mandatory)] [string] $DefaultRelativePath
    )

    $candidate = if ([string]::IsNullOrWhiteSpace($Path)) {
        Join-Path $script:NightGateRepoRoot $DefaultRelativePath
    }
    elseif ([IO.Path]::IsPathRooted($Path)) {
        $Path
    }
    else {
        Join-Path $script:NightGateRepoRoot $Path
    }
    $fullPath = [IO.Path]::GetFullPath($candidate)
    $repoRoot = [IO.Path]::GetFullPath($script:NightGateRepoRoot).TrimEnd('\')
    $repoPrefix = $repoRoot + '\'
    if ($fullPath.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $fullPath.StartsWith(
            $repoPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated output directory must stay below the repository root: $fullPath"
    }
    return $fullPath
}

function Resolve-NightGateRepoScopedFile {
    param(
        [AllowEmptyString()] [string] $Path,
        [Parameter(Mandatory)] [string] $DefaultRelativePath
    )

    $candidate = if ([string]::IsNullOrWhiteSpace($Path)) {
        Join-Path $script:NightGateRepoRoot $DefaultRelativePath
    }
    elseif ([IO.Path]::IsPathRooted($Path)) {
        $Path
    }
    else {
        Join-Path $script:NightGateRepoRoot $Path
    }
    $fullPath = [IO.Path]::GetFullPath($candidate)
    $repoRoot = [IO.Path]::GetFullPath($script:NightGateRepoRoot).TrimEnd('\')
    if (-not $fullPath.StartsWith(
        $repoRoot + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated input or output file must stay below the repository root: $fullPath"
    }
    return $fullPath
}

function Resolve-NightGateDotNet {
    $localDotNet = Join-Path $script:NightGateRepoRoot 'work\.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotNet -PathType Leaf) {
        $candidate = $localDotNet
    }
    else {
        $command = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            $command = Get-Command 'dotnet' -ErrorAction SilentlyContinue
        }
        if ($null -eq $command) {
            throw "The pinned .NET SDK $script:NightGateExpectedSdk is unavailable."
        }
        $candidate = $command.Source
    }

    $actual = (& $candidate --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $script:NightGateExpectedSdk) {
        throw "Expected .NET SDK $script:NightGateExpectedSdk, found '$actual'."
    }
    return [IO.Path]::GetFullPath($candidate)
}

function Resolve-NightGateNode {
    if (-not [string]::IsNullOrWhiteSpace($env:NIGHTGATE_NODE)) {
        $candidate = $env:NIGHTGATE_NODE
    }
    else {
        $bundled = Join-Path $HOME (
            '.cache\codex-runtimes\codex-primary-runtime' +
            '\dependencies\node\bin\node.exe')
        if (Test-Path -LiteralPath $bundled -PathType Leaf) {
            $candidate = $bundled
        }
        else {
            $command = Get-Command 'node.exe' -ErrorAction SilentlyContinue
            if ($null -eq $command) {
                $command = Get-Command 'node' -ErrorAction SilentlyContinue
            }
            if ($null -eq $command) {
                throw 'Node.js is unavailable. Set NIGHTGATE_NODE to a Node executable.'
            }
            $candidate = $command.Source
        }
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Node.js executable not found: $candidate"
    }
    return [IO.Path]::GetFullPath($candidate)
}

function Initialize-NightGateBuildEnvironment {
    $cliHome = Join-Path $script:NightGateRepoRoot 'work\dotnet-home'
    $packages = Join-Path $script:NightGateRepoRoot 'work\.nuget\packages'
    New-Item -ItemType Directory -Path $cliHome, $packages -Force | Out-Null
    $env:DOTNET_CLI_HOME = $cliHome
    $env:NUGET_PACKAGES = $packages
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
}

function Get-NightGateRestoreArguments {
    param(
        [switch] $Offline = ($env:NIGHTGATE_OFFLINE_RESTORE -eq '1')
    )

    $arguments = [Collections.Generic.List[string]]::new()
    if ($Offline) {
        $localFeed = Join-Path $script:NightGateRepoRoot 'work\nuget-feed'
        if (-not (Test-Path -LiteralPath $localFeed -PathType Container)) {
            throw "Offline restore feed is missing: $localFeed"
        }
        # Only explicit offline mode overrides the sources in NuGet.Config.
        $arguments.Add('--source')
        $arguments.Add($localFeed)
    }
    $arguments.Add('-p:NuGetAudit=false')
    $arguments.Add('-p:ContinuousIntegrationBuild=true')
    $arguments.Add('-p:Deterministic=true')
    $arguments.Add('-p:TreatWarningsAsErrors=true')
    return $arguments.ToArray()
}

function Get-NightGateBuildProperties {
    return @(
        '-p:NuGetAudit=false',
        '-p:ContinuousIntegrationBuild=true',
        '-p:Deterministic=true',
        '-p:TreatWarningsAsErrors=true',
        '-p:WarningLevel=9999'
    )
}

function Test-NightGateMissingRuntimePackFailure {
    param([AllowEmptyString()] [string] $Output)

    $errorLines = @($Output -split "`r?`n" | Where-Object {
        $_ -match '(?i)\berror\b'
    })
    if ($errorLines.Count -eq 0) {
        return $false
    }
    foreach ($line in $errorLines) {
        if ($line -match '(?i)\berror\s+NETSDK1112\b') {
            continue
        }
        if ($line -match '(?i)\berror\s+NU1101\b' -and
            $line -match ('Microsoft\.(NETCore|WindowsDesktop|AspNetCore)' +
                '\.App\.Runtime\.win-x64')) {
            continue
        }
        return $false
    }
    return $true
}

function Invoke-NightGateChecked {
    param(
        [Parameter(Mandatory)] [string] $Executable,
        [Parameter()] [string[]] $Arguments = @()
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Executable $($Arguments -join ' ')"
    }
}

function Get-NightGateSha256 {
    param([Parameter(Mandatory)] [string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-NightGateSha512 {
    param([Parameter(Mandatory)] [string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash.ToUpperInvariant()
}

function Write-NightGateJsonAtomically {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [object] $Value
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $fullPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporaryPath = Join-Path $parent (
        '.{0}.{1}.tmp' -f
            [IO.Path]::GetFileName($fullPath),
            [Guid]::NewGuid().ToString('N'))
    $json = ($Value | ConvertTo-Json -Depth 10) + [Environment]::NewLine
    [IO.File]::WriteAllText(
        $temporaryPath,
        $json,
        [Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        # Windows PowerShell 5.1 turns a null backup path into an illegal empty
        # path for File.Replace. A unique backup also preserves prior evidence.
        $backupPath = Join-Path $parent (
            '.{0}.{1}.bak' -f
                [IO.Path]::GetFileName($fullPath),
                [Guid]::NewGuid().ToString('N'))
        [IO.File]::Replace($temporaryPath, $fullPath, $backupPath)
    }
    else {
        [IO.File]::Move($temporaryPath, $fullPath)
    }
}

function Get-NightGateTestSourceFingerprint {
    $root = Get-NightGateRepoRoot
    $sourceInputs = @(
        'Directory.Build.props',
        'NightGate.slnx',
        'NuGet.Config',
        'global.json',
        'README.md',
        'USER-GUIDE.zh-CN.md',
        'assets',
        'docs',
        'installer',
        'scripts',
        'src',
        'tests'
    )
    $ignoredDirectoryNames = @('bin', 'obj', 'node_modules', 'TestResults')
    $sourceFiles = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($sourceInput in $sourceInputs) {
        $inputPath = Join-Path $root $sourceInput
        if (Test-Path -LiteralPath $inputPath -PathType Leaf) {
            $sourceFiles.Add((Get-Item -LiteralPath $inputPath))
            continue
        }
        if (-not (Test-Path -LiteralPath $inputPath -PathType Container)) {
            throw "Test source input is missing: $inputPath"
        }
        foreach ($file in Get-ChildItem -LiteralPath $inputPath -File -Recurse -Force) {
            $relative = (Get-NightGateRelativePath -BasePath $root -Path $file.FullName).
                Replace('\', '/')
            $segments = @($relative -split '/')
            if ($file.Name -notlike '*_wpftmp.csproj' -and
                @($segments | Where-Object {
                $ignoredDirectoryNames -contains $_
            }).Count -eq 0) {
                $sourceFiles.Add($file)
            }
        }
    }

    [string[]]$relativePaths = @($sourceFiles | ForEach-Object {
        (Get-NightGateRelativePath -BasePath $root -Path $_.FullName).
            Replace('\', '/')
    })
    [Array]::Sort($relativePaths, [StringComparer]::Ordinal)
    $inventory = [Text.StringBuilder]::new()
    foreach ($relativePath in $relativePaths) {
        $filePath = Join-Path $root $relativePath.Replace('/', '\')
        $null = $inventory.Append((Get-NightGateSha256 -Path $filePath))
        $null = $inventory.Append(' *')
        $null = $inventory.Append($relativePath)
        $null = $inventory.Append("`n")
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($inventory.ToString())
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).
            Replace('-', '').ToUpperInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Assert-NightGateCompletedTestSummary {
    param(
        [Parameter(Mandatory)] [string] $SummaryPath,
        [Parameter(Mandatory)] [string] $ResultsRoot,
        [Parameter(Mandatory)] [ValidatePattern('^[A-F0-9]{64}$')]
        [string] $ExpectedSourceFingerprint
    )

    try {
        $summary = Get-Content -LiteralPath $SummaryPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    }
    catch {
        throw "Test summary is not valid JSON: $SummaryPath. $($_.Exception.Message)"
    }
    $requiredFields = @(
        'schemaVersion',
        'status',
        'runId',
        'startedAtUtc',
        'completedAtUtc',
        'sourceFingerprintAlgorithm',
        'sourceFingerprint',
        'dotnetPassed',
        'dotnetFailed',
        'dotnetSkipped',
        'nodePassed',
        'nodeFailed',
        'nodeSkipped',
        'failure'
    )
    foreach ($field in $requiredFields) {
        if ($null -eq $summary.PSObject.Properties[$field]) {
            throw "Test summary omitted required field: $field"
        }
    }
    if ($summary.schemaVersion -isnot [int] -and
        $summary.schemaVersion -isnot [long]) {
        throw 'Test summary schemaVersion must be an integer.'
    }
    if ([long]$summary.schemaVersion -ne 1) {
        throw "Unsupported test summary schema: $($summary.schemaVersion)"
    }
    if ([string]$summary.status -cne 'completed') {
        throw "Test summary is not completed; status=$($summary.status)."
    }
    if ([string]$summary.runId -notmatch '^\d{8}-\d{9}-[a-f0-9]{8}$') {
        throw "Test summary runId is invalid: $($summary.runId)"
    }
    if ([string]$summary.sourceFingerprintAlgorithm -cne
        'nightgate-test-source-v1-sha256') {
        throw 'Test summary source fingerprint algorithm is unsupported.'
    }
    if ([string]$summary.sourceFingerprint -cne
        $ExpectedSourceFingerprint.ToUpperInvariant()) {
        throw 'Test summary source fingerprint does not match the current source tree.'
    }
    if ($null -ne $summary.failure) {
        throw 'Completed test summary must not contain failure evidence.'
    }

    $startedAt = [DateTimeOffset]::MinValue
    $completedAt = [DateTimeOffset]::MinValue
    $validStartedAt = [DateTimeOffset]::TryParse(
        [string]$summary.startedAtUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$startedAt)
    $validCompletedAt = [DateTimeOffset]::TryParse(
        [string]$summary.completedAtUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$completedAt)
    if (-not $validStartedAt -or -not $validCompletedAt -or
        $startedAt.Offset -ne [TimeSpan]::Zero -or
        $completedAt.Offset -ne [TimeSpan]::Zero -or
        $completedAt -lt $startedAt) {
        throw 'Test summary timestamps are invalid or not ordered UTC timestamps.'
    }

    foreach ($field in @(
        'dotnetPassed',
        'dotnetFailed',
        'dotnetSkipped',
        'nodePassed',
        'nodeFailed',
        'nodeSkipped')) {
        $value = $summary.$field
        if (($value -isnot [int] -and $value -isnot [long]) -or
            [long]$value -lt 0) {
            throw "Test summary field $field must be a non-negative integer."
        }
    }
    if ([long]$summary.dotnetFailed -ne 0 -or
        [long]$summary.nodeFailed -ne 0) {
        throw 'Test summary contains a nonzero failed test count.'
    }
    if ([long]$summary.dotnetPassed -le 0 -or
        [long]$summary.nodePassed -le 0) {
        throw 'Test summary contains no positive passed test count.'
    }

    $runSummaryPath = Join-Path (
        Join-Path ([IO.Path]::GetFullPath($ResultsRoot)) ([string]$summary.runId)
    ) 'test-summary.json'
    if (-not (Test-Path -LiteralPath $runSummaryPath -PathType Leaf)) {
        throw "Test summary has no matching per-run summary: $runSummaryPath"
    }
    if ((Get-NightGateSha256 -Path $SummaryPath) -cne
        (Get-NightGateSha256 -Path $runSummaryPath)) {
        throw 'Canonical and per-run summary evidence do not match.'
    }
    return $summary
}

function Get-NightGateRuntimePackRequirements {
    return @(
        [pscustomobject][ordered]@{
            id = 'Microsoft.NETCore.App.Runtime.win-x64'
            version = $script:NightGateRuntimeVersion
        },
        [pscustomobject][ordered]@{
            id = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'
            version = $script:NightGateRuntimeVersion
        },
        [pscustomobject][ordered]@{
            id = 'Microsoft.AspNetCore.App.Runtime.win-x64'
            version = $script:NightGateRuntimeVersion
        }
    )
}

function Resolve-NightGateRuntimePackSha512ManifestPath {
    param([AllowEmptyString()] [string] $Path)

    $candidate = $Path
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = $env:NIGHTGATE_RUNTIME_PACK_SHA512_MANIFEST
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        return $null
    }
    elseif (-not [IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path $script:NightGateRepoRoot $candidate
    }
    return [IO.Path]::GetFullPath($candidate)
}

function Get-NightGateNuGetPackageIdentity {
    param([Parameter(Mandatory)] [string] $Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "NuGet package is missing: $fullPath"
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($fullPath)
    try {
        $nuspecEntries = @($archive.Entries | Where-Object {
            $_.FullName -match '(?i)(?:^|/)[^/]+\.nuspec$'
        })
        if ($nuspecEntries.Count -ne 1) {
            throw "Official package must contain exactly one nuspec: $fullPath"
        }
        $stream = $nuspecEntries[0].Open()
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
        try {
            [xml]$nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $metadata = $nuspec.SelectSingleNode(
        "/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) {
        throw "NuGet package has no metadata element: $fullPath"
    }
    $idNode = $metadata.SelectSingleNode("*[local-name()='id']")
    $versionNode = $metadata.SelectSingleNode("*[local-name()='version']")
    if ($null -eq $idNode -or $null -eq $versionNode -or
        [string]::IsNullOrWhiteSpace($idNode.InnerText) -or
        [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "NuGet package identity is incomplete: $fullPath"
    }
    return [pscustomobject][ordered]@{
        id = [string]$idNode.InnerText
        version = [string]$versionNode.InnerText
    }
}

function Assert-NightGateOfficialRuntimePacks {
    param(
        [Parameter(Mandatory)] [string] $DotNetPath,
        [AllowEmptyString()] [string] $ManifestPath,
        [AllowEmptyString()] [string] $FeedPath
    )

    if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) {
        throw "The dotnet executable required for NuGet signature verification is unavailable: $DotNetPath"
    }

    $requirements = @(Get-NightGateRuntimePackRequirements)
    $resolvedManifest = Resolve-NightGateRuntimePackSha512ManifestPath `
        -Path $ManifestPath
    $trustedManifestProvided = -not [string]::IsNullOrWhiteSpace(
        $resolvedManifest)
    $manifestPackages = @()
    if ($trustedManifestProvided) {
        if (-not (Test-Path -LiteralPath $resolvedManifest -PathType Leaf)) {
            throw "Trusted runtime-pack SHA-512 manifest is missing: $resolvedManifest"
        }
        try {
            $manifest = Get-Content -LiteralPath $resolvedManifest -Raw -Encoding UTF8 |
                ConvertFrom-Json
        }
        catch {
            throw "Runtime-pack SHA-512 manifest is invalid JSON: $resolvedManifest. $($_.Exception.Message)"
        }

        if ($null -eq $manifest.PSObject.Properties['schemaVersion'] -or
            [int]$manifest.schemaVersion -ne 1 -or
            $null -eq $manifest.PSObject.Properties['packages']) {
            throw 'Runtime-pack SHA-512 manifest must use schemaVersion 1 and contain packages.'
        }

        $manifestPackages = @($manifest.packages)
        if ($manifestPackages.Count -ne $requirements.Count) {
            throw "Runtime-pack SHA-512 manifest must contain exactly $($requirements.Count) packages."
        }
        $duplicateIds = @($manifestPackages | Group-Object { [string]$_.id } |
            Where-Object Count -ne 1)
        if ($duplicateIds.Count -ne 0) {
            throw 'Runtime-pack SHA-512 manifest contains duplicate package IDs.'
        }
    }

    $feed = $FeedPath
    if ([string]::IsNullOrWhiteSpace($feed)) {
        $feed = Join-Path $script:NightGateRepoRoot 'work\nuget-feed'
    }
    elseif (-not [IO.Path]::IsPathRooted($feed)) {
        $feed = Join-Path $script:NightGateRepoRoot $feed
    }
    $feed = [IO.Path]::GetFullPath($feed)
    $evidence = [Collections.Generic.List[object]]::new()
    foreach ($requirement in $requirements) {
        $expectedSha512 = $null
        if ($trustedManifestProvided) {
            $manifestMatch = @($manifestPackages | Where-Object {
                [string]$_.id -ceq [string]$requirement.id -and
                [string]$_.version -ceq [string]$requirement.version
            })
            if ($manifestMatch.Count -ne 1) {
                throw ("Runtime-pack SHA-512 manifest is missing the exact identity " +
                    "$($requirement.id) $($requirement.version).")
            }
            $expectedSha512 = [string]$manifestMatch[0].sha512
            if ($expectedSha512 -cnotmatch '^[A-F0-9]{128}$') {
                throw "Runtime-pack SHA-512 must be 128 uppercase hexadecimal characters: $($requirement.id)"
            }
        }

        $fileName = "$($requirement.id).$($requirement.version).nupkg"
        $packagePath = Join-Path $feed $fileName
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
            throw "Official signed win-x64 runtime pack is missing: $packagePath"
        }
        $identity = Get-NightGateNuGetPackageIdentity -Path $packagePath
        if ([string]$identity.id -cne [string]$requirement.id -or
            [string]$identity.version -cne [string]$requirement.version) {
            throw ("Runtime-pack nuspec identity mismatch for $fileName; expected " +
                "$($requirement.id) $($requirement.version), found " +
                "$($identity.id) $($identity.version).")
        }

        $actualSha512 = Get-NightGateSha512 -Path $packagePath
        if ($trustedManifestProvided -and
            $actualSha512 -cne $expectedSha512) {
            throw "Runtime-pack SHA-512 mismatch: $fileName"
        }

        $signatureOutput = & $DotNetPath 'nuget' 'verify' $packagePath '--all' `
            '--verbosity' 'detailed' 2>&1 | Out-String
        $signatureExitCode = $LASTEXITCODE
        if ($signatureExitCode -ne 0) {
            throw ("NuGet signature verification failed or is unavailable for " +
                "$fileName (exit $signatureExitCode). $signatureOutput")
        }

        $evidence.Add([pscustomobject][ordered]@{
            id = [string]$requirement.id
            version = [string]$requirement.version
            fileName = $fileName
            sha512 = $actualSha512
            signatureVerified = $true
            trustedManifestMatched = $trustedManifestProvided
        })
    }
    return $evidence.ToArray()
}

function Get-NightGateStableGuid {
    param([Parameter(Mandatory)] [string] $Text)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $algorithm.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes("NightGate/MSI/$Text"))
    }
    finally {
        $algorithm.Dispose()
    }
    $bytes = [byte[]]$hash[0..15]
    $bytes[6] = ($bytes[6] -band 0x0F) -bor 0x50
    $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
    return ([Guid]::new([byte[]]$bytes)).ToString('B').ToUpperInvariant()
}

function Get-NightGateMsiIdentity {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^\d+\.\d+\.\d+$')]
        [string] $ProductVersion
    )

    $versionParts = @($ProductVersion -split '\.' | ForEach-Object { [int]$_ })
    if ($versionParts.Count -ne 3 -or
        $versionParts[0] -gt 255 -or
        $versionParts[1] -gt 255 -or
        $versionParts[2] -gt 65535) {
        throw 'ProductVersion must be a valid three-field Windows Installer version.'
    }
    return [pscustomobject][ordered]@{
        ProductVersion = $ProductVersion
        ProductCode = Get-NightGateStableGuid "product/$ProductVersion"
        UpgradeCode = '{B2D91E43-3320-4F82-AE8B-6D4A8769E066}'
    }
}

function Get-NightGateWixSourceIdentity {
    param([Parameter(Mandatory)] [string] $Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "WiX source is missing: $fullPath"
    }
    [xml]$document = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
    $package = $document.SelectSingleNode(
        "/*[local-name()='Wix']/*[local-name()='Package']")
    if ($null -eq $package) {
        throw "WiX source has no Package element: $fullPath"
    }
    $identity = [pscustomobject][ordered]@{
        ProductVersion = [string]$package.GetAttribute('Version')
        ProductCode = ([string]$package.GetAttribute('ProductCode')).ToUpperInvariant()
        UpgradeCode = ([string]$package.GetAttribute('UpgradeCode')).ToUpperInvariant()
    }
    if ($identity.ProductVersion -notmatch '^\d+\.\d+\.\d+$' -or
        $identity.ProductCode -notmatch '^\{[A-F0-9-]{36}\}$' -or
        $identity.UpgradeCode -notmatch '^\{[A-F0-9-]{36}\}$') {
        throw "WiX source does not contain a literal release identity: $fullPath"
    }
    return $identity
}

function New-NightGateRenderedWixSource {
    param(
        [Parameter(Mandatory)] [string] $TemplatePath,
        [Parameter(Mandatory)] [string] $OutputPath,
        [Parameter(Mandatory)] $Identity
    )

    $template = Get-Content -LiteralPath $TemplatePath -Raw -Encoding UTF8
    $rendered = $template
    $replacements = [ordered]@{
        'ProductCode="$(var.ProductCode)"' =
            "ProductCode=`"$($Identity.ProductCode)`""
        'Version="$(var.ProductVersion)"' =
            "Version=`"$($Identity.ProductVersion)`""
    }
    foreach ($entry in $replacements.GetEnumerator()) {
        $count = [regex]::Matches(
            $rendered,
            [regex]::Escape([string]$entry.Key)).Count
        if ($count -ne 1) {
            throw "WiX identity template token must occur exactly once: $($entry.Key)"
        }
        $rendered = $rendered.Replace([string]$entry.Key, [string]$entry.Value)
    }
    if ($rendered -match '\$\(var\.(?:ProductCode|ProductVersion)\)') {
        throw 'Rendered WiX source retains an unresolved release identity variable.'
    }

    $output = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force |
        Out-Null
    [IO.File]::WriteAllText(
        $output,
        $rendered,
        [Text.UTF8Encoding]::new($false))
    $renderedIdentity = Get-NightGateWixSourceIdentity -Path $output
    foreach ($field in @('ProductVersion', 'ProductCode', 'UpgradeCode')) {
        if ([string]$renderedIdentity.$field -ne [string]$Identity.$field) {
            throw "Rendered WiX $field does not match the requested MSI identity."
        }
    }
}

function Get-NightGateMsiArtifactIdentity {
    param([Parameter(Mandatory)] [string] $Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "MSI artifact is missing: $fullPath"
    }
    $installer = $null
    $database = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($fullPath, 0)
        $properties = [ordered]@{}
        foreach ($name in @('ProductVersion', 'ProductCode', 'UpgradeCode')) {
            $view = $null
            $record = $null
            try {
                $view = $database.OpenView(
                    "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$name'")
                $null = $view.Execute()
                $record = $view.Fetch()
                if ($null -eq $record) {
                    throw "MSI Property table is missing $name."
                }
                $properties[$name] = [string]$record.StringData(1)
            }
            finally {
                if ($null -ne $record) {
                    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) |
                        Out-Null
                }
                if ($null -ne $view) {
                    $null = $view.Close()
                    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) |
                        Out-Null
                }
            }
        }
        return [pscustomobject][ordered]@{
            ProductVersion = $properties.ProductVersion
            ProductCode = $properties.ProductCode.ToUpperInvariant()
            UpgradeCode = $properties.UpgradeCode.ToUpperInvariant()
        }
    }
    finally {
        if ($null -ne $database) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) |
                Out-Null
        }
        if ($null -ne $installer) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) |
                Out-Null
        }
    }
}

function Get-NightGateRelativePath {
    param(
        [Parameter(Mandatory)] [string] $BasePath,
        [Parameter(Mandatory)] [string] $Path
    )
    $base = [IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside its expected root: $full"
    }
    return $full.Substring($base.Length)
}

function Remove-NightGateGeneratedDirectory {
    param([Parameter(Mandatory)] [string] $Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $allowedRoots = @(
        [IO.Path]::GetFullPath((Join-Path $script:NightGateRepoRoot 'outputs')),
        [IO.Path]::GetFullPath((Join-Path $script:NightGateRepoRoot 'artifacts'))
    )
    $insideAllowedRoot = $false
    foreach ($allowedRoot in $allowedRoots) {
        $prefix = $allowedRoot.TrimEnd('\') + '\'
        if ($fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $insideAllowedRoot = $true
            break
        }
    }
    if (-not $insideAllowedRoot) {
        throw "Refusing to clear a path outside generated output roots: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}
