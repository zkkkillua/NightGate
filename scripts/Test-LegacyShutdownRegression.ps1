[CmdletBinding()]
param(
    [datetime]$Since = (Get-Date).Date,
    [datetime]$Until = (Get-Date),
    [int]$WindowStartMinute = 9,
    [int]$WindowEndMinute = 11
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Until -lt $Since) {
    throw 'Until must be greater than or equal to Since.'
}

if ($WindowStartMinute -lt 0 -or $WindowStartMinute -gt 59 -or
    $WindowEndMinute -lt 0 -or $WindowEndMinute -gt 59 -or
    $WindowEndMinute -lt $WindowStartMinute) {
    throw 'The shutdown regression minute window is invalid.'
}

function Test-NoMatchingEventsError {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)

    return $ErrorRecord.FullyQualifiedErrorId -like 'NoMatchingEventsFound*'
}

try {
    $events = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'System'
        Id        = @(1074, 1075)
        StartTime = $Since
        EndTime   = $Until
    } -ErrorAction Stop)
}
catch {
    if (Test-NoMatchingEventsError -ErrorRecord $_) {
        $events = @()
    }
    else {
        Write-Host (
            'INCONCLUSIVE: Windows System shutdown events could not be read: ' +
            $_.Exception.Message) -ForegroundColor Yellow
        exit 2
    }
}

$events = @($events |
    Where-Object {
        $_.TimeCreated.Hour -eq 0 -and
        $_.TimeCreated.Minute -ge $WindowStartMinute -and
        $_.TimeCreated.Minute -le $WindowEndMinute
    } |
    Sort-Object TimeCreated)

$taskEvents = @()
try {
    $taskEvents = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'Microsoft-Windows-TaskScheduler/Operational'
        Id        = @(100, 102, 107, 129, 200, 201, 202)
        StartTime = $Since
        EndTime   = $Until
    } -ErrorAction Stop |
        Where-Object {
            $_.TimeCreated.Hour -eq 0 -and
            $_.TimeCreated.Minute -ge $WindowStartMinute -and
            $_.TimeCreated.Minute -le $WindowEndMinute
        } |
        Sort-Object TimeCreated)
}
catch {
    if (-not (Test-NoMatchingEventsError -ErrorRecord $_)) {
        Write-Host (
            'WARN: Task Scheduler correlation events were unavailable: ' +
            $_.Exception.Message) -ForegroundColor Yellow
    }
}

if ($events) {
    Write-Host 'FAIL: A Windows shutdown request still occurred around 00:10.' -ForegroundColor Red
    $events |
        Select-Object TimeCreated, Id, ProviderName, RecordId, Message |
        Format-List
    if ($taskEvents) {
        Write-Host 'Task Scheduler events from the same window:' -ForegroundColor Yellow
        $taskEvents |
            Select-Object TimeCreated, Id, ProviderName, RecordId, Message |
            Format-List
    }
    exit 1
}

Write-Host 'PASS: No Windows shutdown request was found around 00:10.' -ForegroundColor Green
if ($taskEvents) {
    Write-Host 'Task Scheduler events from the same window (informational):'
    $taskEvents |
        Select-Object TimeCreated, Id, ProviderName, RecordId, Message |
        Format-List
}
exit 0
