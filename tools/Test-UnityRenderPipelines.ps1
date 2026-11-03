#requires -Version 7.4
param(
    [Parameter(Mandatory = $true)][string]$Editor,
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('BuiltIn','URP','HDRP','CustomRP')][string[]]$Pipelines = @('BuiltIn','URP','HDRP','CustomRP')
)
$ErrorActionPreference = 'Stop'
$editorPath = (Resolve-Path -LiteralPath $Editor).Path
$projectPath = (Resolve-Path -LiteralPath $Project).Path.TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputDirectory)
function Assert-OutsideCheckout([string]$Path) {
    $directory = [IO.DirectoryInfo]::new($Path)
    while ($null -ne $directory) {
        if (Test-Path -LiteralPath (Join-Path $directory.FullName '.git')) { throw 'Use an isolated project/output outside source checkouts; existing source projects are preserved.' }
        $directory = $directory.Parent
    }
}
Assert-OutsideCheckout $projectPath
Assert-OutsideCheckout $output
$editorProductVersion = (Get-Item -LiteralPath $editorPath).VersionInfo.ProductVersion
$versionMatch = [regex]::Match($editorProductVersion, '(?<version>\d+\.\d+\.\d+[abfp]\d+)')
if (!$versionMatch.Success) { throw 'Cannot identify the requested Unity Editor version from its executable metadata.' }
$expectedEditorVersion = $versionMatch.Groups['version'].Value
if (!(Test-Path -LiteralPath (Join-Path $projectPath 'ProjectSettings/ProjectVersion.txt'))) { throw 'Provide an isolated existing Unity 6 test project.' }
if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace -lt 2GB) { throw 'Reserve at least 2 GiB for Editor import and rendering caches.' }
# This test project must already contain the sample/SDK and both official SRP packages.
# The tool installs no dependencies and never opens or upgrades the source sample project.
$sample = @('Assets/Scripts/PhysicsGame.cs','Assets/SocketJack.Unity/Samples/Physics/Scripts/PhysicsGame.cs') |
    ForEach-Object { Join-Path $projectPath $_ } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$sample -or !(Get-Content -LiteralPath $sample -Raw).Contains('PhysicsSampleMaterialFactory')) { throw 'Import the current Physics sample into this test project first.' }
$manifest = Get-Content -LiteralPath (Join-Path $projectPath 'Packages/manifest.json') -Raw | ConvertFrom-Json
foreach ($dependency in @('com.unity.render-pipelines.universal','com.unity.render-pipelines.high-definition')) {
    if (!$manifest.dependencies.PSObject.Properties[$dependency]) { throw "The test project must already have $dependency installed. This is a test-only dependency, not an SDK dependency." }
}
function Assert-Idle {
    $active = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
        $_.CommandLine -and $_.CommandLine.Contains($projectPath,[StringComparison]::OrdinalIgnoreCase)
    })
    if ($active.Count) { throw "The project is open; retained PID(s): $($active.ProcessId -join ', ')." }
}
Assert-Idle
$probeRoot = Join-Path $projectPath 'Assets/SocketJackPipelineValidation'
$sources = [ordered]@{
    'Editor/RenderPipelineProbe.cs' = (Join-Path $PSScriptRoot 'Unity.RenderPipelines/RenderPipelineProbe.cs')
    'ProbeUnlit.shader' = (Join-Path $PSScriptRoot 'Unity.RenderPipelines/ProbeUnlit.shader')
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($relative in $sources.Keys) {
    $target = Join-Path $probeRoot $relative
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $sources[$relative]).Hash) {
        throw "An existing probe source differs and is retained: $target"
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $sources[$relative] -Destination $target
}
$ownership = [ordered]@{
    purpose = 'Actual Editor/D3D11 sample-material rendering probe; no SRP native player or production-game acceptance claim'
    project = $projectPath; editor = $editorPath; output = $output; probeAssets = $probeRoot
    expectedEditorVersion = $expectedEditorVersion; editorFileProductVersion = $editorProductVersion
    editorSha256 = (Get-FileHash -LiteralPath $editorPath).Hash.ToLowerInvariant()
    sampleSha256 = (Get-FileHash -LiteralPath $sample).Hash.ToLowerInvariant(); sourceAllowlist = $sources
    pipelines = $Pipelines; requires = 'Current sample and both official URP/HDRP packages preinstalled in the isolated project'
    retention = 'Retain probe assets/generated materials and output receipts until reviewed. No cleanup deletion is performed.'
    filesRemoved = 0; processesTerminated = 0; recordedUtc = [DateTime]::UtcNow.ToString('O')
}
$ownership | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'ownership-manifest.json') -Encoding utf8NoBOM
foreach ($pipeline in $Pipelines) {
    Assert-Idle
    $log = Join-Path $output "$pipeline.log"
    $arguments = @('-batchmode','-force-d3d11','-projectPath',('"'+$projectPath+'"'),
        '-executeMethod','SocketJack.Tests.Rendering.RenderPipelineProbe.Run','-socketjackPipeline',$pipeline,
        '-socketjackRenderOutput',('"'+$output+'"'),'-logFile',('"'+$log+'"'))
    # The asynchronous Editor probe exits itself after GPU pixels and receipts are available; omit -quit.
    $startedUtc = [DateTime]::UtcNow
    $process = Start-Process -FilePath $editorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Write-Host "Pipeline $pipeline rendering started (PID $($process.Id)); log $log"
    if (!$process.WaitForExit(600000)) { throw "Pipeline probe timed out; retained PID $($process.Id). No process was stopped." }
    if ($process.ExitCode -ne 0) { throw "Pipeline $pipeline failed (exit $($process.ExitCode)); inspect $log." }
    $receiptPath = Join-Path $output "$pipeline.json"
    if (!(Test-Path -LiteralPath $receiptPath) -or (Get-Item -LiteralPath $receiptPath).LastWriteTimeUtc -lt $startedUtc) { throw "Pipeline $pipeline did not write a fresh receipt." }
    $text = Get-Content -LiteralPath $log -Raw
    $runningVersion = [regex]::Match($text, 'Initialize engine version:\s*(?<version>\d+\.\d+\.\d+[abfp]\d+)')
    if (!$text.Contains('SOCKETJACK_PIPELINE_RENDER') -or !$runningVersion.Success -or
        $runningVersion.Groups['version'].Value -ne $expectedEditorVersion -or $text -match 'error CS\d+|Aborting batchmode due to failure') { throw 'The running Editor version or successful GPU probe marker did not match the request.' }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.result -ne 'passed' -or $receipt.pipeline -ne $pipeline -or $receipt.width -lt 1200 -or
        $receipt.unityVersion -ne $expectedEditorVersion -or $receipt.magentaPixels -ge 100 -or !$receipt.inspectorOverrideClonedWithoutMutatingAsset) { throw "Pipeline $pipeline did not satisfy GPU acceptance." }
    $image = Join-Path $output "$pipeline.png"
    if (!(Test-Path -LiteralPath $image) -or (Get-Item -LiteralPath $image).LastWriteTimeUtc -lt $startedUtc) { throw 'The GPU image is missing or stale.' }
    Write-Host "Pipeline $pipeline passed: Unity $($receipt.unityVersion), $($receipt.actualPipelineType), $($receipt.shader)."
}
