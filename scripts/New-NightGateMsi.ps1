[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $StageDirectory,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $ProductVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$stage = [IO.Path]::GetFullPath($StageDirectory).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $stage -PathType Container)) {
    throw "MSI stage directory is missing: $stage"
}
if ((Split-Path -Leaf $output) -ne 'NightGate-x64.msi') {
    throw 'The MSI output must be named NightGate-x64.msi.'
}
$versionParts = @($ProductVersion -split '\.' | ForEach-Object { [int]$_ })
if ($versionParts.Count -ne 3 -or
    $versionParts[0] -gt 255 -or
    $versionParts[1] -gt 255 -or
    $versionParts[2] -gt 65535) {
    throw 'ProductVersion must be a valid three-field Windows Installer version.'
}
$productShortName = ([char]0x6536).ToString() + ([char]0x5C3E).ToString()
$productDisplayName = "$productShortName NightGate"

$requiredFiles = @(
    'apps\Desktop\NightGate.Desktop.exe',
    'apps\Desktop\NightGate.ico',
    'apps\Service\NightGate.Service.exe',
    'apps\Service\appsettings.json',
    'apps\NativeHost\NightGate.NativeHost.exe',
    'installer\Finalize-NightGateMsi.ps1',
    'installer\NightGate.Installation.Common.ps1',
    'native-host\com.nightgate.host.json'
)
foreach ($relative in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $relative) -PathType Leaf)) {
        throw "MSI stage is incomplete: $relative"
    }
}
$stagedServiceConfiguration = Get-Content -LiteralPath `
    (Join-Path $stage 'apps\Service\appsettings.json') -Raw -Encoding UTF8
if ($stagedServiceConfiguration -notmatch '__CONFIGURED_WINDOWS_USER_SID__' -or
    $stagedServiceConfiguration -match 'S-1-5-21-\d') {
    throw 'MSI input must retain the fixed target-install SID placeholder.'
}

$buildRoot = Join-Path (Split-Path -Parent $output) 'msi-build'
Remove-NightGateGeneratedDirectory -Path $buildRoot
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
if (Test-Path -LiteralPath $output -PathType Leaf) {
    Remove-Item -LiteralPath $output -Force
}

function Get-NightGateStableHex {
    param([Parameter(Mandatory)] [string] $Text, [int] $Characters = 30)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $sha = $algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))
    }
    finally {
        $algorithm.Dispose()
    }
    return (($sha | ForEach-Object { $_.ToString('X2') }) -join '').Substring(
        0,
        $Characters)
}

function New-MsiTable {
    param(
        [Parameter(Mandatory)] $Database,
        [Parameter(Mandatory)] [string] $Sql
    )

    $view = $null
    try {
        $view = $Database.OpenView($Sql)
        $null = $view.Execute()
    }
    catch {
        throw "Failed to create MSI table with SQL: $Sql`n$($_.Exception.Message)"
    }
    finally {
        if ($null -ne $view) {
            $null = $view.Close()
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
        }
    }
}

function Add-MsiRow {
    param(
        [Parameter(Mandatory)] $Installer,
        [Parameter(Mandatory)] $Database,
        [Parameter(Mandatory)] [string] $Table,
        [Parameter(Mandatory)] [string[]] $Columns,
        [Parameter(Mandatory)] [AllowNull()] [AllowEmptyCollection()] [object[]] $Values
    )

    if ($null -eq $Values -or $Columns.Count -ne $Values.Count) {
        throw "MSI row field mismatch for $Table."
    }
    $columnSql = ($Columns | ForEach-Object { "``$_``" }) -join ', '
    $placeholderSql = (@('?' ) * $Columns.Count) -join ', '
    $view = $Database.OpenView(
        "INSERT INTO ``$Table`` ($columnSql) VALUES ($placeholderSql)")
    $record = $Installer.CreateRecord($Columns.Count)
    try {
        for ($index = 0; $index -lt $Values.Count; $index++) {
            $field = $index + 1
            $value = $Values[$index]
            if ($null -eq $value) {
                continue
            }
            if ($value -is [byte] -or
                $value -is [int16] -or
                $value -is [int32] -or
                $value -is [int64]) {
                $record.IntegerData($field) = [int]$value
            }
            else {
                $record.StringData($field) = [string]$value
            }
        }
        $null = $view.Execute($record)
    }
    finally {
        $null = $view.Close()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    }
}

function Add-MsiStream {
    param(
        [Parameter(Mandatory)] $Installer,
        [Parameter(Mandatory)] $Database,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Path
    )

    $view = $Database.OpenView(
        'INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)')
    $record = $Installer.CreateRecord(2)
    try {
        $record.StringData(1) = $Name
        $record.SetStream(2, $Path)
        $null = $view.Execute($record)
    }
    finally {
        $null = $view.Close()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    }
}

function Add-MsiIcon {
    param(
        [Parameter(Mandatory)] $Installer,
        [Parameter(Mandatory)] $Database,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Path
    )

    $view = $Database.OpenView(
        'INSERT INTO `Icon` (`Name`, `Data`) VALUES (?, ?)')
    $record = $Installer.CreateRecord(2)
    try {
        $record.StringData(1) = $Name
        $record.SetStream(2, $Path)
        $null = $view.Execute($record)
    }
    finally {
        $null = $view.Close()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    }
}

function Get-MsiScalar {
    param(
        [Parameter(Mandatory)] $Database,
        [Parameter(Mandatory)] [string] $Sql
    )

    $view = $Database.OpenView($Sql)
    try {
        $null = $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            return $null
        }
        try {
            return $record.StringData(1)
        }
        finally {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
        }
    }
    finally {
        $null = $view.Close()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    }
}

function Get-MsiStreamSize {
    param(
        [Parameter(Mandatory)] $Database,
        [Parameter(Mandatory)] [string] $Sql
    )

    $view = $Database.OpenView($Sql)
    try {
        $null = $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            return 0
        }
        try {
            return [int]$record.DataSize(1)
        }
        finally {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
        }
    }
    finally {
        $null = $view.Close()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    }
}

$files = @(Get-ChildItem -LiteralPath $stage -File -Recurse |
    Sort-Object { Get-NightGateRelativePath -BasePath $stage -Path $_.FullName })
if ($files.Count -eq 0) {
    throw 'MSI stage contains no files.'
}

