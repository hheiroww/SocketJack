param(
    [Parameter(Mandatory = $true)][string]$Player,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [int]$Port = 7777,
    [ValidateRange(8, 120)][int]$Duration = 20,
    [switch]$HeadlessClients,
    [switch]$OwnerAuthority,
    [ValidateSet('DedicatedServerClient', 'HostPeer')][string]$Topology = 'DedicatedServerClient'
)
$ErrorActionPreference = 'Stop'
$playerPath = (Resolve-Path -LiteralPath $Player).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$runStartedUtc = [DateTime]::UtcNow;
@("Player (preserve): $playerPath", "Owned metrics/logs: $output", 'No deletion or forced process termination is performed.') |
    Set-Content -LiteralPath (Join-Path $output 'ownership-manifest.txt')
$serverMetrics = Join-Path $output 'server.json'
$serverLog = Join-Path $output 'server.log'
$serverRole = if ($Topology -eq 'HostPeer') { '-host' } else { '-server' }
$clientRole = if ($Topology -eq 'HostPeer') { '-peer' } else { '-client' }
$authorityArguments = if ($OwnerAuthority) { @('-owner-authority') } else { @() }
$serverArguments = @('-batchmode', '-nographics', $serverRole, '-no-upnp', '-port', $Port,
    '-duration', ($Duration + 4), '-metrics', ('"' + $serverMetrics + '"'), '-logFile', ('"' + $serverLog + '"')) + $authorityArguments
$server = Start-Process -FilePath $playerPath -ArgumentList $serverArguments -WindowStyle Hidden -PassThru
Write-Host "Actual Unity server started: PID $($server.Id); log $serverLog"
$deadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    if ($server.HasExited) { throw "Server exited before startup. Inspect $serverLog" }
    if ((Test-Path -LiteralPath $serverLog) -and (Get-Item -LiteralPath $serverLog).LastWriteTimeUtc -ge $runStartedUtc -and
        (Select-String -LiteralPath $serverLog -SimpleMatch ("SOCKETJACK_PHYSICS_ROLE " + $serverRole.TrimStart('-')) -Quiet)) { break }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $deadline)
if ([DateTime]::UtcNow -ge $deadline) { throw "Server readiness timed out; retained PID $($server.Id)." }
$clients = @()
foreach ($index in 1..2) {
    $metrics = Join-Path $output "client-$index.json"
    $clientLog = Join-Path $output "client-$index.log"
    $arguments = @($clientRole, '-no-upnp', '-address', '127.0.0.1', '-port', $Port, '-autoplay', '-duration', $Duration,
        '-metrics', ('"' + $metrics + '"'), '-logFile', ('"' + $clientLog + '"')) + $authorityArguments
    if ($HeadlessClients) { $arguments = @('-batchmode', '-nographics') + $arguments }
    $style = if ($HeadlessClients) { 'Hidden' } else { 'Normal' }
    $clientProcess = Start-Process -FilePath $playerPath -ArgumentList $arguments -WindowStyle $style -PassThru
    $clients += $clientProcess
    Write-Host "Actual Unity client $index started: PID $($clientProcess.Id); log $clientLog"
}
foreach ($process in ($clients + @($server))) {
    if (!$process.WaitForExit(($Duration + 40) * 1000)) { throw "Player timed out; retained PID $($process.Id)." }
    if ($process.ExitCode -ne 0) { throw "Unity player exited with code $($process.ExitCode). Logs remain in $output" }
}
$results = @()
foreach ($name in @('server.json', 'client-1.json', 'client-2.json')) {
    $file = Join-Path $output $name
    if (!(Test-Path -LiteralPath $file)) { throw "Missing actual Unity metrics: $file" }
    if ((Get-Item -LiteralPath $file).LastWriteTimeUtc -lt $runStartedUtc) { throw "Stale metrics from a previous run: $file" }
    $results += Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
}
$serverResult = $results[0]
if ($serverResult.automaticPortForwarding -or $serverResult.serverAuthority -eq $OwnerAuthority.IsPresent -or $serverResult.topology -ne $Topology) { throw 'Actual listening role topology/authority/no-UPnP configuration did not match the requested test.' }
if ($OwnerAuthority -and $serverResult.keyframesReceived -lt 10) { throw 'The listening host/server did not apply canonical owner keyframes.' }
foreach ($client in $results[1..2]) {
    if ($client.automaticPortForwarding -or $client.serverAuthority -eq $OwnerAuthority.IsPresent -or $client.topology -ne $Topology) { throw 'Actual Unity player topology/authority/no-UPnP configuration did not match the requested test.' }
    if ($OwnerAuthority -and $client.ownerStatesSent -lt 10) { throw 'Owner-authoritative clients did not send canonical owner keyframes.' }
    if (!$client.clientConnected -or $client.maxObjects -lt 2 -or $client.inputsSent -lt 10 -or $client.inputsReceived -lt 10 -or $client.keyframesReceived -lt 10) {
        throw 'Unity clients did not demonstrate input relay, local physics and sparse correction reception.'
    }
    if ($client.hardCorrections -gt 0 -or $client.meanPositionError -gt 0.5 -or $client.peakPositionError -gt 2) {
        throw 'Unity correction metrics exceeded the loopback acceptance limits. Inspect JSON and visible gameplay.'
    }
    if ($client.roundTrips -lt 5 -or $client.rttP95Milliseconds -gt 100) { throw 'Actual Unity player RTT exceeded the loopback acceptance limit or had insufficient samples.' }
}
if ($serverResult.inputsReceived -lt 20 -or $serverResult.maxConnectedPeers -lt 2) { throw 'Server did not observe both owners sending input.' }
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'results.json')
Write-Host "Actual Unity physics players passed loopback checks. Metrics: $output\results.json"
