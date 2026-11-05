#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [Parameter(Mandatory)][string]$UnityVersion,
    [Parameter(Mandatory)][string]$Physics2DOutputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDll,
    [Parameter(Mandatory)][string]$VoiceDll,
    [Parameter(Mandatory)][string]$ConcentusDll,
    [switch]$AccountSocket,
    [string]$ModuleDirectory,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixture = [IO.Path]::GetFullPath($Physics2DOutputDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $fixture 'project'
$player = Join-Path $fixture 'player/SocketJackPhysics2D.exe'
$editor = (Resolve-Path -LiteralPath $UnityEditor).Path
$ownership = Get-Content -LiteralPath (Join-Path $fixture 'ownership-manifest.json') -Raw | ConvertFrom-Json
if ($ownership.kind -ne 'SocketJack.Physics2D.Acceptance/1' -or $ownership.project -ne $project -or $ownership.sourceCheckout -ne $repository -or $ownership.unityVersion -ne $UnityVersion) { throw 'Only the matching task-owned 2D fixture may be reused.' }
if ($output -eq $repository -or $output.StartsWith($repository + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Receipts/output must remain outside source control.' }
function Assert-Idle {
    $active = @(Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'Unity.exe' -or $_.Name -eq 'SocketJackPhysics2D.exe') -and $_.CommandLine -and $_.CommandLine.Contains($fixture, [StringComparison]::OrdinalIgnoreCase) })
    if ($active.Count) { throw "An owned project/player is active; process IDs $($active.ProcessId -join ', ') were retained." }
}
Assert-Idle
foreach ($name in @('logs','receipts','prior-physics2d-checkpoint')) { New-Item -ItemType Directory -Path (Join-Path $output $name) -Force | Out-Null }
$checkpoint = Join-Path $output 'prior-physics2d-checkpoint/build-receipt.json'
if (!(Test-Path -LiteralPath $checkpoint)) { Copy-Item -LiteralPath (Join-Path $fixture 'player/build-receipt.json') -Destination $checkpoint }
$runtimeHash = (Get-FileHash -LiteralPath $RuntimeDll -Algorithm SHA256).Hash.ToLowerInvariant()
$voiceHash = (Get-FileHash -LiteralPath $VoiceDll -Algorithm SHA256).Hash.ToLowerInvariant()
$opusHash = (Get-FileHash -LiteralPath $ConcentusDll -Algorithm SHA256).Hash.ToLowerInvariant()
$runId = [Guid]::NewGuid().ToString('N')
[ordered]@{ kind='SocketJack.Voice.Acceptance/1'; sourceCheckout=$repository; project=$project; reusedPlayer=$player; receipts=$output; unityVersion=$UnityVersion; runtimeSha256=$runtimeHash; voiceSha256=$voiceHash; opusSha256=$opusHash; runId=$runId; noMicrophoneRecording=$true; noCleanupDeletion=$true; noProcessTermination=$true; preservePhysics2DReceipts=$true } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'ownership-manifest.json')
foreach ($name in @('Assets/Voice','Assets/VoiceProbe','Assets/Editor')) { New-Item -ItemType Directory -Path (Join-Path $project $name) -Force | Out-Null }
Copy-Item -LiteralPath $RuntimeDll -Destination (Join-Path $project 'Assets/Plugins/SocketJack.Unity.dll')
Copy-Item -LiteralPath $VoiceDll -Destination (Join-Path $project 'Assets/Plugins/SocketJack.Unity.Voice.dll')
Copy-Item -LiteralPath $ConcentusDll -Destination (Join-Path $project 'Assets/Plugins/Concentus.dll')
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repository 'SocketJack.Unity/Unity') -Filter '*.cs' -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $project 'Assets/SocketJack.Unity') }
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repository 'SocketJack.Unity.Voice/Unity') -Filter '*.cs' -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $project 'Assets/Voice') }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/VoiceAcceptance.cs') -Destination (Join-Path $project 'Assets/VoiceProbe/VoiceAcceptance.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/Editor/VoiceProbeBuild.cs') -Destination (Join-Path $project 'Assets/Editor/VoiceProbeBuild.cs')
if ($AccountSocket) {
    if (!$ModuleDirectory -or !(Test-Path -LiteralPath $ModuleDirectory -PathType Container)) { throw 'Account voice requires the verified modular DLL directory.' }
    $moduleNames = @('SocketJack.Unity.dll','SocketJack.Unity.Web.Client.dll','SocketJack.Unity.Web.dll','SocketJack.Unity.Voice.dll','SocketJack.Unity.Voice.Web.Client.dll','SocketJack.Unity.Voice.Web.dll','Concentus.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.IO.Pipelines.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Text.Encodings.Web.dll','System.Text.Json.dll')
    foreach ($name in $moduleNames) {
        Copy-Item -LiteralPath (Join-Path $ModuleDirectory $name) -Destination (Join-Path $project "Assets/Plugins/$name")
    }
    if ((Get-FileHash -LiteralPath (Join-Path $project 'Assets/Plugins/SocketJack.Unity.dll') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $runtimeHash -or (Get-FileHash -LiteralPath (Join-Path $project 'Assets/Plugins/SocketJack.Unity.Voice.dll') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $voiceHash) { throw 'Modular DLL source differs from the requested candidate.' }
    foreach ($directory in @('SocketJack.Unity.Web/Unity','SocketJack.Unity.Voice.Web.Client/Unity','SocketJack.Unity.Voice.Web/Unity')) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repository $directory) -Filter '*.cs' -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $project 'Assets/Voice') }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/AccountVoiceAcceptance.cs') -Destination (Join-Path $project 'Assets/VoiceProbe/AccountVoiceAcceptance.cs')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/Editor/AccountVoiceProbeBuild.cs') -Destination (Join-Path $project 'Assets/Editor/AccountVoiceProbeBuild.cs')
    $manifestPath = Join-Path $project 'Packages/manifest.json'; $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $manifest.dependencies['com.unity.modules.unitywebrequest'] = '1.0.0'; $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath
}
function Quote([string]$Value) { return '"' + $Value + '"' }
function Wait-Owned($Process, [int]$Timeout, [string]$Label) {
    $until = [DateTime]::UtcNow.AddSeconds($Timeout)
    while (!$Process.HasExited -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 500; $Process.Refresh() }
    if (!$Process.HasExited) { throw "$Label exceeded its deadline; process $($Process.Id) was retained." }
    if ($Process.ExitCode -ne 0) { throw "$Label failed with exit code $($Process.ExitCode); inspect retained logs." }
}
if (!$SkipBuild) {
    $buildLog = Join-Path $output 'logs/build.log'
    $entryPoint = if ($AccountSocket) { 'SocketJack.Tests.Voice.Editor.AccountVoiceProbeBuild.BuildWindows' } else { 'SocketJack.Tests.Voice.Editor.VoiceProbeBuild.BuildWindows' }
    $build = Start-Process -FilePath $editor -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',(Quote $project),'-executeMethod',$entryPoint,'-socketjackOutput',(Quote $player),'-logFile',(Quote $buildLog)) -WindowStyle Hidden -PassThru
    Wait-Owned $build 420 'Native voice build'
    if ((Get-Content -LiteralPath $buildLog -Raw) -notmatch 'SOCKETJACK_VOICE_BUILD_PASS') { throw 'Native voice build marker missing.' }
}
foreach ($pair in @(@('SocketJack.Unity.dll',$runtimeHash),@('SocketJack.Unity.Voice.dll',$voiceHash),@('Concentus.dll',$opusHash))) {
    if ((Get-FileHash -LiteralPath (Join-Path $fixture "player/SocketJackPhysics2D_Data/Managed/$($pair[0])") -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pair[1]) { throw "Native player assembly differs: $($pair[0])." }
}
$all = @()
if ($AccountSocket) {
    Assert-Idle
    foreach ($name in $moduleNames) {
        $expectedHash = (Get-FileHash -LiteralPath (Join-Path $ModuleDirectory $name) -Algorithm SHA256).Hash
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $fixture "player/SocketJackPhysics2D_Data/Managed/$name") -Algorithm SHA256).Hash
        if ($expectedHash -ne $actualHash) { throw "Account voice native DLL differs from its explicit source: $name." }
    }
    $receiptPath = Join-Path $output "receipts/account-$runId.json"
    $accountData = Join-Path $output 'account-data'; New-Item -ItemType Directory -Path $accountData -Force | Out-Null
    $probe = Start-Process -FilePath $player -ArgumentList @('-batchmode','-nographics','-voice-account-run-id',$runId,'-voice-account-data',(Quote $accountData),'-voice-account-receipt',(Quote $receiptPath),'-logFile',(Quote (Join-Path $output "logs/account-$runId.log"))) -WindowStyle Hidden -PassThru
    Wait-Owned $probe 100 'Native account voice fixture'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.result -ne 'PASS' -or $receipt.runId -ne $runId -or $receipt.unityVersion -ne $UnityVersion -or $receipt.runtimeSha256 -ne $runtimeHash -or $receipt.voiceSha256 -ne $voiceHash -or !$receipt.microphoneNeverOpened -or !$receipt.canonicalSenderIds -or !$receipt.enrolledLeaf -or !$receipt.nativeHostBridge -or !$receipt.reconnectPlayback -or !$receipt.signoutClearsPlayback) { throw 'Native account voice receipt is stale/mismatched or failed.' }
    [ordered]@{ result='PASS'; runId=$runId; unityVersion=$UnityVersion; runtimeSha256=$runtimeHash; voiceSha256=$voiceHash; accountVoice=$receipt; reusedProject=$project; reusedPlayer=$player; processCases=1; nativeRoles=3; cleanupApprovalPending=$true; reclaimedPhysicalBytes=0 } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'acceptance.json')
    Write-Output "PASS nativeaccountvoice $UnityVersion exactUnityhost+twoclientbridges, linkedGlobal/Community, nativeaudioDSP, noMicrophone"
    return
}
foreach ($transport in @('Udp','Tcp')) {
    Assert-Idle
    $readyPath = Join-Path $output "receipts/$transport-$runId-ready.json"
    $common = @('-batchmode','-nographics','-voice-run-id',$runId,'-voice-transport',$transport)
    $server = Start-Process -FilePath $player -ArgumentList ($common + @('-voice-role','server','-voice-port','0','-voice-ready',(Quote $readyPath),'-voice-receipt',(Quote (Join-Path $output "receipts/$transport-$runId-server.json")),'-logFile',(Quote (Join-Path $output "logs/$transport-$runId-server.log")))) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while (!(Test-Path -LiteralPath $readyPath) -and !$server.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100; $server.Refresh() }
    if (!(Test-Path -LiteralPath $readyPath)) { throw 'Native voice server did not publish a loopback port; processes retained.' }
    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if ($ready.runId -ne $runId -or $ready.port -lt 1) { throw 'Stale/invalid voice ready receipt.' }
    $clients = @()
    foreach ($index in @(1,2)) {
        $clients += Start-Process -FilePath $player -ArgumentList ($common + @('-voice-role','client','-voice-port',$ready.port,'-voice-receipt',(Quote (Join-Path $output "receipts/$transport-$runId-client$index.json")),'-logFile',(Quote (Join-Path $output "logs/$transport-$runId-client$index.log")))) -WindowStyle Hidden -PassThru
    }
    foreach ($client in $clients) { Wait-Owned $client 45 'Native voice client' }; Wait-Owned $server 45 'Native voice server'
    foreach ($role in @('server','client1','client2')) {
        $receipt = Get-Content -LiteralPath (Join-Path $output "receipts/$transport-$runId-$role.json") -Raw | ConvertFrom-Json
        if ($receipt.result -ne 'PASS' -or $receipt.runId -ne $runId -or !$receipt.playing -or !$receipt.microphoneNeverOpened -or $receipt.unityVersion -ne $UnityVersion -or $receipt.runtimeSha256 -ne $runtimeHash -or $receipt.voiceSha256 -ne $voiceHash -or $receipt.opusSha256 -ne $opusHash) { throw "Voice acceptance failed/stale for $transport $role." }
        $all += $receipt
    }
    Write-Output "PASS nativevoice $UnityVersion $transport server + two clients, real Opus/audio DSP, no microphone recording"
}
[ordered]@{ result='PASS'; runId=$runId; unityVersion=$UnityVersion; runtimeSha256=$runtimeHash; voiceSha256=$voiceHash; opusSha256=$opusHash; reusedProject=$project; reusedPlayer=$player; processCases=$all.Count; receipts=$all; cleanupApprovalPending=$true; reclaimedPhysicalBytes=0 } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'acceptance.json')
