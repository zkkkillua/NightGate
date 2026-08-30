[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $MsiPath,

    [string] $PreviousMsiPath,

    [ValidateSet('0.3.15', '0.3.16')]
    [string] $PreviousProductVersion = '0.3.16',

    [switch] $RunLifecycle,

    [string] $IceValidatorPath,

    [string] $IceCubePath,

    [string] $LogDirectory = (Join-Path $env:TEMP 'NightGate-Msi-Lifecycle')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$productShortName = ([char]0x6536).ToString() + ([char]0x5C3E).ToString()
$productDisplayName = "$productShortName NightGate"

# This harness is deliberately excluded from scripts/Verify.ps1. It mutates the
# machine and is intended only for a disposable Windows 11 VM snapshot.
$msi = [IO.Path]::GetFullPath($MsiPath)
if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) {
    throw "MSI is missing: $msi"
}
$previousMsi = if ([string]::IsNullOrWhiteSpace($PreviousMsiPath)) {
    $null
}
else {
    [IO.Path]::GetFullPath($PreviousMsiPath)
}
if ($RunLifecycle -and
    ($null -eq $previousMsi -or
     -not (Test-Path -LiteralPath $previousMsi -PathType Leaf))) {
    throw "PreviousMsiPath must name the existing $PreviousProductVersion MSI " +
        'for an upgrade lifecycle run.'
}
$previousFileVersion = switch ($PreviousProductVersion) {
    '0.3.15' { '1.3.15.0' }
    '0.3.16' { '1.3.16.0' }
}
$previousExtensionVersion = switch ($PreviousProductVersion) {
    '0.3.15' { '0.1.5' }
    '0.3.16' { '0.1.5' }
}
$currentProductVersion = '0.3.17'

New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
if (-not [string]::IsNullOrWhiteSpace($IceValidatorPath)) {
    if (-not (Test-Path -LiteralPath $IceValidatorPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $IceCubePath -PathType Leaf)) {
        throw 'Both the Windows SDK msival2.exe and an ICE validation cube are required.'
    }
    & $IceValidatorPath $msi $IceCubePath -f |
        Set-Content -LiteralPath (Join-Path $LogDirectory 'ice-validation.txt') `
            -Encoding UTF8
    if ($LASTEXITCODE -ne 0) {
        throw "msival2 ICE validation failed with exit code $LASTEXITCODE."
    }
}

if (-not $RunLifecycle) {
    throw 'Pass -RunLifecycle only inside a disposable Windows 11 VM snapshot.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The VM lifecycle harness must run elevated.'
}
$WindowsBuild = [Environment]::OSVersion.Version.Build
if ($WindowsBuild -lt 22000 -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'The lifecycle harness requires x64 Windows 11 build 22000 or newer.'
}
if ($null -ne (Get-Service -Name 'NightGate.LocalService' -ErrorAction SilentlyContinue)) {
    throw 'The disposable VM must not contain an existing NightGate installation.'
}

function Invoke-NightGateMsiExec {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [string] $Operation
    )

    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $Arguments `
        -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "$Operation failed with Windows Installer exit code $($process.ExitCode)."
    }
}

function Get-NightGateMsiProperty {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)]
        [ValidateSet('ProductCode', 'ProductVersion')]
        [string] $Name
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($Path, 0)
    $view = $database.OpenView(
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$Name'")
    try {
        $null = $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { throw "MSI has no ${Name}: $Path" }
        try { return [string]$record.StringData(1) }
        finally {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) |
                Out-Null
        }
    }
    finally {
        $null = $view.Close()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
    }
}

function Get-NightGateMsiProductCode {
    param([Parameter(Mandatory)] [string] $Path)

    return Get-NightGateMsiProperty -Path $Path -Name ProductCode
}

function Read-NightGateExact {
    param(
        [Parameter(Mandatory)] [IO.Stream] $Stream,
        [Parameter(Mandatory)] [int] $Count
    )

    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($read -le 0) { throw 'NightGate service closed an incomplete frame.' }
        $offset += $read
    }
    return $buffer
}

