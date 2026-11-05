#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [Parameter(Mandatory)][string]$Browser,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$RuntimeDll,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedRuntimeSha256,
    [switch]$UseRelay,
    [ValidateRange(1024,65535)][int]$GamePort = 19875,
    [ValidateRange(1024,65535)][int]$HttpPort = 19876,
    [ValidatePattern('^[A-Za-z0-9-]{1,40}$')][string]$RunName = 'initial'
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
$editor=(Resolve-Path -LiteralPath $UnityEditor).Path
$browserPath=(Resolve-Path -LiteralPath $Browser).Path
$project=Join-Path $output 'project'
$run=Join-Path $output "runs\$RunName"
if($output -eq $repository -or $output.StartsWith($repository+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an owned output directory outside source.'}
if(Test-Path -LiteralPath $run){throw 'This run name already has retained outputs; choose a new bounded run name.'}
$engine=Join-Path (Split-Path -Parent $editor) 'Data\PlaybackEngines\WebGLSupport'
if(!(Test-Path -LiteralPath $engine)){throw 'The selected Editor does not have its matching WebGL module installed.'}
$editorHash=(Get-FileHash -LiteralPath $editor -Algorithm SHA256).Hash.ToLowerInvariant()
$ownership=Join-Path $output 'ownership-manifest.json'
if((Test-Path -LiteralPath $project) -and @(Get-ChildItem -LiteralPath $project -Force).Count -gt 0){
    if(!(Test-Path -LiteralPath $ownership)){throw 'Existing project has no matching ownership manifest.'}
    $prior=Get-Content -LiteralPath $ownership -Raw|ConvertFrom-Json
    if($prior.kind-ne'SocketJack.WebGL.Acceptance/1'-or$prior.project-ne$project-or$prior.sourceCheckout-ne$repository-or$prior.editorSha256-ne$editorHash){throw 'Existing project belongs to a different source/Editor/owner.'}
}
if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace-lt5GB){throw 'Reserve at least5GiB for isolated WebGL compilation and browser outputs.'}
function Assert-Idle {
    $active=@(Get-CimInstance Win32_Process|Where-Object{$_.Name-in@('Unity.exe','dotnet.exe','node.exe','chrome.exe','chrome-headless-shell.exe')-and$_.CommandLine-and$_.CommandLine.Contains($output,[StringComparison]::OrdinalIgnoreCase)})
    if($active.Count){throw "An owned Editor, server or browser is still active; retained PID(s): $($active.ProcessId-join', ')."}
}
Assert-Idle
New-Item -ItemType Directory -Path $output,$run -Force|Out-Null
if(!$RuntimeDll){
    & dotnet build (Join-Path $repository 'SocketJack.Unity\SocketJack.Unity.csproj') -c Release --artifacts-path (Join-Path $output 'artifacts') -v minimal
    if($LASTEXITCODE-ne0){throw 'Runtime build failed.'}
    $RuntimeDll=Join-Path $output 'artifacts\bin\SocketJack.Unity\release\SocketJack.Unity.dll'
}
$runtime=(Resolve-Path -LiteralPath $RuntimeDll).Path
$identity=[Reflection.AssemblyName]::GetAssemblyName($runtime)
if($identity.Name-ne'SocketJack.Unity'){throw 'Runtime assembly identity differs.'}
$runtimeHash=(Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash.ToLowerInvariant()
if($ExpectedRuntimeSha256-and$runtimeHash-ne$ExpectedRuntimeSha256.ToLowerInvariant()){throw 'Runtime SHA-256 differs from the accepted candidate.'}
[pscustomobject]@{kind='SocketJack.WebGL.Acceptance/1';project=$project;sourceCheckout=$repository;editor=$editor;editorSha256=$editorHash;runtime=$runtime;runtimeSha256=$runtimeHash;retained=$true;noDeletion=$true;noProcessKilling=$true}|ConvertTo-Json|Set-Content -LiteralPath $ownership
$assets=Join-Path $project 'Assets'
foreach($relative in @('Assets\Plugins','Assets\SocketJack.Unity','Assets\Probe\Editor','Assets\Probe\Plugins','ProjectSettings','Packages')){New-Item -ItemType Directory -Path (Join-Path $project $relative) -Force|Out-Null}
Assert-Idle
Copy-Item -LiteralPath $runtime -Destination (Join-Path $assets 'Plugins\SocketJack.Unity.dll')
Get-ChildItem -LiteralPath (Join-Path $repository 'SocketJack.Unity\Unity') -File|Where-Object{$_.Extension-eq'.cs'-or$_.Name-eq'link.xml'}|ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $assets "SocketJack.Unity\$($_.Name)")}
Copy-Item -LiteralPath (Join-Path $repository 'SocketJack.Unity\Unity\Plugins\SocketJackWebSocket.jslib') -Destination (Join-Path $assets 'Plugins\SocketJackWebSocket.jslib')
foreach($name in @('BrowserProbe.cs','BrowserProbeValue.cs')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Assets\$name") -Destination (Join-Path $assets "Probe\$name")}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets\Editor\BrowserProbeBuild.cs') -Destination (Join-Path $assets 'Probe\Editor\BrowserProbeBuild.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets\Plugins\BrowserProbeStatus.jslib') -Destination (Join-Path $assets 'Probe\Plugins\BrowserProbeStatus.jslib')
@{dependencies=@{'com.unity.modules.audio'='1.0.0';'com.unity.modules.animation'='1.0.0';'com.unity.modules.imgui'='1.0.0';'com.unity.modules.physics'='1.0.0';'com.unity.modules.physics2d'='1.0.0';'com.unity.modules.jsonserialize'='1.0.0'}}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $project 'Packages\manifest.json')
$version=(Get-Item -LiteralPath $editor).VersionInfo.ProductVersion
if($version-match'\d+\.\d+\.\d+[abfp]\d+'){$version=$Matches[0]}else{throw 'Editor version unavailable.'}
"m_EditorVersion: $version"|Set-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt')
@'
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!129 &1
PlayerSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 26
  activeInputHandler: 0
