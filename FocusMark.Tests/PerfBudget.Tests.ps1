$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\PerfBudget.ps1"
$boundary = @{
    startupInputIdleMs=600; firstFocusMs=650; hotSamplesMs=@((1..22) + 200 + 1000)
    idleCpuMs=50; idleWallMs=10000; workingSetBytes=100MB; exeBytes=100KB
}
$result = Get-PerformanceResult $boundary
if (!$result.passed -or $result.measured.hotP50Ms -ne 12 -or $result.measured.hotP95Ms -ne 200) { throw 'Inclusive limits or nearest-rank percentile failed' }
foreach ($metric in $result.limits.Keys) {
    $sample = $boundary.Clone()
    switch ($metric) {
        'hotP95Ms' { $sample.hotSamplesMs = @((1..22) + 201 + 1000) }
        'hotMaxMs' { $sample.hotSamplesMs = @((1..22) + 200 + 1001) }
        'idleCpuPercentOfOneCore' { $sample.idleCpuMs++ }
        default { $sample[$metric]++ }
    }
    $over = Get-PerformanceResult $sample
    if ($over.passed -or $over.exceeded.Count -ne 1 -or $over.exceeded[0] -ne $metric) { throw "Missed regression in $metric" }
}
foreach ($bad in @(@{hotSamplesMs=(1..23)}, @{idleCpuMs=$null}, @{workingSetBytes=[double]::NaN}, @{idleWallMs=9999})) {
    $sample = $boundary.Clone()
    foreach ($key in $bad.Keys) { $sample[$key] = $bad[$key] }
    $rejected = $false
    try { $null = Get-PerformanceResult $sample } catch { $rejected = $true }
    if (!$rejected) { throw 'Incomplete sample was accepted' }
}
Write-Output 'PASS performance budget boundaries, seven independent regressions, four incomplete measurements'
