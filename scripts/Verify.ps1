[CmdletBinding()]
param(
    [switch] $SkipPipeline,
    [switch] $AllowPrivateRuntimeFallback,
    [AllowEmptyString()] [string] $RuntimePackSha512ManifestPath,
    [AllowEmptyString()] [string] $PublishDirectory,
    [AllowEmptyString()] [string] $IsolatedArtifactsDirectory,
    [AllowEmptyString()] [string] $OutputDirectory,
    [AllowEmptyString()] [string] $TestSummaryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-NightGateRepoRoot
$productShortName = ([char]0x6536).ToString() + ([char]0x5C3E).ToString()
$productDisplayName = "$productShortName NightGate"
$publishRoot = Resolve-NightGateRepoScopedDirectory `
    -Path $PublishDirectory `
    -DefaultRelativePath 'artifacts\publish'
$isolatedArtifactsRoot = Resolve-NightGateRepoScopedDirectory `
    -Path $IsolatedArtifactsDirectory `
    -DefaultRelativePath 'artifacts\isolated'
$outputs = Resolve-NightGateRepoScopedDirectory `
    -Path $OutputDirectory `
    -DefaultRelativePath 'outputs'
$testSummaryPath = Resolve-NightGateRepoScopedFile `
    -Path $TestSummaryPath `
    -DefaultRelativePath 'outputs\test-results\test-summary.json'
$testResultsRoot = Split-Path -Parent $testSummaryPath
New-Item -ItemType Directory -Path $outputs -Force | Out-Null
$commands = [Collections.Generic.List[string]]::new()

function Invoke-ReleaseScript {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter()] [hashtable] $Arguments = @{}
    )
    $displayArguments = @($Arguments.GetEnumerator() | Sort-Object Key | ForEach-Object {
        if ($_.Value -is [switch] -or $_.Value -eq $true) { "-$($_.Key)" }
        else { "-$($_.Key) '$($_.Value)'" }
    })
    $script:commands.Add("powershell -NoProfile -File scripts/$Name $($displayArguments -join ' ')")
    & (Join-Path $PSScriptRoot $Name) @Arguments
}

if (-not $SkipPipeline) {
    Invoke-ReleaseScript -Name 'Restore.ps1'
    Invoke-ReleaseScript -Name 'Test.ps1' -Arguments @{ SkipRestore = $true }
    Invoke-ReleaseScript -Name 'Build.ps1' -Arguments @{ SkipRestore = $true }
    $publishArguments = @{
        SkipBuild = $true
        PublishDirectory = $publishRoot
        IsolatedArtifactsDirectory = $isolatedArtifactsRoot
    }
    if (-not [string]::IsNullOrWhiteSpace($RuntimePackSha512ManifestPath)) {
        $publishArguments.RuntimePackSha512ManifestPath =
            $RuntimePackSha512ManifestPath
    }
    Invoke-ReleaseScript -Name 'Publish.ps1' -Arguments $publishArguments
    Invoke-ReleaseScript -Name 'Package.ps1' -Arguments @{
        SkipPublish = $true
        PublishDirectory = $publishRoot
        OutputDirectory = $outputs
    }
}

