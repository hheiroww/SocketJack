#requires -Version 7.4
param(
    [Parameter(Mandatory = $true)][string]$Editor,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$RuntimeDll,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedRuntimeSha256,
    [switch]$Build,
    [switch]$TestPlayers,
    [ValidateRange(1, 65535)][int]$Port = 18790
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$editorPath = (Resolve-Path -LiteralPath $Editor).Path
$runtimePath = (Resolve-Path -LiteralPath $RuntimeDll).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $output 'project'
$sourceProject = Join-Path $repository 'Examples/UnityPhysics'
if ($project.Equals($sourceProject, [StringComparison]::OrdinalIgnoreCase) -or
    $project.StartsWith($sourceProject + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use an isolated output directory outside the existing sample project.'
}
if ((Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash -ne $ExpectedRuntimeSha256) { throw 'Explicit runtime SHA-256 differs.' }
$identity = [Reflection.AssemblyName]::GetAssemblyName($runtimePath)
[xml]$libraryProject = Get-Content -LiteralPath (Join-Path $repository 'SocketJack.Unity/SocketJack.Unity.csproj') -Raw
$version = [Version]$libraryProject.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ($identity.Name -ne 'SocketJack.Unity' -or $identity.Version -ne $version) { throw 'Explicit runtime assembly identity/version differs from the current library.' }
$editorInfo = Get-Item -LiteralPath $editorPath
$editorHash = (Get-FileHash -LiteralPath $editorPath -Algorithm SHA256).Hash.ToLowerInvariant()
$ownershipPath = Join-Path $output 'ownership-manifest.json'
if ((Test-Path -LiteralPath $project) -and @(Get-ChildItem -LiteralPath $project -Force).Count -gt 0) {
    if (!(Test-Path -LiteralPath $ownershipPath)) { throw 'Existing project has no task ownership receipt; choose a fresh output directory.' }
    $prior = Get-Content -LiteralPath $ownershipPath -Raw | ConvertFrom-Json
    if ($prior.project -ne $project -or $prior.editorSha256 -ne $editorHash) { throw 'Use a separate output directory for each Editor version; existing project is retained.' }
}
$free = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace
if ($free -lt 2GB) { throw 'Reserve at least 2 GiB for isolated Editor import/build/test outputs.' }
function Assert-ProjectIdle {
    $active = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
        $_.CommandLine -and $_.CommandLine.Contains($project, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($active.Count -gt 0) { throw "An Editor already owns this project; retained PID(s): $($active.ProcessId -join ', ')." }
    $players = @(Get-CimInstance Win32_Process -Filter "Name='SocketJackPhysics.exe'" | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith((Join-Path $output 'player') + '\', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($players.Count -gt 0) { throw "An owned player is active; retained PID(s): $($players.ProcessId -join ', ')." }
}
Assert-ProjectIdle
$sources = [ordered]@{
    'Assets/Plugins/SocketJack.Unity.dll' = $runtimePath
    'Assets/SocketJack.Unity/NetworkManager.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/NetworkManager.cs')
    'Assets/SocketJack.Unity/NetworkIdentity.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/NetworkIdentity.cs')
    'Assets/SocketJack.Unity/NetworkTransform.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/NetworkTransform.cs')
    'Assets/SocketJack.Unity/SocketJackObject.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/SocketJackObject.cs')
    'Assets/SocketJack.Unity/SocketJackMemberSync.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/SocketJackMemberSync.cs')
    'Assets/SocketJack.Unity/ExamplePlayerController.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/ExamplePlayerController.cs')
    'Assets/SocketJack.Unity/link.xml' = (Join-Path $repository 'SocketJack.Unity/Unity/link.xml')
    'Assets/Scripts/PhysicsGame.cs' = (Join-Path $repository 'Examples/UnityPhysics/Assets/Scripts/PhysicsGame.cs')
    'Assets/Editor/PhysicsSampleBuild.cs' = (Join-Path $repository 'Examples/UnityPhysics/Assets/Editor/PhysicsSampleBuild.cs')
    'Packages/manifest.json' = (Join-Path $repository 'Examples/UnityPhysics/Packages/manifest.json')
}
$componentManifest = Get-Content -LiteralPath (Join-Path $repository 'SocketJack.Unity/Unity/package-files.json') -Raw | ConvertFrom-Json
foreach ($file in $componentManifest.files) {
    if ($file -notmatch '^(?:[A-Za-z][A-Za-z0-9_]*\.cs|Editor/[A-Za-z][A-Za-z0-9_]*\.cs|Plugins/[A-Za-z][A-Za-z0-9_]*\.jslib|link\.xml)$') { throw 'Invalid public component path.' }
    $sources["Assets/SocketJack.Unity/$file"] = Join-Path $repository "SocketJack.Unity/Unity/$file"
}
foreach ($source in $sources.Values) { if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing allowlisted source: $source" } }
$guidePath = Join-Path $repository 'docs/Unity-AssetStore-QuickStart.md'
if ((Get-Item -LiteralPath $guidePath).Length -gt 256KB) { throw 'Setup guide exceeds its bounded extraction size.' }
$guide = Get-Content -LiteralPath $guidePath -Raw
$blocks = [regex]::Matches($guide, '(?ms)^```csharp[ \t]*\r?\n(?<code>.*?)^```[ \t]*\r?$')
if ($blocks.Count -ne 2) { throw 'Expected exactly two complete C# setup-guide examples.' }
$examples = [ordered]@{}
foreach ($block in $blocks) {
    $code = $block.Groups['code'].Value
    if ($code.Length -gt 64KB -or $code -match 'RuntimeInitializeOnLoad|InitializeOnLoad|ModuleInitializer') { throw 'Setup-guide example is oversized or automatically executes during import.' }
    $classes = [regex]::Matches($code, '(?m)^\s*public\s+(?:sealed\s+)?class\s+(?<name>\w+)\b')
    if ($classes.Count -ne 1 -or $classes[0].Groups['name'].Value -notin @('GameMultiplayer', 'PlayerNetworkInput') -or
        $code -notmatch '\bnamespace\s+MyGame\b') { throw 'Unexpected setup-guide example class or namespace.' }
    $name = $classes[0].Groups['name'].Value
    if ($examples.Contains($name)) { throw 'Duplicate setup-guide example class.' }
    $examples[$name] = $code
}
$ownership = [ordered]@{
    purpose = 'Isolated real Unity Editor compatibility case; no source project upgrades or dependency downloads'
    project = $project; outputDirectory = $output; sourceCheckout = $repository
    editor = $editorPath; editorSha256 = $editorHash; editorFileProductVersion = $editorInfo.VersionInfo.ProductVersion
    runtimeSource = $runtimePath; runtimeSha256 = $ExpectedRuntimeSha256.ToLowerInvariant()
    freeBytesBefore = $free; buildRequested = [bool]($Build -or $TestPlayers); playerTestRequested = [bool]$TestPlayers
    noCleanupDeletion = $true; noProcessTermination = $true; recordedUtc = [DateTime]::UtcNow.ToString('O')
    sourceAllowlist = $sources; setupGuideSource = $guidePath; setupGuideClasses = @($examples.Keys)
}
New-Item -ItemType Directory -Force -Path $output, (Join-Path $project 'ProjectSettings'), (Join-Path $output 'logs') | Out-Null
$ownership | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ownershipPath -Encoding utf8NoBOM
foreach ($relative in $sources.Keys) {
    $target = Join-Path $project $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $sources[$relative] -Destination $target
}
# The sample itself is silent; the pasted setup-guide bridge also compiles an optional AudioListener.
$manifestPath = Join-Path $project 'Packages/manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$manifest.dependencies['com.unity.modules.audio'] = '1.0.0'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$seed = '2022.3.17f1'
if ($editorInfo.VersionInfo.ProductVersion -match '(?<version>\d+\.\d+\.\d+[abfp]\d+)') { $seed = $Matches.version }
"m_EditorVersion: $seed" | Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') -Encoding utf8NoBOM
# An isolated legacy-input default compiles the pasted guide without installing the optional Input System.
@'
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!129 &1
PlayerSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 26
  activeInputHandler: 0
'@ | Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectSettings.asset') -Encoding utf8NoBOM
$exampleDirectory = Join-Path $project 'Assets/SetupGuideExamples'
New-Item -ItemType Directory -Force -Path $exampleDirectory | Out-Null
foreach ($name in $examples.Keys) { $examples[$name] | Set-Content -LiteralPath (Join-Path $exampleDirectory "$name.cs") -Encoding utf8NoBOM }
$steps = [Collections.Generic.List[object]]::new()
$receipt = [ordered]@{ result = 'running'; editor = $editorPath; editorSha256 = $editorHash; actualEditorVersion = $null
    project = $project; runtimeSha256 = $ExpectedRuntimeSha256.ToLowerInvariant(); setupGuideClasses = @($examples.Keys)
    setupGuideExecution = 'Compiled only; not attached to a scene or instantiated'; steps = $steps
    ownershipManifest = $ownershipPath; filesRemoved = 0; processesTerminated = 0 }
