[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InstallPath,
    [Parameter(Mandatory)] [string] $DataPath,
    [Parameter(Mandatory)] [string] $ProductCode,
    [Parameter(Mandatory)] [string] $ProductVersion,
    [Parameter()] [string] $UserSid,
    [Parameter(Mandatory)]
    [ValidateSet('Install', 'Rollback', 'Uninstall', 'Commit')]
    [string] $Mode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

[ordered]@{
    InstallPath = $InstallPath
    DataPath = $DataPath
    ProductCode = $ProductCode
    ProductVersion = $ProductVersion
    UserSid = $UserSid
    Mode = $Mode
} | ConvertTo-Json -Compress