$zipPath = Join-Path $outputs 'NightGate-win-x64.zip'
$checksumPath = Join-Path $outputs 'NightGate-win-x64.zip.sha256'
$msiPath = Join-Path $outputs 'NightGate-x64.msi'
$msiChecksumPath = Join-Path $outputs 'NightGate-x64.msi.sha256'
$modePath = Join-Path $publishRoot '.publish-mode.json'
$installerStatusPath = Join-Path $outputs 'installer-status.json'
foreach ($required in @(
    $zipPath, $checksumPath, $msiPath, $msiChecksumPath, $modePath,
    $testSummaryPath, $installerStatusPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Verification input is missing: $required"
    }
}
$currentTestSourceFingerprint = Get-NightGateTestSourceFingerprint
$testSummary = Assert-NightGateCompletedTestSummary `
    -SummaryPath $testSummaryPath `
    -ResultsRoot $testResultsRoot `
    -ExpectedSourceFingerprint $currentTestSourceFingerprint

$firstZipHash = Get-NightGateSha256 -Path $zipPath
if (-not $SkipPipeline) {
    Invoke-ReleaseScript -Name 'Package.ps1' -Arguments @{
        SkipPublish = $true
        PublishDirectory = $publishRoot
        OutputDirectory = $outputs
    }
    $secondZipHash = Get-NightGateSha256 -Path $zipPath
    if ($secondZipHash -ne $firstZipHash) {
        throw 'Two packages from the same clean publish tree produced different ZIP hashes.'
    }
}
else {
    $secondZipHash = $firstZipHash
}

$checksumLine = (Get-Content -LiteralPath $checksumPath -Raw -Encoding UTF8).Trim()
$checksumMatch = [regex]::Match(
    $checksumLine,
    '^([A-F0-9]{64}) \*NightGate-win-x64\.zip$')
if (-not $checksumMatch.Success -or
    $checksumMatch.Groups[1].Value -ne $secondZipHash) {
    throw 'The ZIP checksum file does not match the release artifact.'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$extractRoot = Join-Path $outputs 'verify-extract'
Remove-NightGateGeneratedDirectory -Path $extractRoot
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
$readArchive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $extractPrefix = [IO.Path]::GetFullPath($extractRoot).TrimEnd('\') + '\'
    foreach ($entry in $readArchive.Entries) {
        $entryPath = $entry.FullName.Replace('/', '\')
        if ([IO.Path]::IsPathRooted($entryPath)) {
            throw "ZIP contains a rooted entry: $($entry.FullName)"
        }
        $destination = [IO.Path]::GetFullPath((Join-Path $extractRoot $entryPath))
        if (-not $destination.StartsWith(
            $extractPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "ZIP entry escapes the extraction root: $($entry.FullName)"
        }
    }
}
finally {
    $readArchive.Dispose()
}
[IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extractRoot)

$inventoryPath = Join-Path $extractRoot 'file-inventory.sha256'
if (-not (Test-Path -LiteralPath $inventoryPath -PathType Leaf)) {
    throw 'Extracted package has no file inventory.'
}
$expectedInventory = @(Get-Content -LiteralPath $inventoryPath -Encoding UTF8)
$actualInventory = @(Get-ChildItem -LiteralPath $extractRoot -File -Recurse |
    Where-Object { $_.FullName -ne $inventoryPath } |
    Sort-Object { Get-NightGateRelativePath -BasePath $extractRoot -Path $_.FullName } |
    ForEach-Object {
        $relative = (Get-NightGateRelativePath -BasePath $extractRoot -Path $_.FullName).Replace('\', '/')
        '{0}  {1,12}  {2}' -f (Get-NightGateSha256 -Path $_.FullName), $_.Length, $relative
    })
if ($expectedInventory.Count -ne $actualInventory.Count -or
    (Compare-Object -ReferenceObject $expectedInventory -DifferenceObject $actualInventory)) {
    throw 'Extracted file inventory does not round-trip.'
}
$stagedInventory = Get-Content -LiteralPath (
    Join-Path $outputs 'staging\NightGate\file-inventory.sha256') -Encoding UTF8
if (Compare-Object -ReferenceObject $stagedInventory -DifferenceObject $expectedInventory) {
    throw 'Staged and extracted inventories differ.'
}

$publishMode = Get-Content -LiteralPath $modePath -Raw -Encoding UTF8 | ConvertFrom-Json
$releaseMode = Get-Content -LiteralPath (
    Join-Path $extractRoot '.release-mode.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($publishMode.mode -ne $releaseMode.mode) {
    throw 'Publish and packaged release modes disagree.'
}
$diagnosticVerification = $false
if ($publishMode.mode -eq 'private-runtime-fallback') {
    # Formal release verification must never accept the development fallback.
    if (-not $AllowPrivateRuntimeFallback) {
        throw ('Formal release verification rejects private-runtime-fallback. ' +
            'Use -AllowPrivateRuntimeFallback only for explicit diagnostics.')
    }
    $diagnosticVerification = $true
    if ($null -eq $publishMode.PSObject.Properties['releaseEligible'] -or
        [bool]$publishMode.releaseEligible) {
        throw 'Private-runtime diagnostics must be marked releaseEligible=false.'
    }
    if (-not [bool]$publishMode.bundledPrivateRuntime -or
        [bool]$publishMode.requiresInstalledDotNet -or
        -not [bool]$publishMode.frameworkDependentBinaries -or
        $publishMode.launchBinding -ne 'app-relative-only') {
        throw 'Private-runtime publish evidence does not describe its exact launch contract.'
    }
    foreach ($runtimePart in @(
        'runtime\dotnet.exe',
        "runtime\host\fxr\$script:NightGateRuntimeVersion",
        "runtime\shared\Microsoft.NETCore.App\$script:NightGateRuntimeVersion",
        "runtime\shared\Microsoft.WindowsDesktop.App\$script:NightGateRuntimeVersion")) {
        if (-not (Test-Path -LiteralPath (Join-Path $extractRoot $runtimePart))) {
            throw "Private runtime component is missing: $runtimePart"
        }
    }
    $fxrVersions = @(Get-ChildItem -LiteralPath (
        Join-Path $extractRoot 'runtime\host\fxr') -Directory | Select-Object -ExpandProperty Name)
    if ($fxrVersions.Count -ne 1 -or
        $fxrVersions[0] -ne $script:NightGateRuntimeVersion) {
        throw 'Private runtime contains an unexpected host/fxr version.'
    }
}
elseif ($publishMode.mode -ne 'genuine-self-contained') {
    throw "Unsupported release mode: $($publishMode.mode)"
}
elseif ([bool]$publishMode.bundledPrivateRuntime -or
    [bool]$publishMode.requiresInstalledDotNet -or
    [bool]$publishMode.frameworkDependentBinaries -or
    $publishMode.launchBinding -ne 'self-contained-apphost') {
    throw 'Self-contained publish evidence does not describe a self-contained apphost.'
}
else {
    if ($null -eq $publishMode.PSObject.Properties['releaseEligible'] -or
        -not [bool]$publishMode.releaseEligible) {
        throw 'Genuine self-contained publish evidence must be releaseEligible.'
    }

    Initialize-NightGateBuildEnvironment
    $runtimePackDotNet = Resolve-NightGateDotNet
    $verifiedRuntimePacks = @(Assert-NightGateOfficialRuntimePacks `
        -DotNetPath $runtimePackDotNet `
        -ManifestPath $RuntimePackSha512ManifestPath)
    if ($null -eq $publishMode.PSObject.Properties['runtimePacks']) {
        throw 'Published runtime-pack trust evidence is missing.'
    }
    $publishedRuntimePacks = @($publishMode.runtimePacks)
    if ($publishedRuntimePacks.Count -ne $verifiedRuntimePacks.Count) {
        throw 'Published runtime-pack trust evidence is incomplete.'
    }
    foreach ($verifiedPack in $verifiedRuntimePacks) {
        $publishedPack = @($publishedRuntimePacks | Where-Object {
            [string]$_.id -ceq [string]$verifiedPack.id -and
            [string]$_.version -ceq [string]$verifiedPack.version
        })
        if ($publishedPack.Count -ne 1 -or
            [string]$publishedPack[0].sha512 -cne [string]$verifiedPack.sha512 -or
            -not [bool]$publishedPack[0].signatureVerified -or
            [bool]$publishedPack[0].trustedManifestMatched -ne
                [bool]$verifiedPack.trustedManifestMatched) {
            throw "Published runtime-pack trust evidence disagrees for $($verifiedPack.id)."
        }
    }

    $runtimePackLockPath = Join-Path $root `
        'artifacts\publish\runtime-packs.sha512.json'
    if (-not (Test-Path -LiteralPath $runtimePackLockPath -PathType Leaf)) {
        throw "Published runtime-pack SHA-512 lock is missing: $runtimePackLockPath"
    }
    $runtimePackLock = Get-Content -LiteralPath $runtimePackLockPath `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $runtimePackLock.PSObject.Properties['schemaVersion'] -or
        [int]$runtimePackLock.schemaVersion -ne 1 -or
        $null -eq $runtimePackLock.PSObject.Properties['packages']) {
        throw 'Published runtime-pack SHA-512 lock has an unsupported schema.'
    }
    $lockedRuntimePacks = @($runtimePackLock.packages)
    if ($lockedRuntimePacks.Count -ne $verifiedRuntimePacks.Count) {
        throw 'Published runtime-pack SHA-512 lock is incomplete.'
    }
    foreach ($verifiedPack in $verifiedRuntimePacks) {
        $lockedPack = @($lockedRuntimePacks | Where-Object {
            [string]$_.id -ceq [string]$verifiedPack.id -and
            [string]$_.version -ceq [string]$verifiedPack.version
        })
        if ($lockedPack.Count -ne 1 -or
            [string]$lockedPack[0].sha512 -cne [string]$verifiedPack.sha512 -or
            -not [bool]$lockedPack[0].signatureVerified -or
            [bool]$lockedPack[0].trustedManifestMatched -ne
                [bool]$verifiedPack.trustedManifestMatched) {
            throw "Runtime-pack SHA-512 lock disagrees for $($verifiedPack.id)."
        }
    }

    if (Test-Path -LiteralPath (Join-Path $extractRoot 'runtime')) {
        throw 'Genuine self-contained release contains a forbidden top-level runtime directory.'
    }
    $commandWrappers = @(Get-ChildItem -LiteralPath $extractRoot -Filter '*.cmd' -File -Recurse)
    if ($commandWrappers.Count -ne 0) {
        throw 'Genuine self-contained release contains forbidden .cmd launch wrappers.'
    }

    foreach ($application in @('Desktop', 'Service', 'NativeHost')) {
        $applicationRoot = Join-Path $extractRoot "apps\$application"
        foreach ($runtimeAsset in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')) {
            if (-not (Test-Path -LiteralPath (
                Join-Path $applicationRoot $runtimeAsset) -PathType Leaf)) {
                throw "Self-contained $application is missing $runtimeAsset."
            }
        }
    }
    $desktopRoot = Join-Path $extractRoot 'apps\Desktop'
    foreach ($desktopAsset in @(
        'PresentationFramework.dll',
        'PresentationCore.dll',
        'WindowsBase.dll',
        'wpfgfx_cor3.dll')) {
        if (-not (Test-Path -LiteralPath (
            Join-Path $desktopRoot $desktopAsset) -PathType Leaf)) {
            throw "Self-contained Desktop is missing WindowsDesktop asset $desktopAsset."
        }
    }
}

$serviceConfigurationPath = Join-Path $extractRoot `
    'apps\Service\appsettings.json'
