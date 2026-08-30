[CmdletBinding()]
param(
    [switch] $AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$timeline = @(
    [ordered]@{ phase = 'Free'; minute = 0; mutatedMachine = $false },
    [ordered]@{ phase = 'LastStart'; minute = 35; mutatedMachine = $false },
    [ordered]@{ phase = 'Grace'; minute = 45; mutatedMachine = $false },
    [ordered]@{ phase = 'LandingLocked'; minute = 70; mutatedMachine = $false },
    [ordered]@{ phase = 'Morning'; minute = 525; mutatedMachine = $false }
)

if ($AsJson) {
    $timeline | ConvertTo-Json -Compress
}
else {
    $timeline | ForEach-Object {
        '{0,3} min  {1,-16} mutation={2}' -f
            $_.minute, $_.phase, $_.mutatedMachine
    }
}
