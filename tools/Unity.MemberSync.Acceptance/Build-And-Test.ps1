#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [Parameter(Mandatory)][string]$UnityVersion,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$RuntimeDll,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedRuntimeSha256
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$editor = [IO.Path]::GetFullPath($UnityEditor)
$project = Join-Path $output 'project'
$runId = [Guid]::NewGuid().ToString('N')
$startedUtc = [DateTime]::UtcNow
if (!(Test-Path -LiteralPath $editor -PathType Leaf)) { throw 'Unity Editor executable was not found.' }
if ($UnityVersion -notmatch '^\d+\.\d+\.\d+[abfp]\d+$') { throw 'Supply the exact installed Unity version.' }
if ($output -eq $repository -or $output.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Place generated test outputs outside the source repository.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$editorHash = (Get-FileHash -LiteralPath $editor -Algorithm SHA256).Hash.ToLowerInvariant()
$editorVersion = (Get-Item -LiteralPath $editor).VersionInfo.ProductVersion
if ($editorVersion -notlike "$UnityVersion*") { throw 'Requested Unity version differs from the installed executable.' }
$ownershipPath = Join-Path $output 'ownership-manifest.json'
if ((Test-Path -LiteralPath $project) -and @(Get-ChildItem -LiteralPath $project -Force).Count -gt 0) {
    if (!(Test-Path -LiteralPath $ownershipPath -PathType Leaf)) { throw 'Existing project has no matching ownership manifest; choose a fresh output directory.' }
    $prior = Get-Content -LiteralPath $ownershipPath -Raw | ConvertFrom-Json
    if ($prior.kind -ne 'SocketJack.MemberSync.Acceptance/1' -or $prior.project -ne $project -or $prior.sourceCheckout -ne $repository -or $prior.editorSha256 -ne $editorHash -or $prior.unityVersion -ne $UnityVersion) {
        throw 'Existing project has a different owner, source checkout or Editor version; it was retained.'
    }
}
function Assert-ProjectIdle {
    $editors = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($project, [StringComparison]::OrdinalIgnoreCase) })
    $players = @(Get-CimInstance Win32_Process -Filter "Name='SocketJackPhysics.exe'" | Where-Object {
        ($_.ExecutablePath -and $_.ExecutablePath.StartsWith((Join-Path $output 'player') + '\', [StringComparison]::OrdinalIgnoreCase)) -or
        ($_.CommandLine -and $_.CommandLine.Contains((Join-Path $output 'player'), [StringComparison]::OrdinalIgnoreCase))
    })
    if ($editors.Count -gt 0 -or $players.Count -gt 0) { throw "An owned project or player is active; retained PID(s): $((@($editors.ProcessId) + @($players.ProcessId)) -join ', ')." }
}
Assert-ProjectIdle
$drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($output).TrimEnd('\').TrimEnd(':')) -ErrorAction SilentlyContinue
if ($drive -and $null -ne $drive.Free -and $drive.Free -lt 2GB) { throw 'At least 2 GiB free space is required for the isolated Unity build.' }
$assets = Join-Path $project 'Assets'
if (!$RuntimeDll) {
    & dotnet build (Join-Path $repository 'SocketJack.Unity\SocketJack.Unity.csproj') -c Release --artifacts-path (Join-Path $output 'artifacts') -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Portable runtime build failed.' }
    $RuntimeDll = Join-Path $output 'artifacts\bin\SocketJack.Unity\release\SocketJack.Unity.dll'
}
$RuntimeDll = (Resolve-Path -LiteralPath $RuntimeDll).Path
$runtimeIdentity = [Reflection.AssemblyName]::GetAssemblyName($RuntimeDll)
[xml]$libraryProject = Get-Content -LiteralPath (Join-Path $repository 'SocketJack.Unity\SocketJack.Unity.csproj') -Raw
$libraryVersion = [Version]$libraryProject.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ([IO.Path]::GetFileName($RuntimeDll) -ne 'SocketJack.Unity.dll' -or $runtimeIdentity.Name -ne 'SocketJack.Unity' -or $runtimeIdentity.Version -ne $libraryVersion) { throw 'Runtime DLL name or assembly version differs from the current library.' }
$runtimeHash = (Get-FileHash -LiteralPath $RuntimeDll -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ExpectedRuntimeSha256 -and $runtimeHash -ne $ExpectedRuntimeSha256) { throw 'Runtime DLL differs from the expected SHA-256.' }
[ordered]@{ kind='SocketJack.MemberSync.Acceptance/1'; project=$project; sourceCheckout=$repository; editor=$editor; editorSha256=$editorHash; unityVersion=$UnityVersion; runtimeSource=$RuntimeDll; runtimeSha256=$runtimeHash; runId=$runId; startedUtc=$startedUtc.ToString('O'); noCleanupDeletion=$true; noProcessTermination=$true } |
    ConvertTo-Json | Set-Content -LiteralPath $ownershipPath
Assert-ProjectIdle
foreach ($directory in @('Editor','NativeMemberProbe','Plugins','SocketJack.Unity','Scripts')) {
    New-Item -ItemType Directory -Path (Join-Path $assets $directory) -Force | Out-Null
}
foreach ($directory in @('Packages','ProjectSettings')) { New-Item -ItemType Directory -Path (Join-Path $project $directory) -Force | Out-Null }
foreach ($directory in @('logs','receipts','player')) { New-Item -ItemType Directory -Path (Join-Path $output $directory) -Force | Out-Null }
Copy-Item -LiteralPath $RuntimeDll -Destination (Join-Path $assets 'Plugins\SocketJack.Unity.dll')
foreach ($name in @('NetworkManager.cs','NetworkIdentity.cs','NetworkTransform.cs','SocketJackObject.cs','ExamplePlayerController.cs','SocketJackMemberSync.cs','link.xml')) {
    Copy-Item -LiteralPath (Join-Path $repository "SocketJack.Unity\Unity\$name") -Destination (Join-Path $assets "SocketJack.Unity\$name")
}
foreach ($name in @('MemberProbeValue.cs','MemberProbeProvider.cs','NativeMemberProbe.cs')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Assets\NativeMemberProbe\$name") -Destination (Join-Path $assets "NativeMemberProbe\$name")
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets\Editor\MemberProbeBuild.cs') -Destination (Join-Path $assets 'Editor\MemberProbeBuild.cs')
Copy-Item -LiteralPath (Join-Path $repository 'Examples\UnityPhysics\Assets\Editor\PhysicsSampleBuild.cs') -Destination (Join-Path $assets 'Editor\PhysicsSampleBuild.cs')
Copy-Item -LiteralPath (Join-Path $repository 'Examples\UnityPhysics\Assets\Scripts\PhysicsGame.cs') -Destination (Join-Path $assets 'Scripts\PhysicsGame.cs')
Copy-Item -LiteralPath (Join-Path $repository 'Examples\UnityPhysics\Packages\manifest.json') -Destination (Join-Path $project 'Packages\manifest.json')
Copy-Item -LiteralPath (Join-Path $repository 'Examples\UnityPhysics\ProjectSettings\ProjectSettings.asset') -Destination (Join-Path $project 'ProjectSettings\ProjectSettings.asset')
"m_EditorVersion: $UnityVersion" | Set-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt')
function Invoke-UnityProcess([string]$Executable, [string[]]$Arguments, [string]$Label, [string]$Log, [string]$Marker) {
    Assert-ProjectIdle
    $stepStarted = [DateTime]::UtcNow
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(300000)) { throw "$Label exceeded five minutes; process $($process.Id) was retained for inspection." }
    if ($process.ExitCode -ne 0) { throw "$Label failed with exit code $($process.ExitCode); inspect retained logs." }
    if (!(Test-Path -LiteralPath $Log -PathType Leaf) -or (Get-Item -LiteralPath $Log).LastWriteTimeUtc -lt $stepStarted.AddSeconds(-1)) { throw "$Label did not write a fresh log." }
    $logText = Get-Content -LiteralPath $Log -Raw
    if (!$logText.Contains($Marker) -or $logText -match 'error CS\d+|Scripts have compiler errors|Aborting batchmode due to failure') { throw "$Label log did not contain its PASS marker or contains a compile failure." }
}
function Quote-Argument([string]$Value) { return '"' + $Value + '"' }
$player = Join-Path $output 'player\SocketJackPhysics.exe'
$buildLog = Join-Path $output 'logs\build.log'; $editorLog = Join-Path $output 'logs\editor-play.log'; $playerLog = Join-Path $output 'logs\player.log'
Invoke-UnityProcess $editor @('-batchmode','-nographics','-quit','-projectPath',(Quote-Argument $project),'-executeMethod','MemberProbeBuild.BuildWindows','-socketjackOutput',(Quote-Argument $player),'-logFile',(Quote-Argument $buildLog)) 'Unity fixture build' $buildLog 'SOCKETJACK_PHYSICS_BUILD_PASS'
$buildReceipt = Get-Content -LiteralPath (Join-Path $output 'player\build-receipt.json') -Raw | ConvertFrom-Json
if ($buildReceipt.runtimeSha256 -ne $runtimeHash -or $buildReceipt.unityVersion -ne $UnityVersion -or (Get-Item -LiteralPath (Join-Path $output 'player\build-receipt.json')).LastWriteTimeUtc -lt $startedUtc.AddSeconds(-1)) { throw 'Build receipt differs from the requested runtime/version or is stale.' }
Invoke-UnityProcess $editor @('-batchmode','-nographics','-projectPath',(Quote-Argument $project),'-executeMethod','MemberProbeBuild.Play','-member-sync-probe','-member-run-id',$runId,'-member-receipt',(Quote-Argument (Join-Path $output 'receipts\editor.json')),'-logFile',(Quote-Argument $editorLog)) 'Editor Play Mode member acceptance' $editorLog 'NATIVE_MEMBER_PROBE_PASS cases=6'
Invoke-UnityProcess $player @('-batchmode','-nographics','-member-sync-probe','-member-run-id',$runId,'-member-receipt',(Quote-Argument (Join-Path $output 'receipts\player.json')),'-logFile',(Quote-Argument $playerLog)) 'Native player member acceptance' $playerLog 'NATIVE_MEMBER_PROBE_PASS cases=6'
foreach ($name in @('editor','player')) {
    $receipt = Get-Content -LiteralPath (Join-Path $output "receipts\$name.json") -Raw | ConvertFrom-Json
    if ($receipt.runId -ne $runId -or $receipt.unityVersion -ne $UnityVersion -or $receipt.runtimeSha256 -ne $runtimeHash -or $receipt.result -ne 'PASS' -or !$receipt.playing -or $receipt.cases.Count -ne 6 -or @($receipt.cases | Where-Object { !$_.passed }).Count -gt 0 -or (Get-Item -LiteralPath (Join-Path $output "receipts\$name.json")).LastWriteTimeUtc -lt $startedUtc.AddSeconds(-1)) { throw "$name acceptance receipt is stale, mismatched or did not pass all six cases." }
}
$files = @(Get-ChildItem -LiteralPath $output -File -Recurse)
[pscustomobject]@{ outputDirectory=$output; files=$files.Count; logicalBytes=($files | Measure-Object Length -Sum).Sum; player=$player; runtimeSha256=(Get-FileHash -LiteralPath (Join-Path $assets 'Plugins\SocketJack.Unity.dll') -Algorithm SHA256).Hash.ToLowerInvariant(); retained=$true } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'output-inventory.json')
Write-Output "Member acceptance passed: six Editor Play Mode and six native player cases. Retained outputs: $output"