function Invoke-NightGateNativeHeartbeat {
    param(
        [Parameter(Mandatory)] [Diagnostics.Process] $Process,
        [Parameter(Mandatory)] [string] $ProfileToken,
        [Parameter(Mandatory)] [long] $PolicyRevision,
        [Parameter(Mandatory)] [string] $ExtensionVersion,
        [Parameter(Mandatory)] [bool] $ProtectionReady,
        [Parameter(Mandatory)] [bool] $ExpectedAccepted
    )

    $heartbeatRequestId = [Guid]::NewGuid().ToString('N')
    $heartbeat = [ordered]@{
        version = 1
        type = 'heartbeat'
        requestId = $heartbeatRequestId
        profileToken = $ProfileToken
        payload = [ordered]@{
            revision = $PolicyRevision
            extensionVersion = $ExtensionVersion
            incognitoAllowed = $false
            protectionReady = $ProtectionReady
        }
    } | ConvertTo-Json -Compress
    $heartbeatBytes = [Text.Encoding]::UTF8.GetBytes($heartbeat)
    $heartbeatPrefix = [BitConverter]::GetBytes([int]$heartbeatBytes.Length)
    $Process.StandardInput.BaseStream.Write(
        $heartbeatPrefix, 0, $heartbeatPrefix.Length)
    $Process.StandardInput.BaseStream.Write(
        $heartbeatBytes, 0, $heartbeatBytes.Length)
    $Process.StandardInput.BaseStream.Flush()

    $responsePrefix = Read-NightGateExact `
        -Stream $Process.StandardOutput.BaseStream -Count 4
    $responseLength = [BitConverter]::ToInt32($responsePrefix, 0)
    if ($responseLength -le 0 -or $responseLength -gt 65536) {
        throw "Native host returned an invalid heartbeat frame length: $responseLength"
    }
    $responseBytes = Read-NightGateExact `
        -Stream $Process.StandardOutput.BaseStream -Count $responseLength
    $response = [Text.Encoding]::UTF8.GetString($responseBytes) |
        ConvertFrom-Json
    if ($response.version -ne 1 -or
        $response.type -ne 'heartbeatResult' -or
        $response.requestId -ne $heartbeatRequestId -or
        $response.profileToken -ne $ProfileToken -or
        $response.payload.accepted -ne $ExpectedAccepted) {
        throw "Installed heartbeat for extension $ExtensionVersion returned " +
            "'$($response.payload.accepted)', expected '$ExpectedAccepted'."
    }
}

function Test-NightGateUserStatePipe {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        '.',
        'NightGateService',
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::None)
    try {
        $pipe.Connect(10000)
        $requestId = [Guid]::NewGuid().ToString('N')
        $request = [Text.Encoding]::UTF8.GetBytes(
            "{`"version`":1,`"type`":`"getUserState`",`"requestId`":`"$requestId`",`"payload`":{}}")
        $prefix = [BitConverter]::GetBytes([int]$request.Length)
        $pipe.Write($prefix, 0, $prefix.Length)
        $pipe.Write($request, 0, $request.Length)
        $pipe.Flush()

        $responsePrefix = Read-NightGateExact -Stream $pipe -Count 4
        $responseLength = [BitConverter]::ToInt32($responsePrefix, 0)
        if ($responseLength -le 0 -or $responseLength -gt 1048576) {
            throw "NightGate service returned an invalid frame length: $responseLength"
        }
        $responseBytes = Read-NightGateExact -Stream $pipe -Count $responseLength
        $response = [Text.Encoding]::UTF8.GetString($responseBytes) |
            ConvertFrom-Json
        if ($response.version -ne 1 -or
            $response.type -ne 'getUserStateResult' -or
            $response.requestId -ne $requestId -or
            $response.payload.status -ne 'success') {
            throw 'NightGate getUserState did not return a correlated success result.'
        }
    }
    finally {
        $pipe.Dispose()
    }
}

