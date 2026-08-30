[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutputPath,
    [Parameter(Mandatory)] [string] $HostExecutablePath,
    [string] $TemplatePath,
    [string] $ExtensionManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-NightGateRepoRoot
if ([string]::IsNullOrWhiteSpace($TemplatePath)) {
    $TemplatePath = Join-Path $root 'src\NightGate.NativeHost\com.nightgate.host.json'
}
if ([string]::IsNullOrWhiteSpace($ExtensionManifestPath)) {
    $ExtensionManifestPath = Join-Path $root 'src\NightGate.Chrome.Extension\manifest.json'
}
if (-not [IO.Path]::IsPathRooted($HostExecutablePath)) {
    throw 'The native host executable path must be absolute.'
}

$templateText = Get-Content -LiteralPath $TemplatePath -Raw -Encoding UTF8
$template = $templateText | ConvertFrom-Json
if ($template.path -ne '__NIGHTGATE_NATIVE_HOST_PATH__') {
    throw 'Native host source template must retain its path placeholder.'
}
$extension = Get-Content -LiteralPath $ExtensionManifestPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
$keyBytes = [Convert]::FromBase64String([string]$extension.key)
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $digest = $sha256.ComputeHash($keyBytes)
}
finally {
    $sha256.Dispose()
}
$idBuilder = [Text.StringBuilder]::new(32)
foreach ($index in 0..15) {
    $null = $idBuilder.Append([char]([int][char]'a' + ($digest[$index] -shr 4)))
    $null = $idBuilder.Append([char]([int][char]'a' + ($digest[$index] -band 15)))
}
$origin = "chrome-extension://$($idBuilder.ToString())/"
if (@($template.allowed_origins).Count -ne 1 -or
    [string]$template.allowed_origins[0] -ne $origin) {
    throw "Native host origin does not match the extension public key: $origin"
}

$template.path = [IO.Path]::GetFullPath($HostExecutablePath)
$json = $template | ConvertTo-Json -Depth 8
if ($json -match '__[A-Z0-9_]+__') {
    throw 'Generated native host manifest still contains a placeholder.'
}
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $parent -Force | Out-Null
[IO.File]::WriteAllText(
    [IO.Path]::GetFullPath($OutputPath),
    $json + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