$serviceConfigurationText = Get-Content -LiteralPath $serviceConfigurationPath `
    -Raw -Encoding UTF8
if ($serviceConfigurationText -notmatch '__CONFIGURED_WINDOWS_USER_SID__' -or
    $serviceConfigurationText -match 'S-1-5-21-\d') {
    throw 'Release payload contains a build-machine SID instead of the install-time placeholder.'
}

$nativeManifestPath = Join-Path $extractRoot 'native-host\com.nightgate.host.json'
$nativeManifestText = Get-Content -LiteralPath $nativeManifestPath -Raw -Encoding UTF8
if ($nativeManifestText -match '__[A-Z0-9_]+__') {
    throw 'Packaged native-host manifest contains a placeholder.'
}
$nativeManifest = $nativeManifestText | ConvertFrom-Json
if (@($nativeManifest.allowed_origins).Count -ne 1 -or
    $nativeManifest.allowed_origins[0] -ne
        'chrome-extension://eefgemhlhbdodhlgjmicnoifhclhdgmm/' -or
    -not [IO.Path]::IsPathRooted([string]$nativeManifest.path)) {
    throw 'Packaged native-host manifest is not bound to the fixed extension and an absolute path.'
}

$extensionManifestPath = Join-Path $extractRoot 'chrome-extension\manifest.json'
$extensionManifest = Get-Content -LiteralPath $extensionManifestPath `
    -Raw -Encoding UTF8 | ConvertFrom-Json
if ($extensionManifest.version -ne '0.1.5') {
    throw 'Packaged Chrome extension must provide release version 0.1.5.'
}

$commands.Add('powershell -NoProfile -File scripts/Invoke-DemoSmoke.ps1 -AsJson')
$demoJson = & (Join-Path $PSScriptRoot 'Invoke-DemoSmoke.ps1') -AsJson
$parsedDemo = $demoJson | ConvertFrom-Json
$demo = @($parsedDemo)
if ($demo.Count -ne 5 -or @($demo | Where-Object { $_.mutatedMachine }).Count -ne 0) {
    throw 'The non-mutating demo smoke result is invalid.'
}

$nativeHostExe = Join-Path $extractRoot 'apps\NativeHost\NightGate.NativeHost.exe'
$commands.Add('staged NativeHost isolated runtime < empty-input (system dotnet disabled)')
$environmentNames = @(
    'DOTNET_ROOT',
    'DOTNET_ROOT_X64',
    'DOTNET_MULTILEVEL_LOOKUP',
    'PATH')
$previousEnvironment = @{}
foreach ($environmentName in $environmentNames) {
    $previousEnvironment[$environmentName] = [Environment]::GetEnvironmentVariable(
        $environmentName, 'Process')
}
$previousConsoleInputEncoding = [Console]::InputEncoding
$restoreEnvironment = $false
$restoreConsoleInputEncoding = $false
$nativeHostProcess = $null
$nativeHostError = ''
$nativeHostExitCode = $null
try {
    # Windows PowerShell 5 creates Process.StandardInput with the current
    # console input encoding. Test runners can switch it to UTF-8 with a BOM,
    # which would turn an empty-input smoke into a three-byte malformed frame.
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    $restoreConsoleInputEncoding = $true
    # ProcessStartInfo.EnvironmentVariables is unreliable in Windows
    # PowerShell 5 when the inherited environment has case-duplicate keys.
    # Point every system-runtime probe at a nonexistent-system-dotnet sentinel
    # and remove dotnet.exe directories from PATH. This must apply to genuine
    # self-contained and diagnostic app-relative binaries alike.
    $restoreEnvironment = $true
    $nonexistentSystemDotNet = Join-Path $extractRoot `
        'nonexistent-system-dotnet'
    [Environment]::SetEnvironmentVariable(
        'DOTNET_ROOT', $nonexistentSystemDotNet, 'Process')
    [Environment]::SetEnvironmentVariable(
        'DOTNET_ROOT_X64', $nonexistentSystemDotNet, 'Process')
    [Environment]::SetEnvironmentVariable(
        'DOTNET_MULTILEVEL_LOOKUP', '0', 'Process')
    $pathWithoutDotNet = @(
        ([string]$previousEnvironment['PATH']) -split ';' |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) -and
                -not (Test-Path -LiteralPath (Join-Path $_ 'dotnet.exe') `
                    -PathType Leaf)
            }) -join ';'
    [Environment]::SetEnvironmentVariable(
        'PATH', $pathWithoutDotNet, 'Process')

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $nativeHostExe
    $startInfo.WorkingDirectory = Split-Path -Parent $nativeHostExe
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $nativeHostProcess = New-Object Diagnostics.Process
    $nativeHostProcess.StartInfo = $startInfo
    if (-not $nativeHostProcess.Start()) {
        throw 'The staged native host could not start.'
    }
    $nativeHostProcess.StandardInput.BaseStream.Close()
    if (-not $nativeHostProcess.WaitForExit(10000)) {
        $nativeHostProcess.Kill()
        throw 'The staged native host did not exit after receiving EOF.'
    }
    $nativeHostError = $nativeHostProcess.StandardError.ReadToEnd()
    $nativeHostExitCode = $nativeHostProcess.ExitCode
}
finally {
    if ($null -ne $nativeHostProcess) {
        $nativeHostProcess.Dispose()
    }
    if ($restoreEnvironment) {
        foreach ($environmentName in $environmentNames) {
            [Environment]::SetEnvironmentVariable(
                $environmentName,
                $previousEnvironment[$environmentName],
                'Process')
        }
    }
    if ($restoreConsoleInputEncoding) {
        [Console]::InputEncoding = $previousConsoleInputEncoding
    }
}
if ($nativeHostExitCode -ne 0) {
    throw "The staged native-host EOF smoke failed ($nativeHostExitCode): $nativeHostError"
}

$releaseSourceFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -File -Recurse
    Get-ChildItem -LiteralPath (Join-Path $root 'installer') -File -Recurse
    Get-Item -LiteralPath (Join-Path $root 'README.md')
    Get-ChildItem -LiteralPath (Join-Path $root 'tests\NightGate.Release.Tests') -File -Recurse
)
foreach ($file in $releaseSourceFiles) {
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName -Encoding UTF8) {
        $lineNumber++
        if ($line -match '[ \t]+$') {
            throw "Trailing whitespace: $($file.FullName):$lineNumber"
        }
    }
}

$git = Get-Command 'git.exe' -ErrorAction SilentlyContinue
if ($null -eq $git) {
    $git = Get-Command 'git' -ErrorAction SilentlyContinue
}
if ($null -eq $git) {
    $gitVersion = 'unavailable'
    $commands.Add(
        'PowerShell release-source trailing-whitespace scan (git diff --check unavailable)')
    $sourceHygieneEvidence =
        'Git unavailable; release-source trailing-whitespace scan PASS; git diff --check was not run.'
}
else {
    $harnessGitDir = Join-Path $root 'work\repo.git'
    $gitArguments = if (Test-Path -LiteralPath $harnessGitDir -PathType Container) {
        @("--git-dir=$harnessGitDir", "--work-tree=$root", 'diff', '--check')
    }
    else {
        @('-C', $root, 'diff', '--check')
    }
    $gitDiffCommand = "git $($gitArguments -join ' ')" # auditable git diff --check
    $commands.Add($gitDiffCommand)
    Invoke-NightGateChecked -Executable $git.Source -Arguments $gitArguments
    $gitVersion = (& $git.Source --version | Out-String).Trim()
    $sourceHygieneEvidence =
        'Static release/installer/README safety contracts and ``git diff --check`` PASS.'
}

