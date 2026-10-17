$ErrorActionPreference = 'Stop'
$report = $PSScriptRoot
$sources = Get-Content -Raw -LiteralPath (Join-Path $report 'source-manifest.json') | ConvertFrom-Json
$dlls = Get-Content -Raw -LiteralPath (Join-Path $report 'dll-freeze.json') | ConvertFrom-Json
$sourceDrift = @($sources | Where-Object { (Get-FileHash -LiteralPath $_.path -Algorithm SHA256).Hash -ne $_.sha256 })
$dllDrift = @($dlls | Where-Object { (Get-FileHash -LiteralPath $_.path -Algorithm SHA256).Hash -ne $_.sha256 })
if ($sourceDrift.Count -ne 0 -or $dllDrift.Count -ne 0) { throw 'Measured source or DLL changed.' }
$core = $dlls[0].sha256
$main = Get-Content -Raw -LiteralPath (Join-Path $report 'results.json') | ConvertFrom-Json
$sustained = Get-Content -Raw -LiteralPath (Join-Path $report 'sustained.json') | ConvertFrom-Json
$adaptive = Get-Content -Raw -LiteralPath (Join-Path $report 'adaptive.json') | ConvertFrom-Json
$fanout = Get-Content -Raw -LiteralPath (Join-Path $report 'fanout.json') | ConvertFrom-Json
foreach ($series in @($main, $sustained, $adaptive, $fanout)) {
    if ($series.coreSha256 -ne $core) { throw 'Measurement series uses a different core.' }
}
if (@($main.harnessSha256, $sustained.harnessSha256, $adaptive.harnessSha256 | Select-Object -Unique).Count -ne 1) { throw 'Endpoint benchmark harness differs between series.' }
if ($main.measurements.Count -ne 440 -or $sustained.measurements.Count -ne 108 -or $adaptive.measurements.Count -ne 130 -or $fanout.measurements.Count -ne 10) { throw 'Incomplete measurement matrix.' }
if ($main.rounds -ne 10 -or $sustained.rounds -ne 6 -or $adaptive.rounds -ne 5 -or $fanout.rounds -ne 5) { throw 'Unexpected round counts.' }
$objects = [long](($main.measurements + $sustained.measurements + $adaptive.measurements | Measure-Object -Property messages -Sum).Sum + ($fanout.measurements | Measure-Object -Property messageCount -Sum).Sum)
if ($objects -ne 131688) { throw "Unexpected verified object count: $objects" }
$random = @($main.measurements + $sustained.measurements | Where-Object { $_.enabled -and $_.workload -in @('random-32k', 'sustained-random-32k') })
if (@($random | Where-Object { $_.encodingAttempts -ne 0 -or $_.learningPasses -ne 0 -or $_.candidateCount -ne 0 -or $_.dictionaryCount -ne 0 }).Count -ne 0) { throw 'Random traffic unexpectedly entered full encoding or exact learning.' }
[xml]$mainTests = Get-Content -Raw -LiteralPath (Join-Path $report 'tests/tests-main.trx')
[xml]$reliableTests = Get-Content -Raw -LiteralPath (Join-Path $report 'tests/tests-reliable.trx')
$passed = [int]$mainTests.TestRun.ResultSummary.Counters.passed + [int]$reliableTests.TestRun.ResultSummary.Counters.passed
$skipped = @($mainTests.TestRun.Results.UnitTestResult | Where-Object outcome -EQ 'NotExecuted').Count + @($reliableTests.TestRun.Results.UnitTestResult | Where-Object outcome -EQ 'NotExecuted').Count
if ($passed -ne 264 -or $skipped -ne 1) { throw 'Final test counters differ.' }
$result = [ordered]@{
    verifiedUtc = [DateTime]::UtcNow.ToString('o')
    sourceFilesVerified = $sources.Count
    sourceDifferences = $sourceDrift.Count
    dllFilesVerified = $dlls.Count
    dllDifferences = $dllDrift.Count
    coreSha256 = $core
    mainPhases = $main.measurements.Count
    sustainedPhases = $sustained.measurements.Count
    adaptivePhases = $adaptive.measurements.Count
    fanoutTrials = $fanout.measurements.Count
    byteVerifiedObjects = $objects
    pairedRoundsAcrossSeries = 108
    passed = $passed
    skipped = $skipped
    browserChecks = 59
    randomMaxEncodingAttempts = ($random | Measure-Object encodingAttempts -Maximum).Maximum
    randomMaxLearningPasses = ($random | Measure-Object learningPasses -Maximum).Maximum
    randomMaxCacheBytes = ($random | Measure-Object cacheBytes -Maximum).Maximum
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $report 'verification.json') -Encoding utf8
$result | ConvertTo-Json -Depth 4
