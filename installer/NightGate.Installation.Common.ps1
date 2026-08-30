Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-NightGateCanonicalDesktopSid {
    param([AllowEmptyString()] [string] $SidValue)

    if ([string]::IsNullOrWhiteSpace($SidValue)) {
        throw 'A target interactive desktop user SID is required.'
    }
    try {
        $sid = New-Object Security.Principal.SecurityIdentifier($SidValue)
    }
    catch {
        throw "The target desktop user SID is not canonical: $SidValue"
    }
    if (-not $sid.IsAccountSid()) {
        throw "The target SID is not a Windows account SID: $SidValue"
    }
    $canonical = $sid.Value
    if ($canonical -eq 'S-1-5-19') {
        throw 'The target SID must be the interactive desktop user, not LocalService.'
    }
    return $canonical
}

function New-NightGateSidFileSystemAccessRule {
    param(
        [Parameter(Mandatory)] [string] $SidValue,
        [Parameter(Mandatory)]
        [Security.AccessControl.FileSystemRights] $Rights,
        [Parameter(Mandatory)]
        [Security.AccessControl.InheritanceFlags] $InheritanceFlags,
        [Parameter(Mandatory)]
        [Security.AccessControl.PropagationFlags] $PropagationFlags,
        [Parameter(Mandatory)]
        [Security.AccessControl.AccessControlType] $AccessControlType
    )

    try {
        $identity = [Security.Principal.SecurityIdentifier]::new($SidValue)
    }
    catch {
        throw "The ACL identity SID is invalid: $SidValue"
    }
    return [Security.AccessControl.FileSystemAccessRule]::new(
        $identity,
        $Rights,
        $InheritanceFlags,
        $PropagationFlags,
        $AccessControlType)
}

function Get-NightGateFileSystemAclSnapshot {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [switch] $File
    )

    $pathType = if ($File) { 'Leaf' } else { 'Container' }
    if (-not (Test-Path -LiteralPath $Path -PathType $pathType)) {
        return [ordered]@{
            wasPresent = $false
            isFile = [bool]$File
            sddl = $null
        }
    }
    # NightGate changes only the DACL. Limiting the snapshot to Access avoids
    # requiring SeSecurityPrivilege merely to restore an unchanged audit ACL.
    $sections = [Security.AccessControl.AccessControlSections]::Access
    $acl = Get-Acl -LiteralPath $Path
    return [ordered]@{
        wasPresent = $true
        isFile = [bool]$File
        sddl = $acl.GetSecurityDescriptorSddlForm($sections)
    }
}

function Restore-NightGateFileSystemAclSnapshot {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] $Snapshot
    )

    if (-not [bool]$Snapshot.wasPresent) {
        return
    }
    $isFile = [bool]$Snapshot.isFile
    $pathType = if ($isFile) { 'Leaf' } else { 'Container' }
    if (-not (Test-Path -LiteralPath $Path -PathType $pathType)) {
        throw "Cannot restore a missing NightGate ACL target: $Path"
    }
    $sections = [Security.AccessControl.AccessControlSections]::Access
    # Start from the current descriptor so owner/group/audit sections remain
    # untouched and Set-Acl does not request privileges NightGate never needs.
    $acl = Get-Acl -LiteralPath $Path
    try {
        $acl.SetSecurityDescriptorSddlForm([string]$Snapshot.sddl, $sections)
    }
    catch {
        throw "The saved NightGate ACL descriptor is invalid for: $Path"
    }
    if ($isFile) {
        ([IO.FileInfo]::new([IO.Path]::GetFullPath($Path))).SetAccessControl(
            [Security.AccessControl.FileSecurity]$acl)
    }
    else {
        ([IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path))).SetAccessControl(
            [Security.AccessControl.DirectorySecurity]$acl)
    }
}

