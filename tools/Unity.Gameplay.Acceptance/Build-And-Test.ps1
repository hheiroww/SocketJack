#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [Parameter(Mandatory)][string]$UnityVersion,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeDll,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedRuntimeSha256,
    [switch]$SkipBuild
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output=[IO.Path]::GetFullPath($OutputDirectory);$project=Join-Path $output 'project'
$editor=(Resolve-Path -LiteralPath $UnityEditor).Path;$runtime=(Resolve-Path -LiteralPath $RuntimeDll).Path
if($output-eq$repository-or$output.StartsWith($repository+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Generated projects and receipts must remain outside source control.'}
if($UnityVersion-notmatch'^\d+\.\d+\.\d+[abfp]\d+$'-or(Get-Item -LiteralPath $editor).VersionInfo.ProductVersion-notlike"$UnityVersion*"){throw 'Requested exact Unity version differs from the selected Editor.'}
$hash=(Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash.ToLowerInvariant()
if($hash-ne$ExpectedRuntimeSha256.ToLowerInvariant()-or[Reflection.AssemblyName]::GetAssemblyName($runtime).Name-ne'SocketJack.Unity'){throw 'Runtime identity/hash differs from the accepted candidate.'}
$editorHash=(Get-FileHash -LiteralPath $editor -Algorithm SHA256).Hash.ToLowerInvariant()
$sourceFiles=@(Get-ChildItem -LiteralPath (Join-Path $repository 'SocketJack.Unity/Unity') -File|Where-Object{$_.Extension-eq'.cs'-or$_.Name-eq'link.xml'})+@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Assets') -Filter '*.cs' -File -Recurse)
$sourceDeclaration=($sourceFiles|Sort-Object FullName|ForEach-Object{[IO.Path]::GetRelativePath($repository,$_.FullName)+':'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash})-join"`n"
$sourceHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourceDeclaration))).ToLowerInvariant()
$ownership=Join-Path $output 'ownership-manifest.json'
if((Test-Path -LiteralPath $project)-and@(Get-ChildItem -LiteralPath $project -Force).Count){
    if(!(Test-Path -LiteralPath $ownership)){throw 'Existing nonempty project has no matching ownership manifest.'}
    $prior=Get-Content -LiteralPath $ownership -Raw|ConvertFrom-Json
    if($prior.kind-ne'SocketJack.Gameplay.Acceptance/1'-or$prior.project-ne$project-or$prior.sourceCheckout-ne$repository-or$prior.editorSha256-ne$editorHash-or$prior.unityVersion-ne$UnityVersion){throw 'Existing project owner/source/Editor differs; retained.'}
}
if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace-lt2GB){throw 'Reserve at least2GiB for this isolated native build.'}
function Assert-Idle {
    $active=@(Get-CimInstance Win32_Process|Where-Object{$_.Name-in@('Unity.exe','SocketJackGameplay.exe')-and$_.CommandLine-and$_.CommandLine.Contains($output,[StringComparison]::OrdinalIgnoreCase)})
    if($active.Count){throw "Owned Editor/player still active; retained PID(s): $($active.ProcessId-join', ')."}
}
Assert-Idle
$runId=[Guid]::NewGuid().ToString('N');$started=[DateTime]::UtcNow
foreach($folder in @('Assets/Plugins','Assets/SocketJack.Unity','Assets/Probe/Editor','Packages','ProjectSettings')){New-Item -ItemType Directory -Path (Join-Path $project $folder) -Force|Out-Null}
foreach($folder in @('player','logs','receipts')){New-Item -ItemType Directory -Path (Join-Path $output $folder) -Force|Out-Null}
[pscustomobject]@{kind='SocketJack.Gameplay.Acceptance/1';project=$project;sourceCheckout=$repository;unityVersion=$UnityVersion;editorSha256=$editorHash;runtimeSha256=$hash;runId=$runId;noDeletion=$true;noProcessKilling=$true}|ConvertTo-Json|Set-Content -LiteralPath $ownership
Copy-Item -LiteralPath $runtime -Destination (Join-Path $project 'Assets/Plugins/SocketJack.Unity.dll')
Get-ChildItem -LiteralPath (Join-Path $repository 'SocketJack.Unity/Unity') -File|Where-Object{$_.Extension-eq'.cs'-or$_.Name-eq'link.xml'}|ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $project 'Assets/SocketJack.Unity')}
foreach($name in @('GameplayProbe.cs','GameplayLifecycle.cs')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Assets/$name") -Destination (Join-Path $project 'Assets/Probe')}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/Editor/GameplayProbeBuild.cs') -Destination (Join-Path $project 'Assets/Probe/Editor')
@{dependencies=@{'com.unity.modules.animation'='1.0.0';'com.unity.modules.audio'='1.0.0';'com.unity.modules.physics'='1.0.0';'com.unity.modules.physics2d'='1.0.0';'com.unity.modules.imgui'='1.0.0';'com.unity.modules.jsonserialize'='1.0.0'}}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json')
"m_EditorVersion: $UnityVersion"|Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt')
function Quote([string]$value){'"'+$value+'"'}
function Wait-Owned($process,[int]$seconds,[string]$label){
    $until=[DateTime]::UtcNow.AddSeconds($seconds)
    while(!$process.HasExited-and[DateTime]::UtcNow-lt$until){Start-Sleep -Milliseconds 300;$process.Refresh()}
    if(!$process.HasExited){throw "$label deadline; retained PID $($process.Id). No process was killed."}
    if($process.ExitCode-ne0){throw "$label failed with exit $($process.ExitCode); inspect retained receipts/logs."}
}
$player=Join-Path $output 'player/SocketJackGameplay.exe';$buildReceipt=Join-Path $output 'player/build-receipt.json'
if(!$SkipBuild){
    Assert-Idle;$buildLog=Join-Path $output "logs/build-$runId.log";$buildAt=[DateTime]::UtcNow
    $build=Start-Process -FilePath $editor -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',(Quote $project),'-executeMethod','GameplayProbeBuild.Build','-gameplayOutput',(Quote $player),'-logFile',(Quote $buildLog)) -WindowStyle Hidden -PassThru
    Write-Output "Owned gameplay Editor PID $($build.Id)";Wait-Owned $build 480 'Gameplay fixture build'
    if((Get-Item -LiteralPath $buildLog).LastWriteTimeUtc-lt$buildAt-or!(Get-Content -LiteralPath $buildLog -Raw).Contains('SOCKETJACK_GAMEPLAY_BUILD_PASS')){throw 'Fresh gameplay build PASS marker missing.'}
    $embedded=Join-Path $output 'player/SocketJackGameplay_Data/Managed/SocketJack.Unity.dll'
    if((Get-FileHash -LiteralPath $embedded -Algorithm SHA256).Hash.ToLowerInvariant()-ne$hash){throw 'Built player embedded a different runtime DLL.'}
    [pscustomobject]@{unityVersion=$UnityVersion;runtimeSha256=$hash;sourceSha256=$sourceHash;builtAtUtc=[DateTime]::UtcNow}|ConvertTo-Json|Set-Content -LiteralPath $buildReceipt
}
$identity=Get-Content -LiteralPath $buildReceipt -Raw|ConvertFrom-Json
if($identity.unityVersion-ne$UnityVersion-or$identity.runtimeSha256-ne$hash-or$identity.sourceSha256-ne$sourceHash){throw 'Retained native player differs from requested runtime/version or current fixture/adapter source.'}
$receipts=@()
foreach($transport in @('Udp','Tcp')){
    Assert-Idle;$case="$transport-$runId";$ready=Join-Path $output "receipts/$case-ready.txt";$lateReady=Join-Path $output "receipts/$case-late-ready.txt"
    $common=@('-batchmode','-nographics','-probe-run-id',$runId,'-probe-transport',$transport,'-probe-runtime-hash',$hash)
    $server=Start-Process -FilePath $player -ArgumentList ($common+@('-probe-role','server','-probe-port','0','-probe-ready',(Quote $ready),'-probe-late-ready',(Quote $lateReady),'-probe-receipt',(Quote (Join-Path $output "receipts/$case-server.json")),'-logFile',(Quote (Join-Path $output "logs/$case-server.log")))) -WindowStyle Hidden -PassThru
    $until=[DateTime]::UtcNow.AddSeconds(20)
    $port=0
    while(!$server.HasExited-and[DateTime]::UtcNow-lt$until){
        if(Test-Path -LiteralPath $ready){try{[int]::TryParse((Get-Content -LiteralPath $ready -Raw),[ref]$port)|Out-Null}catch [IO.IOException]{}}
        if($port-ge1-and$port-le65535){break}
        Start-Sleep -Milliseconds 100;$server.Refresh()
    }
    if($port-lt1-or$port-gt65535){throw 'Owned gameplay server did not publish a valid bound loopback port.'}
    $clients=@()
    foreach($index in @(1,2)){$clients+=Start-Process -FilePath $player -ArgumentList ($common+@('-probe-role','client','-probe-port',$port,'-probe-receipt',(Quote (Join-Path $output "receipts/$case-client$index.json")),'-logFile',(Quote (Join-Path $output "logs/$case-client$index.log")))) -WindowStyle Hidden -PassThru}
    $until=[DateTime]::UtcNow.AddSeconds(35)
    while(!(Test-Path -LiteralPath $lateReady)-and!$server.HasExited-and[DateTime]::UtcNow-lt$until){Start-Sleep -Milliseconds 100;$server.Refresh()}
    if(!(Test-Path -LiteralPath $lateReady)){throw 'Server did not complete gameplay/ownership phases; owned processes retain their75s self-deadline.'}
    $clients+=Start-Process -FilePath $player -ArgumentList ($common+@('-probe-role','client','-probe-late','true','-probe-port',$port,'-probe-receipt',(Quote (Join-Path $output "receipts/$case-client3.json")),'-logFile',(Quote (Join-Path $output "logs/$case-client3.log")))) -WindowStyle Hidden -PassThru
    foreach($client in $clients){Wait-Owned $client 90 'Gameplay client'};Wait-Owned $server 95 'Gameplay server'
    foreach($role in @('server','client1','client2','client3')){
        $receipt=Join-Path $output "receipts/$case-$role.json";$value=Get-Content -LiteralPath $receipt -Raw|ConvertFrom-Json
        $expectedPhase=if($role-eq'server'){9}else{6}
        if((Get-Item -LiteralPath $receipt).LastWriteTimeUtc-lt$started-or$value.runId-ne$runId-or$value.unityVersion-ne$UnityVersion-or$value.runtimeSha256-ne$hash-or$value.result-ne'PASS'-or!$value.playing-or$value.completedPhase-ne$expectedPhase-or!$value.balancedSeen-or!$value.fullSeen-or$value.lifecycle.Count-lt10-or@($value.lifecycle|Where-Object{!$_.valid}).Count){throw "Fresh gameplay acceptance failed for $transport/$role."}
        if($role-eq'server'-and($value.peers-ne3-or$value.sceneReadyEvents-ne6)){throw 'Server did not observe all three peers and both scene-readiness generations.'}
        $receipts+=$value
    }
    Write-Output "PASS gameplay $UnityVersion ${transport}: server + two clients + late joiner, ownership, scenes, pooling and adaptive precision"
}
$files=@(Get-ChildItem -LiteralPath $output -File -Recurse)
[pscustomobject]@{result='PASS';runtimeSha256=$hash;unityVersion=$UnityVersion;runId=$runId;receipts=$receipts;files=$files.Count;logicalBytes=($files|Measure-Object Length -Sum).Sum;retained=$true;noDeletion=$true}|ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $output "acceptance-$runId.json")