$fileRows = [Collections.Generic.List[object]]::new()
$nightGatePayloadFileVersion = $null
$directoryRows = [ordered]@{ '' = 'INSTALLFOLDER' }
foreach ($file in $files) {
    $relative = (Get-NightGateRelativePath -BasePath $stage -Path $file.FullName)
    $relativeDirectory = Split-Path -Parent $relative
    if ($relativeDirectory -eq '.') {
        $relativeDirectory = ''
    }
    if (-not $directoryRows.Contains($relativeDirectory)) {
        $segments = $relativeDirectory -split '[\\/]'
        $current = ''
        foreach ($segment in $segments) {
            $parent = $current
            $current = if ($current.Length -eq 0) {
                $segment
            }
            else {
                "$current\$segment"
            }
            if (-not $directoryRows.Contains($current)) {
                $directoryRows[$current] = 'D_' + (Get-NightGateStableHex $current 30)
            }
        }
    }

    $portable = $relative.Replace('\', '/').ToLowerInvariant()
    $msiFileVersion = if ($relative -match
        '^apps[\\/][^\\/]+[\\/]NightGate\..+\.(?:exe|dll)$') {
        # The package uses an afterInstallExecute major-upgrade schedule. MSI
        # must store the PE's exact FileVersion, and that version must outrank
        # the legacy payloads that were emitted as 1.0.0.0.
        $actualFileVersion =
            [Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName).FileVersion
        $parsedFileVersion = $null
        if (-not [Version]::TryParse($actualFileVersion, [ref]$parsedFileVersion) -or
            $parsedFileVersion -le [Version]'1.0.0.0') {
            throw "NightGate binary FileVersion must outrank legacy 1.0.0.0: " +
                "$relative is '$actualFileVersion'."
        }
        if ($null -eq $nightGatePayloadFileVersion) {
            $nightGatePayloadFileVersion = $actualFileVersion
        }
        elseif ($actualFileVersion -ne $nightGatePayloadFileVersion) {
            throw "NightGate application binaries have inconsistent FileVersion values: " +
                "$relative is '$actualFileVersion', expected " +
                "'$nightGatePayloadFileVersion'."
        }
        $actualFileVersion
    }
    else {
        $null
    }
    $fileRows.Add([pscustomobject]@{
        Source = $file.FullName
        Relative = $relative
        Directory = $directoryRows[$relativeDirectory]
        FileId = 'F_' + (Get-NightGateStableHex "file/$portable" 30)
        ComponentId = 'C_' + (Get-NightGateStableHex "component/$portable" 30)
        ComponentGuid = Get-NightGateStableGuid "component/$portable"
        Name = $file.Name
        Size = [int64]$file.Length
        Version = $msiFileVersion
    })
}

$cabinetName = 'nightgate.cab'
$cabinetPath = Join-Path $buildRoot $cabinetName
$ddfPath = Join-Path $buildRoot 'nightgate.ddf'
$ddfLines = [Collections.Generic.List[string]]::new()
$ddfLines.Add('.OPTION EXPLICIT')
$ddfLines.Add(".Set CabinetNameTemplate=$cabinetName")
$ddfLines.Add(".Set DiskDirectoryTemplate=$buildRoot")
$ddfLines.Add(".Set InfFileName=$(Join-Path $buildRoot 'setup.inf')")
$ddfLines.Add(".Set RptFileName=$(Join-Path $buildRoot 'setup.rpt')")
$ddfLines.Add('.Set Cabinet=on')
$ddfLines.Add('.Set Compress=on')
$ddfLines.Add('.Set CompressionType=LZX')
$ddfLines.Add('.Set CompressionMemory=21')
$ddfLines.Add('.Set MaxDiskSize=0')
foreach ($row in $fileRows) {
    $source = $row.Source.Replace('"', '""')
    $ddfLines.Add("`"$source`" $($row.FileId)")
}
[IO.File]::WriteAllLines($ddfPath, $ddfLines, [Text.Encoding]::ASCII)
$makecab = Join-Path $env:SystemRoot 'System32\makecab.exe'
$makecabLog = Join-Path $buildRoot 'makecab.log'
& $makecab /F $ddfPath *> $makecabLog
if ($LASTEXITCODE -ne 0 -or
    -not (Test-Path -LiteralPath $cabinetPath -PathType Leaf) -or
    (Get-Item -LiteralPath $cabinetPath).Length -eq 0) {
    Get-Content -LiteralPath $makecabLog -Tail 80 | Out-Host
    throw 'makecab failed to create the embedded NightGate cabinet.'
}

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($output, 3)
try {
    $tableSql = @(
        'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)',
        'CREATE TABLE `AppSearch` (`Property` CHAR(72) NOT NULL, `Signature_` CHAR(72) NOT NULL PRIMARY KEY `Property`, `Signature_`)',
        'CREATE TABLE `Signature` (`Signature` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `MinVersion` CHAR(20), `MaxVersion` CHAR(20), `MinSize` LONG, `MaxSize` LONG, `MinDate` LONG, `MaxDate` LONG, `Languages` CHAR(255) PRIMARY KEY `Signature`)',
        'CREATE TABLE `RegLocator` (`Signature_` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL, `Name` CHAR(255), `Type` SHORT PRIMARY KEY `Signature_`)',
        'CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)',
        'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)',
        'CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)',
        'CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)',
        'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)',
        'CREATE TABLE `Icon` (`Name` CHAR(72) NOT NULL, `Data` OBJECT NOT NULL PRIMARY KEY `Name`)',
        'CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL, `Name` CHAR(255), `Value` CHAR(0) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)',
        'CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(0) NOT NULL LOCALIZABLE, `Arguments` CHAR(255) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72), `DisplayResourceDLL` CHAR(255), `DisplayResourceId` LONG, `DescriptionResourceDLL` CHAR(255), `DescriptionResourceId` LONG PRIMARY KEY `Shortcut`)',
        'CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)',
        'CREATE TABLE `CreateFolder` (`Directory_` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Directory_`, `Component_`)',
        'CREATE TABLE `RemoveFile` (`FileKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) LOCALIZABLE, `DirProperty` CHAR(72) NOT NULL, `InstallMode` SHORT NOT NULL PRIMARY KEY `FileKey`)',
        'CREATE TABLE `ServiceInstall` (`ServiceInstall` CHAR(72) NOT NULL, `Name` CHAR(255), `DisplayName` CHAR(255) LOCALIZABLE, `ServiceType` LONG NOT NULL, `StartType` LONG NOT NULL, `ErrorControl` LONG NOT NULL, `LoadOrderGroup` CHAR(255), `Dependencies` CHAR(255), `StartName` CHAR(255), `Password` CHAR(255), `Arguments` CHAR(255), `Component_` CHAR(72) NOT NULL, `Description` CHAR(255) LOCALIZABLE PRIMARY KEY `ServiceInstall`)',
        'CREATE TABLE `ServiceControl` (`ServiceControl` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL, `Event` SHORT NOT NULL, `Arguments` CHAR(255), `Wait` SHORT, `Component_` CHAR(72) NOT NULL PRIMARY KEY `ServiceControl`)',
        'CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(72), `Target` CHAR(0), `ExtendedType` LONG PRIMARY KEY `Action`)',
        'CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)',
        'CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)',
        'CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`)',
        'CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)'
    )
    foreach ($sql in $tableSql) {
        New-MsiTable -Database $database -Sql $sql
    }

    $releaseIdentity = Get-NightGateMsiIdentity -ProductVersion $ProductVersion
    $productCode = $releaseIdentity.ProductCode
    $upgradeCode = $releaseIdentity.UpgradeCode
    $packageCode = ([Guid]::NewGuid()).ToString('B').ToUpperInvariant()
    $properties = [ordered]@{
        ProductCode = $productCode
        ProductLanguage = '2052'
        ProductName = $productDisplayName
        ProductVersion = $ProductVersion
        Manufacturer = 'NightGate'
        UpgradeCode = $upgradeCode
        ARPPRODUCTICON = 'NightGateProductIcon'
        INSTALLDESKTOPSHORTCUT = '1'
        ALLUSERS = '1'
        INSTALLLEVEL = '1'
        ARPNOREPAIR = '1'
        ARPNOMODIFY = '1'
        MSIFASTINSTALL = '7'
        SecureCustomProperties =
            'OLDPRODUCTS;NEWERPRODUCTFOUND;NIGHTGATEWINDOWSBUILD;NIGHTGATEPRODUCTTYPE;INSTALLDESKTOPSHORTCUT'
    }
    foreach ($entry in $properties.GetEnumerator()) {
        Add-MsiRow $installer $database 'Property' @('Property', 'Value') `
            @($entry.Key, $entry.Value)
    }

    Add-MsiRow $installer $database 'AppSearch' @('Property', 'Signature_') `
        @('NIGHTGATEWINDOWSBUILD', 'NightGateWindowsBuild')
    Add-MsiRow $installer $database 'RegLocator' `
        @('Signature_', 'Root', 'Key', 'Name', 'Type') `
        @('NightGateWindowsBuild', 2,
          'SOFTWARE\Microsoft\Windows NT\CurrentVersion',
          'CurrentBuildNumber', 18)
    Add-MsiRow $installer $database 'AppSearch' @('Property', 'Signature_') `
        @('NIGHTGATEPRODUCTTYPE', 'NightGateProductType')
    Add-MsiRow $installer $database 'RegLocator' `
        @('Signature_', 'Root', 'Key', 'Name', 'Type') `
        @('NightGateProductType', 2,
          'SYSTEM\CurrentControlSet\Control\ProductOptions',
          'ProductType', 18)

    $baseDirectories = @(
        ,@('TARGETDIR', $null, 'SourceDir')
        ,@('ProgramMenuFolder', 'TARGETDIR', '.')
        ,@('DesktopFolder', 'TARGETDIR', '.')
        ,@('ProgramFiles64Folder', 'TARGETDIR', '.')
        ,@('INSTALLFOLDER', 'ProgramFiles64Folder', 'NightGate')
        ,@('CommonAppDataFolder', 'TARGETDIR', '.')
        ,@('NIGHTGATEDATA', 'CommonAppDataFolder', 'NightGate')
        ,@('NIGHTGATEINSTALLERSTATE', 'NIGHTGATEDATA', 'installer-state')
        ,@('WindowsFolder', 'TARGETDIR', '.')
        ,@('System64Folder', 'WindowsFolder', 'System32')
        ,@('WindowsPowerShellFolder', 'System64Folder', 'WindowsPowerShell')
        ,@('PowerShellV1Folder', 'WindowsPowerShellFolder', 'v1.0')
    )
    foreach ($row in $baseDirectories) {
        Add-MsiRow $installer $database 'Directory' `
            @('Directory', 'Directory_Parent', 'DefaultDir') $row
    }
    foreach ($entry in $directoryRows.GetEnumerator()) {
        if ($entry.Key.Length -eq 0) {
            continue
        }
        $parentRelative = Split-Path -Parent $entry.Key
        if ($parentRelative -eq '.') {
            $parentRelative = ''
        }
        Add-MsiRow $installer $database 'Directory' `
            @('Directory', 'Directory_Parent', 'DefaultDir') `
            @($entry.Value, $directoryRows[$parentRelative], (Split-Path -Leaf $entry.Key))
    }

    Add-MsiRow $installer $database 'Feature' `
        @('Feature', 'Feature_Parent', 'Title', 'Description', 'Display', 'Level', 'Directory_', 'Attributes') `
        @('Complete', $null, $productDisplayName, 'Early-sleep commitment device', 1, 1, 'INSTALLFOLDER', 0)

    $sequence = 0
    foreach ($row in $fileRows) {
        $sequence++
        Add-MsiRow $installer $database 'Component' `
            @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') `
            @($row.ComponentId, $row.ComponentGuid, $row.Directory, 256, $null, $row.FileId)
        Add-MsiRow $installer $database 'FeatureComponents' `
            @('Feature_', 'Component_') @('Complete', $row.ComponentId)
        Add-MsiRow $installer $database 'File' `
            @('File', 'Component_', 'FileName', 'FileSize', 'Version', 'Language', 'Attributes', 'Sequence') `
            @($row.FileId, $row.ComponentId, $row.Name, $row.Size,
              $row.Version, $null, 512, $sequence)
    }

    $shortcutColumns = @(
        'Shortcut', 'Directory_', 'Name', 'Component_', 'Target', 'Arguments',
        'Description', 'Hotkey', 'Icon_', 'IconIndex', 'ShowCmd', 'WkDir',
        'DisplayResourceDLL', 'DisplayResourceId', 'DescriptionResourceDLL',
        'DescriptionResourceId')
    $shortcutTarget = '[INSTALLFOLDER]apps\Desktop\NightGate.Desktop.exe'
    $installerRegistryKey = 'Software\NightGate\Installer'

    $startMenuComponent = 'C_NightGateStartMenuShortcut'
    Add-MsiRow $installer $database 'Component' `
        @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') `
        @($startMenuComponent, '{1CAAAF77-83FA-4356-A9DC-F11A68457702}',
          'ProgramMenuFolder', 260, $null, 'R_NightGateStartMenuShortcut')
    Add-MsiRow $installer $database 'FeatureComponents' `
        @('Feature_', 'Component_') @('Complete', $startMenuComponent)
    Add-MsiRow $installer $database 'Registry' `
        @('Registry', 'Root', 'Key', 'Name', 'Value', 'Component_') `
        @('R_NightGateStartMenuShortcut', 2, $installerRegistryKey,
          'StartMenuShortcut', '#1', $startMenuComponent)
    Add-MsiRow -Installer $installer -Database $database -Table 'Shortcut' `
        -Columns $shortcutColumns -Values @(
            'NightGateStartMenuShortcut', 'ProgramMenuFolder', $productDisplayName,
            $startMenuComponent, $shortcutTarget, $null,
            'Open NightGate', $null, 'NightGateProductIcon', 0, 1,
            'INSTALLFOLDER', $null, $null, $null, $null)

    $desktopComponent = 'C_NightGateDesktopShortcut'
    Add-MsiRow $installer $database 'Component' `
        @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') `
        @($desktopComponent, '{10D5411C-2D2D-A459-A7E7-3F0C48333AED}',
          'DesktopFolder', 260, 'INSTALLDESKTOPSHORTCUT=1',
          'R_NightGateDesktopShortcut')
    Add-MsiRow $installer $database 'FeatureComponents' `
        @('Feature_', 'Component_') @('Complete', $desktopComponent)
    Add-MsiRow $installer $database 'Registry' `
        @('Registry', 'Root', 'Key', 'Name', 'Value', 'Component_') `
        @('R_NightGateDesktopShortcut', 2, $installerRegistryKey,
          'DesktopShortcut', '#1', $desktopComponent)
    Add-MsiRow -Installer $installer -Database $database -Table 'Shortcut' `
        -Columns $shortcutColumns -Values @(
            'NightGateDesktopShortcut', 'DesktopFolder', $productShortName,
            $desktopComponent, $shortcutTarget, $null,
            'Open NightGate', $null, 'NightGateProductIcon', 0, 1,
            'INSTALLFOLDER', $null, $null, $null, $null)

    Add-MsiIcon -Installer $installer -Database $database `
        -Name 'NightGateProductIcon' `
        -Path (Join-Path $stage 'apps\Desktop\NightGate.ico')

    $dataComponent = 'C_NightGateData'
    Add-MsiRow $installer $database 'Component' `
        @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') `
        @($dataComponent, (Get-NightGateStableGuid 'component/program-data'), 'NIGHTGATEDATA', 272, $null, $null)
    Add-MsiRow $installer $database 'FeatureComponents' `
        @('Feature_', 'Component_') @('Complete', $dataComponent)
    Add-MsiRow $installer $database 'CreateFolder' `
        @('Directory_', 'Component_') @('NIGHTGATEDATA', $dataComponent)

    # Keep the ProgramData root permanent, but make this child component
    # removable so RemoveFiles can transactionally delete only the transient
    # rollback snapshot after UninstallNightGate has created it.
    $rollbackCleanupComponent = 'C_RollbackSnapshotCleanup'
    Add-MsiRow $installer $database 'Component' `
        @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') `
        @($rollbackCleanupComponent,
          (Get-NightGateStableGuid 'component/rollback-snapshot-cleanup'),
          'NIGHTGATEINSTALLERSTATE', 256, $null, $null)
    Add-MsiRow $installer $database 'FeatureComponents' `
        @('Feature_', 'Component_') @('Complete', $rollbackCleanupComponent)
    Add-MsiRow $installer $database 'CreateFolder' `
        @('Directory_', 'Component_') `
        @('NIGHTGATEINSTALLERSTATE', $rollbackCleanupComponent)
    Add-MsiRow $installer $database 'RemoveFile' `
        @('FileKey', 'Component_', 'FileName', 'DirProperty', 'InstallMode') `
        @('RemoveNightGateRollbackSnapshot', $rollbackCleanupComponent,
          'rollback-snapshot.json', 'NIGHTGATEINSTALLERSTATE', 2)
    Add-MsiRow $installer $database 'RemoveFile' `
        @('FileKey', 'Component_', 'FileName', 'DirProperty', 'InstallMode') `
        @('RemoveNightGateLegacyMsiState', $rollbackCleanupComponent,
          'msi-install-state.json', 'NIGHTGATEDATA', 2)

    $serviceRow = $fileRows | Where-Object {
        $_.Relative -ieq 'apps\Service\NightGate.Service.exe'
    } | Select-Object -First 1
    $serviceInstallColumns = @(
        'ServiceInstall', 'Name', 'DisplayName', 'ServiceType', 'StartType',
        'ErrorControl', 'LoadOrderGroup', 'Dependencies', 'StartName', 'Password',
        'Arguments', 'Component_', 'Description')
    $serviceInstallValues = @(
        'NightGateServiceInstall', 'NightGate.LocalService', 'NightGate Service',
        16, 2, 32769, $null, $null, 'NT AUTHORITY\LocalService', $null, $null,
        $serviceRow.ComponentId, 'Stores local night policy and privacy-safe events.')
    Add-MsiRow -Installer $installer -Database $database `
        -Table 'ServiceInstall' -Columns $serviceInstallColumns `
        -Values $serviceInstallValues
    $serviceControlColumns = @(
        'ServiceControl', 'Name', 'Event', 'Arguments', 'Wait', 'Component_')
    $serviceControlValues = @(
        'NightGateServiceControl', 'NightGate.LocalService', 163, $null, 1,
        $serviceRow.ComponentId)
    Add-MsiRow -Installer $installer -Database $database `
        -Table 'ServiceControl' -Columns $serviceControlColumns `
        -Values $serviceControlValues

    $finalizeRow = $fileRows | Where-Object {
        $_.Relative -ieq 'installer\Finalize-NightGateMsi.ps1'
    } | Select-Object -First 1
    $commonCommand = '"[PowerShellV1Folder]powershell.exe" ' +
        '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden ' +
        '-ExecutionPolicy Bypass -File ' +
        "`"[#$($finalizeRow.FileId)]`" -InstallPath `"[INSTALLFOLDER]\`" " +
        "-DataPath `"[NIGHTGATEDATA]\`" -ProductCode `"$productCode`" " +
        "-ProductVersion `"$ProductVersion`""
    $customActions = @(
        ,@('PrepareInstallNightGate', 3106, 'PowerShellV1Folder', "$commonCommand -Mode Prepare", $null)
        ,@('PrepareUninstallNightGate', 3106, 'PowerShellV1Folder', "$commonCommand -Mode Prepare", $null)
        ,@('RollbackInstallNightGate', 3362, 'PowerShellV1Folder', "$commonCommand -ExpectedOperation Install -Mode Rollback", $null)
        ,@('RollbackUninstallNightGate', 3362, 'PowerShellV1Folder', "$commonCommand -ExpectedOperation Uninstall -Mode Rollback", $null)
        ,@('FinalizeNightGate', 3106, 'PowerShellV1Folder', "$commonCommand -UserSid `"[UserSID]`" -Mode Install", $null)
        ,@('UninstallNightGate', 3106, 'PowerShellV1Folder', "$commonCommand -Mode Uninstall", $null)
        ,@('CommitNightGate', 3618, 'PowerShellV1Folder', "$commonCommand -Mode Commit", $null)
    )
    foreach ($row in $customActions) {
        Add-MsiRow $installer $database 'CustomAction' `
            @('Action', 'Type', 'Source', 'Target', 'ExtendedType') $row
    }

    $uiSequence = @(
        ,@('FindRelatedProducts', $null, 25)
        ,@('AppSearch', $null, 400)
        ,@('LaunchConditions', $null, 500)
        ,@('ExecuteAction', $null, 1300)
    )
    foreach ($row in $uiSequence) {
        Add-MsiRow $installer $database 'InstallUISequence' `
            @('Action', 'Condition', 'Sequence') $row
    }

    $executeSequence = @(
        ,@('FindRelatedProducts', $null, 25)
        ,@('AppSearch', $null, 400)
        ,@('LaunchConditions', $null, 500)
        ,@('ValidateProductID', $null, 700)
        ,@('CostInitialize', $null, 800)
        ,@('FileCost', $null, 900)
        ,@('CostFinalize', $null, 1000)
        ,@('MigrateFeatureStates', 'OLDPRODUCTS', 1200)
        ,@('InstallValidate', $null, 1400)
        ,@('InstallInitialize', $null, 1500)
        ,@('ProcessComponents', $null, 1600)
        ,@('UnpublishFeatures', $null, 1800)
        ,@('StopServices', 'VersionNT', 1900)
        ,@('DeleteServices', 'VersionNT', 2000)
        ,@('RemoveRegistryValues', $null, 2600)
        ,@('RemoveShortcuts', $null, 3200)
        ,@('PrepareUninstallNightGate', 'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE', 3370)
        ,@('RollbackUninstallNightGate', 'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE', 3380)
        ,@('UninstallNightGate', 'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE', 3400)
        ,@('RemoveFiles', $null, 3500)
        ,@('RemoveFolders', $null, 3600)
        ,@('CreateFolders', $null, 3700)
        ,@('InstallFiles', $null, 4000)
        ,@('PrepareInstallNightGate', 'NOT REMOVE~="ALL"', 4002)
        ,@('RollbackInstallNightGate', 'NOT REMOVE~="ALL"', 4005)
        ,@('FinalizeNightGate', 'NOT REMOVE~="ALL"', 4020)
        ,@('CreateShortcuts', $null, 4500)
        ,@('WriteRegistryValues', $null, 5000)
        ,@('InstallServices', 'VersionNT', 5800)
        ,@('StartServices', 'VersionNT', 5900)
        ,@('RegisterUser', $null, 6000)
        ,@('RegisterProduct', $null, 6100)
        ,@('PublishFeatures', $null, 6300)
        ,@('PublishProduct', $null, 6400)
        ,@('CommitNightGate', 'NOT REMOVE~="ALL"', 6490)
        ,@('InstallExecute', $null, 6500)
        # Execute the queued service stop and file replacement before invoking
        # the old product's uninstall, while keeping both operations in the
        # same rollback transaction.
        ,@('RemoveExistingProducts', 'OLDPRODUCTS', 6550)
        ,@('InstallFinalize', $null, 6600)
    )
    foreach ($row in $executeSequence) {
        Add-MsiRow $installer $database 'InstallExecuteSequence' `
            @('Action', 'Condition', 'Sequence') $row
    }

    Add-MsiRow $installer $database 'LaunchCondition' `
        @('Condition', 'Description') `
        @('Installed OR (NIGHTGATEWINDOWSBUILD >= 22000 AND NIGHTGATEPRODUCTTYPE = "WinNT")',
          'NightGate requires Windows 11 build 22000 or newer.')
    Add-MsiRow $installer $database 'LaunchCondition' `
        @('Condition', 'Description') `
        @('NOT NEWERPRODUCTFOUND', 'A newer version of NightGate is already installed.')
    Add-MsiRow $installer $database 'LaunchCondition' `
        @('Condition', 'Description') `
        @('NOT RollbackDisabled',
          'NightGate requires Windows Installer rollback to be enabled.')
    Add-MsiRow $installer $database 'Upgrade' `
        @('UpgradeCode', 'VersionMin', 'VersionMax', 'Language', 'Attributes', 'Remove', 'ActionProperty') `
        @($upgradeCode, $null, $ProductVersion, $null, 1, $null, 'OLDPRODUCTS')
    Add-MsiRow $installer $database 'Upgrade' `
        @('UpgradeCode', 'VersionMin', 'VersionMax', 'Language', 'Attributes', 'Remove', 'ActionProperty') `
        @($upgradeCode, $ProductVersion, $null, $null, 258, $null,
          'NEWERPRODUCTFOUND')
    Add-MsiRow $installer $database 'Media' `
        @('DiskId', 'LastSequence', 'DiskPrompt', 'Cabinet', 'VolumeLabel', 'Source') `
        @(1, $sequence, $null, "#$cabinetName", $null, $null)
    Add-MsiStream -Installer $installer -Database $database `
        -Name $cabinetName -Path $cabinetPath

    $summary = $database.SummaryInformation(20)
    try {
        $summary.Property(1) = 936
        $summary.Property(2) = 'Installation Database'
        $summary.Property(3) = $productDisplayName
        $summary.Property(4) = 'NightGate'
        $summary.Property(6) = "$productDisplayName Win11 early-sleep commitment tool"
        $summary.Property(7) = 'x64;2052'
        $summary.Property(9) = $packageCode
        $summary.Property(14) = 500
        $summary.Property(15) = 2
        $summary.Property(19) = 2
        $summary.Persist()
    }
    finally {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) | Out-Null
    }
    $database.Commit()
}
finally {
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
}

