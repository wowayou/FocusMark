# Desktop acceptance budget v1. All times are milliseconds, sizes are bytes.
function Get-PerformanceResult([hashtable]$observed) {
    $limits = [ordered]@{
        startupInputIdleMs = 600
        firstFocusMs       = 650
        hotP95Ms           = 200
        hotMaxMs           = 1000
        idleCpuPercentOfOneCore = 0.5
        workingSetBytes    = 100MB
        exeBytes           = 100KB
    }
    foreach ($key in 'startupInputIdleMs','firstFocusMs','idleWallMs','idleCpuMs','workingSetBytes','exeBytes') {
        $v = $observed[$key]
        if (($v -isnot [int] -and $v -isnot [long] -and $v -isnot [double]) -or
            [double]::IsNaN($v) -or [double]::IsInfinity($v) -or $v -lt 0) { throw "Missing or invalid measurement: $key" }
    }
    if (!$observed.exeBytes -or !$observed.workingSetBytes -or $observed.idleWallMs -lt 10000) { throw 'Incomplete size or idle sample' }
    $samples = @($observed.hotSamplesMs)
    if ($samples.Count -ne 24) { throw 'Expected exactly 24 hotkey samples' }
    foreach ($ms in $samples) {
        if (($ms -isnot [int] -and $ms -isnot [long]) -or $ms -lt 0) { throw 'Invalid hotkey sample' }
    }
    $sorted = @($samples | Sort-Object)
    $metrics = [ordered]@{
        startupInputIdleMs = $observed.startupInputIdleMs
        firstFocusMs = $observed.firstFocusMs
        hotP50Ms = $sorted[11]
        hotP95Ms = $sorted[22] # nearest rank: ceiling(24 * 0.95)
        hotMaxMs = $sorted[23]
        idleCpuPercentOfOneCore = 100.0 * $observed.idleCpuMs / $observed.idleWallMs
        workingSetBytes = $observed.workingSetBytes
        exeBytes = $observed.exeBytes
    }
    $exceeded = @(foreach ($name in $limits.Keys) {
        if ($metrics[$name] -gt $limits[$name]) { $name }
    })
    [pscustomobject]@{budgetVersion=1; passed=($exceeded.Count -eq 0); exceeded=$exceeded; limits=$limits; measured=$metrics; raw=$observed}
}