$dotnet = Resolve-NightGateDotNet
$node = Resolve-NightGateNode
$dotnetVersion = (& $dotnet --version | Out-String).Trim()
$runtimeLines = (& $dotnet --list-runtimes | Out-String).Trim()
$nodeVersion = (& $node --version | Out-String).Trim()
$installerStatus = Get-Content -LiteralPath $installerStatusPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
$wixStatusFields = @(
    'productVersion',
    'productCode',
    'upgradeCode',
    'wixSourceArtifact',
    'wixSourceStatus',
    'wixSourceCompiled'
)
foreach ($field in $wixStatusFields) {
    if ($null -eq $installerStatus.PSObject.Properties[$field]) {
        throw "Installer status omitted WiX/MSI identity evidence: $field"
    }
}
if ($installerStatus.wixSourceArtifact -ne 'installer/NightGate.wxs' -or
    $installerStatus.wixSourceStatus -ne 'authored-only' -or
    [bool]$installerStatus.wixSourceCompiled) {
    throw 'Installer status must describe the rendered WiX source as authored-only and not compiled.'
}
$wixSourcePath = Join-Path $extractRoot ([string]$installerStatus.wixSourceArtifact)
$wixIdentity = Get-NightGateWixSourceIdentity -Path $wixSourcePath
$releaseIdentity = Get-NightGateMsiIdentity `
    -ProductVersion ([string]$installerStatus.productVersion)
foreach ($field in @('ProductVersion', 'ProductCode', 'UpgradeCode')) {
    if ([string]$wixIdentity.$field -ne [string]$releaseIdentity.$field -or
        [string]$installerStatus.$field -ne [string]$releaseIdentity.$field) {
        throw "Packaged WiX source and installer status disagree on $field."
    }
}
$targetSidContractProperty = $installerStatus.PSObject.Properties[
    'targetInteractiveSidContractImplemented']
if ($null -eq $targetSidContractProperty) {
    throw 'Installer status omitted the target interactive SID contract evidence.'
}
if ($installerStatus.available) {
    if (-not [bool]$targetSidContractProperty.Value) {
        throw 'MSI availability requires the target-machine UserSID contract.'
    }
    $msiArtifactPath = Join-Path $root ([string]$installerStatus.artifact)
    if (-not (Test-Path -LiteralPath $msiArtifactPath -PathType Leaf) -or
        (Get-Item -LiteralPath $msiArtifactPath).Length -eq 0 -or
        (Get-NightGateSha256 -Path $msiArtifactPath) -ne $installerStatus.sha256) {
        throw 'Installer status claims an MSI that is missing, empty, or has the wrong hash.'
    }
    if ($installerStatus.targetIdentityProperty -ne 'UserSID' -or
        $installerStatus.authoringEngine -ne 'WindowsInstaller.Installer') {
        throw 'MSI status does not identify its Windows Installer UserSID authoring contract.'
    }
    if ($installerStatus.PSObject.Properties['productVersion'] -eq $null -or
    $installerStatus.productVersion -ne '0.3.17') {
    throw 'Installer status must identify the 0.3.17 release.'
    }
    $msiChecksumLine = (
        Get-Content -LiteralPath $msiChecksumPath -Raw -Encoding UTF8).Trim()
    if ($msiChecksumLine -ne "$($installerStatus.sha256) *NightGate-x64.msi") {
        throw 'The MSI checksum file does not match the release artifact.'
    }

    $commands.Add('WindowsInstaller.Installer read-only MSI structure validation')
    $msiInstaller = New-Object -ComObject WindowsInstaller.Installer
    $msiDatabase = $msiInstaller.OpenDatabase($msiArtifactPath, 0)
    try {
        function Get-NightGateMsiScalar {
            param([Parameter(Mandatory)] [string] $Sql)
            $view = $msiDatabase.OpenView($Sql)
            try {
                $null = $view.Execute()
                $record = $view.Fetch()
                if ($null -eq $record) { return $null }
                try { return $record.StringData(1) }
                finally {
                    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) |
                        Out-Null
                }
            }
            finally {
                $null = $view.Close()
                [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) |
                    Out-Null
            }
        }

        function Get-NightGateMsiStreamSize {
            param([Parameter(Mandatory)] [string] $Sql)
            $view = $msiDatabase.OpenView($Sql)
            try {
                $null = $view.Execute()
                $record = $view.Fetch()
                if ($null -eq $record) { return 0 }
                try { return [int]$record.DataSize(1) }
                finally {
                    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) |
                        Out-Null
                }
            }
            finally {
                $null = $view.Close()
                [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) |
                    Out-Null
            }
        }

        $serviceName = Get-NightGateMsiScalar `
            "SELECT ``Name`` FROM ``ServiceInstall`` WHERE ``ServiceInstall``='NightGateServiceInstall'"
        $serviceErrorControl = Get-NightGateMsiScalar `
            "SELECT ``ErrorControl`` FROM ``ServiceInstall`` WHERE ``ServiceInstall``='NightGateServiceInstall'"
        $prepareInstallCommand = Get-NightGateMsiScalar `
            "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='PrepareInstallNightGate'"
        $prepareUninstallCommand = Get-NightGateMsiScalar `
            "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='PrepareUninstallNightGate'"
        $sidCommand = Get-NightGateMsiScalar `
            "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='FinalizeNightGate'"
        $uninstallCommand = Get-NightGateMsiScalar `
            "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='UninstallNightGate'"
        $rollbackInstallCommand = Get-NightGateMsiScalar `
            "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='RollbackInstallNightGate'"
        $rollbackUninstallCommand = Get-NightGateMsiScalar `
            "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='RollbackUninstallNightGate'"
        $commitCommand = Get-NightGateMsiScalar `
            "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='CommitNightGate'"
        $windowsCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``LaunchCondition`` WHERE ``Description``='NightGate requires Windows 11 build 22000 or newer.'"
        $rollbackRequiredCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``LaunchCondition`` WHERE ``Description``='NightGate requires Windows Installer rollback to be enabled.'"
        $buildSignature = Get-NightGateMsiScalar `
            "SELECT ``Signature_`` FROM ``AppSearch`` WHERE ``Property``='NIGHTGATEWINDOWSBUILD'"
        $buildLocator = Get-NightGateMsiScalar `
            "SELECT ``Key`` FROM ``RegLocator`` WHERE ``Signature_``='NightGateWindowsBuild'"
        $buildLocatorType = Get-NightGateMsiScalar `
            "SELECT ``Type`` FROM ``RegLocator`` WHERE ``Signature_``='NightGateWindowsBuild'"
        $productTypeSignature = Get-NightGateMsiScalar `
            "SELECT ``Signature_`` FROM ``AppSearch`` WHERE ``Property``='NIGHTGATEPRODUCTTYPE'"
        $productTypeLocator = Get-NightGateMsiScalar `
            "SELECT ``Key`` FROM ``RegLocator`` WHERE ``Signature_``='NightGateProductType'"
        $productTypeLocatorType = Get-NightGateMsiScalar `
            "SELECT ``Type`` FROM ``RegLocator`` WHERE ``Signature_``='NightGateProductType'"
        $executeAppSearch = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='AppSearch'"
        $executeAppSearchCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='AppSearch'"
        $executeLaunchConditions = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='LaunchConditions'"
        $executeLaunchConditionsCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='LaunchConditions'"
        $uiAppSearch = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallUISequence`` WHERE ``Action``='AppSearch'"
        $uiAppSearchCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallUISequence`` WHERE ``Action``='AppSearch'"
        $uiLaunchConditions = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallUISequence`` WHERE ``Action``='LaunchConditions'"
        $uiLaunchConditionsCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallUISequence`` WHERE ``Action``='LaunchConditions'"
        $uiExecuteAction = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallUISequence`` WHERE ``Action``='ExecuteAction'"
        $uiExecuteActionCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallUISequence`` WHERE ``Action``='ExecuteAction'"
        $downgradeCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``LaunchCondition`` WHERE ``Description``='A newer version of NightGate is already installed.'"
        $removeExistingSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RemoveExistingProducts'"
        $stopServicesSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='StopServices'"
        $installExecuteSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='InstallExecute'"
        $serviceControlEvent = Get-NightGateMsiScalar `
            "SELECT ``Event`` FROM ``ServiceControl`` WHERE ``ServiceControl``='NightGateServiceControl'"
        $desktopExeFileVersion = Get-NightGateMsiScalar `
            "SELECT ``Version`` FROM ``File`` WHERE ``FileName``='NightGate.Desktop.exe'"
        $desktopDllFileVersion = Get-NightGateMsiScalar `
            "SELECT ``Version`` FROM ``File`` WHERE ``FileName``='NightGate.Desktop.dll'"
        $serviceExeFileVersion = Get-NightGateMsiScalar `
            "SELECT ``Version`` FROM ``File`` WHERE ``FileName``='NightGate.Service.exe'"
        $nativeHostExeFileVersion = Get-NightGateMsiScalar `
            "SELECT ``Version`` FROM ``File`` WHERE ``FileName``='NightGate.NativeHost.exe'"
        $rollbackSnapshotCleanupFile = Get-NightGateMsiScalar `
            "SELECT ``FileName`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateRollbackSnapshot'"
        $rollbackSnapshotCleanupMode = Get-NightGateMsiScalar `
            "SELECT ``InstallMode`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateRollbackSnapshot'"
        $rollbackSnapshotCleanupComponent = Get-NightGateMsiScalar `
            "SELECT ``Component_`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateRollbackSnapshot'"
        $rollbackSnapshotCleanupDirectory = Get-NightGateMsiScalar `
            "SELECT ``Directory_`` FROM ``Component`` WHERE ``Component``='C_RollbackSnapshotCleanup'"
        $rollbackSnapshotCleanupGuid = Get-NightGateMsiScalar `
            "SELECT ``ComponentId`` FROM ``Component`` WHERE ``Component``='C_RollbackSnapshotCleanup'"
        $rollbackSnapshotCleanupKeyPath = Get-NightGateMsiScalar `
            "SELECT ``KeyPath`` FROM ``Component`` WHERE ``Component``='C_RollbackSnapshotCleanup'"
        $rollbackSnapshotCleanupAttributes = Get-NightGateMsiScalar `
            "SELECT ``Attributes`` FROM ``Component`` WHERE ``Component``='C_RollbackSnapshotCleanup'"
        $programDataComponentAttributes = Get-NightGateMsiScalar `
            "SELECT ``Attributes`` FROM ``Component`` WHERE ``Component``='C_NightGateData'"
        $rollbackSnapshotCleanupParent = Get-NightGateMsiScalar `
            "SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory``='NIGHTGATEINSTALLERSTATE'"
        $legacyMsiStateCleanupFile = Get-NightGateMsiScalar `
            "SELECT ``FileName`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateLegacyMsiState'"
        $legacyMsiStateCleanupMode = Get-NightGateMsiScalar `
            "SELECT ``InstallMode`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateLegacyMsiState'"
        $legacyMsiStateCleanupDirectory = Get-NightGateMsiScalar `
            "SELECT ``DirProperty`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateLegacyMsiState'"
        $prepareUninstallSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='PrepareUninstallNightGate'"
        $prepareUninstallCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='PrepareUninstallNightGate'"
        $rollbackUninstallCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RollbackUninstallNightGate'"
        $uninstallSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='UninstallNightGate'"
        $uninstallCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='UninstallNightGate'"
        $removeFilesSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RemoveFiles'"
        $commitCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='CommitNightGate'"
        $commitSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='CommitNightGate'"
        $secureProperties = Get-NightGateMsiScalar `
            "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='SecureCustomProperties'"
        $upgradeCode = Get-NightGateMsiScalar `
            "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='UpgradeCode'"
        $oldUpgradeCode = Get-NightGateMsiScalar `
            "SELECT ``UpgradeCode`` FROM ``Upgrade`` WHERE ``ActionProperty``='OLDPRODUCTS'"
        $oldUpgradeMaximum = Get-NightGateMsiScalar `
            "SELECT ``VersionMax`` FROM ``Upgrade`` WHERE ``ActionProperty``='OLDPRODUCTS'"
        $oldUpgradeAttributes = Get-NightGateMsiScalar `
            "SELECT ``Attributes`` FROM ``Upgrade`` WHERE ``ActionProperty``='OLDPRODUCTS'"
        $newUpgradeCode = Get-NightGateMsiScalar `
            "SELECT ``UpgradeCode`` FROM ``Upgrade`` WHERE ``ActionProperty``='NEWERPRODUCTFOUND'"
        $newUpgradeMinimum = Get-NightGateMsiScalar `
            "SELECT ``VersionMin`` FROM ``Upgrade`` WHERE ``ActionProperty``='NEWERPRODUCTFOUND'"
        $newUpgradeAttributes = Get-NightGateMsiScalar `
            "SELECT ``Attributes`` FROM ``Upgrade`` WHERE ``ActionProperty``='NEWERPRODUCTFOUND'"
        $prepareInstallActionType = Get-NightGateMsiScalar `
            "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='PrepareInstallNightGate'" # PrepareInstallNightGate 3106
        $prepareUninstallActionType = Get-NightGateMsiScalar `
            "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='PrepareUninstallNightGate'" # PrepareUninstallNightGate 3106
        $finalizeActionType = Get-NightGateMsiScalar `
            "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='FinalizeNightGate'" # FinalizeNightGate 3106
        $uninstallActionType = Get-NightGateMsiScalar `
            "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='UninstallNightGate'" # UninstallNightGate 3106
        $rollbackInstallActionType = Get-NightGateMsiScalar `
            "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='RollbackInstallNightGate'" # RollbackInstallNightGate 3362
        $rollbackUninstallActionType = Get-NightGateMsiScalar `
            "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='RollbackUninstallNightGate'" # RollbackUninstallNightGate 3362
        $commitActionType = Get-NightGateMsiScalar `
            "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='CommitNightGate'" # CommitNightGate 3618
        $rollbackInstallSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RollbackInstallNightGate'"
        $rollbackInstallCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RollbackInstallNightGate'"
        $rollbackUninstallSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RollbackUninstallNightGate'"
        $prepareInstallSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='PrepareInstallNightGate'"
        $prepareInstallCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='PrepareInstallNightGate'"
        $finalizeSequence = Get-NightGateMsiScalar `
            "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='FinalizeNightGate'"
        $finalizeCondition = Get-NightGateMsiScalar `
            "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='FinalizeNightGate'"
        $productCode = Get-NightGateMsiScalar `
            "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductCode'"
        $productVersion = Get-NightGateMsiScalar `
            "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"
        $productName = Get-NightGateMsiScalar `
            "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductName'"
        $arpProductIcon = Get-NightGateMsiScalar `
            "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ARPPRODUCTICON'"
        $desktopShortcutDefault = Get-NightGateMsiScalar `
            "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='INSTALLDESKTOPSHORTCUT'"
        $arpSystemComponent = Get-NightGateMsiScalar `
            "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ARPSYSTEMCOMPONENT'"
        $shellExpectations = @(
            ,@("SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory``='ProgramMenuFolder'", 'TARGETDIR', 'ProgramMenuFolder parent')
            ,@("SELECT ``DefaultDir`` FROM ``Directory`` WHERE ``Directory``='ProgramMenuFolder'", '.', 'ProgramMenuFolder default')
            ,@("SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory``='DesktopFolder'", 'TARGETDIR', 'DesktopFolder parent')
            ,@("SELECT ``DefaultDir`` FROM ``Directory`` WHERE ``Directory``='DesktopFolder'", '.', 'DesktopFolder default')
            ,@("SELECT ``ComponentId`` FROM ``Component`` WHERE ``Component``='C_NightGateStartMenuShortcut'", '{1CAAAF77-83FA-4356-A9DC-F11A68457702}', 'Start menu component GUID')
            ,@("SELECT ``Directory_`` FROM ``Component`` WHERE ``Component``='C_NightGateStartMenuShortcut'", 'ProgramMenuFolder', 'Start menu component directory')
            ,@("SELECT ``Attributes`` FROM ``Component`` WHERE ``Component``='C_NightGateStartMenuShortcut'", '260', 'Start menu component attributes')
            ,@("SELECT ``KeyPath`` FROM ``Component`` WHERE ``Component``='C_NightGateStartMenuShortcut'", 'R_NightGateStartMenuShortcut', 'Start menu component key path')
            ,@("SELECT ``ComponentId`` FROM ``Component`` WHERE ``Component``='C_NightGateDesktopShortcut'", '{10D5411C-2D2D-A459-A7E7-3F0C48333AED}', 'Desktop component GUID')
            ,@("SELECT ``Directory_`` FROM ``Component`` WHERE ``Component``='C_NightGateDesktopShortcut'", 'DesktopFolder', 'Desktop component directory')
            ,@("SELECT ``Attributes`` FROM ``Component`` WHERE ``Component``='C_NightGateDesktopShortcut'", '260', 'Desktop component attributes')
            ,@("SELECT ``Condition`` FROM ``Component`` WHERE ``Component``='C_NightGateDesktopShortcut'", 'INSTALLDESKTOPSHORTCUT=1', 'Desktop opt-out condition')
            ,@("SELECT ``KeyPath`` FROM ``Component`` WHERE ``Component``='C_NightGateDesktopShortcut'", 'R_NightGateDesktopShortcut', 'Desktop component key path')
            ,@("SELECT ``Root`` FROM ``Registry`` WHERE ``Registry``='R_NightGateStartMenuShortcut'", '2', 'Start menu marker root')
            ,@("SELECT ``Key`` FROM ``Registry`` WHERE ``Registry``='R_NightGateStartMenuShortcut'", 'Software\NightGate\Installer', 'Start menu marker key')
            ,@("SELECT ``Name`` FROM ``Registry`` WHERE ``Registry``='R_NightGateStartMenuShortcut'", 'StartMenuShortcut', 'Start menu marker name')
            ,@("SELECT ``Value`` FROM ``Registry`` WHERE ``Registry``='R_NightGateStartMenuShortcut'", '#1', 'Start menu marker value')
            ,@("SELECT ``Component_`` FROM ``Registry`` WHERE ``Registry``='R_NightGateStartMenuShortcut'", 'C_NightGateStartMenuShortcut', 'Start menu marker component')
            ,@("SELECT ``Root`` FROM ``Registry`` WHERE ``Registry``='R_NightGateDesktopShortcut'", '2', 'Desktop marker root')
            ,@("SELECT ``Key`` FROM ``Registry`` WHERE ``Registry``='R_NightGateDesktopShortcut'", 'Software\NightGate\Installer', 'Desktop marker key')
            ,@("SELECT ``Name`` FROM ``Registry`` WHERE ``Registry``='R_NightGateDesktopShortcut'", 'DesktopShortcut', 'Desktop marker name')
            ,@("SELECT ``Value`` FROM ``Registry`` WHERE ``Registry``='R_NightGateDesktopShortcut'", '#1', 'Desktop marker value')
            ,@("SELECT ``Component_`` FROM ``Registry`` WHERE ``Registry``='R_NightGateDesktopShortcut'", 'C_NightGateDesktopShortcut', 'Desktop marker component')
            ,@("SELECT ``Feature_`` FROM ``FeatureComponents`` WHERE ``Component_``='C_NightGateStartMenuShortcut'", 'Complete', 'Start menu feature')
            ,@("SELECT ``Feature_`` FROM ``FeatureComponents`` WHERE ``Component_``='C_NightGateDesktopShortcut'", 'Complete', 'Desktop feature')
            ,@("SELECT ``Directory_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'", 'ProgramMenuFolder', 'Start menu shortcut directory')
            ,@("SELECT ``Name`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'", $productDisplayName, 'Start menu shortcut name')
            ,@("SELECT ``Component_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'", 'C_NightGateStartMenuShortcut', 'Start menu shortcut component')
            ,@("SELECT ``Target`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'", '[INSTALLFOLDER]apps\Desktop\NightGate.Desktop.exe', 'Start menu shortcut target')
            ,@("SELECT ``Icon_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'", 'NightGateProductIcon', 'Start menu shortcut icon')
            ,@("SELECT ``WkDir`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'", 'INSTALLFOLDER', 'Start menu shortcut working directory')
            ,@("SELECT ``Directory_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'", 'DesktopFolder', 'Desktop shortcut directory')
            ,@("SELECT ``Name`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'", $productShortName, 'Desktop shortcut name')
            ,@("SELECT ``Component_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'", 'C_NightGateDesktopShortcut', 'Desktop shortcut component')
            ,@("SELECT ``Target`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'", '[INSTALLFOLDER]apps\Desktop\NightGate.Desktop.exe', 'Desktop shortcut target')
            ,@("SELECT ``Icon_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'", 'NightGateProductIcon', 'Desktop shortcut icon')
            ,@("SELECT ``WkDir`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'", 'INSTALLFOLDER', 'Desktop shortcut working directory')
            ,@("SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RemoveRegistryValues'", '2600', 'RemoveRegistryValues sequence')
            ,@("SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RemoveShortcuts'", '3200', 'RemoveShortcuts sequence')
            ,@("SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='CreateShortcuts'", '4500', 'CreateShortcuts sequence')
            ,@("SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='WriteRegistryValues'", '5000', 'WriteRegistryValues sequence')
        )
        foreach ($expectation in $shellExpectations) {
            $actual = Get-NightGateMsiScalar $expectation[0]
            if ($actual -ne $expectation[1]) {
                throw "MSI shell validation failed for $($expectation[2]): $actual"
            }
        }
        $iconDataSize = Get-NightGateMsiStreamSize `
            "SELECT ``Data`` FROM ``Icon`` WHERE ``Name``='NightGateProductIcon'"
        $cabinet = Get-NightGateMsiScalar `
            'SELECT `Cabinet` FROM `Media` WHERE `DiskId`=1'
        $summary = $msiDatabase.SummaryInformation(0)
        try {
            $summarySubject = [string]$summary.Property(3)
            $summaryTemplate = [string]$summary.Property(7)
            $packageCode = [string]$summary.Property(9)
        }
        finally {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) |
                Out-Null
        }
        $transactionCommands = [ordered]@{
            PrepareInstallNightGate = $prepareInstallCommand
            PrepareUninstallNightGate = $prepareUninstallCommand
            RollbackInstallNightGate = $rollbackInstallCommand
            RollbackUninstallNightGate = $rollbackUninstallCommand
            FinalizeNightGate = $sidCommand
            UninstallNightGate = $uninstallCommand
            CommitNightGate = $commitCommand
        }
        $expectedModes = @{
            PrepareInstallNightGate = 'Prepare'
            PrepareUninstallNightGate = 'Prepare'
            RollbackInstallNightGate = 'Rollback'
            RollbackUninstallNightGate = 'Rollback'
            FinalizeNightGate = 'Install'
            UninstallNightGate = 'Uninstall'
            CommitNightGate = 'Commit'
        }
        $requiredPowerShellFlags = [regex]::Escape(
            '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass')
        foreach ($entry in $transactionCommands.GetEnumerator()) {
            $target = [string]$entry.Value
            if ([string]::IsNullOrWhiteSpace($target) -or
                $target -notmatch '^"\[PowerShellV1Folder\]powershell\.exe"\s+' -or
                $target -notmatch $requiredPowerShellFlags -or
                $target -notmatch '-File\s+"\[#[^\]]+\]"' -or
                $target -notmatch '-InstallPath\s+"\[INSTALLFOLDER\]\\"' -or
                $target -notmatch '-DataPath\s+"\[NIGHTGATEDATA\]\\"' -or
                $target -notmatch "-Mode\s+$($expectedModes[$entry.Key])(?:\s|$)" -or
                $target -match '\[CustomActionData\]') {
                throw "MSI action $($entry.Key) has an unsafe PowerShell target: $target"
            }
        }
        foreach ($legacySetter in @(
            'SetPrepareInstallNightGateData',
            'SetPrepareUninstallNightGateData',
            'SetRollbackInstallNightGateData',
            'SetRollbackUninstallNightGateData',
            'SetFinalizeNightGateData',
            'SetUninstallNightGateData',
            'SetCommitNightGateData')) {
            $legacySetterTarget = Get-NightGateMsiScalar `
                "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='$legacySetter'"
            if ($null -ne $legacySetterTarget) {
                throw "MSI retains obsolete CustomActionData bridge: $legacySetter"
            }
        }
        $formatSession = $null
        $formatRecord = $null
        try {
            $formatSession = $msiInstaller.OpenPackage($msiArtifactPath, 0)
            $null = $formatSession.DoAction('CostInitialize')
            $null = $formatSession.DoAction('FileCost')
            $null = $formatSession.DoAction('CostFinalize')
            $formatRecord = $msiInstaller.CreateRecord(1)
            $formatRecord.StringData(0) = $sidCommand
            $formattedFinalizeCommand = $formatSession.FormatRecord($formatRecord)
            if ($formattedFinalizeCommand -notmatch
                    '^"[A-Z]:\\[^\r\n]*\\powershell\.exe"\s+' -or
                $formattedFinalizeCommand -notmatch $requiredPowerShellFlags -or
                $formattedFinalizeCommand -notmatch
                    '-File\s+"[^"\r\n]*Finalize-NightGateMsi\.ps1"' -or
                $formattedFinalizeCommand -notmatch
                    '-InstallPath\s+"[^"\r\n]*\\\\"' -or
                $formattedFinalizeCommand -notmatch
                    '-DataPath\s+"[^"\r\n]*\\\\"' -or
                $formattedFinalizeCommand -notmatch '-UserSid\s+"S-1-5-[^"]+"' -or
                $formattedFinalizeCommand -notmatch '-Mode\s+Install(?:\s|$)') {
                throw "MSI FinalizeNightGate formats to an incomplete command: $formattedFinalizeCommand"
            }
        }
        finally {
            if ($null -ne $formatRecord) {
                [Runtime.InteropServices.Marshal]::FinalReleaseComObject($formatRecord) |
                    Out-Null
            }
            if ($null -ne $formatSession) {
                [Runtime.InteropServices.Marshal]::FinalReleaseComObject($formatSession) |
                    Out-Null
            }
        }
        if ($serviceName -ne 'NightGate.LocalService' -or
            $productName -ne $productDisplayName -or
            $productVersion -ne $wixIdentity.ProductVersion -or
            $productCode -ne $wixIdentity.ProductCode -or
            $upgradeCode -ne $wixIdentity.UpgradeCode -or
            $arpProductIcon -ne 'NightGateProductIcon' -or
            $desktopShortcutDefault -ne '1' -or
            $null -ne $arpSystemComponent -or
            $iconDataSize -le 0 -or
            $serviceErrorControl -ne '32769' -or
            $sidCommand -notmatch '\[UserSID\]' -or
            $prepareInstallCommand -match '\[UserSID\]|-ExpectedOperation' -or
            $prepareUninstallCommand -match '\[UserSID\]|-ExpectedOperation' -or
            $rollbackInstallCommand -notmatch '-ExpectedOperation\s+Install(?:\s|$)' -or
            $rollbackUninstallCommand -notmatch '-ExpectedOperation\s+Uninstall(?:\s|$)' -or
            $uninstallCommand -match '\[UserSID\]' -or
            $rollbackInstallCommand -match '\[UserSID\]' -or
            $rollbackUninstallCommand -match '\[UserSID\]' -or
            $commitCommand -match '\[UserSID\]' -or
            $windowsCondition -ne
                'Installed OR (NIGHTGATEWINDOWSBUILD >= 22000 AND NIGHTGATEPRODUCTTYPE = "WinNT")' -or
            $rollbackRequiredCondition -ne 'NOT RollbackDisabled' -or
            $buildSignature -ne 'NightGateWindowsBuild' -or
            $buildLocator -ne 'SOFTWARE\Microsoft\Windows NT\CurrentVersion' -or
            $buildLocatorType -ne '18' -or
            $productTypeSignature -ne 'NightGateProductType' -or
            $productTypeLocator -ne
                'SYSTEM\CurrentControlSet\Control\ProductOptions' -or
            $productTypeLocatorType -ne '18' -or
            $executeAppSearch -ne '400' -or
            -not [string]::IsNullOrEmpty($executeAppSearchCondition) -or
            $executeLaunchConditions -ne '500' -or
            -not [string]::IsNullOrEmpty($executeLaunchConditionsCondition) -or
            $uiAppSearch -ne '400' -or
            -not [string]::IsNullOrEmpty($uiAppSearchCondition) -or
            $uiLaunchConditions -ne '500' -or
            -not [string]::IsNullOrEmpty($uiLaunchConditionsCondition) -or
            $uiExecuteAction -ne '1300' -or
            -not [string]::IsNullOrEmpty($uiExecuteActionCondition) -or
            $downgradeCondition -ne 'NOT NEWERPRODUCTFOUND' -or
            $stopServicesSequence -ne '1900' -or
            $installExecuteSequence -ne '6500' -or
            $removeExistingSequence -ne '6550' -or
            $serviceControlEvent -ne '163' -or
    $desktopExeFileVersion -ne '1.3.17.0' -or
    $desktopDllFileVersion -ne '1.3.17.0' -or
    $serviceExeFileVersion -ne '1.3.17.0' -or
    $nativeHostExeFileVersion -ne '1.3.17.0' -or
            $rollbackSnapshotCleanupFile -ne 'rollback-snapshot.json' -or
            $rollbackSnapshotCleanupMode -ne '2' -or
            $rollbackSnapshotCleanupComponent -ne 'C_RollbackSnapshotCleanup' -or
            $rollbackSnapshotCleanupDirectory -ne 'NIGHTGATEINSTALLERSTATE' -or
            $rollbackSnapshotCleanupGuid -ne
                '{B19C091A-1794-EA51-A267-874C0EC6B21E}' -or
            -not [string]::IsNullOrEmpty($rollbackSnapshotCleanupKeyPath) -or
            $rollbackSnapshotCleanupAttributes -ne '256' -or
            $programDataComponentAttributes -ne '272' -or
            $rollbackSnapshotCleanupParent -ne 'NIGHTGATEDATA' -or
            $legacyMsiStateCleanupFile -ne 'msi-install-state.json' -or
            $legacyMsiStateCleanupMode -ne '2' -or
            $legacyMsiStateCleanupDirectory -ne 'NIGHTGATEDATA' -or
            $prepareUninstallActionType -ne '3106' -or
            $prepareUninstallSequence -ne '3370' -or
            $prepareUninstallCondition -ne
                'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE' -or
            $rollbackUninstallCondition -ne
                'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE' -or
            $uninstallActionType -ne '3106' -or
            $uninstallSequence -ne '3400' -or
            $uninstallCondition -ne
                'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE' -or
            $removeFilesSequence -ne '3500' -or
            $commitSequence -ne '6490' -or
            $commitCondition -ne 'NOT REMOVE~="ALL"' -or
            $secureProperties -ne
                'OLDPRODUCTS;NEWERPRODUCTFOUND;NIGHTGATEWINDOWSBUILD;NIGHTGATEPRODUCTTYPE;INSTALLDESKTOPSHORTCUT' -or
            $oldUpgradeCode -ne $upgradeCode -or
            $newUpgradeCode -ne $upgradeCode -or
            $oldUpgradeMaximum -ne $productVersion -or
            $newUpgradeMinimum -ne $productVersion -or
            $oldUpgradeAttributes -ne '1' -or
            $newUpgradeAttributes -ne '258' -or
            $rollbackInstallActionType -ne '3362' -or
            $rollbackUninstallActionType -ne '3362' -or
            $prepareInstallActionType -ne '3106' -or
            $prepareInstallSequence -ne '4002' -or
            $prepareInstallCondition -ne 'NOT REMOVE~="ALL"' -or
            $rollbackInstallCondition -ne 'NOT REMOVE~="ALL"' -or
            $rollbackInstallSequence -ne '4005' -or
            $rollbackUninstallSequence -ne '3380' -or
            $finalizeActionType -ne '3106' -or
            $finalizeSequence -ne '4020' -or
            $finalizeCondition -ne 'NOT REMOVE~="ALL"' -or
            $commitActionType -ne '3618' -or # CommitNightGate 3618
            $summarySubject -ne $productDisplayName -or
            $summaryTemplate -ne 'x64;2052' -or
            $packageCode -eq $productCode -or
            $cabinet -ne '#nightgate.cab') {
            throw 'The MSI service, version/upgrade, SID transaction, rollback cleanup, OS condition, or cabinet is invalid.'
        }
    }
    finally {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($msiDatabase) |
            Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($msiInstaller) |
            Out-Null
    }
}
elseif ($null -ne $installerStatus.artifact) {
    throw 'Installer status is unavailable but still names an MSI artifact.'
}
# installer availability is reported exactly; absence never becomes a fabricated MSI claim.
$installerAvailability = if ($installerStatus.available) {
    "available; read-only structural validation; artifact=$($installerStatus.artifact); sha256=$($installerStatus.sha256)"
}
else {
    "unavailable; $($installerStatus.reason)"
}
$wix = Get-Command 'wix.exe' -ErrorAction SilentlyContinue
if ($null -eq $wix) { $wix = Get-Command 'wix' -ErrorAction SilentlyContinue }
$wixVersion = if ($null -eq $wix) { 'not found' }
else { (& $wix.Source --version | Out-String).Trim() }
$attemptLogPath = Join-Path $publishRoot 'self-contained-attempt.log'
$attemptLog = if (Test-Path -LiteralPath $attemptLogPath) {
    Get-Content -LiteralPath $attemptLogPath -Raw -Encoding UTF8
}
else {
    'No attempt log was produced.'
}
if ((Get-NightGateTestSourceFingerprint) -cne
    $currentTestSourceFingerprint) {
    throw 'The test source tree changed while release verification was in progress.'
}
$overallResult = if ($diagnosticVerification) {
    'DIAGNOSTIC PASS (NOT RELEASE)'
}
else {
    'PASS'
}
$modeStatement = if ($publishMode.mode -eq 'genuine-self-contained') {
    'Genuine self-contained win-x64 publish, validated from the ZIP.'
}
else {
    'Explicit development-only app-relative private runtime; not release eligible.'
}
$zipRelativePath = (
    Get-NightGateRelativePath -BasePath $root -Path $zipPath).Replace('\', '/')
$zipChecksumRelativePath = (
    Get-NightGateRelativePath -BasePath $root -Path $checksumPath).Replace('\', '/')
$msiRelativePath = (
    Get-NightGateRelativePath -BasePath $root -Path $msiPath).Replace('\', '/')
$msiChecksumRelativePath = (
    Get-NightGateRelativePath -BasePath $root -Path $msiChecksumPath).Replace('\', '/')

$reportPath = Join-Path $outputs 'verification-report.md'
$report = @"
# NightGate release verification report

Generated: $([DateTimeOffset]::UtcNow.ToString('O'))

## Result

- Overall: $overallResult
- Distribution mode: ``$($publishMode.mode)``
- Mode statement: $modeStatement
- Fallback reason: $($publishMode.fallbackReason)
- Installer availability: $installerAvailability

## Tool versions

- PowerShell: $($PSVersionTable.PSVersion)
- .NET SDK: $dotnetVersion
- Node: $nodeVersion
- Git: $gitVersion
- WiX: $wixVersion (detection only; the rendered source was not compiled)

``````text
$runtimeLines
``````

## Exact commands

$(($commands | ForEach-Object { '- ``' + $_ + '``' }) -join [Environment]::NewLine)

The self-contained attempt used these exact underlying commands and exit codes:

``````text
$attemptLog
``````

## Test counts

- .NET: $($testSummary.dotnetPassed) passed, $($testSummary.dotnetFailed) failed, $($testSummary.dotnetSkipped) skipped.
- Node: $($testSummary.nodePassed) passed, $($testSummary.nodeFailed) failed, $($testSummary.nodeSkipped) skipped.

## Artifacts and hashes

- ``$zipRelativePath``: ``$secondZipHash`` ($((Get-Item -LiteralPath $zipPath).Length) bytes)
- ``$zipChecksumRelativePath``: ``$(Get-NightGateSha256 -Path $checksumPath)``
- ``$msiRelativePath``: ``$($installerStatus.sha256)`` ($((Get-Item -LiteralPath $msiPath).Length) bytes)
- ``$msiChecksumRelativePath``: ``$(Get-NightGateSha256 -Path $msiChecksumPath)``
- Extracted inventory: $($actualInventory.Count) files; staged/extracted/hash round-trip PASS.
- Repeated deterministic package hash: ``$firstZipHash`` = ``$secondZipHash``.

## Automated evidence

- Offline restore from the project-local feed, all .NET and Node tests, Release x64 build.
- Formal release verification requires a genuine self-contained publish backed by
  exact, SHA-512-pinned, NuGet-signature-verified official runtime packs.
- NativeHost launched while installed-runtime probes and PATH dotnet discovery were
  disabled for every accepted verification mode.
- Fixed-key Chrome extension ID and exact native-host origin; no staged host-path placeholder.
- Non-mutating five-phase demo smoke and staged native-host EOF smoke.
- Safe ZIP extraction, SHA-256 verification, inventory round-trip, deterministic repack.
- $sourceHygieneEvidence
- Identity-rendered WiX audit source is authored-only; not compiled. Its ProductVersion,
  ProductCode, and UpgradeCode match the reopened, read-only validated Windows Installer MSI.
  No install, repair, upgrade, or uninstall was executed here.
- Embedded cabinet, vital LocalService registration, Windows 11 build floor,
  version/upgrade metadata, transaction actions, checksum, and target-machine ``UserSID`` match.

## Real-machine checks not automated here

Run only on a recoverable Windows 11 test account after reviewing installer ``-WhatIf`` output:

The optional ``installer/Test-NightGateMsiLifecycle.ps1 -RunLifecycle`` harness is for a
disposable VM snapshot and is not part of this PASS result.

1. Install: expected result is only NightGate-owned files, service, ACLs, and the selected SID's two registry entries.
2. Service/logon: expected result is LocalService plus one tray agent after the selected user signs in.
3. Existing/new game processes: expected result is no action against the pre-gate instance and a close request only for a post-gate instance.
4. Lock/re-login and sleep/wake: expected result is persistent phase and exception counters without power/network changes.
5. Chrome current/next media, extension disabled, and incognito: expected result is grandfathering, next-item blocking, or an explicit degraded warning.
6. Three exception paths: expected result is the documented cooling-off, duration, and rolling rescue limit.
7. iPhone restrictions: expected result keeps calls, verification codes, transport, payment, and health tools usable.
8. Uninstall ``-WhatIf`` and normal uninstall: expected result removes only recorded NightGate entries and retains ProgramData by default.

No production enforcement process, service, workstation action, network/power change, Chrome registration,
or real task entry was started or modified by this automated verification.
"@
[IO.File]::WriteAllText(
    $reportPath,
    $report.TrimEnd() + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
Write-Host "Verification PASS: $reportPath"