$validator = New-Object -ComObject WindowsInstaller.Installer
$readOnlyDatabase = $validator.OpenDatabase($output, 0)
try {
    $storedSummary = $readOnlyDatabase.SummaryInformation(0)
    try { $storedSummaryTemplate = [string]$storedSummary.Property(7) }
    finally {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($storedSummary) |
            Out-Null
    }
    $storedProductCode = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductCode'"
    $storedService = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Name`` FROM ``ServiceInstall`` WHERE ``ServiceInstall``='NightGateServiceInstall'"
    $storedErrorControl = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``ErrorControl`` FROM ``ServiceInstall`` WHERE ``ServiceInstall``='NightGateServiceInstall'"
    $storedVersion = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"
    $storedProductName = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductName'"
    $storedArpIcon = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ARPPRODUCTICON'"
    $storedDesktopDefault = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='INSTALLDESKTOPSHORTCUT'"
    $storedArpSystemComponent = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ARPSYSTEMCOMPONENT'"
    $storedStartMenuDirectoryParent = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory``='ProgramMenuFolder'"
    $storedStartMenuDirectoryDefault = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``DefaultDir`` FROM ``Directory`` WHERE ``Directory``='ProgramMenuFolder'"
    $storedDesktopDirectoryParent = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory``='DesktopFolder'"
    $storedDesktopDirectoryDefault = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``DefaultDir`` FROM ``Directory`` WHERE ``Directory``='DesktopFolder'"
    $storedStartMenuComponentGuid = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``ComponentId`` FROM ``Component`` WHERE ``Component``='C_NightGateStartMenuShortcut'"
    $storedStartMenuComponentDirectory = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Directory_`` FROM ``Component`` WHERE ``Component``='C_NightGateStartMenuShortcut'"
    $storedStartMenuComponentAttributes = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Attributes`` FROM ``Component`` WHERE ``Component``='C_NightGateStartMenuShortcut'"
    $storedStartMenuComponentKeyPath = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``KeyPath`` FROM ``Component`` WHERE ``Component``='C_NightGateStartMenuShortcut'"
    $storedDesktopComponentGuid = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``ComponentId`` FROM ``Component`` WHERE ``Component``='C_NightGateDesktopShortcut'"
    $storedDesktopComponentDirectory = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Directory_`` FROM ``Component`` WHERE ``Component``='C_NightGateDesktopShortcut'"
    $storedDesktopComponentAttributes = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Attributes`` FROM ``Component`` WHERE ``Component``='C_NightGateDesktopShortcut'"
    $storedDesktopComponentCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``Component`` WHERE ``Component``='C_NightGateDesktopShortcut'"
    $storedDesktopComponentKeyPath = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``KeyPath`` FROM ``Component`` WHERE ``Component``='C_NightGateDesktopShortcut'"
    $storedStartMenuRegistryName = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Name`` FROM ``Registry`` WHERE ``Registry``='R_NightGateStartMenuShortcut'"
    $storedStartMenuRegistryRoot = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Root`` FROM ``Registry`` WHERE ``Registry``='R_NightGateStartMenuShortcut'"
    $storedStartMenuRegistryKey = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Key`` FROM ``Registry`` WHERE ``Registry``='R_NightGateStartMenuShortcut'"
    $storedStartMenuRegistryValue = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Value`` FROM ``Registry`` WHERE ``Registry``='R_NightGateStartMenuShortcut'"
    $storedStartMenuRegistryComponent = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Component_`` FROM ``Registry`` WHERE ``Registry``='R_NightGateStartMenuShortcut'"
    $storedDesktopRegistryName = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Name`` FROM ``Registry`` WHERE ``Registry``='R_NightGateDesktopShortcut'"
    $storedDesktopRegistryRoot = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Root`` FROM ``Registry`` WHERE ``Registry``='R_NightGateDesktopShortcut'"
    $storedDesktopRegistryKey = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Key`` FROM ``Registry`` WHERE ``Registry``='R_NightGateDesktopShortcut'"
    $storedDesktopRegistryValue = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Value`` FROM ``Registry`` WHERE ``Registry``='R_NightGateDesktopShortcut'"
    $storedDesktopRegistryComponent = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Component_`` FROM ``Registry`` WHERE ``Registry``='R_NightGateDesktopShortcut'"
    $storedStartMenuFeature = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Feature_`` FROM ``FeatureComponents`` WHERE ``Component_``='C_NightGateStartMenuShortcut'"
    $storedDesktopFeature = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Feature_`` FROM ``FeatureComponents`` WHERE ``Component_``='C_NightGateDesktopShortcut'"
    $storedStartMenuShortcutName = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Name`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'"
    $storedStartMenuShortcutDirectory = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Directory_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'"
    $storedStartMenuShortcutComponent = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Component_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'"
    $storedStartMenuShortcutTarget = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Target`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'"
    $storedStartMenuShortcutIcon = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Icon_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'"
    $storedStartMenuShortcutWorkingDirectory = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``WkDir`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateStartMenuShortcut'"
    $storedDesktopShortcutName = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Name`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'"
    $storedDesktopShortcutDirectory = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Directory_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'"
    $storedDesktopShortcutComponent = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Component_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'"
    $storedDesktopShortcutTarget = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Target`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'"
    $storedDesktopShortcutIcon = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Icon_`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'"
    $storedDesktopShortcutWorkingDirectory = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``WkDir`` FROM ``Shortcut`` WHERE ``Shortcut``='NightGateDesktopShortcut'"
    $storedIconSize = Get-MsiStreamSize $readOnlyDatabase `
        "SELECT ``Data`` FROM ``Icon`` WHERE ``Name``='NightGateProductIcon'"
    $storedPrepareInstallTarget = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='PrepareInstallNightGate'"
    $storedPrepareUninstallTarget = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='PrepareUninstallNightGate'"
    $storedFinalizeTarget = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='FinalizeNightGate'"
    $storedRollbackInstallTarget = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='RollbackInstallNightGate'"
    $storedRollbackUninstallTarget = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='RollbackUninstallNightGate'"
    $storedUninstallTarget = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='UninstallNightGate'"
    $storedCommitTarget = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='CommitNightGate'"
    $storedWindowsCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``LaunchCondition`` WHERE ``Description``='NightGate requires Windows 11 build 22000 or newer.'"
    $storedRollbackCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``LaunchCondition`` WHERE ``Description``='NightGate requires Windows Installer rollback to be enabled.'"
    $storedBuildSignature = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Signature_`` FROM ``AppSearch`` WHERE ``Property``='NIGHTGATEWINDOWSBUILD'"
    $storedBuildLocator = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Key`` FROM ``RegLocator`` WHERE ``Signature_``='NightGateWindowsBuild'"
    $storedBuildLocatorType = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Type`` FROM ``RegLocator`` WHERE ``Signature_``='NightGateWindowsBuild'"
    $storedProductTypeSignature = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Signature_`` FROM ``AppSearch`` WHERE ``Property``='NIGHTGATEPRODUCTTYPE'"
    $storedProductTypeLocator = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Key`` FROM ``RegLocator`` WHERE ``Signature_``='NightGateProductType'"
    $storedProductTypeLocatorType = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Type`` FROM ``RegLocator`` WHERE ``Signature_``='NightGateProductType'"
    $storedExecuteAppSearch = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='AppSearch'"
    $storedExecuteAppSearchCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='AppSearch'"
    $storedExecuteLaunch = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='LaunchConditions'"
    $storedExecuteLaunchCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='LaunchConditions'"
    $storedUiAppSearch = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallUISequence`` WHERE ``Action``='AppSearch'"
    $storedUiAppSearchCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallUISequence`` WHERE ``Action``='AppSearch'"
    $storedUiLaunch = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallUISequence`` WHERE ``Action``='LaunchConditions'"
    $storedUiLaunchCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallUISequence`` WHERE ``Action``='LaunchConditions'"
    $storedUiExecuteAction = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallUISequence`` WHERE ``Action``='ExecuteAction'"
    $storedUiExecuteActionCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallUISequence`` WHERE ``Action``='ExecuteAction'"
    $storedSecureProperties = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='SecureCustomProperties'"
    $storedRemoveSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RemoveExistingProducts'"
    $storedStopServicesSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='StopServices'"
    $storedInstallExecuteSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='InstallExecute'"
    $storedServiceControlEvent = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Event`` FROM ``ServiceControl`` WHERE ``ServiceControl``='NightGateServiceControl'"
    $storedDesktopFileVersion = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Version`` FROM ``File`` WHERE ``FileName``='NightGate.Desktop.exe'"
    $storedServiceFileVersion = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Version`` FROM ``File`` WHERE ``FileName``='NightGate.Service.exe'"
    $storedNativeHostFileVersion = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Version`` FROM ``File`` WHERE ``FileName``='NightGate.NativeHost.exe'"
    $storedCleanupFile = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``FileName`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateRollbackSnapshot'"
    $storedCleanupMode = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``InstallMode`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateRollbackSnapshot'"
    $storedCleanupComponent = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Component_`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateRollbackSnapshot'"
    $storedCleanupDirectory = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Directory_`` FROM ``Component`` WHERE ``Component``='C_RollbackSnapshotCleanup'"
    $storedCleanupGuid = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``ComponentId`` FROM ``Component`` WHERE ``Component``='C_RollbackSnapshotCleanup'"
    $storedCleanupKeyPath = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``KeyPath`` FROM ``Component`` WHERE ``Component``='C_RollbackSnapshotCleanup'"
    $storedCleanupAttributes = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Attributes`` FROM ``Component`` WHERE ``Component``='C_RollbackSnapshotCleanup'"
    $storedDataAttributes = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Attributes`` FROM ``Component`` WHERE ``Component``='C_NightGateData'"
    $storedCleanupParent = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory``='NIGHTGATEINSTALLERSTATE'"
    $storedLegacyCleanupFile = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``FileName`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateLegacyMsiState'"
    $storedLegacyCleanupMode = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``InstallMode`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateLegacyMsiState'"
    $storedLegacyCleanupDirectory = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``DirProperty`` FROM ``RemoveFile`` WHERE ``FileKey``='RemoveNightGateLegacyMsiState'"
    $storedPrepareUninstallType = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='PrepareUninstallNightGate'"
    $storedPrepareUninstallSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='PrepareUninstallNightGate'"
    $storedPrepareUninstallCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='PrepareUninstallNightGate'"
    $storedRollbackUninstallType = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='RollbackUninstallNightGate'"
    $storedRollbackUninstallSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RollbackUninstallNightGate'"
    $storedRollbackUninstallCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RollbackUninstallNightGate'"
    $storedUninstallType = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='UninstallNightGate'"
    $storedUninstallSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='UninstallNightGate'"
    $storedUninstallCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='UninstallNightGate'"
    $storedRemoveFilesSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RemoveFiles'"
    $storedRemoveRegistryValuesSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RemoveRegistryValues'"
    $storedRemoveShortcutsSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RemoveShortcuts'"
    $storedCreateShortcutsSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='CreateShortcuts'"
    $storedWriteRegistryValuesSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='WriteRegistryValues'"
    $storedCommitCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='CommitNightGate'"
    $storedPrepareInstallType = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='PrepareInstallNightGate'"
    $storedPrepareInstallSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='PrepareInstallNightGate'"
    $storedPrepareInstallCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='PrepareInstallNightGate'"
    $storedRollbackInstallType = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='RollbackInstallNightGate'"
    $storedRollbackInstallSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RollbackInstallNightGate'"
    $storedRollbackInstallCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='RollbackInstallNightGate'"
    $storedFinalizeType = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='FinalizeNightGate'"
    $storedFinalizeSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='FinalizeNightGate'"
    $storedFinalizeCondition = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='FinalizeNightGate'"
    $storedCommitType = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='CommitNightGate'"
    $storedCommitSequence = Get-MsiScalar $readOnlyDatabase `
        "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='CommitNightGate'"
    $storedActionTargets = [ordered]@{
        PrepareInstallNightGate = $storedPrepareInstallTarget
        PrepareUninstallNightGate = $storedPrepareUninstallTarget
        RollbackInstallNightGate = $storedRollbackInstallTarget
        RollbackUninstallNightGate = $storedRollbackUninstallTarget
        FinalizeNightGate = $storedFinalizeTarget
        UninstallNightGate = $storedUninstallTarget
        CommitNightGate = $storedCommitTarget
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
    $requiredFlags = [regex]::Escape(
        '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass')
    foreach ($entry in $storedActionTargets.GetEnumerator()) {
        $target = [string]$entry.Value
        if ([string]::IsNullOrWhiteSpace($target) -or
            $target -notmatch '^"\[PowerShellV1Folder\]powershell\.exe"\s+' -or
            $target -notmatch $requiredFlags -or
            $target -notmatch '-File\s+"\[#[^\]]+\]"' -or
            $target -notmatch '-InstallPath\s+"\[INSTALLFOLDER\]\\"' -or
            $target -notmatch '-DataPath\s+"\[NIGHTGATEDATA\]\\"' -or
            $target -notmatch "-Mode\s+$($expectedModes[$entry.Key])(?:\s|$)" -or
            $target -match '\[CustomActionData\]') {
            throw "MSI action $($entry.Key) has an unsafe PowerShell target: $target"
        }
    }
    if ($storedFinalizeTarget -notmatch '-UserSid\s+"\[UserSID\]"' -or
        $storedRollbackInstallTarget -notmatch '-ExpectedOperation\s+Install(?:\s|$)' -or
        $storedRollbackUninstallTarget -notmatch '-ExpectedOperation\s+Uninstall(?:\s|$)' -or
        $storedPrepareInstallTarget -match '-UserSid|-ExpectedOperation' -or
        $storedPrepareUninstallTarget -match '-UserSid|-ExpectedOperation' -or
        $storedRollbackInstallTarget -match '-UserSid' -or
        $storedRollbackUninstallTarget -match '-UserSid' -or
        $storedUninstallTarget -match '-UserSid' -or
        $storedCommitTarget -match '-UserSid') {
        throw 'Only FinalizeNightGate may receive the installation-time UserSID.'
    }
    foreach ($legacySetter in @(
        'SetPrepareInstallNightGateData',
        'SetPrepareUninstallNightGateData',
        'SetRollbackInstallNightGateData',
        'SetRollbackUninstallNightGateData',
        'SetFinalizeNightGateData',
        'SetUninstallNightGateData',
        'SetCommitNightGateData')) {
        $legacySetterTarget = Get-MsiScalar $readOnlyDatabase `
            "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='$legacySetter'"
        if ($null -ne $legacySetterTarget) {
            throw "Legacy CustomActionData bridge remains in MSI: $legacySetter"
        }
    }
    if ($storedSummaryTemplate -ne 'x64;2052' -or
        $storedProductCode -ne $productCode -or
        $storedVersion -ne $ProductVersion -or
        $storedProductName -ne $productDisplayName -or
        $storedArpIcon -ne 'NightGateProductIcon' -or
        $storedDesktopDefault -ne '1' -or
        $null -ne $storedArpSystemComponent -or
        $storedStartMenuDirectoryParent -ne 'TARGETDIR' -or
        $storedStartMenuDirectoryDefault -ne '.' -or
        $storedDesktopDirectoryParent -ne 'TARGETDIR' -or
        $storedDesktopDirectoryDefault -ne '.' -or
        $storedStartMenuComponentGuid -ne
            '{1CAAAF77-83FA-4356-A9DC-F11A68457702}' -or
        $storedStartMenuComponentDirectory -ne 'ProgramMenuFolder' -or
        $storedStartMenuComponentAttributes -ne '260' -or
        $storedStartMenuComponentKeyPath -ne 'R_NightGateStartMenuShortcut' -or
        $storedDesktopComponentGuid -ne
            '{10D5411C-2D2D-A459-A7E7-3F0C48333AED}' -or
        $storedDesktopComponentDirectory -ne 'DesktopFolder' -or
        $storedDesktopComponentAttributes -ne '260' -or
        $storedDesktopComponentCondition -ne 'INSTALLDESKTOPSHORTCUT=1' -or
        $storedDesktopComponentKeyPath -ne 'R_NightGateDesktopShortcut' -or
        $storedStartMenuRegistryRoot -ne '2' -or
        $storedStartMenuRegistryKey -ne 'Software\NightGate\Installer' -or
        $storedStartMenuRegistryName -ne 'StartMenuShortcut' -or
        $storedStartMenuRegistryValue -ne '#1' -or
        $storedStartMenuRegistryComponent -ne
            'C_NightGateStartMenuShortcut' -or
        $storedDesktopRegistryRoot -ne '2' -or
        $storedDesktopRegistryKey -ne 'Software\NightGate\Installer' -or
        $storedDesktopRegistryName -ne 'DesktopShortcut' -or
        $storedDesktopRegistryValue -ne '#1' -or
        $storedDesktopRegistryComponent -ne 'C_NightGateDesktopShortcut' -or
        $storedStartMenuFeature -ne 'Complete' -or
        $storedDesktopFeature -ne 'Complete' -or
        $storedStartMenuShortcutName -ne $productDisplayName -or
        $storedStartMenuShortcutDirectory -ne 'ProgramMenuFolder' -or
        $storedStartMenuShortcutComponent -ne
            'C_NightGateStartMenuShortcut' -or
        $storedStartMenuShortcutTarget -ne
            '[INSTALLFOLDER]apps\Desktop\NightGate.Desktop.exe' -or
        $storedStartMenuShortcutIcon -ne 'NightGateProductIcon' -or
        $storedStartMenuShortcutWorkingDirectory -ne 'INSTALLFOLDER' -or
        $storedDesktopShortcutName -ne $productShortName -or
        $storedDesktopShortcutDirectory -ne 'DesktopFolder' -or
        $storedDesktopShortcutComponent -ne 'C_NightGateDesktopShortcut' -or
        $storedDesktopShortcutTarget -ne
            '[INSTALLFOLDER]apps\Desktop\NightGate.Desktop.exe' -or
        $storedDesktopShortcutIcon -ne 'NightGateProductIcon' -or
        $storedDesktopShortcutWorkingDirectory -ne 'INSTALLFOLDER' -or
        $storedIconSize -le 0 -or
        $storedService -ne 'NightGate.LocalService' -or
        $storedErrorControl -ne '32769' -or
        $storedWindowsCondition -ne
            'Installed OR (NIGHTGATEWINDOWSBUILD >= 22000 AND NIGHTGATEPRODUCTTYPE = "WinNT")' -or
        $storedRollbackCondition -ne 'NOT RollbackDisabled' -or
        $storedBuildSignature -ne 'NightGateWindowsBuild' -or
        $storedBuildLocator -ne 'SOFTWARE\Microsoft\Windows NT\CurrentVersion' -or
        $storedBuildLocatorType -ne '18' -or
        $storedProductTypeSignature -ne 'NightGateProductType' -or
        $storedProductTypeLocator -ne
            'SYSTEM\CurrentControlSet\Control\ProductOptions' -or
        $storedProductTypeLocatorType -ne '18' -or
        $storedExecuteAppSearch -ne '400' -or
        -not [string]::IsNullOrEmpty($storedExecuteAppSearchCondition) -or
        $storedExecuteLaunch -ne '500' -or
        -not [string]::IsNullOrEmpty($storedExecuteLaunchCondition) -or
        $storedUiAppSearch -ne '400' -or
        -not [string]::IsNullOrEmpty($storedUiAppSearchCondition) -or
        $storedUiLaunch -ne '500' -or
        -not [string]::IsNullOrEmpty($storedUiLaunchCondition) -or
        $storedUiExecuteAction -ne '1300' -or
        -not [string]::IsNullOrEmpty($storedUiExecuteActionCondition) -or
        $storedSecureProperties -ne
            'OLDPRODUCTS;NEWERPRODUCTFOUND;NIGHTGATEWINDOWSBUILD;NIGHTGATEPRODUCTTYPE;INSTALLDESKTOPSHORTCUT' -or
        $storedStopServicesSequence -ne '1900' -or
        $storedInstallExecuteSequence -ne '6500' -or
        $storedRemoveSequence -ne '6550' -or
        $storedServiceControlEvent -ne '163' -or
        $storedDesktopFileVersion -ne $nightGatePayloadFileVersion -or
        $storedServiceFileVersion -ne $nightGatePayloadFileVersion -or
        $storedNativeHostFileVersion -ne $nightGatePayloadFileVersion -or
        $storedCleanupFile -ne 'rollback-snapshot.json' -or
        $storedCleanupMode -ne '2' -or
        $storedCleanupComponent -ne 'C_RollbackSnapshotCleanup' -or
        $storedCleanupDirectory -ne 'NIGHTGATEINSTALLERSTATE' -or
        $storedCleanupGuid -ne '{B19C091A-1794-EA51-A267-874C0EC6B21E}' -or
        -not [string]::IsNullOrEmpty($storedCleanupKeyPath) -or
        $storedCleanupAttributes -ne '256' -or
        $storedDataAttributes -ne '272' -or
        $storedCleanupParent -ne 'NIGHTGATEDATA' -or
        $storedLegacyCleanupFile -ne 'msi-install-state.json' -or
        $storedLegacyCleanupMode -ne '2' -or
        $storedLegacyCleanupDirectory -ne 'NIGHTGATEDATA' -or
        $storedPrepareUninstallType -ne '3106' -or
        $storedPrepareUninstallSequence -ne '3370' -or
        $storedPrepareUninstallCondition -ne
            'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE' -or
        $storedRollbackUninstallType -ne '3362' -or
        $storedRollbackUninstallSequence -ne '3380' -or
        $storedRollbackUninstallCondition -ne
            'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE' -or
        $storedUninstallType -ne '3106' -or
        $storedUninstallSequence -ne '3400' -or
        $storedUninstallCondition -ne
            'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE' -or
        $storedRemoveFilesSequence -ne '3500' -or
        $storedRemoveRegistryValuesSequence -ne '2600' -or
        $storedRemoveShortcutsSequence -ne '3200' -or
        $storedCreateShortcutsSequence -ne '4500' -or
        $storedWriteRegistryValuesSequence -ne '5000' -or
        $storedPrepareInstallType -ne '3106' -or
        $storedPrepareInstallSequence -ne '4002' -or
        $storedPrepareInstallCondition -ne 'NOT REMOVE~="ALL"' -or
        $storedRollbackInstallType -ne '3362' -or
        $storedRollbackInstallSequence -ne '4005' -or
        $storedRollbackInstallCondition -ne 'NOT REMOVE~="ALL"' -or
        $storedFinalizeType -ne '3106' -or
        $storedFinalizeSequence -ne '4020' -or
        $storedFinalizeCondition -ne 'NOT REMOVE~="ALL"' -or
        $storedCommitType -ne '3618' -or
        $storedCommitSequence -ne '6490' -or
        $storedCommitCondition -ne 'NOT REMOVE~="ALL"') {
        throw "The authored MSI failed its structural safety validation: " +
            "product=$storedProductCode; service=$storedService; " +
            "errorControl=$storedErrorControl; version=$storedVersion; " +
            "finalize=$storedFinalizeTarget; windows=$storedWindowsCondition; " +
            "removeExisting=$storedRemoveSequence; cleanup=$storedCleanupFile; " +
            "cleanupMode=$storedCleanupMode; cleanupComponent=$storedCleanupComponent"
    }
}
finally {
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($readOnlyDatabase) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($validator) | Out-Null
}
if (-not (Test-Path -LiteralPath $output -PathType Leaf) -or
    (Get-Item -LiteralPath $output).Length -le (Get-Item -LiteralPath $cabinetPath).Length) {
    throw 'The authored MSI is missing or did not embed its cabinet.'
}

Write-Host "Windows Installer package: $output"
Write-Host "Product version: $ProductVersion; ProductCode: $productCode; PackageCode: $packageCode"
Write-Host "Payload FileVersion: $nightGatePayloadFileVersion"
Write-Host 'Target user contract: Windows Installer UserSID -> validated per-user SID'
