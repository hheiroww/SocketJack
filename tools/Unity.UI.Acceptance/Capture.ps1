#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][string]$ModuleDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedRuntimeSha256
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture=[IO.Path]::GetFullPath($FixtureDirectory);$output=[IO.Path]::GetFullPath($OutputDirectory)
$project=Join-Path $fixture 'project';$player=Join-Path $fixture 'player/SocketJackPhysics2D.exe'
$editor=(Resolve-Path -LiteralPath $UnityEditor).Path;$modules=(Resolve-Path -LiteralPath $ModuleDirectory).Path
$owner=Get-Content -LiteralPath (Join-Path $fixture 'ownership-manifest.json') -Raw|ConvertFrom-Json
if($owner.kind -ne 'SocketJack.Physics2D.Acceptance/1' -or $owner.project -ne $project -or $owner.sourceCheckout -ne $repository){throw 'Only the explicitly owned acceptance project/player may be reused.'}
if($output -eq $repository -or $output.StartsWith($repository+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Capture staging must remain outside source control.'}
if((Get-PSDrive -Name ([IO.Path]::GetPathRoot($output).Substring(0,1))).Free -lt 512MB){throw 'Reserve at least 512 MiB for the reused player and three PNGs.'}
$active=@(Get-CimInstance Win32_Process|Where-Object{($_.Name -eq 'Unity.exe' -or $_.Name -eq 'SocketJackPhysics2D.exe') -and $_.CommandLine -and $_.CommandLine.Contains($fixture,[StringComparison]::OrdinalIgnoreCase)})
if($active.Count){throw 'An owned project/player is active; nothing was stopped.'}
$names=@('SocketJack.Unity.dll','SocketJack.Unity.Web.Client.dll','SocketJack.Unity.Web.dll','SocketJack.Unity.Voice.dll','SocketJack.Unity.Voice.Web.Client.dll','SocketJack.Unity.Voice.Web.dll','Concentus.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.IO.Pipelines.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Text.Encodings.Web.dll','System.Text.Json.dll')
foreach($name in $names){if(!(Test-Path -LiteralPath (Join-Path $modules $name))){throw "Missing allowed module: $name"}}
if((Get-FileHash -LiteralPath (Join-Path $modules 'SocketJack.Unity.dll')).Hash -ne $ExpectedRuntimeSha256){throw 'Runtime differs from reviewed input.'}
if(Test-Path -LiteralPath (Join-Path $project 'Assets/Plugins/System.Numerics.Vectors.dll')){throw 'Duplicate Unity framework Vectors DLL retained; use the reviewed module allowlist.'}
$run=Join-Path $output ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'));New-Item -ItemType Directory -Path $run -Force|Out-Null
$inputHashes=@($names|ForEach-Object{[ordered]@{name=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $modules $_)).Hash.ToLowerInvariant()}})
[ordered]@{purpose='Actual rendered SDK chat/social/voice documentation screenshots';project=$project;reusedPlayer=$player;inputs=$inputHashes;noPhysicalMicrophone=$true;noForegroundOrMouse=$true;noCleanupDeletion=$true;rebuildImpact='Reuses the owned player executable; run existing Physics2D/Voice build tools to restore their previous entry scene. Prior receipts and reviewed module inputs remain.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $run 'ownership-manifest.json') -Encoding utf8NoBOM
$oldReceipt=Join-Path $fixture 'player/build-receipt.json';if(Test-Path -LiteralPath $oldReceipt){Copy-Item -LiteralPath $oldReceipt -Destination (Join-Path $run 'prior-player-receipt.json')}
foreach($directory in @('Assets/UiCapture','Assets/Editor','Assets/VoiceProbe','Assets/Voice')){New-Item -ItemType Directory -Path (Join-Path $project $directory) -Force|Out-Null}
foreach($name in $names){Copy-Item -LiteralPath (Join-Path $modules $name) -Destination (Join-Path $project ('Assets/Plugins/'+$name))}
foreach($module in @('SocketJack.Unity.Web','SocketJack.Unity.Voice','SocketJack.Unity.Voice.Web.Client','SocketJack.Unity.Voice.Web')){foreach($file in Get-ChildItem -LiteralPath (Join-Path $repository ($module+'/Unity')) -Filter '*.cs' -File){Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $project 'Assets/Voice')}}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $repository 'SocketJack.Unity/Unity') -Filter '*.cs' -File){Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $project 'Assets/SocketJack.Unity')}
Copy-Item -LiteralPath (Join-Path $repository 'tools/Unity.Voice.Acceptance/Assets/AccountVoiceAcceptance.cs') -Destination (Join-Path $project 'Assets/VoiceProbe/AccountVoiceAcceptance.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/RuntimeUiCapture.cs') -Destination (Join-Path $project 'Assets/UiCapture/RuntimeUiCapture.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/Editor/RuntimeUiCaptureBuild.cs') -Destination (Join-Path $project 'Assets/Editor/RuntimeUiCaptureBuild.cs')
$manifestPath=Join-Path $project 'Packages/manifest.json';$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json -AsHashtable
foreach($module in @('com.unity.modules.screencapture','com.unity.modules.imageconversion','com.unity.modules.unitywebrequest')){$manifest.dependencies[$module]='1.0.0'}
$manifest|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
function Wait-Owned($process,[int]$seconds,[string]$label){$until=[DateTime]::UtcNow.AddSeconds($seconds);while(!$process.WaitForExit(500)){if([DateTime]::UtcNow -ge $until){throw "$label exceeded deadline; process retained."}};if($process.ExitCode -ne 0){throw "$label failed; retained logs in $run"}}
$buildLog=Join-Path $run 'build.log'
$build=Start-Process -FilePath $editor -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','SocketJack.Acceptance.UI.Editor.RuntimeUiCaptureBuild.BuildWindows','-socketjackOutput',('"'+$player+'"'),'-logFile',('"'+$buildLog+'"')) -WindowStyle Hidden -PassThru
Write-Host "UI capture build Editor PID $($build.Id); $buildLog";Wait-Owned $build 420 'UI capture build'
if(!(Get-Content -LiteralPath $buildLog -Raw).Contains('SOCKETJACK_UI_CAPTURE_BUILD_PASS')){throw 'UI build marker missing.'}
foreach($entry in $inputHashes){if((Get-FileHash -LiteralPath (Join-Path $fixture ('player/SocketJackPhysics2D_Data/Managed/'+$entry.name))).Hash -ne $entry.sha256){throw ('Built input mismatch: '+$entry.name)}}
$data=Join-Path $run 'synthetic-account-data';$images=Join-Path $run 'images';New-Item -ItemType Directory -Path $data,$images -Force|Out-Null
$accountReceipt=Join-Path $run 'account-acceptance.json';$log=Join-Path $run 'player.log';$id=[Guid]::NewGuid().ToString('N')
# Graphics stays enabled. The built player runs hidden in batch mode and uses only Unity ScreenCapture.
$process=Start-Process -FilePath $player -ArgumentList @('-batchmode','-force-d3d11','-screen-width','1440','-screen-height','840','-voice-account-run-id',$id,'-voice-account-data',('"'+$data+'"'),'-voice-account-receipt',('"'+$accountReceipt+'"'),'-ui-capture-output',('"'+$images+'"'),'-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
Write-Host "Hidden graphics UI player PID $($process.Id); $log";Wait-Owned $process 90 'UI capture player'
$account=Get-Content -LiteralPath $accountReceipt -Raw|ConvertFrom-Json;$capture=Get-Content -LiteralPath (Join-Path $images 'capture-receipt.json') -Raw|ConvertFrom-Json
if($account.result -ne 'PASS' -or !$account.microphoneNeverOpened -or !$account.reconnectPlayback -or !$account.signoutClearsPlayback){throw 'The existing exact account/voice acceptance did not pass alongside capture.'}
if($capture.result -ne 'pass' -or $capture.screenshots.Count -ne 3 -or $capture.physicalMicrophoneUsed -or $capture.renderer -eq 'Null'){throw 'Actual rendered UI capture did not pass.'}
[ordered]@{result='pass';run=$run;capture=$capture;accountAcceptance=$account;inputs=$inputHashes;reviewRequired='Inspect raw PNGs for privacy and readability before copying to public documentation.'}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $run 'ui-capture-acceptance.json') -Encoding utf8NoBOM
Write-Host "SOCKETJACK_UI_CAPTURE_PASS $run"
