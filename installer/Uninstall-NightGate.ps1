[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $StatePath = (Join-Path $env:ProgramData 'NightGate\install-state.json'),
    [switch] $RemoveApplicationData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this uninstaller from an elevated PowerShell session.'
    }
}

function Assert-RecordedNightGatePath {
    param([string] $Path)
    $full = [IO.Path]::GetFullPath($Path)
    if ((Split-Path -Leaf $full) -ne 'NightGate' -or $full.Length -lt 8) {
        throw "The installation record contains an unsafe path: $full"
    }
    return $full
}

function Invoke-ServiceControl {
    param([string[]] $Arguments, [int[]] $AllowedExitCodes = @(0))
    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -notin $AllowedExitCodes) {
        throw "Service control failed with exit code ${LASTEXITCODE}."
    }
}

function Open-NightGateTargetUserKey {
    param(
        [Parameter(Mandatory)] [string] $TargetSid,
        [Parameter(Mandatory)] [string] $SubKey,
        [Parameter(Mandatory)]
        [Microsoft.Win32.RegistryView] $RegistryView,
        [switch] $Writable,
        [switch] $Create
    )

    $users = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::Users,
        $RegistryView)
    try {
        $fullSubKey = "$TargetSid\$SubKey"
        $key = if ($Create) {
            $users.CreateSubKey($fullSubKey, $true)
        }
        else {
            $users.OpenSubKey($fullSubKey, [bool]$Writable)
        }
    }
    finally {
        $users.Dispose()
    }
    if ($null -eq $key -and $Create) {
        throw "The recorded user's registry hive is not loaded: $TargetSid"
    }
    return $key
}

