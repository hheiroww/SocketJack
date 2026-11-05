#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [Parameter(Mandatory)][string]$RuntimeDll,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$InputSystemPackage,
    [string]$ExpectedRuntimeSha256,
    [ValidatePattern('^[A-Za-z0-9-]{1,40}$')][string]$RunName='initial'
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$output=[IO.Path]::GetFullPath($OutputDirectory)
$editor=(Resolve-Path -LiteralPath $UnityEditor).Path;$runtime=(Resolve-Path -LiteralPath $RuntimeDll).Path
if($output-eq$repository-or$output.StartsWith($repository+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Acceptance output must remain outside source.'}
$project=Join-Path $output 'project';$ownership=Join-Path $output 'ownership.json'
$editorHash=(Get-FileHash -LiteralPath $editor).Hash.ToLowerInvariant()
if(Test-Path -LiteralPath $output){
    if(!(Test-Path -LiteralPath $ownership)){throw 'Existing output has no matching fixture ownership.'}
    $prior=Get-Content -Raw -LiteralPath $ownership|ConvertFrom-Json
    if($prior.kind-ne'SocketJack.Input.Acceptance/1'-or$prior.project-ne$project-or$prior.sourceCheckout-ne$repository-or$prior.editorSha256-ne$editorHash){throw 'Existing output belongs to another source/Editor/project; retained.'}
}
if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace-lt2GB){throw 'Reserve at least2GiB for this isolated Editor project.'}
$identity=[Reflection.AssemblyName]::GetAssemblyName($runtime);$hash=(Get-FileHash -LiteralPath $runtime).Hash.ToLowerInvariant()
if($identity.Name-ne'SocketJack.Unity'-or[IO.Path]::GetFileName($runtime)-cne'SocketJack.Unity.dll'-or($ExpectedRuntimeSha256-and$ExpectedRuntimeSha256-ne$hash)){throw 'Runtime identity/hash differs.'}
[xml]$coreProject=Get-Content -Raw -LiteralPath (Join-Path $repository 'SocketJack.Unity/SocketJack.Unity.csproj')
$sourceVersion=@($coreProject.Project.PropertyGroup.Version|Where-Object{$_})[0]
if($identity.Version-ne[version]$sourceVersion){throw 'Runtime assembly version differs from the current source release.'}
$package=$null
if($InputSystemPackage){$package=(Resolve-Path -LiteralPath $InputSystemPackage).Path;$description=Get-Content -Raw -LiteralPath (Join-Path $package 'package.json')|ConvertFrom-Json;if($description.name-ne'com.unity.inputsystem'){throw 'Explicit package is not Unity Input System.'}}
$run=Join-Path $output "runs/$RunName";if(Test-Path -LiteralPath $run){throw 'Choose a fresh run name; existing receipts/logs are retained.'}
$log=Join-Path $run 'editor.log';$receipt=Join-Path $run 'receipt.json'
function Assert-Idle{
    $active=@(Get-CimInstance Win32_Process|Where-Object{$_.Name-eq'Unity.exe'-and$_.CommandLine-and$_.CommandLine.Contains($output,[StringComparison]::OrdinalIgnoreCase)})
    if($active.Count){throw "Owned Editor is active; retained PID(s): $($active.ProcessId-join', ')."}
}
Assert-Idle
New-Item -ItemType Directory -Path $run -Force|Out-Null
foreach($folder in @('Assets/Plugins','Assets/SDK/Runtime/Components','Assets/Probe/Editor','Packages','ProjectSettings')){New-Item -ItemType Directory -Path (Join-Path $project $folder) -Force|Out-Null}
[pscustomobject]@{kind='SocketJack.Input.Acceptance/1';project=$project;sourceCheckout=$repository;editor=$editor;editorSha256=$editorHash;runtimeSha256=$hash;inputSystemPackage=$package;retained=$true;noDeletion=$true;syntheticInputOnly=$true}|ConvertTo-Json|Set-Content -LiteralPath $ownership
Copy-Item -LiteralPath $runtime -Destination (Join-Path $project 'Assets/Plugins/SocketJack.Unity.dll')
$manifest=Get-Content -Raw -LiteralPath (Join-Path $repository 'SocketJack.Unity/Unity/package-files.json')|ConvertFrom-Json
foreach($relative in $manifest.files){$target=Join-Path $project "Assets/SDK/Runtime/Components/$relative";New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null;Copy-Item -LiteralPath (Join-Path $repository "SocketJack.Unity/Unity/$relative") -Destination $target}
$sourceHashes=@($manifest.files|ForEach-Object{$path=Join-Path $repository "SocketJack.Unity/Unity/$_";[pscustomobject]@{file=$_;sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}})
$sourceHashes|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'source-hashes.json')
. (Join-Path $repository 'tools/Unity-Distribution.ps1')
foreach($entry in (Get-UnityAssemblyDefinitions $false).GetEnumerator()|Where-Object{$_.Key.StartsWith('Runtime/')}){$target=Join-Path $project ('Assets/SDK/'+$entry.Key);New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null;$entry.Value|Set-Content -LiteralPath $target}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/InputBridgeProbe.cs') -Destination (Join-Path $project 'Assets/Probe/InputBridgeProbe.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/Editor/InputBridgeRunner.cs') -Destination (Join-Path $project 'Assets/Probe/Editor/InputBridgeRunner.cs')
$refs=@('SocketJack.Multiplayer.Components');if($package){$refs+='Unity.InputSystem'}
@{name='SocketJack.Tests.Input';references=$refs;autoReferenced=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $project 'Assets/Probe/SocketJack.Tests.Input.asmdef')
@{name='SocketJack.Tests.Input.Editor';references=@('SocketJack.Tests.Input','SocketJack.Multiplayer.Components');includePlatforms=@('Editor');autoReferenced=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $project 'Assets/Probe/Editor/SocketJack.Tests.Input.Editor.asmdef')
$dependencies=@{'com.unity.modules.animation'='1.0.0';'com.unity.modules.audio'='1.0.0';'com.unity.modules.physics'='1.0.0';'com.unity.modules.physics2d'='1.0.0';'com.unity.modules.imgui'='1.0.0';'com.unity.modules.jsonserialize'='1.0.0';'com.unity.modules.uielements'='1.0.0'}
if($package){$dependencies['com.unity.inputsystem']='file:'+($package.Replace('\','/'))}
@{dependencies=$dependencies}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json')
$version=(Get-Item -LiteralPath $editor).VersionInfo.ProductVersion;if($version-match'\d+\.\d+\.\d+[abfp]\d+'){$version=$Matches[0]}else{throw 'Exact Unity version unavailable.'}
"m_EditorVersion: $version"|Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt')
$handling=if($package){1}else{0}
@"
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!129 &1
PlayerSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 26
  companyName: SocketJackTests
  productName: InputAcceptance
  activeInputHandler: $handling
"@|Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectSettings.asset')
Assert-Idle;$started=[DateTime]::UtcNow
$process=Start-Process -FilePath $editor -ArgumentList @('-batchmode','-nographics','-projectPath',('"'+$project+'"'),'-executeMethod','InputBridgeRunner.Run','-input-receipt',('"'+$receipt+'"'),'-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
Write-Host "Owned Input acceptance Editor PID $($process.Id), log $log"
if(!$process.WaitForExit(600000)){throw "Editor deadline; retained PID $($process.Id), no process killed."}
if($process.ExitCode-ne0-or!(Test-Path -LiteralPath $receipt)-or(Get-Item -LiteralPath $receipt).LastWriteTimeUtc-lt$started-or!(Get-Content -Raw -LiteralPath $log).Contains('SOCKETJACK_INPUT_PASS')){throw 'Input acceptance failed; inspect retained log/receipt.'}
$result=Get-Content -Raw -LiteralPath $receipt|ConvertFrom-Json
if($result.result-ne'PASS'-or!$result.playing-or$result.unityVersion-ne$version){throw 'Fresh actual Play Mode receipt differs.'}
$result|ConvertTo-Json -Depth 5
