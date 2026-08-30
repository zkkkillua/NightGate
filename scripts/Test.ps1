[CmdletBinding()]
param(
    [switch] $SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$root = Get-NightGateRepoRoot
$resultsRoot = Join-Path $root 'outputs\test-results'
New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
$runId = '{0}-{1}' -f `
    (Get-Date -Format 'yyyyMMdd-HHmmssfff'),
    ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$results = Join-Path $resultsRoot $runId
New-Item -ItemType Directory -Path $results | Out-Null
$canonicalSummaryPath = Join-Path $resultsRoot 'test-summary.json'
$runSummaryPath = Join-Path $results 'test-summary.json'
$startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
$sourceFingerprint = $null
$dotnetPassed = 0
$dotnetFailed = 0
$dotnetSkipped = 0
$nodePassed = 0
$nodeFailed = 0
$nodeSkipped = 0

function New-TestSummary {
    param(
        [Parameter(Mandatory)] [ValidateSet('running', 'failed', 'completed')]
        [string] $Status,
        [AllowNull()] [string] $CompletedAtUtc,
        [AllowNull()] [string] $SourceFingerprint,
        [AllowNull()] [object] $Failure
    )

    return [ordered]@{
        schemaVersion = 1
        status = $Status
        runId = $runId
        startedAtUtc = $startedAtUtc
        completedAtUtc = $CompletedAtUtc
        sourceFingerprintAlgorithm = 'nightgate-test-source-v1-sha256'
        sourceFingerprint = $SourceFingerprint
        dotnetPassed = $dotnetPassed
        dotnetFailed = $dotnetFailed
        dotnetSkipped = $dotnetSkipped
        nodePassed = $nodePassed
        nodeFailed = $nodeFailed
        nodeSkipped = $nodeSkipped
        failure = $Failure
    }
}

function Publish-TestSummary {
    param(
        [Parameter(Mandatory)] [object] $Summary,
        [switch] $Completed
    )

    if ($Completed) {
        # A canonical success is visible only after its immutable per-run evidence.
        Write-NightGateJsonAtomically -Path $runSummaryPath -Value $Summary
        Write-NightGateJsonAtomically -Path $canonicalSummaryPath -Value $Summary
    }
    else {
        # Invalidate any earlier success before doing more work for this run.
        Write-NightGateJsonAtomically -Path $canonicalSummaryPath -Value $Summary
        Write-NightGateJsonAtomically -Path $runSummaryPath -Value $Summary
    }
}

$runLockPath = Join-Path $resultsRoot 'test-run.lock'
try {
    $runLock = [IO.File]::Open(
        $runLockPath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
}
catch {
    throw 'Another NightGate test run is already in progress.'
}
try {
    $runningSummary = New-TestSummary -Status 'running' `
        -CompletedAtUtc $null -SourceFingerprint $null -Failure $null
    Publish-TestSummary -Summary $runningSummary

    $sourceFingerprint = Get-NightGateTestSourceFingerprint
    $runningSummary = New-TestSummary -Status 'running' `
        -CompletedAtUtc $null `
        -SourceFingerprint $sourceFingerprint `
        -Failure $null
    Publish-TestSummary -Summary $runningSummary

    if (-not $SkipRestore) {
        & (Join-Path $PSScriptRoot 'Restore.ps1')
    }

    Initialize-NightGateBuildEnvironment
    $dotnet = Resolve-NightGateDotNet
    $dotnetArguments = @(
        'test', (Join-Path $root 'NightGate.slnx'),
        '--configuration', 'Release',
        '--no-restore',
        '--results-directory', $results,
        '--logger', 'trx'
    ) + (Get-NightGateBuildProperties) + @('-p:PlatformTarget=x64')
    Invoke-NightGateChecked -Executable $dotnet -Arguments $dotnetArguments

    foreach ($trxFile in Get-ChildItem -LiteralPath $results -Filter '*.trx' -File) {
        [xml]$trx = Get-Content -LiteralPath $trxFile.FullName -Raw -Encoding UTF8
        $counters = $trx.TestRun.ResultSummary.Counters
        $dotnetPassed += [int]$counters.passed
        $dotnetFailed += [int]$counters.failed
        $dotnetSkipped += [int]$counters.notExecuted
    }
    if ($dotnetPassed -eq 0 -or $dotnetFailed -ne 0) {
        throw 'The .NET TRX summary is missing or reports a failure.'
    }

    $node = Resolve-NightGateNode
    $nodeArguments = @(
        '--test',
        '--test-reporter=tap',
        (Join-Path $root 'tests\NightGate.Chrome.Extension.Tests\*.test.mjs'),
        (Join-Path $root 'tests\NightGate.Release.Tests\*.test.mjs')
    )
    $nodeOutput = @(& $node @nodeArguments 2>&1)
    $nodeExitCode = $LASTEXITCODE
    $nodeOutput | ForEach-Object { Write-Host $_ }
    $nodeLog = Join-Path $results 'node-tests.tap'
    [IO.File]::WriteAllLines(
        $nodeLog,
        [string[]]$nodeOutput,
        [Text.UTF8Encoding]::new($false))
    if ($nodeExitCode -ne 0) {
        throw "Node tests failed with exit code $nodeExitCode."
    }
    $nodeText = $nodeOutput -join [Environment]::NewLine
    $nodePassMatch = [regex]::Match($nodeText, '(?m)^# pass\s+(\d+)\s*$')
    $nodeFailMatch = [regex]::Match($nodeText, '(?m)^# fail\s+(\d+)\s*$')
    $nodeSkipMatch = [regex]::Match($nodeText, '(?m)^# skipped\s+(\d+)\s*$')
    if (-not $nodePassMatch.Success -or -not $nodeFailMatch.Success) {
        throw 'Node TAP summary is missing.'
    }
    $nodePassed = [int]$nodePassMatch.Groups[1].Value
    $nodeFailed = [int]$nodeFailMatch.Groups[1].Value
    $nodeSkipped = if ($nodeSkipMatch.Success) {
        [int]$nodeSkipMatch.Groups[1].Value
    }
    else {
        0
    }
    if ($nodePassed -eq 0 -or $nodeFailed -ne 0) {
        throw 'Node TAP summary reports a failure.'
    }

    $finalSourceFingerprint = Get-NightGateTestSourceFingerprint
    if ($finalSourceFingerprint -cne $sourceFingerprint) {
        throw 'The test source tree changed while the test run was in progress.'
    }
    $summary = New-TestSummary -Status 'completed' `
        -CompletedAtUtc ([DateTimeOffset]::UtcNow.ToString('O')) `
        -SourceFingerprint $sourceFingerprint `
        -Failure $null
    Publish-TestSummary -Summary $summary -Completed
    $summary | ConvertTo-Json -Depth 10
}
catch {
    $originalError = $_
    $failure = [ordered]@{
        type = if ($null -eq $originalError.Exception) {
            'System.Management.Automation.ErrorRecord'
        }
        else {
            $originalError.Exception.GetType().FullName
        }
        message = [string]$originalError.Exception.Message
    }
    $failedSummary = New-TestSummary -Status 'failed' `
        -CompletedAtUtc ([DateTimeOffset]::UtcNow.ToString('O')) `
        -SourceFingerprint $sourceFingerprint `
        -Failure $failure
    try {
        Publish-TestSummary -Summary $failedSummary
    }
    catch {
        throw (
            "Tests failed and failure evidence could not be persisted. " +
            "Original failure: $($originalError.Exception.Message). " +
            "Persistence failure: $($_.Exception.Message)")
    }
    throw $originalError
}
finally {
    $runLock.Dispose()
}