function Test-NightGateInstalledNativeHostPolicy {
    $nativeHostPath = Join-Path $env:ProgramFiles `
        'NightGate\apps\NativeHost\NightGate.NativeHost.exe'
    if (-not (Test-Path -LiteralPath $nativeHostPath -PathType Leaf)) {
        throw "Installed native host is missing: $nativeHostPath"
    }

    $requestId = [Guid]::NewGuid().ToString('N')
    $profileToken = 'A' * 43
    $request = [ordered]@{
        version = 1
        type = 'getPolicy'
        requestId = $requestId
        profileToken = $profileToken
        payload = [ordered]@{}
    } | ConvertTo-Json -Compress
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $nativeHostPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        Invoke-NightGateNativeHeartbeat `
            -Process $process `
            -ProfileToken $profileToken `
            -PolicyRevision -1 `
            -ExtensionVersion '0.1.3' `
            -ProtectionReady $false `
            -ExpectedAccepted $false
        Invoke-NightGateNativeHeartbeat `
            -Process $process `
            -ProfileToken $profileToken `
            -PolicyRevision -1 `
            -ExtensionVersion '0.1.4' `
            -ProtectionReady $false `
            -ExpectedAccepted $true

        $requestBytes = [Text.Encoding]::UTF8.GetBytes($request)
        $prefix = [BitConverter]::GetBytes([int]$requestBytes.Length)
        $process.StandardInput.BaseStream.Write($prefix, 0, $prefix.Length)
        $process.StandardInput.BaseStream.Write($requestBytes, 0, $requestBytes.Length)
        $process.StandardInput.BaseStream.Flush()

        $responsePrefix = Read-NightGateExact `
            -Stream $process.StandardOutput.BaseStream -Count 4
        $responseLength = [BitConverter]::ToInt32($responsePrefix, 0)
        if ($responseLength -le 0 -or $responseLength -gt 65536) {
            throw "Native host returned an invalid frame length: $responseLength"
        }
        $responseBytes = Read-NightGateExact `
            -Stream $process.StandardOutput.BaseStream -Count $responseLength
        $response = [Text.Encoding]::UTF8.GetString($responseBytes) |
            ConvertFrom-Json
        if ($response.version -ne 1 -or
            $response.type -ne 'getPolicyResult' -or
            $response.requestId -ne $requestId -or
            $response.profileToken -ne $profileToken -or
            $response.payload.revision -lt 0 -or
            [string]::IsNullOrWhiteSpace([string]$response.payload.evaluatedAtUtc) -or
            [string]::IsNullOrWhiteSpace([string]$response.payload.mode)) {
            throw 'Installed Service-to-NativeHost policy protocol is incompatible.'
        }

        Invoke-NightGateNativeHeartbeat `
            -Process $process `
            -ProfileToken $profileToken `
            -PolicyRevision ([int64]$response.payload.revision) `
            -ExtensionVersion '0.1.5' `
            -ProtectionReady $true `
            -ExpectedAccepted $true

        $process.StandardInput.Close()
        if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0) {
            $stderr = $process.StandardError.ReadToEnd()
            throw "Installed native-host smoke failed: $stderr"
        }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

function Assert-NightGateInstalledNativeHostRegistration {
    param([Parameter(Mandatory)] [string] $TargetSid)

    $nativeSubKey =
        'Software\Google\Chrome\NativeMessagingHosts\com.nightgate.host'
    $expectedManifestPath = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles `
        'NightGate\native-host\com.nightgate.host.json'))
    $expectedNativeHostPath = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles `
        'NightGate\apps\NativeHost\NightGate.NativeHost.exe'))
    $expectedOrigin =
        'chrome-extension://eefgemhlhbdodhlgjmicnoifhclhdgmm/'
    if (-not (Test-Path -LiteralPath $expectedManifestPath -PathType Leaf)) {
        throw "Installed native-host manifest is missing: $expectedManifestPath"
    }

    foreach ($view in @(
        [Microsoft.Win32.RegistryView]::Registry32,
        [Microsoft.Win32.RegistryView]::Registry64)) {
        $users = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::Users,
            $view)
        try {
            $key = $users.OpenSubKey("$TargetSid\$nativeSubKey", $false)
            if ($null -eq $key) {
                throw "Target SID $TargetSid has no native-host registration in $view."
            }
            try {
                $registeredManifestPath = [string]$key.GetValue(
                    '',
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
            finally {
                $key.Dispose()
            }
        }
        finally {
            $users.Dispose()
        }
        if ([string]::IsNullOrWhiteSpace($registeredManifestPath) -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath($registeredManifestPath),
                $expectedManifestPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Target SID $TargetSid native-host registration in $view does not point to the installed manifest."
        }
    }

    $manifest = Get-Content -LiteralPath $expectedManifestPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ($manifest.name -ne 'com.nightgate.host' -or
        $manifest.type -ne 'stdio' -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$manifest.path),
            $expectedNativeHostPath,
            [StringComparison]::OrdinalIgnoreCase) -or
        @($manifest.allowed_origins).Count -ne 1 -or
        $manifest.allowed_origins[0] -ne $expectedOrigin) {
        throw 'Installed native-host manifest has incompatible name, path, type, or origin.'
    }
}

function Assert-NightGateShellRegistration {
    param(
        [Parameter(Mandatory)] [string] $ProductCode,
        [Parameter(Mandatory)] [string] $StartMenuShortcut,
        [Parameter(Mandatory)] [string] $DesktopShortcut
    )

    foreach ($shortcut in @($StartMenuShortcut, $DesktopShortcut)) {
        if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) {
            throw "Expected Windows shortcut is missing: $shortcut"
        }
    }
    $arpPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$ProductCode"
    $arp = Get-ItemProperty -LiteralPath $arpPath -ErrorAction Stop
    if ($arp.DisplayName -ne $productDisplayName -or
        [string]::IsNullOrWhiteSpace([string]$arp.DisplayIcon)) {
        throw 'Installed Apps registration is missing the NightGate name or icon.'
    }
}

function Assert-NightGateProtocolPayloadVersions {
    param([Parameter(Mandatory)] [string] $ExpectedFileVersion)

    $installRoot = Join-Path $env:ProgramFiles 'NightGate'
    foreach ($relativePath in @(
        'apps\Desktop\NightGate.Desktop.exe',
        'apps\Desktop\NightGate.Desktop.dll',
        'apps\Service\NightGate.Service.exe',
        'apps\NativeHost\NightGate.NativeHost.exe')) {
        $path = Join-Path $installRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Installed protocol payload is missing: $path"
        }
        $actualFileVersion =
            [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
        if ($actualFileVersion -ne $ExpectedFileVersion) {
            throw "Installed $relativePath FileVersion is '$actualFileVersion', " +
                "expected '$ExpectedFileVersion'."
        }
    }
}

function Assert-NightGateExtensionVersion {
    param([Parameter(Mandatory)] [string] $ExpectedVersion)

    $manifestPath = Join-Path $env:ProgramFiles `
        'NightGate\chrome-extension\manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ($manifest.version -ne $ExpectedVersion) {
        throw "Installed Chrome extension version is '$($manifest.version)', " +
            "expected '$ExpectedVersion'."
    }
}