'@|Set-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectSettings.asset')
$web=Join-Path $output 'webgl'
$log=Join-Path $run 'build.log'
$arguments=@('-batchmode','-nographics','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','BrowserProbeBuild.Build','-browserOutput',('"'+$web+'"'),'-logFile',('"'+$log+'"'))
$started=[DateTime]::UtcNow
$build=Start-Process -FilePath $editor -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Host "Owned WebGL Editor build PID $($build.Id), log $log"
if(!$build.WaitForExit(1800000)){throw "WebGL build deadline; retained owned PID $($build.Id). No process was killed."}
if($build.ExitCode-ne0-or!(Test-Path -LiteralPath $log)-or(Get-Item -LiteralPath $log).LastWriteTimeUtc-lt$started-or!(Get-Content -LiteralPath $log -Raw).Contains('SOCKETJACK_WEBGL_BUILD_PASS')){throw 'Actual WebGL build failed; inspect retained build log.'}
& dotnet build (Join-Path $PSScriptRoot 'Server\Server.csproj') -c Release --artifacts-path (Join-Path $output 'server-artifacts') "-p:SocketJackRuntimePath=$runtime" -v minimal
if($LASTEXITCODE-ne0){throw 'Native browser-test server build failed.'}
$ready=Join-Path $run 'server.ready';$stop=Join-Path $run 'server.stop';$serverReceipt=Join-Path $run 'server-receipt.json'
$serverDll=Join-Path $output 'server-artifacts\bin\Server\release\Server.dll'
$serverArguments=@(('"'+$serverDll+'"'),$GamePort,"http://127.0.0.1:$HttpPort",('"'+$ready+'"'),('"'+$stop+'"'),('"'+$serverReceipt+'"'))
if($UseRelay){$serverArguments+='relay'}
$native=Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $serverArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'server.stdout.log') -RedirectStandardError (Join-Path $run 'server.stderr.log')
try {
    $wait=[Diagnostics.Stopwatch]::StartNew()
    while(!(Test-Path -LiteralPath $ready)-and!$native.HasExited-and$wait.Elapsed.TotalSeconds-lt15){Start-Sleep -Milliseconds 100}
    if(!(Test-Path -LiteralPath $ready)){throw 'Owned native WebSocket server did not become ready.'}
    $probeArguments=@((Join-Path $PSScriptRoot 'probe-browser.mjs'),$browserPath,$web,$run,$GamePort,$HttpPort)
    if($UseRelay){$probeArguments+='relay'}
    & node @probeArguments
    if($LASTEXITCODE-ne0){throw 'Actual headless WebGL browser probe failed; inspect its retained receipt.'}
} finally {
    'stop'|Set-Content -LiteralPath $stop
    if(!$native.WaitForExit(10000)){throw "Owned test server PID $($native.Id) retained after graceful shutdown deadline. No process was killed."}
}
$serverResult=Get-Content -LiteralPath $serverReceipt -Raw|ConvertFrom-Json
if($native.ExitCode-ne0-or$serverResult.result-ne'PASS'-or(Get-Item -LiteralPath $serverReceipt).LastWriteTimeUtc-lt$started){throw 'Native server did not freshly observe the actual browser connection and owner input.'}
$browserResult=Get-Content -LiteralPath (Join-Path $run 'browser-receipt.json') -Raw|ConvertFrom-Json
if($browserResult.retainedBrowserPid){throw "Owned browser PID $($browserResult.retainedBrowserPid) is retained; graceful shutdown did not complete."}
$files=@(Get-ChildItem -LiteralPath $output -File -Recurse)
[pscustomobject]@{result='PASS';unityVersion=$version;runtimeSha256=$runtimeHash;browserReceipt=Join-Path $run 'browser-receipt.json';serverReceipt=$serverReceipt;files=$files.Count;logicalBytes=($files|Measure-Object Length -Sum).Sum;retained=$true;noDeletion=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'acceptance.json')
Get-Content -LiteralPath (Join-Path $run 'acceptance.json')
