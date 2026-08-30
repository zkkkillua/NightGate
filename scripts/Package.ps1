[CmdletBinding()]
param(
    [switch] $SkipPublish,
    [switch] $ForcePrivateRuntimeFallback,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $ProductVersion = '0.3.17',

    [AllowEmptyString()] [string] $PublishDirectory,
    [AllowEmptyString()] [string] $IsolatedArtifactsDirectory,
    [AllowEmptyString()] [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-NightGateRepoRoot
$releaseIdentity = Get-NightGateMsiIdentity -ProductVersion $ProductVersion
$publishRoot = Resolve-NightGateRepoScopedDirectory `
    -Path $PublishDirectory `
    -DefaultRelativePath 'artifacts\publish'
$isolatedArtifactsRoot = Resolve-NightGateRepoScopedDirectory `
    -Path $IsolatedArtifactsDirectory `
    -DefaultRelativePath 'artifacts\isolated'
$outputs = Resolve-NightGateRepoScopedDirectory `
    -Path $OutputDirectory `
    -DefaultRelativePath 'outputs'
if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'Publish.ps1') `
        -ForcePrivateRuntimeFallback:$ForcePrivateRuntimeFallback `
        -PublishDirectory $publishRoot `
        -IsolatedArtifactsDirectory $isolatedArtifactsRoot
}

$modePath = Join-Path $publishRoot '.publish-mode.json'
if (-not (Test-Path -LiteralPath $modePath -PathType Leaf)) {
    throw 'Publish evidence is missing. Run scripts/Publish.ps1 first.'
}
$mode = Get-Content -LiteralPath $modePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($mode.mode -notin @('genuine-self-contained', 'private-runtime-fallback')) {
    throw "Unrecognized publish mode: $($mode.mode)"
}

New-Item -ItemType Directory -Path $outputs -Force | Out-Null
$stageParent = Join-Path $outputs 'staging'
$stage = Join-Path $stageParent 'NightGate'
Remove-NightGateGeneratedDirectory -Path $stageParent
New-Item -ItemType Directory -Path $stage -Force | Out-Null

if ($mode.mode -eq 'genuine-self-contained') {
    $sourceRoot = Join-Path $publishRoot 'self-contained'
    New-Item -ItemType Directory -Path (Join-Path $stage 'apps') -Force | Out-Null
    foreach ($app in @('Desktop', 'Service', 'NativeHost')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $app) `
            -Destination (Join-Path $stage "apps\$app") -Recurse
    }
}
else {
    $sourceRoot = Join-Path $publishRoot 'private-runtime-fallback'
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'apps') `
        -Destination (Join-Path $stage 'apps') -Recurse
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'runtime') `
        -Destination (Join-Path $stage 'runtime') -Recurse
}

Copy-Item -LiteralPath (Join-Path $root 'src\NightGate.Chrome.Extension') `
    -Destination (Join-Path $stage 'chrome-extension') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'USER-GUIDE.zh-CN.md') -Destination $stage
New-Item -ItemType Directory -Path (Join-Path $stage 'installer') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'installer\Install-NightGate.ps1') `
    -Destination (Join-Path $stage 'installer')
Copy-Item -LiteralPath (Join-Path $root 'installer\Uninstall-NightGate.ps1') `
    -Destination (Join-Path $stage 'installer')
Copy-Item -LiteralPath (Join-Path $root 'installer\NightGate.Installation.Common.ps1') `
    -Destination (Join-Path $stage 'installer')
Copy-Item -LiteralPath (Join-Path $root 'installer\Finalize-NightGateMsi.ps1') `
    -Destination (Join-Path $stage 'installer')
$stagedWixSource = Join-Path $stage 'installer\NightGate.wxs'
New-NightGateRenderedWixSource `
    -TemplatePath (Join-Path $root 'installer\NightGate.wxs') `
    -OutputPath $stagedWixSource `
    -Identity $releaseIdentity

$installedHostPath = 'C:\Program Files\NightGate\apps\NativeHost\NightGate.NativeHost.exe'
$stagedManifest = Join-Path $stage 'native-host\com.nightgate.host.json'
& (Join-Path $PSScriptRoot 'New-NativeHostManifest.ps1') `
    -OutputPath $stagedManifest `
    -HostExecutablePath $installedHostPath
$stagedManifestText = Get-Content -LiteralPath $stagedManifest -Raw -Encoding UTF8
if ($stagedManifestText -match '__NIGHTGATE_NATIVE_HOST_PATH__') {
    throw 'The staged native-host manifest contains an unresolved placeholder.'
}

if ($mode.mode -eq 'private-runtime-fallback') {
    $launchers = [ordered]@{
        'NightGate.Desktop.cmd' = 'apps\Desktop\NightGate.Desktop.exe'
        'NightGate.Service.Console.cmd' = 'apps\Service\NightGate.Service.exe'
        'NightGate.NativeHost.cmd' = 'apps\NativeHost\NightGate.NativeHost.exe'
    }
    foreach ($launcher in $launchers.GetEnumerator()) {
        $content = @(
            '@echo off',
            'setlocal',
            'set "DOTNET_ROOT=%~dp0runtime"',
            ('"%~dp0{0}" %*' -f $launcher.Value),
            'exit /b %ERRORLEVEL%'
        ) -join "`r`n"
        [IO.File]::WriteAllText(
            (Join-Path $stage $launcher.Key),
            $content + "`r`n",
            [Text.ASCIIEncoding]::new())
    }
}

