[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Prepare', 'Install', 'Rollback', 'Uninstall', 'Commit')]
    [string] $Mode,

    [Parameter(Mandatory)]
    [string] $InstallPath,

    [Parameter(Mandatory)]
    [string] $DataPath,

    [AllowEmptyString()]
    [string] $UserSid = '',

    [Parameter(Mandatory)]
    [string] $ProductCode,

    [Parameter(Mandatory)]
    [string] $ProductVersion,

    [ValidateSet('', 'Install', 'Uninstall')]
    [string] $ExpectedOperation = '',

    [switch] $ValidatePayloadOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NightGate.Installation.Common.ps1')

$extensionOrigin = 'chrome-extension://eefgemhlhbdodhlgjmicnoifhclhdgmm/'
$install = [IO.Path]::GetFullPath($InstallPath).TrimEnd('\')
$data = [IO.Path]::GetFullPath($DataPath).TrimEnd('\')
if (-not [IO.Path]::IsPathRooted($install) -or
    -not [IO.Path]::IsPathRooted($data) -or
    (Split-Path -Leaf $install) -ne 'NightGate' -or
    (Split-Path -Leaf $data) -ne 'NightGate') {
    throw 'Windows Installer supplied an unsafe NightGate path.'
}
if (-not ([Guid]::TryParse($ProductCode, [ref]([Guid]::Empty))) -or
    $ProductVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Windows Installer supplied invalid product identity data.'
}
if ($ValidatePayloadOnly -and $Mode -ne 'Install') {
    throw 'ValidatePayloadOnly is supported only for install validation.'
}

$machineStateDirectory = Join-Path $data 'installer-state'
$machineStatePath = Join-Path $machineStateDirectory 'install-state.json'
$legacyMachineStatePath = Join-Path $data 'msi-install-state.json'
$rollbackSnapshotPath = Join-Path $machineStateDirectory 'rollback-snapshot.json'
$runSubKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$nativeSubKey = 'Software\Google\Chrome\NativeMessagingHosts\com.nightgate.host'
$desktopExe = Join-Path $install 'apps\Desktop\NightGate.Desktop.exe'
$nativeHostExe = Join-Path $install 'apps\NativeHost\NightGate.NativeHost.exe'
$serviceConfig = Join-Path $install 'apps\Service\appsettings.json'
$nativeManifestPath = Join-Path $install 'native-host\com.nightgate.host.json'
$desktopCommand = "`"$desktopExe`" --background"

function Set-NightGateMachineStateAcl {
    param([Parameter(Mandatory)] [string] $Path, [switch] $File)

    $acl = if ($File) {
        [Security.AccessControl.FileSecurity]::new()
    }
    else {
        [Security.AccessControl.DirectorySecurity]::new()
    }
    $acl.SetAccessRuleProtection($true, $false)
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $inheritance = if ($File) {
        [Security.AccessControl.InheritanceFlags]::None
    }
    else {
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    }
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $rule = New-NightGateSidFileSystemAccessRule `
            -SidValue $sid `
            -Rights FullControl `
            -InheritanceFlags $inheritance `
            -PropagationFlags ([Security.AccessControl.PropagationFlags]::None) `
            -AccessControlType $allow
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Initialize-NightGateMachineStateDirectory {
    New-Item -ItemType Directory -Path $machineStateDirectory -Force | Out-Null
    Set-NightGateMachineStateAcl -Path $machineStateDirectory
}

function Write-NightGateAtomicJson {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] $Value
    )

    Initialize-NightGateMachineStateDirectory
    $temporaryPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText(
            $temporaryPath,
            ($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
        Set-NightGateMachineStateAcl -Path $temporaryPath -File
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
        Set-NightGateMachineStateAcl -Path $Path -File
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

function Read-NightGateJsonFile {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 |
            ConvertFrom-Json
    }
    catch {
        throw "NightGate installer state is invalid: $Path"
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
        throw "The installed target user's registry hive is not loaded: $TargetSid"
    }
    return $key
}

function Get-NightGateRegistryValueSnapshot {
    param(
        [Parameter(Mandatory)] [string] $TargetSid,
        [Parameter(Mandatory)] [string] $SubKey,
        [Parameter(Mandatory)]
        [Microsoft.Win32.RegistryView] $RegistryView,
        [AllowEmptyString()] [string] $Name
    )

    $key = Open-NightGateTargetUserKey -TargetSid $TargetSid -SubKey $SubKey `
        -RegistryView $RegistryView
    if ($null -eq $key) {
        return [ordered]@{
            subKey = $SubKey
            name = $Name
            registryView = $RegistryView.ToString()
            wasPresent = $false
            kind = $null
            encodedValue = $null
        }
    }
    try {
        if ($Name -notin @($key.GetValueNames())) {
            return [ordered]@{
                subKey = $SubKey
                name = $Name
                registryView = $RegistryView.ToString()
                wasPresent = $false
                kind = $null
                encodedValue = $null
            }
        }
        $kind = $key.GetValueKind($Name)
        $value = $key.GetValue(
            $Name,
            $null,
            [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $encoded = switch ($kind) {
            ([Microsoft.Win32.RegistryValueKind]::Binary) {
                [Convert]::ToBase64String([byte[]]$value)
                break
            }
            ([Microsoft.Win32.RegistryValueKind]::None) {
                [Convert]::ToBase64String([byte[]]$value)
                break
            }
            ([Microsoft.Win32.RegistryValueKind]::MultiString) {
                @([string[]]$value)
                break
            }
            default {
                [Convert]::ToString($value, [Globalization.CultureInfo]::InvariantCulture)
            }
        }
        return [ordered]@{
            subKey = $SubKey
            name = $Name
            registryView = $RegistryView.ToString()
            wasPresent = $true
            kind = $kind.ToString()
            encodedValue = $encoded
        }
    }
    finally {
        $key.Dispose()
    }
}

function Restore-NightGateRegistryValueSnapshot {
    param(
        [Parameter(Mandatory)] [string] $TargetSid,
        [Parameter(Mandatory)] $Snapshot
    )

    $subKey = [string]$Snapshot.subKey
    $name = [string]$Snapshot.name
    $registryViewProperty = $Snapshot.PSObject.Properties['registryView']
    $registryView = if ($null -eq $registryViewProperty -or
        [string]::IsNullOrWhiteSpace([string]$registryViewProperty.Value)) {
        # v0.3.6 and earlier ran in 64-bit Windows PowerShell and did not record
        # the implicit view. Preserve that exact legacy meaning on upgrade.
        [Microsoft.Win32.RegistryView]::Registry64
    }
    else {
        $parsedView = [Microsoft.Win32.RegistryView]([Enum]::Parse(
            [Microsoft.Win32.RegistryView],
            [string]$registryViewProperty.Value,
            $false))
        if ($parsedView -notin @(
            [Microsoft.Win32.RegistryView]::Registry32,
            [Microsoft.Win32.RegistryView]::Registry64)) {
            throw 'NightGate installer state contains an unsupported registry view.'
        }
        $parsedView
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

function Set-NightGateTargetUserStringValue {
    param(
        [Parameter(Mandatory)] [string] $TargetSid,
        [Parameter(Mandatory)] [string] $SubKey,
        [Parameter(Mandatory)]
        [Microsoft.Win32.RegistryView] $RegistryView,
        [AllowEmptyString()] [string] $Name,
        [Parameter(Mandatory)] [string] $Value
    )

    $key = Open-NightGateTargetUserKey -TargetSid $TargetSid `
        -SubKey $SubKey -RegistryView $RegistryView -Create
    try {
        $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $key.Dispose()
    }
}

function Set-NightGateMsiAcl {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $DesktopSid,
        [switch] $LocalServiceWritable
    )

    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $rules = @(
        (New-NightGateSidFileSystemAccessRule `
            -SidValue 'S-1-5-18' -Rights FullControl `
            -InheritanceFlags $inheritance -PropagationFlags $propagation `
            -AccessControlType $allow)
        (New-NightGateSidFileSystemAccessRule `
            -SidValue 'S-1-5-32-544' -Rights FullControl `
            -InheritanceFlags $inheritance -PropagationFlags $propagation `
            -AccessControlType $allow)
        (New-NightGateSidFileSystemAccessRule `
            -SidValue 'S-1-5-19' `
            -Rights $(if ($LocalServiceWritable) { 'Modify' } else { 'ReadAndExecute' }) `
            -InheritanceFlags $inheritance -PropagationFlags $propagation `
            -AccessControlType $allow)
        (New-NightGateSidFileSystemAccessRule `
            -SidValue $DesktopSid -Rights ReadAndExecute `
            -InheritanceFlags $inheritance -PropagationFlags $propagation `
            -AccessControlType $allow)
    )
    foreach ($rule in $rules) {
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Get-NightGatePreviousStateSnapshot {
    if (-not (Test-Path -LiteralPath $machineStatePath -PathType Leaf)) {
        return [ordered]@{
            wasPresent = $false
            base64 = $null
            acl = Get-NightGateFileSystemAclSnapshot -Path $machineStatePath -File
        }
    }
    return [ordered]@{
        wasPresent = $true
        base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($machineStatePath))
        acl = Get-NightGateFileSystemAclSnapshot -Path $machineStatePath -File
    }
}

function Restore-NightGatePreviousStateSnapshot {
    param([Parameter(Mandatory)] $Snapshot)

    if (-not [bool]$Snapshot.wasPresent) {
        if (Test-Path -LiteralPath $machineStatePath -PathType Leaf) {
            [IO.File]::Delete($machineStatePath)
        }
        return
    }
    Initialize-NightGateMachineStateDirectory
    [IO.File]::WriteAllBytes(
        $machineStatePath,
        [Convert]::FromBase64String([string]$Snapshot.base64))
    $aclProperty = $Snapshot.PSObject.Properties['acl']
    if ($null -ne $aclProperty -and $null -ne $aclProperty.Value) {
        Restore-NightGateFileSystemAclSnapshot -Path $machineStatePath `
            -Snapshot $aclProperty.Value
    }
    else {
        Set-NightGateMachineStateAcl -Path $machineStatePath -File
    }
}

function New-NightGateAbsentRegistrySnapshot {
    param(
        [Parameter(Mandatory)] [string] $SubKey,
        [Parameter(Mandatory)]
        [Microsoft.Win32.RegistryView] $RegistryView,
        [AllowEmptyString()] [string] $Name
    )
    return [ordered]@{
        subKey = $SubKey
        name = $Name
        registryView = $RegistryView.ToString()
        wasPresent = $false
        kind = $null
        encodedValue = $null
    }
}

function Get-NightGateNativeHostRegistrySnapshots {
    param([Parameter(Mandatory)] [string] $TargetSid)

    return [ordered]@{
        registry32 = Get-NightGateRegistryValueSnapshot -TargetSid $TargetSid `
            -SubKey $nativeSubKey -Name '' `
            -RegistryView ([Microsoft.Win32.RegistryView]::Registry32)
        registry64 = Get-NightGateRegistryValueSnapshot -TargetSid $TargetSid `
            -SubKey $nativeSubKey -Name '' `
            -RegistryView ([Microsoft.Win32.RegistryView]::Registry64)
    }
}

function ConvertTo-NightGateNativeHostRegistrySnapshots {
    param(
        [Parameter(Mandatory)] $Snapshots,
        $LegacyRegistry32Snapshot = $null
    )

    $registry32 = $Snapshots.PSObject.Properties['registry32']
    $registry64 = $Snapshots.PSObject.Properties['registry64']
    if ($null -ne $registry32 -and $null -ne $registry32.Value -and
        $null -ne $registry64 -and $null -ne $registry64.Value) {
        return [ordered]@{
            registry32 = $registry32.Value
            registry64 = $registry64.Value
        }
    }

    # v0.3.6 stored only the 64-bit/default-view original. Preserve that value,
    # while taking the 32-bit original from the fresh transaction snapshot made
    # before v0.3.7 writes either view.
    if ($null -ne $Snapshots.PSObject.Properties['subKey']) {
        if ($null -eq $LegacyRegistry32Snapshot) {
            throw 'A legacy native-host snapshot requires the current Registry32 snapshot.'
        }
        return [ordered]@{
            registry32 = $LegacyRegistry32Snapshot
            registry64 = $Snapshots
        }
    }

    throw 'NightGate installer state has an invalid native-host registry snapshot.'
}

function Restore-NightGateNativeHostRegistrySnapshots {
    param(
        [Parameter(Mandatory)] [string] $TargetSid,
        [Parameter(Mandatory)] $Snapshots,
        $LegacyRegistry32Snapshot = $null
    )

    $normalized = ConvertTo-NightGateNativeHostRegistrySnapshots `
        -Snapshots $Snapshots `
        -LegacyRegistry32Snapshot $LegacyRegistry32Snapshot
    Restore-NightGateRegistryValueSnapshot -TargetSid $TargetSid `
        -Snapshot $normalized.registry32
    Restore-NightGateRegistryValueSnapshot -TargetSid $TargetSid `
        -Snapshot $normalized.registry64
}

if ($Mode -eq 'Prepare') {
    # This action runs before Windows Installer schedules the matching rollback
    # action. Removing an abandoned snapshot here prevents a later rollback from
    # replaying state captured by an older transaction of the same operation.
    if (Test-Path -LiteralPath $rollbackSnapshotPath -PathType Leaf) {
        [IO.File]::Delete($rollbackSnapshotPath)
    }
    return
}

if ($Mode -eq 'Commit') {
    if (Test-Path -LiteralPath $rollbackSnapshotPath -PathType Leaf) {
        [IO.File]::Delete($rollbackSnapshotPath)
    }
    return
}

if ($Mode -eq 'Rollback') {
    $rollbackSnapshot = Read-NightGateJsonFile -Path $rollbackSnapshotPath
    if ($null -eq $rollbackSnapshot) {
        return
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedOperation)) {
        throw 'A rollback action must declare its expected NightGate operation.'
    }
    $operationProperty = $rollbackSnapshot.PSObject.Properties['operation']
    if ($null -eq $operationProperty -or
        [string]$operationProperty.Value -ne $ExpectedOperation) {
        # A snapshot from a completed older transaction must never be replayed.
        return
    }
    $rollbackSid = ConvertTo-NightGateCanonicalDesktopSid `
        -SidValue ([string]$rollbackSnapshot.configuredWindowsUserSid)
    Restore-NightGateRegistryValueSnapshot -TargetSid $rollbackSid `
        -Snapshot $rollbackSnapshot.registry.run
    Restore-NightGateNativeHostRegistrySnapshots -TargetSid $rollbackSid `
        -Snapshots $rollbackSnapshot.registry.nativeHost
    Restore-NightGatePreviousStateSnapshot `
        -Snapshot $rollbackSnapshot.previousInstallerState
    [IO.File]::Delete($rollbackSnapshotPath)
    $directoryAclsProperty = $rollbackSnapshot.PSObject.Properties['directoryAcls']
    if ($null -ne $directoryAclsProperty -and
        $null -ne $directoryAclsProperty.Value) {
        $installAclProperty = $directoryAclsProperty.Value.PSObject.Properties['install']
        if ($null -ne $installAclProperty -and $null -ne $installAclProperty.Value) {
            Restore-NightGateFileSystemAclSnapshot -Path $install `
                -Snapshot $directoryAclsProperty.Value.install
        }
        Restore-NightGateFileSystemAclSnapshot -Path $machineStateDirectory `
            -Snapshot $directoryAclsProperty.Value.installerState
        Restore-NightGateFileSystemAclSnapshot -Path $data `
            -Snapshot $directoryAclsProperty.Value.data
    }
    return
}

if ($Mode -eq 'Install') {
    # Windows Installer owns executable delivery. During a major upgrade it may
    # defer replacement of an in-use executable until restart, so the custom
    # action must validate only the configuration file it edits immediately.
    if (-not (Test-Path -LiteralPath $serviceConfig -PathType Leaf)) {
        throw "NightGate MSI payload is incomplete: $serviceConfig"
    }
    if ($ValidatePayloadOnly) {
        return
    }
}

$existingState = Read-NightGateJsonFile -Path $machineStatePath
if ($null -eq $existingState) {
    # v0.1.0 stored the target SID in this legacy machine-level file. Read it
    # during the first transactional upgrade so a different repair account can
    # never replace the original target identity.
    $existingState = Read-NightGateJsonFile -Path $legacyMachineStatePath
}
$canonicalSid = if ($null -ne $existingState) {
    ConvertTo-NightGateCanonicalDesktopSid `
        -SidValue ([string]$existingState.configuredWindowsUserSid)
}
elseif ($Mode -eq 'Install') {
    ConvertTo-NightGateCanonicalDesktopSid -SidValue $UserSid
}
else {
    $null
}

if ($Mode -eq 'Uninstall' -and $null -eq $existingState) {
    return
}

$rollbackSnapshot = [ordered]@{
    schemaVersion = 1
    operation = $Mode
    configuredWindowsUserSid = $canonicalSid
    registry = [ordered]@{
        run = Get-NightGateRegistryValueSnapshot -TargetSid $canonicalSid `
            -SubKey $runSubKey -Name 'NightGate.Desktop' `
            -RegistryView ([Microsoft.Win32.RegistryView]::Registry64)
        nativeHost = Get-NightGateNativeHostRegistrySnapshots `
            -TargetSid $canonicalSid
    }
    previousInstallerState = Get-NightGatePreviousStateSnapshot
    directoryAcls = [ordered]@{
        install = Get-NightGateFileSystemAclSnapshot -Path $install
        data = Get-NightGateFileSystemAclSnapshot -Path $data
        installerState = Get-NightGateFileSystemAclSnapshot `
            -Path $machineStateDirectory
    }
}
Write-NightGateAtomicJson -Path $rollbackSnapshotPath -Value $rollbackSnapshot

if ($Mode -eq 'Uninstall') {
    $originalRegistry = $existingState.PSObject.Properties['originalRegistry']
    if ($null -eq $originalRegistry) {
        Restore-NightGateRegistryValueSnapshot -TargetSid $canonicalSid `
            -Snapshot (New-NightGateAbsentRegistrySnapshot `
                -SubKey $runSubKey -Name 'NightGate.Desktop' `
                -RegistryView ([Microsoft.Win32.RegistryView]::Registry64))
        Restore-NightGateNativeHostRegistrySnapshots -TargetSid $canonicalSid `
            -Snapshots ([ordered]@{
                registry32 = New-NightGateAbsentRegistrySnapshot `
                    -SubKey $nativeSubKey -Name '' `
                    -RegistryView ([Microsoft.Win32.RegistryView]::Registry32)
                registry64 = New-NightGateAbsentRegistrySnapshot `
                    -SubKey $nativeSubKey -Name '' `
                    -RegistryView ([Microsoft.Win32.RegistryView]::Registry64)
            })
    }
    else {
        Restore-NightGateRegistryValueSnapshot -TargetSid $canonicalSid `
            -Snapshot $originalRegistry.Value.run
        Restore-NightGateNativeHostRegistrySnapshots -TargetSid $canonicalSid `
            -Snapshots $originalRegistry.Value.nativeHost `
            -LegacyRegistry32Snapshot `
                $rollbackSnapshot.registry.nativeHost.registry32
    }
    [IO.File]::Delete($machineStatePath)
    return
}

New-Item -ItemType Directory -Path $data -Force | Out-Null
Set-NightGateServiceConfigurationSid `
    -InputPath $serviceConfig `
    -OutputPath $serviceConfig `
    -DesktopSid $canonicalSid
Set-NightGateMsiAcl -Path $install -DesktopSid $canonicalSid
Set-NightGateMsiAcl -Path $data -DesktopSid $canonicalSid -LocalServiceWritable
Initialize-NightGateMachineStateDirectory

$manifest = [ordered]@{
    name = 'com.nightgate.host'
    description = 'NightGate Chrome native bridge'
    path = $nativeHostExe
    type = 'stdio'
    allowed_origins = @($extensionOrigin)
}
New-Item -ItemType Directory -Path (Split-Path -Parent $nativeManifestPath) -Force |
    Out-Null
[IO.File]::WriteAllText(
    $nativeManifestPath,
    ($manifest | ConvertTo-Json -Depth 5) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

Set-NightGateTargetUserStringValue -TargetSid $canonicalSid `
    -SubKey $runSubKey -Name 'NightGate.Desktop' -Value $desktopCommand `
    -RegistryView ([Microsoft.Win32.RegistryView]::Registry64)
Set-NightGateTargetUserStringValue -TargetSid $canonicalSid `
    -SubKey $nativeSubKey -Name '' -Value $nativeManifestPath `
    -RegistryView ([Microsoft.Win32.RegistryView]::Registry32)
Set-NightGateTargetUserStringValue -TargetSid $canonicalSid `
    -SubKey $nativeSubKey -Name '' -Value $nativeManifestPath `
    -RegistryView ([Microsoft.Win32.RegistryView]::Registry64)

$originalRegistry = if ($null -ne $existingState -and
    $null -ne $existingState.PSObject.Properties['originalRegistry']) {
    $existingState.originalRegistry
}
else {
    [ordered]@{
        run = $rollbackSnapshot.registry.run
        nativeHost = $rollbackSnapshot.registry.nativeHost
    }
}
$originalRegistry = [ordered]@{
    run = $originalRegistry.run
    nativeHost = ConvertTo-NightGateNativeHostRegistrySnapshots `
        -Snapshots $originalRegistry.nativeHost `
        -LegacyRegistry32Snapshot `
            $rollbackSnapshot.registry.nativeHost.registry32
}
$state = [ordered]@{
    schemaVersion = 3
    product = 'NightGate'
    installer = 'WindowsInstaller'
    productCode = $ProductCode
    productVersion = $ProductVersion
    configuredWindowsUserSid = $canonicalSid
    installPath = $install
    dataPath = $data
    originalRegistry = $originalRegistry
}
Write-NightGateAtomicJson -Path $machineStatePath -Value $state