function Restore-NightGateRegistryValueSnapshot {
    param(
        [Parameter(Mandatory)] [string] $TargetSid,
        [Parameter(Mandatory)] $Snapshot
    )

    $subKey = [string]$Snapshot.subKey
    $name = [string]$Snapshot.name
    $registryView = [Microsoft.Win32.RegistryView]([Enum]::Parse(
        [Microsoft.Win32.RegistryView], [string]$Snapshot.registryView, $false))
    if ($registryView -notin @(
        [Microsoft.Win32.RegistryView]::Registry32,
        [Microsoft.Win32.RegistryView]::Registry64)) {
        throw 'NightGate installation state contains an unsupported registry view.'
    }
    if (-not [bool]$Snapshot.wasPresent) {
        $key = Open-NightGateTargetUserKey -TargetSid $TargetSid `
            -SubKey $subKey -RegistryView $registryView -Writable
        if ($null -ne $key) {
            try { $key.DeleteValue($name, $false) }
            finally { $key.Dispose() }
        }
        return
    }

    $kind = [Microsoft.Win32.RegistryValueKind]([Enum]::Parse(
        [Microsoft.Win32.RegistryValueKind], [string]$Snapshot.kind, $false))
    $encoded = $Snapshot.encodedValue
    $value = switch ($kind) {
        ([Microsoft.Win32.RegistryValueKind]::Binary) {
            [Convert]::FromBase64String([string]$encoded)
            break
        }
        ([Microsoft.Win32.RegistryValueKind]::None) {
            [Convert]::FromBase64String([string]$encoded)
            break
        }
        ([Microsoft.Win32.RegistryValueKind]::MultiString) {
            [string[]]@($encoded)
            break
        }
        ([Microsoft.Win32.RegistryValueKind]::DWord) {
            [int]::Parse([string]$encoded, [Globalization.CultureInfo]::InvariantCulture)
            break
        }
        ([Microsoft.Win32.RegistryValueKind]::QWord) {
            [long]::Parse([string]$encoded, [Globalization.CultureInfo]::InvariantCulture)
            break
        }
        default { [string]$encoded }
    }
    $key = Open-NightGateTargetUserKey -TargetSid $TargetSid `
        -SubKey $subKey -RegistryView $registryView -Create
    try { $key.SetValue($name, $value, $kind) }
    finally { $key.Dispose() }
}

function Restore-NightGateNativeHostRegistrySnapshots {
    param(
        [Parameter(Mandatory)] [string] $TargetSid,
        [Parameter(Mandatory)] $Snapshots
    )

    $registry32 = $Snapshots.PSObject.Properties['registry32']
    $registry64 = $Snapshots.PSObject.Properties['registry64']
    if ($null -eq $registry32 -or $null -eq $registry32.Value -or
        $null -eq $registry64 -or $null -eq $registry64.Value) {
        throw 'NightGate installation state is missing a native-host registry view.'
    }
    Restore-NightGateRegistryValueSnapshot -TargetSid $TargetSid `
        -Snapshot $registry32.Value
    Restore-NightGateRegistryValueSnapshot -TargetSid $TargetSid `
        -Snapshot $registry64.Value
}

Assert-Elevated
$fullStatePath = [IO.Path]::GetFullPath($StatePath)
if (-not (Test-Path -LiteralPath $fullStatePath -PathType Leaf)) {
    throw 'NightGate installation state is missing; no system entries will be guessed.'
}
$state = Get-Content -LiteralPath $fullStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($state.product -ne 'NightGate' -or $state.schemaVersion -notin @(1, 2)) {
    throw 'NightGate installation state is invalid.'
}
$installPath = Assert-RecordedNightGatePath -Path ([string]$state.installPath)
$dataPath = Assert-RecordedNightGatePath -Path ([string]$state.dataPath)
$nativeSubKey = 'Software\Google\Chrome\NativeMessagingHosts\com.nightgate.host'
$legacyNativeSubKey = "$($state.desktopUserSid)\$nativeSubKey"

if ($state.serviceName -eq 'NightGate.LocalService' -and
    $PSCmdlet.ShouldProcess($state.serviceName, 'Stop and remove the recorded NightGate service')) {
    if ($null -ne (Get-Service -Name $state.serviceName -ErrorAction SilentlyContinue)) {
        Invoke-ServiceControl -Arguments @('stop', $state.serviceName) `
            -AllowedExitCodes @(0, 1062)
        Invoke-ServiceControl -Arguments @('delete', $state.serviceName)
    }
}

foreach ($entry in @($state.registryEntries)) {
    if ([string]$entry.subKey -eq $legacyNativeSubKey) {
        # Schema v1 did not record the original native-host value. Do not touch
        # it: an existing value may have belonged to the user before NightGate.
        continue
    }
    $display = "HKEY_USERS\$($entry.subKey) [$($entry.valueName)]"
    if ($PSCmdlet.ShouldProcess($display, 'Remove the recorded NightGate registry value')) {
        $key = [Microsoft.Win32.Registry]::Users.OpenSubKey(
            [string]$entry.subKey,
            [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree)
        if ($null -ne $key) {
            try {
                $key.DeleteValue([string]$entry.valueName, $false)
            }
            finally {
                $key.Dispose()
            }
        }
        if ([bool]$entry.removeKey) {
            [Microsoft.Win32.Registry]::Users.DeleteSubKey(
                [string]$entry.subKey, $false)
        }
    }
}

$originalRegistry = $state.PSObject.Properties['originalRegistry']
$nativeHostSnapshots = if ($null -ne $originalRegistry -and
    $null -ne $originalRegistry.Value -and
    $null -ne $originalRegistry.Value.PSObject.Properties['nativeHost']) {
    $originalRegistry.Value.nativeHost
}
else {
    $null
}
if ($null -ne $nativeHostSnapshots) {
    if ($PSCmdlet.ShouldProcess(
        "HKEY_USERS\$($state.desktopUserSid) (Registry32 and Registry64)",
        'Restore the original Chrome native-host values')) {
        Restore-NightGateNativeHostRegistrySnapshots `
            -TargetSid ([string]$state.desktopUserSid) `
            -Snapshots $nativeHostSnapshots
    }
}
else {
    # Old state has no dual-view snapshot, so legacy native-host values are not
    # touched rather than guessing they were NightGate-owned.
    Write-Verbose 'Legacy native-host registry state has no original values; do not touch it.'
}

if ($PSCmdlet.ShouldProcess($installPath, 'Remove the NightGate-owned program directory')) {
    if (Test-Path -LiteralPath $installPath) {
        Remove-Item -LiteralPath $installPath -Recurse -Force
    }
}

if ($RemoveApplicationData) {
    if ($PSCmdlet.ShouldProcess(
        $dataPath,
        'Explicit choice: remove NightGate ProgramData, history, and installation state')) {
        if (Test-Path -LiteralPath $dataPath) {
            Remove-Item -LiteralPath $dataPath -Recurse -Force
        }
    }
}
else {
    Write-Host "NightGate data and installation state were retained: $dataPath"
    Write-Host 'Specify -RemoveApplicationData explicitly only when removal is intended.'
}

Write-Host 'NightGate uninstall completed without searching for unrelated legacy tasks.'
