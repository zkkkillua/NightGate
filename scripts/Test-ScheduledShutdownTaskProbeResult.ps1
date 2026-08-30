[CmdletBinding()]
param(
    [AllowEmptyString()] [string] $ProbePath,
    [datetimeoffset] $MinimumCheckedAtLocal = [datetimeoffset]::MinValue,
    [datetimeoffset] $MaximumCheckedAtLocal = `
        ([datetimeoffset]::Now.AddMinutes(5)),
    [datetimeoffset] $ForbiddenRunOnOrAfterLocal = [datetimeoffset]::MaxValue
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$probe = if ([string]::IsNullOrWhiteSpace($ProbePath)) {
    $localData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    [IO.Path]::GetFullPath((Join-Path $localData `
        'NightGate\Diagnostics\legacy-shutdown-task-evidence.json'))
}
else {
    [IO.Path]::GetFullPath($ProbePath)
}
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) {
    Write-Host "INCONCLUSIVE: scheduled-task probe result is missing: $probe" `
        -ForegroundColor Yellow
    exit 2
}

function Exit-Inconclusive([string] $Message) {
    Write-Host "INCONCLUSIVE: $Message" -ForegroundColor Yellow
    exit 2
}

if ($MaximumCheckedAtLocal -lt $MinimumCheckedAtLocal) {
    Exit-Inconclusive 'the evidence freshness window is invalid.'
}

function Test-ExactPropertySet(
    [AllowNull()] [object] $Value,
    [string[]] $Expected) {
    if ($null -eq $Value) {
        return $false
    }

    [string[]] $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count) {
        return $false
    }

    foreach ($name in $Expected) {
        if ($actual -cnotcontains $name) {
            return $false
        }
    }

    return $true
}

function Test-Token(
    [AllowNull()] [object] $Value,
    [int] $MaximumLength) {
    if ($Value -isnot [string] `
        -or [string]::IsNullOrWhiteSpace($Value) `
        -or $Value.Length -gt $MaximumLength) {
        return $false
    }

    return $Value -cmatch '^[A-Za-z0-9_-]+$'
}

$probeFile = Get-Item -LiteralPath $probe
if ($probeFile.Length -le 0 -or $probeFile.Length -gt (256 * 1024)) {
    Exit-Inconclusive 'scheduled-task evidence has an invalid file size.'
}
$rawProbe = Get-Content -LiteralPath $probe -Raw -Encoding UTF8
$isJson = [IO.Path]::GetExtension($probe) -ieq '.json' `
    -or $rawProbe.TrimStart().StartsWith('{', [StringComparison]::Ordinal)
if ($isJson) {
    try {
        $document = $rawProbe | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Exit-Inconclusive 'scheduled-task evidence JSON is malformed.'
    }

    $rootProperties = @(
        'schemaVersion',
        'probeDateLocal',
        'checkedAtLocal',
        'checkedAtUtc',
        'status',
        'error',
        'tasks')
    if (-not (Test-ExactPropertySet $document $rootProperties)) {
        Exit-Inconclusive 'scheduled-task evidence has an unexpected root shape.'
    }
    if ($document.schemaVersion -isnot [int] -or $document.schemaVersion -ne 1) {
        Exit-Inconclusive 'scheduled-task evidence schemaVersion is unsupported.'
    }
    if ($document.probeDateLocal -isnot [string] `
        -or $document.probeDateLocal -cnotmatch '^\d{4}-\d{2}-\d{2}$') {
        Exit-Inconclusive 'probeDateLocal is invalid.'
    }

    $checkedAt = [datetimeoffset]::MinValue
    $checkedAtUtc = [datetimeoffset]::MinValue
    if ($document.checkedAtLocal -isnot [string] `
        -or -not [datetimeoffset]::TryParse(
            $document.checkedAtLocal,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$checkedAt) `
        -or $document.checkedAtUtc -isnot [string] `
        -or -not [datetimeoffset]::TryParse(
            $document.checkedAtUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$checkedAtUtc) `
        -or $checkedAtUtc.Offset -ne [timespan]::Zero `
        -or $checkedAt.ToUniversalTime() -ne $checkedAtUtc `
        -or $checkedAt.ToString(
            'yyyy-MM-dd',
            [Globalization.CultureInfo]::InvariantCulture) -cne `
            $document.probeDateLocal) {
        Exit-Inconclusive 'checkedAtLocal/UTC or probeDateLocal is inconsistent.'
    }
    if ($checkedAt -lt $MinimumCheckedAtLocal) {
        Exit-Inconclusive ("task evidence is stale; checked at {0:O}, requires {1:O}." -f `
            $checkedAt,
            $MinimumCheckedAtLocal)
    }
    if ($checkedAt -gt $MaximumCheckedAtLocal) {
        Exit-Inconclusive ("task evidence is future-dated; checked at {0:O}, maximum {1:O}." -f `
            $checkedAt,
            $MaximumCheckedAtLocal)
    }
    if ($MinimumCheckedAtLocal -ne [datetimeoffset]::MinValue) {
        $expectedProbeDate = $MinimumCheckedAtLocal.ToString(
            'yyyy-MM-dd',
            [Globalization.CultureInfo]::InvariantCulture)
        if ($document.probeDateLocal -cne $expectedProbeDate) {
            Exit-Inconclusive (
                "probeDateLocal belongs to $($document.probeDateLocal); " +
                "expected $expectedProbeDate.")
        }
    }
    if ($document.status -cnotin @('complete', 'inconclusive')) {
        Exit-Inconclusive 'scheduled-task evidence status is invalid.'
    }
    if ($document.status -ceq 'inconclusive') {
        if (-not (Test-Token $document.error 64)) {
            Exit-Inconclusive 'inconclusive evidence has no valid error token.'
        }

        Exit-Inconclusive "Desktop evidence reported $($document.error)."
    }
    if ($null -ne $document.error) {
        Exit-Inconclusive 'complete evidence unexpectedly contains an error.'
    }

    $taskProperties = @(
        'migrationId',
        'taskPath',
        'actionFingerprint',
        'migrationStatus',
        'identityStatus',
        'enabled',
        'lastRunTimeLocal',
        'lastRunTimeUtc',
        'lastTaskResult')
    [object[]] $tasks = @($document.tasks)
    foreach ($taskItem in $tasks) {
        if (-not (Test-ExactPropertySet $taskItem $taskProperties)) {
            Exit-Inconclusive 'scheduled-task evidence has an unexpected task shape.'
        }
    }

    $expectedTaskPath = ('\{0}{1}{2}{3}' -f `
        [char]0x5B9A,
        [char]0x65F6,
        [char]0x5173,
        [char]0x673A)
    [object[]] $matchingTasks = @($tasks | Where-Object {
        $_.taskPath -ceq $expectedTaskPath
    })
    $failures = [Collections.Generic.List[string]]::new()
    if ($matchingTasks.Count -ne 1) {
        $failures.Add("matchingTaskCount=$($matchingTasks.Count)")
    }
    else {
        $task = $matchingTasks[0]
        if (-not (Test-Token $task.migrationId 128)) {
            Exit-Inconclusive 'migrationId is invalid.'
        }
        if ($task.actionFingerprint -isnot [string] `
            -or $task.actionFingerprint -cnotmatch '^[0-9a-f]{64}$') {
            Exit-Inconclusive 'actionFingerprint is invalid.'
        }
        if ($task.enabled -isnot [bool]) {
            Exit-Inconclusive 'enabled is not a Boolean.'
        }
        if ($task.lastTaskResult -isnot [int] `
            -and $task.lastTaskResult -isnot [long]) {
            Exit-Inconclusive 'lastTaskResult is not an integer.'
        }

        $lastRunLocal = [datetimeoffset]::MinValue
        $lastRunUtc = [datetimeoffset]::MinValue
        if ($task.lastRunTimeLocal -isnot [string] `
            -or -not [datetimeoffset]::TryParse(
                $task.lastRunTimeLocal,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$lastRunLocal) `
            -or $task.lastRunTimeUtc -isnot [string] `
            -or -not [datetimeoffset]::TryParse(
                $task.lastRunTimeUtc,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$lastRunUtc) `
            -or $lastRunUtc.Offset -ne [timespan]::Zero `
            -or $lastRunLocal.ToUniversalTime() -ne $lastRunUtc) {
            Exit-Inconclusive 'lastRunTimeLocal/UTC is invalid or inconsistent.'
        }

        if ($task.migrationStatus -cne 'disabled') {
            $failures.Add("migrationStatus=$($task.migrationStatus)")
        }
        if ($task.identityStatus -cne 'matchingDisabled') {
            $failures.Add("identityStatus=$($task.identityStatus)")
        }
        if ($task.enabled) {
            $failures.Add('enabled=True')
        }
        if ($lastRunLocal -ge $ForbiddenRunOnOrAfterLocal) {
            $failures.Add(("lastRunTime={0:O} reached forbidden boundary {1:O}" -f `
                $lastRunLocal,
                $ForbiddenRunOnOrAfterLocal))
        }
    }

    if ($failures.Count -ne 0) {
        Write-Host 'FAIL: the legacy shutdown task was enabled, changed, duplicated, or ran again.' `
            -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  - $_" }
        exit 1
    }

    Write-Host ("PASS: Desktop evidence confirms the matching task is disabled; " +
        "lastRun={0:O}, lastTaskResult={1}, checkedAt={2:O}." -f `
        $lastRunLocal,
        $matchingTasks[0].lastTaskResult,
        $checkedAt) -ForegroundColor Green
    exit 0
}

$values = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath $probe -Encoding UTF8) {
    $separator = $line.IndexOf('=')
    if ($separator -le 0) {
        continue
    }
    $key = $line.Substring(0, $separator)
    $value = $line.Substring($separator + 1)
    if ($values.ContainsKey($key)) {
        Write-Host "INCONCLUSIVE: duplicate probe field '$key'." `
            -ForegroundColor Yellow
        exit 2
    }
    $values.Add($key, $value)
}

$required = @(
    'matchingTaskCount',
    'identity',
    'checkedAtLocal',
    'path',
    'enabled',
    'lastRunTime',
    'command')
foreach ($key in $required) {
    if (-not $values.ContainsKey($key)) {
        Write-Host "INCONCLUSIVE: probe field '$key' is missing." `
            -ForegroundColor Yellow
        exit 2
    }
}

$checkedAt = [datetimeoffset]::MinValue
if (-not [datetimeoffset]::TryParse(
        $values['checkedAtLocal'],
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$checkedAt)) {
    Write-Host 'INCONCLUSIVE: checkedAtLocal is invalid.' -ForegroundColor Yellow
    exit 2
}
if ($checkedAt -lt $MinimumCheckedAtLocal) {
    Write-Host ("INCONCLUSIVE: task probe is stale; checked at {0:O}, requires {1:O}." -f `
        $checkedAt,
        $MinimumCheckedAtLocal) -ForegroundColor Yellow
    exit 2
}
if ($checkedAt -gt $MaximumCheckedAtLocal) {
    Write-Host ("INCONCLUSIVE: task probe is future-dated; checked at {0:O}, maximum {1:O}." -f `
        $checkedAt,
        $MaximumCheckedAtLocal) -ForegroundColor Yellow
    exit 2
}
if ($MinimumCheckedAtLocal -ne [datetimeoffset]::MinValue -and
    $checkedAt.ToString(
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture) -cne
    $MinimumCheckedAtLocal.ToString(
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture)) {
    Write-Host 'INCONCLUSIVE: task probe belongs to a different local date.' `
        -ForegroundColor Yellow
    exit 2
}

$lastRun = [datetimeoffset]::MinValue
if (-not [datetimeoffset]::TryParse(
        $values['lastRunTime'],
        [Globalization.CultureInfo]::CurrentCulture,
        ([Globalization.DateTimeStyles]::AllowWhiteSpaces -bor `
            [Globalization.DateTimeStyles]::AssumeLocal),
        [ref]$lastRun)) {
    Write-Host 'INCONCLUSIVE: lastRunTime is invalid.' -ForegroundColor Yellow
    exit 2
}

$failures = [Collections.Generic.List[string]]::new()
$expectedTaskPath = ('\{0}{1}{2}{3}' -f `
    [char]0x5B9A,
    [char]0x65F6,
    [char]0x5173,
    [char]0x673A)
if ($values['matchingTaskCount'] -ne '1') {
    $failures.Add("matchingTaskCount=$($values['matchingTaskCount'])")
}
if ($values['path'] -ne $expectedTaskPath) {
    $failures.Add("unexpected path=$($values['path'])")
}
if ($values['enabled'] -ne 'False') {
    $failures.Add("enabled=$($values['enabled'])")
}
if ([IO.Path]::GetFileName($values['command']) -ine 'shutdown.exe') {
    $failures.Add("unexpected command=$($values['command'])")
}
if ($lastRun -ge $ForbiddenRunOnOrAfterLocal) {
    $failures.Add(("lastRunTime={0:O} reached forbidden boundary {1:O}" -f `
        $lastRun,
        $ForbiddenRunOnOrAfterLocal))
}

if ($failures.Count -ne 0) {
    Write-Host 'FAIL: the legacy shutdown task was enabled, changed, duplicated, or ran again.' `
        -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host ("PASS: one matching task remains disabled and last ran at {0:O}; " +
    "probe checked at {1:O}." -f $lastRun, $checkedAt) -ForegroundColor Green
exit 0