$currentProductCode = Get-NightGateMsiProductCode -Path $msi
$previousProductCode = Get-NightGateMsiProductCode -Path $previousMsi
$actualCurrentVersion = Get-NightGateMsiProperty -Path $msi -Name ProductVersion
$actualPreviousVersion = Get-NightGateMsiProperty `
    -Path $previousMsi -Name ProductVersion
if ($actualCurrentVersion -ne $currentProductVersion) {
    throw "Current MSI ProductVersion is '$actualCurrentVersion', " +
        "expected '$currentProductVersion'."
}
if ($actualPreviousVersion -ne $PreviousProductVersion) {
    throw "Previous MSI ProductVersion is '$actualPreviousVersion', " +
        "expected '$PreviousProductVersion'."
}
if ($currentProductCode -eq $previousProductCode) {
    throw 'The 0.3.17 MSI must have a new ProductCode for major upgrade coverage.'
}
$startMenuShortcut = Join-Path `
    ([Environment]::GetFolderPath('CommonPrograms')) "$productDisplayName.lnk"
$desktopShortcut = Join-Path `
    ([Environment]::GetFolderPath('CommonDesktopDirectory')) "$productShortName.lnk"
$programDataRoot = Join-Path $env:ProgramData 'NightGate'
$sentinelPath = Join-Path $programDataRoot 'lifecycle-upgrade-sentinel.txt'
$installedProductCode = $null
try {
    Invoke-NightGateMsiExec `
        -Operation "install previous $PreviousProductVersion /i" -Arguments @(
        '/i', "`"$previousMsi`"", '/qn', '/norestart',
        '/l*v', "`"$(Join-Path $LogDirectory "install-$PreviousProductVersion.log")`"")
    $installedProductCode = $previousProductCode
    Assert-NightGateProtocolPayloadVersions `
        -ExpectedFileVersion $previousFileVersion
    Assert-NightGateExtensionVersion -ExpectedVersion $previousExtensionVersion
    New-Item -ItemType Directory -Path $programDataRoot -Force | Out-Null
    Set-Content -LiteralPath $sentinelPath -Value 'preserve-across-upgrade' `
        -Encoding UTF8

    Invoke-NightGateMsiExec `
        -Operation "major upgrade to $currentProductVersion /i" -Arguments @(
        '/i', "`"$msi`"", '/qn', '/norestart',
        '/l*v', "`"$(Join-Path $LogDirectory "upgrade-$currentProductVersion.log")`"")
    $installedProductCode = $currentProductCode
    Assert-NightGateProtocolPayloadVersions -ExpectedFileVersion '1.3.17.0'
    Assert-NightGateExtensionVersion -ExpectedVersion '0.1.5'
    $service = Get-CimInstance Win32_Service -Filter `
        "Name='NightGate.LocalService'"
    if ($null -eq $service -or $service.StartName -ne 'NT AUTHORITY\LocalService') {
        throw 'Installed service identity is not LocalService.'
    }

    $statePath = Join-Path $env:ProgramData `
        'NightGate\installer-state\install-state.json'
    $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($state.configuredWindowsUserSid)) {
        throw 'Machine installer state did not retain the installation-time SID.'
    }
    if ($state.configuredWindowsUserSid -ne $identity.User.Value) {
        throw 'Machine installer state is not bound to the invoking desktop SID.'
    }
    Assert-NightGateInstalledNativeHostRegistration `
        -TargetSid $state.configuredWindowsUserSid
    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw 'The 0.3.17 upgrade removed the ProgramData preservation sentinel.'
    }
    $oldArpPath =
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$previousProductCode"
    if (Test-Path -LiteralPath $oldArpPath) {
        throw 'The 0.3.17 major upgrade left the previous product registered.'
    }
    Assert-NightGateShellRegistration -ProductCode $currentProductCode `
        -StartMenuShortcut $startMenuShortcut -DesktopShortcut $desktopShortcut
    Assert-NightGateProtocolPayloadVersions -ExpectedFileVersion '1.3.17.0'
    Assert-NightGateExtensionVersion -ExpectedVersion '0.1.5'
    Test-NightGateUserStatePipe
    Test-NightGateInstalledNativeHostPolicy

    Remove-Item -LiteralPath $startMenuShortcut -Force
    Remove-Item -LiteralPath $desktopShortcut -Force
    Invoke-NightGateMsiExec -Operation 'repair /fa' -Arguments @(
        '/fa', "`"$msi`"", '/qn', '/norestart',
        '/l*v', "`"$(Join-Path $LogDirectory 'repair.log')`"")
    $repairedState = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ($repairedState.configuredWindowsUserSid -ne
        $state.configuredWindowsUserSid) {
        throw 'Repair changed the installation-time target SID.'
    }
    Assert-NightGateInstalledNativeHostRegistration `
        -TargetSid $repairedState.configuredWindowsUserSid
    Assert-NightGateShellRegistration -ProductCode $currentProductCode `
        -StartMenuShortcut $startMenuShortcut -DesktopShortcut $desktopShortcut
    Test-NightGateUserStatePipe
    Test-NightGateInstalledNativeHostPolicy

    Invoke-NightGateMsiExec -Operation 'uninstall /x' -Arguments @(
        '/x', "`"$msi`"", '/qn', '/norestart',
        '/l*v', "`"$(Join-Path $LogDirectory 'uninstall.log')`"")
    $installedProductCode = $null
    if ($null -ne (Get-Service -Name 'NightGate.LocalService' `
        -ErrorAction SilentlyContinue)) {
        throw 'Uninstall left the NightGate service registered.'
    }
    if ((Test-Path -LiteralPath $startMenuShortcut -PathType Leaf) -or
        (Test-Path -LiteralPath $desktopShortcut -PathType Leaf)) {
        throw 'Uninstall left a NightGate shortcut behind.'
    }
    $currentArpPath =
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$currentProductCode"
    if (Test-Path -LiteralPath $currentArpPath) {
        throw 'Uninstall left the NightGate Installed Apps registration behind.'
    }
    if (-not (Test-Path -LiteralPath $programDataRoot -PathType Container) -or
        -not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw 'Uninstall removed preserved NightGate ProgramData.'
    }
}
finally {
    if ($null -ne $installedProductCode) {
        $cleanup = Start-Process -FilePath 'msiexec.exe' -ArgumentList @(
            '/x', "`"$installedProductCode`"", '/qn', '/norestart',
            '/l*v', "`"$(Join-Path $LogDirectory 'cleanup-uninstall.log')`"") `
            -Wait -PassThru -WindowStyle Hidden
        if ($cleanup.ExitCode -notin @(0, 1605, 3010)) {
            Write-Warning "Cleanup uninstall returned $($cleanup.ExitCode). Restore the VM snapshot."
        }
    }
}

Write-Host "MSI install/repair/uninstall lifecycle PASS. Logs: $LogDirectory"
