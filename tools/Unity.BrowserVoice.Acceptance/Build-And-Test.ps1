#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [Parameter(Mandatory)][string]$Browser,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$RuntimeDirectory,
    [ValidatePattern('^[A-Za-z0-9-]{1,40}$')][string]$RunName='initial',
    [ValidateRange(1024,65535)][int]$ApiPort=19885,
    [ValidateRange(1024,65535)][int]$HttpPort=19886
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$output=[IO.Path]::GetFullPath($OutputDirectory)
$editor=(Resolve-Path -LiteralPath $UnityEditor).Path;$browserExe=(Resolve-Path -LiteralPath $Browser).Path
$project=Join-Path $output 'project';$run=Join-Path $output "runs/$RunName"
if($output-eq$repository-or$output.StartsWith($repository+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Keep generated projects, accounts, profiles and receipts outside source.'}
if(Test-Path -LiteralPath $run){throw 'This run has retained outputs; choose a fresh bounded run name.'}
if(!(Test-Path -LiteralPath (Join-Path (Split-Path -Parent $editor) 'Data/PlaybackEngines/WebGLSupport'))){throw 'The selected Editor needs its matching installed WebGL module.'}
if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace-lt5GB){throw 'Reserve at least5GiB for this isolated IL2CPP build.'}
$editorHash=(Get-FileHash -LiteralPath $editor -Algorithm SHA256).Hash.ToLowerInvariant();$ownership=Join-Path $output 'ownership.json'
if((Test-Path -LiteralPath $project)-and@(Get-ChildItem -LiteralPath $project -Force).Count){
    if(!(Test-Path -LiteralPath $ownership)){throw 'Existing project lacks this fixture ownership manifest.'}
    $prior=Get-Content -LiteralPath $ownership -Raw|ConvertFrom-Json
    if($prior.kind-ne'SocketJack.BrowserVoice.Acceptance/1'-or$prior.project-ne$project-or$prior.sourceCheckout-ne$repository-or$prior.editorSha256-ne$editorHash){throw 'Existing project owner/source/Editor differs; retained.'}
}
function Assert-Idle{
    $active=@(Get-CimInstance Win32_Process|Where-Object{$_.Name-in@('Unity.exe','dotnet.exe','node.exe','chrome.exe','chrome-headless-shell.exe')-and$_.CommandLine-and$_.CommandLine.Contains($output,[StringComparison]::OrdinalIgnoreCase)})
    if($active.Count){throw "Owned Editor/server/browser is still active; retained PID(s): $($active.ProcessId-join', ')."}
}
Assert-Idle
New-Item -ItemType Directory -Path $output,$run -Force|Out-Null
if(!$RuntimeDirectory){
    dotnet build (Join-Path $repository 'SocketJack.Unity.Voice.Web/SocketJack.Unity.Voice.Web.csproj') -c Release -p:CopyLocalLockFileAssemblies=true --artifacts-path (Join-Path $output 'runtime-artifacts') -v minimal
    if($LASTEXITCODE-ne0){throw 'Portable module build failed.'}
    $RuntimeDirectory=Join-Path $output 'runtime-artifacts/bin/SocketJack.Unity.Voice.Web/release'
}
$runtimeRoot=(Resolve-Path -LiteralPath $RuntimeDirectory).Path
# Native server assemblies and build-only System.Numerics.Vectors are deliberately excluded from WebGL.
$assemblies=@('SocketJack.Unity','SocketJack.Unity.Web.Client','SocketJack.Unity.Voice','SocketJack.Unity.Voice.Web.Client','Concentus','Microsoft.Bcl.AsyncInterfaces','System.IO.Pipelines','System.Runtime.CompilerServices.Unsafe','System.Text.Encodings.Web','System.Text.Json')
$runtime=@();foreach($name in $assemblies){$path=Join-Path $runtimeRoot "$name.dll";if(!(Test-Path -LiteralPath $path)-or[Reflection.AssemblyName]::GetAssemblyName($path).Name-ne$name){throw "Missing or mismatched client assembly: $name"};$runtime+=[pscustomobject]@{name=$name;path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}}
[pscustomobject]@{kind='SocketJack.BrowserVoice.Acceptance/1';project=$project;sourceCheckout=$repository;editor=$editor;editorSha256=$editorHash;runtime=$runtime;retained=$true;realMicrophoneUsed=$false;noDeletion=$true;noProcessKilling=$true}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $ownership
foreach($folder in @('Assets/Plugins','Assets/Base','Assets/WebClient','Assets/Voice','Assets/VoiceWebClient','Assets/Probe/Editor','Assets/Probe/Plugins','Packages','ProjectSettings')){New-Item -ItemType Directory -Path (Join-Path $project $folder) -Force|Out-Null}
Assert-Idle
foreach($assembly in $runtime){Copy-Item -LiteralPath $assembly.path -Destination (Join-Path $project "Assets/Plugins/$($assembly.name).dll")}
$family=@(& (Join-Path $repository 'tools/Get-UnityPackageFamily.ps1'))
$folders=@{'SocketJack.Unity'='Base';'SocketJack.Unity.Web.Client'='WebClient';'SocketJack.Unity.Voice'='Voice';'SocketJack.Unity.Voice.Web.Client'='VoiceWebClient'}
$sourceHashes=@()
foreach($spec in $family|Where-Object{$_.Id-in$folders.Keys}){
    foreach($entry in $spec.Sources.GetEnumerator()|Where-Object{$_.Key.StartsWith('contentFiles/',[StringComparison]::Ordinal)}){
        $relative=($entry.Key-split'/',5)[4];$target=Join-Path $project "Assets/$($folders[$spec.Id])/$relative"
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null;Copy-Item -LiteralPath $entry.Value -Destination $target
        $sourceHashes+=[pscustomobject]@{source=$entry.Value;sha256=(Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/BrowserVoiceProbe.cs') -Destination (Join-Path $project 'Assets/Probe/BrowserVoiceProbe.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/Editor/BrowserVoiceBuild.cs') -Destination (Join-Path $project 'Assets/Probe/Editor/BrowserVoiceBuild.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/Plugins/BrowserVoiceProbe.jslib') -Destination (Join-Path $project 'Assets/Probe/Plugins/BrowserVoiceProbe.jslib')
@{dependencies=@{'com.unity.modules.audio'='1.0.0';'com.unity.modules.animation'='1.0.0';'com.unity.modules.imgui'='1.0.0';'com.unity.modules.physics'='1.0.0';'com.unity.modules.physics2d'='1.0.0';'com.unity.modules.jsonserialize'='1.0.0';'com.unity.modules.unitywebrequest'='1.0.0'}}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json')
$version=(Get-Item -LiteralPath $editor).VersionInfo.ProductVersion;if($version-match'\d+\.\d+\.\d+[abfp]\d+'){$version=$Matches[0]}else{throw 'Exact Editor version unavailable.'}
"m_EditorVersion: $version"|Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt')
$web=Join-Path $output 'webgl';$log=Join-Path $run 'build.log';$started=[DateTime]::UtcNow
$build=Start-Process -FilePath $editor -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','BrowserVoiceBuild.Build','-browserOutput',('"'+$web+'"'),'-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
Write-Host "Owned browser Voice Editor PID $($build.Id), log $log"
if(!$build.WaitForExit(1800000)){throw "Browser Voice build deadline; retained PID $($build.Id). No process was killed."}
if($build.ExitCode-ne0-or!(Test-Path -LiteralPath $log)-or(Get-Item -LiteralPath $log).LastWriteTimeUtc-lt$started-or!(Get-Content -LiteralPath $log -Raw).Contains('SOCKETJACK_BROWSER_VOICE_BUILD_PASS')){throw 'Actual browser Voice build failed; inspect retained build log.'}
dotnet build (Join-Path $PSScriptRoot 'Server/Server.csproj') -c Release --artifacts-path (Join-Path $output 'server-artifacts') -v minimal
if($LASTEXITCODE-ne0){throw 'Owned native Voice server build failed.'}
$ready=Join-Path $run 'server.ready';$stop=Join-Path $run 'server.stop';$serverReceipt=Join-Path $run 'server-receipt.json'
$server=Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @(('"'+(Join-Path $output 'server-artifacts/bin/Server/release/Server.dll')+'"'),$ApiPort,"http://127.0.0.1:$HttpPort",('"'+$ready+'"'),('"'+$stop+'"'),('"'+$serverReceipt+'"'),('"'+(Join-Path $run 'accounts')+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'server.stdout.log') -RedirectStandardError (Join-Path $run 'server.stderr.log')
try{
    $until=[DateTime]::UtcNow.AddSeconds(20);while(!(Test-Path -LiteralPath $ready)-and!$server.HasExited-and[DateTime]::UtcNow-lt$until){Start-Sleep -Milliseconds 100;$server.Refresh()}
    if(!(Test-Path -LiteralPath $ready)){throw 'Owned native Voice server did not become ready.'}
    node (Join-Path $PSScriptRoot 'probe-browser.mjs') $browserExe $web $run $ApiPort $HttpPort
    if($LASTEXITCODE-ne0){throw 'Actual WebGL Voice acceptance failed; inspect retained browser receipt.'}
}finally{'stop'|Set-Content -LiteralPath $stop;if(!$server.WaitForExit(10000)){throw "Owned Voice server PID $($server.Id) retained after graceful stop deadline."}}
$serverResult=Get-Content -LiteralPath $serverReceipt -Raw|ConvertFrom-Json
if($server.ExitCode-ne0-or$serverResult.result-ne'PASS'-or(Get-Item -LiteralPath $serverReceipt).LastWriteTimeUtc-lt$started){throw 'Native Voice counterpart did not freshly verify decoded duplex audio and chat.'}
$browserResult=Get-Content -LiteralPath (Join-Path $run 'browser-receipt.json') -Raw|ConvertFrom-Json
if($browserResult.retainedBrowserPid){throw 'Owned browser did not finish its graceful shutdown.'}
$files=@(Get-ChildItem -LiteralPath $output -File -Recurse)
[pscustomobject]@{result='PASS';unityVersion=$version;runtime=$runtime;source=$sourceHashes;browserReceipt=Join-Path $run 'browser-receipt.json';serverReceipt=$serverReceipt;realMicrophoneUsed=$false;hardwareSpeakersVerified=$false;webAudioDspVerified=$true;files=$files.Count;logicalBytes=($files|Measure-Object Length -Sum).Sum;retained=$true;noDeletion=$true}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $run 'acceptance.json')
Get-Content -LiteralPath (Join-Path $run 'acceptance.json')
