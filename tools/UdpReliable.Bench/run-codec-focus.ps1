param(
    [Parameter(Mandatory=$true)][string]$BaselineDirectory,
    [Parameter(Mandatory=$true)][string]$OptimizedDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [int]$Runs = 10
)
$ErrorActionPreference = 'Stop'
if ($Runs -lt 10) { throw 'Use at least ten measured rounds.' }
$baseline = (Resolve-Path -LiteralPath $BaselineDirectory).Path
$optimized = (Resolve-Path -LiteralPath $OptimizedDirectory).Path
$baselineHarness = Join-Path $baseline 'UdpReliable.Bench.dll'
$optimizedHarness = Join-Path $optimized 'UdpReliable.Bench.dll'
if ((Get-FileHash -LiteralPath $baselineHarness).Hash -ne (Get-FileHash -LiteralPath $optimizedHarness).Hash) {
    throw 'Both directories must contain the identical codec-focus harness and their respective SocketJack.dll.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
for ($round = 0; $round -lt $Runs; $round++) {
    $order = if ($round % 2 -eq 0) { @('baseline','optimized') } else { @('optimized','baseline') }
    foreach ($variant in $order) {
        $harness = if ($variant -eq 'baseline') { $baselineHarness } else { $optimizedHarness }
        $destination = Join-Path $output "$variant-$round.json"
        if (Test-Path -LiteralPath $destination) { throw "Refusing to replace existing measurement: $destination" }
        & dotnet $harness codec-focus $destination 1 $round
        if ($LASTEXITCODE -ne 0) { throw "Benchmark failed: $variant round $round" }
        Write-Output "Completed $variant round $round"
    }
}
