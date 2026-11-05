#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [Parameter(Mandatory)][string]$RuntimeDll,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedRuntimeSha256,
    [ValidatePattern('^[A-Za-z0-9-]{1,40}$')][string]$RunName='initial'
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$output=[IO.Path]::GetFullPath($OutputDirectory)
if($output-eq$repository-or$output.StartsWith($repository+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Keep generated output outside source.'}
if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace-lt512MB){throw 'Reserve512MiB for this one project/player.'}
$editor=(Resolve-Path -LiteralPath $UnityEditor).Path;$runtime=(Resolve-Path -LiteralPath $RuntimeDll).Path
if([Reflection.AssemblyName]::GetAssemblyName($runtime).Name-ne'SocketJack.Unity'-or(Get-FileHash -LiteralPath $runtime).Hash-ne$ExpectedRuntimeSha256){throw 'Runtime identity/hash differs.'}
$project=Join-Path $output 'project';$player=Join-Path $output 'player/SocketJackArena.exe'
$ownerPath=Join-Path $output 'ownership.json'
if(Test-Path -LiteralPath $output){
    if(!(Test-Path -LiteralPath $ownerPath)){throw 'Existing output has no capture ownership manifest.'}
    $prior=Get-Content -Raw -LiteralPath $ownerPath|ConvertFrom-Json
    if($prior.kind-ne'SocketJack.GameplayScreenshots/1'-or$prior.project-ne$project-or$prior.sourceCheckout-ne$repository-or$prior.runtimeSha256-ne$ExpectedRuntimeSha256){throw 'Existing project/source/runtime identity differs; retained.'}
}
$run=Join-Path $output "runs/$RunName";if(Test-Path -LiteralPath $run){throw 'Choose a fresh run name; prior capture evidence is retained.'}
function Assert-Idle{$active=@(Get-CimInstance Win32_Process|Where-Object{$_.Name-in@('Unity.exe','SocketJackArena.exe')-and$_.CommandLine-and$_.CommandLine.Contains($output,[StringComparison]::OrdinalIgnoreCase)});if($active.Count){throw 'Owned Editor/player still active; nothing stopped.'}}
Assert-Idle
foreach($folder in @('Assets/Plugins','Assets/SDK/Runtime/Components','Assets/Sample/Editor','Assets/Capture/Editor','Packages','ProjectSettings')){New-Item -ItemType Directory -Path (Join-Path $project $folder) -Force|Out-Null}
New-Item -ItemType Directory -Path (Join-Path $run 'logs'),(Join-Path $run 'metrics') -Force|Out-Null
[pscustomobject]@{kind='SocketJack.GameplayScreenshots/1';sourceCheckout=$repository;project=$project;runtimeSha256=$ExpectedRuntimeSha256;purpose='One actual PhysicsGame host and two autoplay peers, raw D3D11 screenshot';noForeground=$true;syntheticInputOnly=$true;routerMapping=$false;noDeletion=$true;budgetBytes=512MB}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'ownership.json')
Copy-Item -LiteralPath $runtime -Destination (Join-Path $project 'Assets/Plugins/SocketJack.Unity.dll')
$manifest=Get-Content -Raw -LiteralPath (Join-Path $repository 'SocketJack.Unity/Unity/package-files.json')|ConvertFrom-Json
foreach($relative in $manifest.files){$target=Join-Path $project "Assets/SDK/Runtime/Components/$relative";New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null;Copy-Item -LiteralPath (Join-Path $repository "SocketJack.Unity/Unity/$relative") -Destination $target}
Copy-Item -LiteralPath (Join-Path $repository 'Examples/UnityPhysics/Assets/Scripts/PhysicsGame.cs') -Destination (Join-Path $project 'Assets/Sample/PhysicsGame.cs')
Copy-Item -LiteralPath (Join-Path $repository 'Examples/UnityPhysics/Assets/Editor/PhysicsSampleBuild.cs') -Destination (Join-Path $project 'Assets/Sample/Editor/PhysicsSampleBuild.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/ArenaCapture.cs') -Destination (Join-Path $project 'Assets/Capture/ArenaCapture.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/Editor/ArenaCaptureBuild.cs') -Destination (Join-Path $project 'Assets/Capture/Editor/ArenaCaptureBuild.cs')
$dependencies=@{};foreach($name in @('animation','audio','imgui','jsonserialize','physics','physics2d','screencapture','imageconversion')){$dependencies['com.unity.modules.'+$name]='1.0.0'}
@{dependencies=$dependencies}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json')
$version=(Get-Item -LiteralPath $editor).VersionInfo.ProductVersion;if($version-match'\d+\.\d+\.\d+[abfp]\d+'){$version=$Matches[0]}else{throw 'Exact Editor version unavailable.'};"m_EditorVersion: $version"|Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt')
function Quote([string]$value){'"'+$value+'"'}
function Wait-Owned($process,[int]$seconds,[string]$label){if(!$process.WaitForExit($seconds*1000)){throw "$label deadline; retained process, no kill."};if($process.ExitCode-ne0){throw "$label failed; inspect retained logs."}}
Assert-Idle;$started=[DateTime]::UtcNow;$buildLog=Join-Path $run 'logs/build.log'
$build=Start-Process -FilePath $editor -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',(Quote $project),'-executeMethod','SocketJack.Tests.GameplayScreenshots.Editor.ArenaCaptureBuild.Build','-arena-output',(Quote $player),'-logFile',(Quote $buildLog)) -WindowStyle Hidden -PassThru
Write-Host "Owned arena Editor PID $($build.Id)";Wait-Owned $build 360 'Arena build'
if(!(Get-Content -Raw -LiteralPath $buildLog).Contains('SOCKETJACK_ARENA_BUILD_PASS')){throw 'Build PASS marker missing.'}
if((Get-FileHash -LiteralPath (Join-Path $output 'player/SocketJackArena_Data/Managed/SocketJack.Unity.dll')).Hash-ne$ExpectedRuntimeSha256){throw 'Built runtime differs.'}
$image=Join-Path $run 'physics-arena.png';$proof=Join-Path $run 'capture-proof.json';$hostLog=Join-Path $run 'logs/host.log'
$hostArgs=@('-batchmode','-force-d3d11','-screen-width','1920','-screen-height','1080','-screen-fullscreen','0','-host','-autoplay','-no-upnp','-port','0','-duration','18','-metrics',(Quote (Join-Path $run 'metrics/host.json')),'-arena-capture',(Quote $image),'-arena-receipt',(Quote $proof),'-logFile',(Quote $hostLog))
$hostPlayer=Start-Process -FilePath $player -ArgumentList $hostArgs -WindowStyle Hidden -PassThru
$until=[DateTime]::UtcNow.AddSeconds(20);$port=0
while(!$hostPlayer.HasExited-and[DateTime]::UtcNow-lt$until){if(Test-Path -LiteralPath $hostLog){$match=[regex]::Match((Get-Content -Raw -LiteralPath $hostLog),'SOCKETJACK_PHYSICS_ROLE host port=(\d+)');if($match.Success){$port=[int]$match.Groups[1].Value;break}};Start-Sleep -Milliseconds 100;$hostPlayer.Refresh()}
if(!$port){throw 'Host did not publish its bound loopback port; player has its own18s deadline.'}
$peers=@();foreach($index in 1..2){$peers+=Start-Process -FilePath $player -ArgumentList @('-batchmode','-nographics','-peer','-autoplay','-no-upnp','-address','127.0.0.1','-port',$port,'-duration','14','-metrics',(Quote (Join-Path $run "metrics/peer$index.json")),'-logFile',(Quote (Join-Path $run "logs/peer$index.log"))) -WindowStyle Hidden -PassThru}
foreach($process in @($peers)+@($hostPlayer)){Wait-Owned $process 35 'Arena player'}
$capture=Get-Content -Raw -LiteralPath $proof|ConvertFrom-Json
if($capture.result-ne'PASS'-or$capture.visiblePlayers-lt3-or$capture.peers-lt3-or!$capture.routerMappingDisabled-or@($capture.players|Where-Object planarMovement -le .5).Count){throw 'Capture proof did not establish three visible moving admitted players.'}
foreach($name in @('host','peer1','peer2')){$data=Get-Content -Raw -LiteralPath (Join-Path $run "metrics/$name.json")|ConvertFrom-Json;if(!$data.clientConnected-or$data.maxObjects-lt3-or$data.inputsSent-lt1-or$data.automaticPortForwarding-or$data.hardCorrections-ne0){throw "Actual gameplay metrics failed for $name"}}
$files=@(Get-ChildItem -LiteralPath $output -File -Recurse);$size=($files|Measure-Object Length -Sum).Sum
if($size-gt512MB){throw 'Owned capture output exceeded512MiB; retained for review, nothing deleted.'}
[pscustomobject]@{result='PASS';unityVersion=$version;runtimeSha256=$ExpectedRuntimeSha256;image=$image;imageSha256=(Get-FileHash -LiteralPath $image).Hash.ToLowerInvariant();width=$capture.width;height=$capture.height;rawUnityCameraReadback=$true;actualPlayers=3;allPlayersExitedNormally=$true;noForeground=$true;syntheticInputOnly=$true;physicalMicrophoneUsed=$false;files=$files.Count;logicalBytes=$size;retained=$true;noDeletion=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'acceptance.json')
Get-Content -LiteralPath (Join-Path $run 'acceptance.json')