$releaseMode = [ordered]@{
    mode = $mode.mode
    runtimeIdentifier = 'win-x64'
    generatedUtc = '2000-01-01T00:00:00Z'
    sourceEvidence = (
        Get-NightGateRelativePath -BasePath $root -Path $modePath).Replace('\', '/')
    fallbackReason = $mode.fallbackReason
}
[IO.File]::WriteAllText(
    (Join-Path $stage '.release-mode.json'),
    ($releaseMode | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$inventoryPath = Join-Path $stage 'file-inventory.sha256'
$inventoryLines = Get-ChildItem -LiteralPath $stage -File -Recurse |
    Where-Object { $_.FullName -ne $inventoryPath } |
    Sort-Object { Get-NightGateRelativePath -BasePath $stage -Path $_.FullName } |
    ForEach-Object {
        $relative = (Get-NightGateRelativePath -BasePath $stage -Path $_.FullName).Replace('\', '/')
        '{0}  {1,12}  {2}' -f (Get-NightGateSha256 -Path $_.FullName), $_.Length, $relative
    }
[IO.File]::WriteAllLines(
    $inventoryPath,
    $inventoryLines,
    [Text.UTF8Encoding]::new($false))

$zipPath = Join-Path $outputs 'NightGate-win-x64.zip'
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open(
    $zipPath,
    [IO.Compression.ZipArchiveMode]::Create)
try {
    $fixedTimestamp = [DateTimeOffset]::Parse('2000-01-01T00:00:00Z')
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse |
        Sort-Object { Get-NightGateRelativePath -BasePath $stage -Path $_.FullName }) {
        $entryName = (Get-NightGateRelativePath -BasePath $stage -Path $file.FullName).Replace('\', '/')
        $entry = $archive.CreateEntry(
            $entryName,
            [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $fixedTimestamp
        $input = [IO.File]::OpenRead($file.FullName)
        $output = $entry.Open()
        try {
            $input.CopyTo($output)
        }
        finally {
            $output.Dispose()
            $input.Dispose()
        }
    }
}
finally {
    $archive.Dispose()
}

$zipHash = Get-NightGateSha256 -Path $zipPath
$checksumPath = Join-Path $outputs 'NightGate-win-x64.zip.sha256'
[IO.File]::WriteAllText(
    $checksumPath,
    "$zipHash *NightGate-win-x64.zip" + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$wixCommand = Get-Command 'wix.exe' -ErrorAction SilentlyContinue
if ($null -eq $wixCommand) {
    $wixCommand = Get-Command 'wix' -ErrorAction SilentlyContinue
}
$wixToolingDetected = $null -ne $wixCommand
$msiTargetSidContractImplemented = $true
$msiPath = Join-Path $outputs 'NightGate-x64.msi'
if (-not $msiTargetSidContractImplemented) {
    throw 'The mandatory Windows Installer UserSID contract is disabled.'
}
& (Join-Path $PSScriptRoot 'New-NightGateMsi.ps1') `
    -StageDirectory $stage `
    -OutputPath $msiPath `
    -ProductVersion $ProductVersion
if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf) -or
    (Get-Item -LiteralPath $msiPath).Length -eq 0) {
    throw 'Native Windows Installer authoring produced no MSI.'
}
$wixIdentity = Get-NightGateWixSourceIdentity -Path $stagedWixSource
$msiIdentity = Get-NightGateMsiArtifactIdentity -Path $msiPath
if ($wixIdentity.ProductVersion -ne $releaseIdentity.ProductVersion -or
    $wixIdentity.ProductCode -ne $releaseIdentity.ProductCode -or
    $wixIdentity.UpgradeCode -ne $releaseIdentity.UpgradeCode -or
    $msiIdentity.ProductVersion -ne $releaseIdentity.ProductVersion -or
    $msiIdentity.ProductCode -ne $releaseIdentity.ProductCode -or
    $msiIdentity.UpgradeCode -ne $releaseIdentity.UpgradeCode) {
    throw 'Rendered WiX source and actual MSI disagree on release identity.'
}
$msiHash = Get-NightGateSha256 -Path $msiPath
[IO.File]::WriteAllText(
    (Join-Path $outputs 'NightGate-x64.msi.sha256'),
    "$msiHash *NightGate-x64.msi" + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$installerStatus = [ordered]@{
    available = $true
    artifact = (
        Get-NightGateRelativePath -BasePath $root -Path $msiPath).Replace('\', '/')
    sha256 = $msiHash
    productVersion = $msiIdentity.ProductVersion
    productCode = $msiIdentity.ProductCode
    upgradeCode = $msiIdentity.UpgradeCode
    authoringEngine = 'WindowsInstaller.Installer'
    wixToolingDetected = $wixToolingDetected
    wixSourceArtifact = 'installer/NightGate.wxs'
    wixSourceStatus = 'authored-only'
    wixSourceCompiled = $false
    targetInteractiveSidContractImplemented = $true
    targetIdentityProperty = 'UserSID'
    reason = $null
}
[IO.File]::WriteAllText(
    (Join-Path $outputs 'installer-status.json'),
    ($installerStatus | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

Write-Host "Release ZIP: $zipPath"
Write-Host "SHA256: $zipHash"
Write-Host "Publish mode: $($mode.mode)"
