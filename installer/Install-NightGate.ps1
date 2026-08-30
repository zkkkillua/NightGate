[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $SourcePath = (Split-Path -Parent $PSScriptRoot),
    [string] $InstallPath = (Join-Path $env:ProgramFiles 'NightGate'),
    [string] $DataPath = (Join-Path $env:ProgramData 'NightGate'),
    [switch] $StartService
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NightGate.Installation.Common.ps1')

$serviceName = 'NightGate.LocalService'
$extensionOrigin = 'chrome-extension://eefgemhlhbdodhlgjmicnoifhclhdgmm/'

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this installer from an elevated PowerShell session.'
    }
}

function Assert-SafeInstallPath {
    param([string] $Path, [string] $ExpectedLeaf)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Path]::IsPathRooted($full) -or
        (Split-Path -Leaf $full) -ne $ExpectedLeaf -or
        $full.Length -lt 8) {
        throw "Refusing an unsafe installation path: $full"
    }
    return $full
}

function Set-NightGateAcl {
    param([string] $Path, [string] $DesktopSid, [switch] $Writable)

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
            -Rights $(if ($Writable) { 'Modify' } else { 'ReadAndExecute' }) `
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

function Invoke-ServiceControl {
    param([string[]] $Arguments)
    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
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
        throw "The selected user's registry hive is not loaded: $TargetSid"
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

    $absent = [ordered]@{
        subKey = $SubKey
        name = $Name
        registryView = $RegistryView.ToString()
        wasPresent = $false
        kind = $null
        encodedValue = $null
    }
    $key = Open-NightGateTargetUserKey -TargetSid $TargetSid -SubKey $SubKey `
        -RegistryView $RegistryView
    if ($null -eq $key) {
        return $absent
    }
    try {
        if ($Name -notin @($key.GetValueNames())) {
            return $absent
        }
        $kind = $key.GetValueKind($Name)
        $value = $key.GetValue(
            $Name, $null,
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
    $registryView = [Microsoft.Win32.RegistryView]([Enum]::Parse(
        [Microsoft.Win32.RegistryView], [string]$Snapshot.registryView, $false))
    if ($registryView -notin @(
        [Microsoft.Win32.RegistryView]::Registry32,
        [Microsoft.Win32.RegistryView]::Registry64)) {
        throw 'NightGate installer state contains an unsupported registry view.'
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

function Get-NightGateNativeHostRegistrySnapshots {
    param(
        [Parameter(Mandatory)] [string] $TargetSid,
        [Parameter(Mandatory)] [string] $SubKey
    )

    return [ordered]@{
        registry32 = Get-NightGateRegistryValueSnapshot -TargetSid $TargetSid `
            -SubKey $SubKey -Name '' `
            -RegistryView ([Microsoft.Win32.RegistryView]::Registry32)
        registry64 = Get-NightGateRegistryValueSnapshot -TargetSid $TargetSid `
            -SubKey $SubKey -Name '' `
            -RegistryView ([Microsoft.Win32.RegistryView]::Registry64)
    }
}

function Restore-NightGateNativeHostRegistrySnapshots {
    param(
        [Parameter(Mandatory)] [string] $TargetSid,
        [Parameter(Mandatory)] $Snapshots
    )

    foreach ($snapshot in @($Snapshots.registry32, $Snapshots.registry64)) {
        if ($null -eq $snapshot) {
            throw 'NightGate installer state is missing a native-host registry view.'
        }
        Restore-NightGateRegistryValueSnapshot -TargetSid $TargetSid `
            -Snapshot $snapshot
    }
}

Assert-Elevated
$source = [IO.Path]::GetFullPath($SourcePath)
$install = Assert-SafeInstallPath -Path $InstallPath -ExpectedLeaf 'NightGate'
$data = Assert-SafeInstallPath -Path $DataPath -ExpectedLeaf 'NightGate'
$releaseModePath = Join-Path $source '.release-mode.json'
if (-not (Test-Path -LiteralPath $releaseModePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $source 'apps\Service\NightGate.Service.exe'))) {
    throw 'SourcePath is not a complete NightGate release directory.'
}
$desktopSid = Get-NightGateInteractiveDesktopSid
$sourceServiceConfig = Join-Path $source 'apps\Service\appsettings.json'
$statePath = Join-Path $data 'install-state.json'
$existingState = if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
}
else {
    $null
}
if ($null -ne $existingState -and $existingState.product -ne 'NightGate') {
    throw 'The existing installation record is not owned by NightGate.'
}

if ($PSCmdlet.ShouldProcess($install, 'Copy NightGate-owned program files')) {
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $install -Recurse -Force
    }
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    Set-NightGateServiceConfigurationSid `
        -InputPath $sourceServiceConfig `
        -OutputPath (Join-Path $install 'apps\Service\appsettings.json') `
        -DesktopSid $desktopSid
    Set-NightGateAcl -Path $install -DesktopSid $desktopSid
    Set-NightGateAcl -Path $data -DesktopSid $desktopSid -Writable
}

$nativeHostExe = Join-Path $install 'apps\NativeHost\NightGate.NativeHost.exe'
$nativeManifestPath = Join-Path $install 'native-host\com.nightgate.host.json'
if ($PSCmdlet.ShouldProcess($nativeManifestPath, 'Write an absolute native-host manifest')) {
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
}

$runSubKey = "$desktopSid\Software\Microsoft\Windows\CurrentVersion\Run"
$nativeSubKey = 'Software\Google\Chrome\NativeMessagingHosts\com.nightgate.host'
$registryRollback = Get-NightGateNativeHostRegistrySnapshots `
    -TargetSid $desktopSid -SubKey $nativeSubKey
$nativeHostWriteAttempted = $false
try {
    if ($PSCmdlet.ShouldProcess(
        "HKEY_USERS\$desktopSid (Registry32 and Registry64)",
        'Register the selected user logon agent and Chrome host')) {
        $runKey = [Microsoft.Win32.Registry]::Users.CreateSubKey($runSubKey, $true)
        try {
            $desktopExe = Join-Path $install 'apps\Desktop\NightGate.Desktop.exe'
            $runKey.SetValue('NightGate.Desktop', "`"$desktopExe`" --background",
                [Microsoft.Win32.RegistryValueKind]::String)
        }
        finally {
            $runKey.Dispose()
        }
        # If either native-host view fails, the catch block below restores both
        # exact pre-install values, including their original registry kinds.
        $nativeHostWriteAttempted = $true
        Set-NightGateTargetUserStringValue -TargetSid $desktopSid `
            -SubKey $nativeSubKey -Name '' -Value $nativeManifestPath `
            -RegistryView ([Microsoft.Win32.RegistryView]::Registry32)
        Set-NightGateTargetUserStringValue -TargetSid $desktopSid `
            -SubKey $nativeSubKey -Name '' -Value $nativeManifestPath `
            -RegistryView ([Microsoft.Win32.RegistryView]::Registry64)
    }

    $serviceExe = Join-Path $install 'apps\Service\NightGate.Service.exe'
    if ($PSCmdlet.ShouldProcess($serviceName, 'Create or update the LocalService service without starting it by default')) {
        $knownUpgrade = $null -ne $existingState -and
            $existingState.serviceName -eq $serviceName
        $present = $null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)
        if ($present -and -not $knownUpgrade) {
            throw 'A same-named service exists without a NightGate installation record.'
        }
        $binPath = "`"$serviceExe`""
        if ($present) {
            Invoke-ServiceControl -Arguments @(
                'config', $serviceName, "binPath= $binPath", 'start= auto',
                'obj= NT AUTHORITY\LocalService')
        }
        else {
            Invoke-ServiceControl -Arguments @(
                'create', $serviceName, "binPath= $binPath", 'start= auto',
                'obj= NT AUTHORITY\LocalService',
                'DisplayName= NightGate Service')
        }
        Invoke-ServiceControl -Arguments @(
            'description', $serviceName, 'NightGate policy and exception-token service')
        if ($StartService) {
            Invoke-ServiceControl -Arguments @('start', $serviceName)
        }
    }

    $nativeHostOriginal = if ($null -ne $existingState -and
        $existingState.schemaVersion -eq 2 -and
        $null -ne $existingState.PSObject.Properties['originalRegistry'] -and
        $null -ne $existingState.originalRegistry.PSObject.Properties['nativeHost']) {
        $existingState.originalRegistry.nativeHost
    }
    elseif ($null -ne $existingState) {
        # Schema v1 did not capture the original native-host values. Do not
        # invent ownership of those values during an upgrade or later uninstall.
        $null
    }
    else {
        $registryRollback
    }
    $state = [ordered]@{
        schemaVersion = 2
        product = 'NightGate'
        installPath = $install
        dataPath = $data
        desktopUserSid = $desktopSid
        serviceName = $serviceName
        registryEntries = @(
            [ordered]@{ subKey = $runSubKey; valueName = 'NightGate.Desktop'; removeKey = $false }
        )
        originalRegistry = [ordered]@{
            nativeHost = $nativeHostOriginal
        }
        installedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    if ($PSCmdlet.ShouldProcess($statePath, 'Write the ACL-protected NightGate installation record')) {
        [IO.File]::WriteAllText(
            $statePath,
            ($state | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
    }
}
catch {
    if ($nativeHostWriteAttempted) {
        Restore-NightGateNativeHostRegistrySnapshots -TargetSid $desktopSid `
            -Snapshots $registryRollback
    }
    throw
}

Write-Host "NightGate installation actions completed. Desktop user SID: $desktopSid"
if (-not $StartService) {
    Write-Host 'The service is registered but was not started; start it explicitly or reboot.'
}