function Get-NightGateInteractiveDesktopSid {
    if ($null -eq ('NightGate.Release.WtsSessionIdentity' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace NightGate.Release
{
    public static class WtsSessionIdentity
    {
        public const int WTSUserName = 5;
        public const int WTSDomainName = 7;

        [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WTSQuerySessionInformation(
            IntPtr server, int sessionId, int infoClass,
            out IntPtr buffer, out int bytesReturned);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr buffer);

        public static string QueryString(int sessionId, int infoClass)
        {
            IntPtr buffer;
            int bytes;
            if (!WTSQuerySessionInformation(
                IntPtr.Zero, sessionId, infoClass, out buffer, out bytes)
                || buffer == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
    }
}
'@
    }

    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $userName = [NightGate.Release.WtsSessionIdentity]::QueryString(
        $sessionId, [NightGate.Release.WtsSessionIdentity]::WTSUserName)
    $domainName = [NightGate.Release.WtsSessionIdentity]::QueryString(
        $sessionId, [NightGate.Release.WtsSessionIdentity]::WTSDomainName)
    if ([string]::IsNullOrWhiteSpace($userName)) {
        throw 'No interactive desktop user is available.'
    }
    $accountName = if ([string]::IsNullOrWhiteSpace($domainName)) {
        $userName
    }
    else {
        "$domainName\$userName"
    }
    $account = New-Object Security.Principal.NTAccount($accountName)
    try {
        $sid = $account.Translate([Security.Principal.SecurityIdentifier])
    }
    catch {
        throw 'The interactive desktop user could not be translated to a Windows SID.'
    }
    return ConvertTo-NightGateCanonicalDesktopSid -SidValue $sid.Value
}

function Get-NightGateServiceConfigurationSid {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "NightGate service configuration is missing: $Path"
    }
    $config = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $nightGateProperty = $config.PSObject.Properties['NightGate']
    if ($null -eq $nightGateProperty -or $null -eq $nightGateProperty.Value) {
        throw 'NightGate service configuration has no NightGate object.'
    }
    $sidProperty = $nightGateProperty.Value.PSObject.Properties[
        'ConfiguredWindowsUserSid']
    if ($null -eq $sidProperty) {
        throw 'NightGate service configuration has no ConfiguredWindowsUserSid.'
    }
    return ConvertTo-NightGateCanonicalDesktopSid -SidValue ([string]$sidProperty.Value)
}

function Write-NightGateServiceConfiguration {
    param(
        [Parameter(Mandatory)] [string] $TemplatePath,
        [Parameter(Mandatory)] [string] $OutputPath,
        [AllowEmptyString()] [string] $DesktopSid
    )

    $canonical = ConvertTo-NightGateCanonicalDesktopSid -SidValue $DesktopSid
    $placeholder = '__CONFIGURED_WINDOWS_USER_SID__'
    $template = Get-Content -LiteralPath $TemplatePath -Raw -Encoding UTF8
    if ([regex]::Matches($template, [regex]::Escape($placeholder)).Count -ne 1) {
        throw 'The service configuration template must contain exactly one SID placeholder.'
    }
    $rendered = $template.Replace($placeholder, $canonical)
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($OutputPath),
        $rendered.TrimEnd() + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
    $writtenSid = Get-NightGateServiceConfigurationSid -Path $OutputPath
    if ($writtenSid -ne $canonical) {
        throw 'The rendered service configuration SID did not round-trip.'
    }
}

function Set-NightGateServiceConfigurationSid {
    param(
        [Parameter(Mandatory)] [string] $InputPath,
        [Parameter(Mandatory)] [string] $OutputPath,
        [AllowEmptyString()] [string] $DesktopSid
    )

    $canonical = ConvertTo-NightGateCanonicalDesktopSid -SidValue $DesktopSid
    $placeholder = '__CONFIGURED_WINDOWS_USER_SID__'
    $inputText = Get-Content -LiteralPath $InputPath -Raw -Encoding UTF8
    $config = $inputText | ConvertFrom-Json
    $nightGateProperty = $config.PSObject.Properties['NightGate']
    if ($null -eq $nightGateProperty -or $null -eq $nightGateProperty.Value) {
        throw 'NightGate service configuration has no NightGate object.'
    }
    $sidProperty = $nightGateProperty.Value.PSObject.Properties[
        'ConfiguredWindowsUserSid']
    if ($null -eq $sidProperty) {
        throw 'NightGate service configuration has no ConfiguredWindowsUserSid.'
    }
    $placeholderCount = [regex]::Matches(
        $inputText, [regex]::Escape($placeholder)).Count
    if ($placeholderCount -eq 1 -and [string]$sidProperty.Value -eq $placeholder) {
        # Fresh package: replace the one fixed publication placeholder.
    }
    elseif ($placeholderCount -eq 0) {
        # Repair/upgrade: accept only an already valid account SID.
        $null = ConvertTo-NightGateCanonicalDesktopSid `
            -SidValue ([string]$sidProperty.Value)
    }
    else {
        throw 'The service configuration must contain one SID placeholder or one valid SID.'
    }
    $config.NightGate.ConfiguredWindowsUserSid = $canonical
    $json = $config | ConvertTo-Json -Depth 16
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($OutputPath),
        $json + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
    $writtenSid = Get-NightGateServiceConfigurationSid -Path $OutputPath
    if ($writtenSid -ne $canonical) {
        throw 'The installed service configuration SID did not round-trip.'
    }
}
