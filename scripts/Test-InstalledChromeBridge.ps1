[CmdletBinding()]
param(
    [ValidateRange(1, 60)]
    [int] $HeartbeatTimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$hostName = 'com.nightgate.host'
$nativeHostSubKey =
    'Software\Google\Chrome\NativeMessagingHosts\com.nightgate.host'
$expectedOrigin =
    'chrome-extension://eefgemhlhbdodhlgjmicnoifhclhdgmm/'

function Initialize-NightGateWtsSessionApi {
    if ($null -ne ('NightGate.Tools.WtsSessionApi' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NightGate.Tools
{
    public static class WtsSessionApi
    {
        [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WTSQuerySessionInformation(
            IntPtr server,
            int sessionId,
            int infoClass,
            out IntPtr buffer,
            out int bytesReturned);

        [DllImport("Wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        public static string QuerySessionString(int sessionId, int infoClass)
        {
            IntPtr buffer;
            int bytesReturned;
            if (!WTSQuerySessionInformation(
                IntPtr.Zero,
                sessionId,
                infoClass,
                out buffer,
                out bytesReturned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    WTSFreeMemory(buffer);
                }
            }
        }
    }
}
'@ | Out-Null
}

function Get-NightGateInteractiveSessionIdentity {
    Initialize-NightGateWtsSessionApi

    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $userName = [NightGate.Tools.WtsSessionApi]::QuerySessionString(
        $sessionId,
        5)
    if ([string]::IsNullOrWhiteSpace($userName)) {
        return $null
    }

    $domain = [NightGate.Tools.WtsSessionApi]::QuerySessionString(
        $sessionId,
        7)
    $identityName = if ([string]::IsNullOrWhiteSpace($domain)) {
        $userName
    }
    else {
        "$domain\$userName"
    }

    $sid = $null
    try {
        $account = [Security.Principal.NTAccount]::new($identityName)
        $translated = $account.Translate(
            [Security.Principal.SecurityIdentifier])
        $sid = [string]$translated.Value
    }
    catch [Security.Principal.IdentityNotMappedException] {
        $sid = $null
    }

    [pscustomobject]@{
        Name = $identityName
        Sid = $sid
        SessionId = $sessionId
    }
}

function Get-NightGateProbeIdentityMismatch {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $CurrentIdentityName,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $CurrentSid,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $InteractiveIdentityName,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $InteractiveSid
    )

    $sidMismatch =
        -not [string]::IsNullOrWhiteSpace($InteractiveSid) -and
        -not [string]::Equals(
            $CurrentSid,
            $InteractiveSid,
            [StringComparison]::OrdinalIgnoreCase)
    $nameMismatch =
        [string]::IsNullOrWhiteSpace($InteractiveSid) -and
        -not [string]::IsNullOrWhiteSpace($InteractiveIdentityName) -and
        -not [string]::Equals(
            $CurrentIdentityName,
            $InteractiveIdentityName,
            [StringComparison]::OrdinalIgnoreCase)
    if ($sidMismatch -or $nameMismatch) {
        return "This check is running as $CurrentIdentityName (SID $CurrentSid), " +
            "but Windows session belongs to $InteractiveIdentityName " +
            "(SID $InteractiveSid). HKCU and the NightGate pipe would test the " +
            'wrong Windows account. Run this check from a non-elevated ' +
            'PowerShell opened normally by the actual interactive desktop account.'
    }

    $accountName = @($CurrentIdentityName -split '\\')[-1]
    if ($accountName -in @('CodexSandboxOffline', 'CodexSandboxOnline')) {
        return "This check is running under the Codex sandbox identity " +
            "$CurrentIdentityName (SID $CurrentSid), so its HKCU is not the " +
            'installed desktop user registry. Run this check from a non-elevated ' +
            'PowerShell opened normally by the actual interactive desktop account.'
    }

    return $null
}

function Get-NightGateCurrentUserManifestPath {
    param(
        [Parameter(Mandatory)]
        [Microsoft.Win32.RegistryView] $View
    )

    $currentUser = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        $View)
    try {
        $key = $currentUser.OpenSubKey($nativeHostSubKey, $false)
        if ($null -eq $key) {
            return $null
        }
        try {
            return [string]$key.GetValue(
                '',
                $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        }
        finally {
            $key.Dispose()
        }
    }
    finally {
        $currentUser.Dispose()
    }
}

function Read-NightGateExactBytes {
    param(
        [Parameter(Mandatory)] [IO.Stream] $Stream,
        [Parameter(Mandatory)] [int] $Count
    )

    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $readTask = $Stream.ReadAsync($buffer, $offset, $Count - $offset)
        if (-not $readTask.Wait(3000)) {
            throw 'NightGate service did not return a response frame within 3 seconds.'
        }
        $read = $readTask.GetAwaiter().GetResult()
        if ($read -le 0) {
            throw 'NightGate service closed an incomplete response frame.'
        }
        $offset += $read
    }
    return ,$buffer
}

function Get-NightGateInstalledUserState {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        '.',
        'NightGateService',
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous,
        [Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(2000)
        # A connected but unhealthy local service must not leave the installed
        # verification probe waiting forever for a response frame.
        $requestId = [Guid]::NewGuid().ToString('N')
        $requestJson = [ordered]@{
            version = 1
            type = 'getUserState'
            requestId = $requestId
            payload = [ordered]@{}
        } | ConvertTo-Json -Depth 4 -Compress
        $request = [Text.Encoding]::UTF8.GetBytes($requestJson)
        $prefix = [BitConverter]::GetBytes([int]$request.Length)
        $pipe.Write($prefix, 0, $prefix.Length)
        $pipe.Write($request, 0, $request.Length)
        $pipe.Flush()

        $responsePrefix = Read-NightGateExactBytes -Stream $pipe -Count 4
        $responseLength = [BitConverter]::ToInt32($responsePrefix, 0)
        if ($responseLength -le 0 -or $responseLength -gt 1048576) {
            throw "NightGate service returned an invalid frame length: $responseLength"
        }
        $responseBytes = Read-NightGateExactBytes `
            -Stream $pipe -Count $responseLength
        $response = [Text.Encoding]::UTF8.GetString($responseBytes) |
            ConvertFrom-Json
        if ($response.version -ne 1 -or
            $response.type -ne 'getUserStateResult' -or
            $response.requestId -ne $requestId -or
            $response.payload.status -ne 'success') {
            throw 'NightGate getUserState did not return a correlated success result.'
        }
        return $response.payload.data
    }
    finally {
        $pipe.Dispose()
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$interactiveIdentityName = ''
$interactiveSid = ''
try {
    $interactiveIdentity = Get-NightGateInteractiveSessionIdentity
    if ($null -ne $interactiveIdentity) {
        $interactiveIdentityName = [string]$interactiveIdentity.Name
        $interactiveSid = [string]$interactiveIdentity.Sid
    }
}
catch {
    # WTS discovery is a diagnostic guard. If Windows cannot expose it, the
    # strict current-user registry and pipe checks below remain authoritative.
}
$identityMismatch = Get-NightGateProbeIdentityMismatch `
    -CurrentIdentityName $identity.Name `
    -CurrentSid $identity.User.Value `
    -InteractiveIdentityName $interactiveIdentityName `
    -InteractiveSid $interactiveSid
if (-not [string]::IsNullOrWhiteSpace($identityMismatch)) {
    throw $identityMismatch
}

$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this check from the ordinary, non-elevated desktop user session.'
}

$viewResults = [ordered]@{}
foreach ($view in @(
    [Microsoft.Win32.RegistryView]::Registry32,
    [Microsoft.Win32.RegistryView]::Registry64)) {
    $path = Get-NightGateCurrentUserManifestPath -View $view
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Current user $($identity.User.Value) has no $hostName registration in $view."
    }
    $viewResults[$view.ToString()] = [IO.Path]::GetFullPath($path)
}

$manifestPath = [string]$viewResults.Registry32
if (-not [string]::Equals(
    $manifestPath,
    [string]$viewResults.Registry64,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The current user has conflicting 32-bit and 64-bit native-host registrations.'
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "The current-user native-host manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
if ($manifest.name -ne $hostName -or
    $manifest.type -ne 'stdio' -or
    [string]::IsNullOrWhiteSpace([string]$manifest.path) -or
    @($manifest.allowed_origins) -notcontains $expectedOrigin) {
    throw 'The current-user native-host manifest has invalid identity or origin data.'
}
$nativeHostPath = [IO.Path]::GetFullPath([string]$manifest.path)
if (-not (Test-Path -LiteralPath $nativeHostPath -PathType Leaf)) {
    throw "The registered NightGate native-host executable is missing: $nativeHostPath"
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($HeartbeatTimeoutSeconds)
$lastObservation = 'No service response was observed.'
$health = $null
do {
    try {
        $state = Get-NightGateInstalledUserState
        $health = $state.chromeProtection
        $lastObservation =
            "status=$($health.status); lastHeartbeatAtUtc=$($health.lastHeartbeatAtUtc)"
        if ([bool]$health.isHealthy) {
            break
        }
    }
    catch {
        $lastObservation = $_.Exception.Message
    }
    if ([DateTimeOffset]::UtcNow -ge $deadline) {
        break
    }
    Start-Sleep -Milliseconds 1000
} while ($true)

if ($null -eq $health -or -not [bool]$health.isHealthy) {
    throw "Chrome native-host registration is readable, but no healthy heartbeat arrived within $HeartbeatTimeoutSeconds seconds. Last observation: $lastObservation"
}
if ([string]$health.extensionVersion -ne '0.1.5') {
    throw "Chrome reported extension version '$($health.extensionVersion)', expected '0.1.5'."
}

[ordered]@{
    result = 'PASS'
    identity = $identity.Name
    sid = $identity.User.Value
    registryViews = $viewResults
    manifestPath = $manifestPath
    nativeHostPath = $nativeHostPath
    chromeStatus = $health.status
    extensionVersion = $health.extensionVersion
    lastHeartbeatAtUtc = $health.lastHeartbeatAtUtc
} | ConvertTo-Json -Depth 5
