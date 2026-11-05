#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [Parameter(Mandatory)][string]$UnityVersion,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDll,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedRuntimeSha256,
    [ValidateRange(10,60)][int]$Seconds = 20,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $output 'project'
$editor = (Resolve-Path -LiteralPath $UnityEditor).Path
$runtime = (Resolve-Path -LiteralPath $RuntimeDll).Path
$runtimeHash = (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash.ToLowerInvariant()
if ($runtimeHash -ne $ExpectedRuntimeSha256.ToLowerInvariant()) { throw 'Candidate runtime SHA-256 differs from the expected value.' }
if ($UnityVersion -notmatch '^\d+\.\d+\.\d+[abfp]\d+$' -or (Get-Item -LiteralPath $editor).VersionInfo.ProductVersion -notlike "$UnityVersion*") { throw 'Requested version differs from the installed Unity Editor.' }
if ($output -eq $repository -or $output.StartsWith($repository + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Generated projects must remain outside source control.' }
$driveName = [IO.Path]::GetPathRoot($output).TrimEnd('\').TrimEnd(':')
if ((Get-PSDrive -Name $driveName).Free -lt 2GB) { throw 'At least 2 GiB free staging space is required.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$ownershipPath = Join-Path $output 'ownership-manifest.json'
if ((Test-Path -LiteralPath $project) -and @(Get-ChildItem -LiteralPath $project -Force).Count -gt 0) {
    if (!(Test-Path -LiteralPath $ownershipPath -PathType Leaf)) { throw 'Existing project lacks this fixture ownership manifest; retained.' }
    $prior = Get-Content -LiteralPath $ownershipPath -Raw | ConvertFrom-Json
    if ($prior.kind -ne 'SocketJack.Physics2D.Acceptance/1' -or $prior.project -ne $project -or $prior.sourceCheckout -ne $repository -or $prior.unityVersion -ne $UnityVersion) { throw 'Existing project ownership/version differs; retained.' }
}
function Assert-Idle {
    $active = @(Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'Unity.exe' -or $_.Name -eq 'SocketJackPhysics2D.exe') -and $_.CommandLine -and $_.CommandLine.Contains($output, [StringComparison]::OrdinalIgnoreCase) })
    if ($active.Count) { throw "An owned Editor/player is active; retained process IDs: $($active.ProcessId -join ', ')." }
}
Assert-Idle
$runId = [Guid]::NewGuid().ToString('N')
[ordered]@{ kind='SocketJack.Physics2D.Acceptance/1'; project=$project; sourceCheckout=$repository; unityVersion=$UnityVersion; unityEditor=$editor; runtimeSource=$runtime; runtimeSha256=$runtimeHash; runId=$runId; noCleanupDeletion=$true; noProcessTermination=$true } |
    ConvertTo-Json | Set-Content -LiteralPath $ownershipPath
foreach ($directory in @('Assets/Plugins','Assets/SocketJack.Unity','Assets/Editor','Assets/Probe','Packages','ProjectSettings')) { New-Item -ItemType Directory -Path (Join-Path $project $directory) -Force | Out-Null }
foreach ($directory in @('player','logs','receipts')) { New-Item -ItemType Directory -Path (Join-Path $output $directory) -Force | Out-Null }
Copy-Item -LiteralPath $runtime -Destination (Join-Path $project 'Assets/Plugins/SocketJack.Unity.dll')
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repository 'SocketJack.Unity/Unity') -Filter '*.cs' -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $project 'Assets/SocketJack.Unity') }
Copy-Item -LiteralPath (Join-Path $repository 'SocketJack.Unity/Unity/link.xml') -Destination (Join-Path $project 'Assets/SocketJack.Unity/link.xml')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/Physics2DAcceptance.cs') -Destination (Join-Path $project 'Assets/Probe/Physics2DAcceptance.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/Editor/Physics2DProbeBuild.cs') -Destination (Join-Path $project 'Assets/Editor/Physics2DProbeBuild.cs')
@{ dependencies = @{ 'com.unity.modules.physics'='1.0.0'; 'com.unity.modules.physics2d'='1.0.0'; 'com.unity.modules.imgui'='1.0.0'; 'com.unity.modules.animation'='1.0.0'; 'com.unity.modules.audio'='1.0.0'; 'com.unity.modules.jsonserialize'='1.0.0' } } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json')
Copy-Item -LiteralPath (Join-Path $repository 'Examples/UnityPhysics/ProjectSettings/ProjectSettings.asset') -Destination (Join-Path $project 'ProjectSettings/ProjectSettings.asset')
"m_EditorVersion: $UnityVersion" | Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt')
function Quote([string]$Value) { return '"' + $Value + '"' }
function Wait-Owned($Process, [int]$TimeoutSeconds, [string]$Label) {
    $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (!$Process.HasExited -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 500; $Process.Refresh() }
    if (!$Process.HasExited) { throw "$Label exceeded its deadline; process $($Process.Id) was retained." }
    if ($Process.ExitCode -ne 0) { throw "$Label failed with exit code $($Process.ExitCode); inspect retained logs." }
}
$player = Join-Path $output 'player/SocketJackPhysics2D.exe'
if (!$SkipBuild) {
    $buildStarted = [DateTime]::UtcNow
    $buildLog = Join-Path $output 'logs/build.log'
    $build = Start-Process -FilePath $editor -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',(Quote $project),'-executeMethod','SocketJack.Tests.Physics2D.Editor.Physics2DProbeBuild.BuildWindows','-socketjackOutput',(Quote $player),'-logFile',(Quote $buildLog)) -WindowStyle Hidden -PassThru
    Wait-Owned $build 420 'Native 2D build'
    if ((Get-Item -LiteralPath $buildLog).LastWriteTimeUtc -lt $buildStarted -or (Get-Content -LiteralPath $buildLog -Raw) -notmatch 'SOCKETJACK_PHYSICS2D_BUILD_PASS modes=8') { throw 'Fresh 2D build PASS marker missing.' }
}
$buildReceipt = Get-Content -LiteralPath (Join-Path $output 'player/build-receipt.json') -Raw | ConvertFrom-Json
if ($buildReceipt.result -ne 'PASS' -or $buildReceipt.runtimeSha256 -ne $runtimeHash -or $buildReceipt.unityVersion -ne $UnityVersion) { throw 'Native build receipt differs from the candidate.' }
$all = @()
foreach ($transport in @('Udp','Tcp')) {
    foreach ($authority in @('server','owner')) {
        Assert-Idle
        $case = "$transport-$authority-$runId"
        $readyPath = Join-Path $output "receipts/$case-ready.json"
        $serverReceipt = Join-Path $output "receipts/$case-server.json"
        $arguments = @('-batchmode','-nographics','-probe-run-id',$runId,'-probe-duration',$Seconds,'-probe-transport',$transport,'-probe-authority',$authority,'-probe-runtime-hash',$runtimeHash)
        $server = Start-Process -FilePath $player -ArgumentList ($arguments + @('-probe-role','server','-probe-port','0','-probe-ready',(Quote $readyPath),'-probe-receipt',(Quote $serverReceipt),'-logFile',(Quote (Join-Path $output "logs/$case-server.log")))) -WindowStyle Hidden -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        while (!(Test-Path -LiteralPath $readyPath) -and !$server.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100; $server.Refresh() }
        if (!(Test-Path -LiteralPath $readyPath)) { throw '2D server did not publish its loopback port; all processes retained.' }
        $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
        if ($ready.runId -ne $runId -or $ready.port -lt 1) { throw 'Invalid readiness receipt.' }
        $clients = @()
        foreach ($index in @(1,2)) {
            $clientReceipt = Join-Path $output "receipts/$case-client$index.json"
            $clients += Start-Process -FilePath $player -ArgumentList ($arguments + @('-probe-role','client','-probe-port',$ready.port,'-probe-receipt',(Quote $clientReceipt),'-logFile',(Quote (Join-Path $output "logs/$case-client$index.log")))) -WindowStyle Hidden -PassThru
        }
        foreach ($client in $clients) { Wait-Owned $client ($Seconds + 40) '2D client' }
        Wait-Owned $server ($Seconds + 45) '2D server'
        foreach ($role in @('server','client1','client2')) {
            $receipt = Get-Content -LiteralPath (Join-Path $output "receipts/$case-$role.json") -Raw | ConvertFrom-Json
            if ($receipt.result -ne 'PASS' -or !$receipt.playing -or $receipt.runId -ne $runId -or $receipt.runtimeSha256 -ne $runtimeHash -or $receipt.unityVersion -ne $UnityVersion -or $receipt.spawned -ne 4 -or $receipt.objects.Count -ne 4 -or $receipt.seconds -lt $Seconds) { throw "2D acceptance failed/mismatched for $transport $authority $role." }
            $all += $receipt
        }
        Write-Output "PASS native2D $UnityVersion $transport $authority server + two clients"
    }
}
$files = @(Get-ChildItem -LiteralPath $output -File -Recurse)
[ordered]@{ result='PASS'; unityVersion=$UnityVersion; runtimeSha256=$runtimeHash; runId=$runId; scenarios=4; processCases=$all.Count; logicalBytes=($files | Measure-Object Length -Sum).Sum; files=$files.Count; receipts=$all; cleanupApprovalPending=$true; reclaimedPhysicalBytes=0 } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'acceptance.json')
Write-Output "Physics2D acceptance passed; retained owned outputs: $output"