function Invoke-CompatibilityEditor([string]$Method, [string]$LogName, [string]$Marker, [string[]]$Extra = @()) {
    Assert-ProjectIdle
    $log = Join-Path $output "logs/$LogName"
    $arguments = @('-batchmode', '-nographics', '-quit', '-projectPath', ('"' + $project + '"'),
        '-executeMethod', $Method, '-logFile', ('"' + $log + '"')) + $Extra
    $process = Start-Process -FilePath $editorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $step = [ordered]@{ method = $Method; log = $log; pid = $process.Id; result = 'running'; exitCode = $null }
    $steps.Add($step)
    Write-Host "Unity compatibility step $Method started (PID $($process.Id)); log $log"
    if (!$process.WaitForExit(600000)) {
        $step.result = 'timeout-retained'; throw "Editor timed out; retained PID $($process.Id), project $project. No process was stopped."
    }
    $step.exitCode = $process.ExitCode
    if (!(Test-Path -LiteralPath $log)) { throw 'Editor did not write its compatibility log.' }
    $text = Get-Content -LiteralPath $log -Raw
    $reported = [regex]::Match($text, 'Initialize engine version:\s*(?<version>\d+\.\d+\.\d+[abfp]\d+)')
    if (!$reported.Success) { $reported = [regex]::Match($text, "Version is '(?<version>\d+\.\d+\.\d+[abfp]\d+)") }
    if (!$reported.Success) { throw 'Could not determine the actual running Editor version from its log.' }
    $receipt.actualEditorVersion = $reported.Groups['version'].Value
    if ($process.ExitCode -ne 0 -or $text -match 'error CS\d+|Scripts have compiler errors|Aborting batchmode due to failure' -or !$text.Contains($Marker)) {
        $step.result = 'failed'; throw "Real Editor verification failed (exit $($process.ExitCode)); inspect $log."
    }
    $step.result = 'passed'
}
try {
    Invoke-CompatibilityEditor 'SocketJack.Samples.Physics.Editor.PhysicsSampleBuild.VerifyTransformAuthority' 'transform-authority.log' 'SOCKETJACK_TRANSFORM_AUTHORITY_PASS'
    if ($Build -or $TestPlayers) {
        $player = Join-Path $output 'player/SocketJackPhysics.exe'
        Invoke-CompatibilityEditor 'SocketJack.Samples.Physics.Editor.PhysicsSampleBuild.BuildWindows' 'player-build.log' 'SOCKETJACK_PHYSICS_BUILD_PASS' @('-socketjackOutput', ('"' + $player + '"'))
        $buildReceipt = Get-Content -LiteralPath (Join-Path $output 'player/build-receipt.json') -Raw | ConvertFrom-Json
        if ($buildReceipt.runtimeSha256 -ne $ExpectedRuntimeSha256.ToLowerInvariant() -or $buildReceipt.unityVersion -ne $receipt.actualEditorVersion) { throw 'Player build receipt differs from the requested runtime/actual Editor.' }
        $receipt.buildReceipt = Join-Path $output 'player/build-receipt.json'
        if ($TestPlayers) {
            & (Join-Path $PSScriptRoot 'Test-UnityPhysicsPlayers.ps1') -Player $player -OutputDirectory (Join-Path $output 'player-check') -Port $Port -HeadlessClients -Duration 20
            $receipt.playerResults = Join-Path $output 'player-check/results.json'
        }
    }
    $receipt.result = 'passed'
} catch {
    $receipt.result = 'failed'; $receipt.error = $_.Exception.Message; throw
} finally {
    $receipt.checkedUtc = [DateTime]::UtcNow.ToString('O')
    $receipt | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'compatibility-receipt.json') -Encoding utf8NoBOM
}
Write-Host "Actual Editor compatibility passed: $($receipt.actualEditorVersion); setup-guide C# examples compiled."
Write-Host "Receipt: $(Join-Path $output 'compatibility-receipt.json')"
