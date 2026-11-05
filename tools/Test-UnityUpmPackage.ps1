#requires -Version 7.4
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedPackageSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedRuntimeSha256,
    [Parameter(Mandatory = $true)][string]$StagingDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$PackageVersion = '2.0.0',
    [string]$Editor = 'C:\Program Files\Unity\Hub\Editor\2022.3.17f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = [IO.Path]::GetFullPath($StagingDirectory)
$archive = (Resolve-Path -LiteralPath $Package).Path
$editorPath = (Resolve-Path -LiteralPath $Editor).Path
if ($stage -eq $repository -or $stage.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fresh UPM import staging must be outside source control.' }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $ExpectedPackageSha256) { throw 'The tarball differs from its reviewed SHA256.' }
if ((Get-PSDrive -Name ([IO.Path]::GetPathRoot($stage).Substring(0,1))).Free -lt 1GB) { throw 'Reserve at least 1 GiB for the reusable isolated import project.' }
$entries = @(& tar -tzf $archive)
if ($LASTEXITCODE -ne 0 -or $entries.Count -lt 20 -or @($entries | Where-Object { $_ -notmatch '^package(?:/|$)' -or $_ -match '(^|/)\.\.(/|$)' -or $_.Contains('\') }).Count) { throw 'The archive does not have canonical UPM package/ paths.' }
$metadataText = @(& tar -xOf $archive 'package/package.json') -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'The tarball has no package manifest.' }
$metadata = $metadataText | ConvertFrom-Json
if ($metadata.name -ne 'com.socketjack.multiplayer' -or $metadata.version -ne $PackageVersion -or $metadata.socketjackRuntimeSha256 -ne $ExpectedRuntimeSha256.ToLowerInvariant() -or $metadata.samples.Count -ne 2) { throw 'Expected the complete reviewed SocketJack UPM expansion and both sample definitions.' }
$project = Join-Path $stage 'project'
$owner = Join-Path $stage 'upm-import-ownership.json'
if ((Test-Path -LiteralPath $project) -and !(Test-Path -LiteralPath $owner)) { throw 'An existing project without this harness ownership marker is retained and cannot be reused.' }
if (Test-Path -LiteralPath $owner) {
    $previous = Get-Content -LiteralPath $owner -Raw | ConvertFrom-Json
    if ($previous.project -ne $project -or $previous.kind -ne 'SocketJack isolated UPM import acceptance') { throw 'Staging ownership does not match this reusable project.' }
}
$active = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -like "*$project*" })
if ($active.Count) { throw 'An Editor already owns this project; no process was stopped.' }
New-Item -ItemType Directory -Force -Path (Join-Path $project 'Assets/Editor'),(Join-Path $project 'Packages'),(Join-Path $project 'ProjectSettings') | Out-Null
[ordered]@{kind='SocketJack isolated UPM import acceptance';project=$project;purpose='Reusable fresh project, imported public samples, generated demo scenes, cache and private receipts';preserve='The reviewed tarball and runtime inputs are read only; no cleanup deletion is authorized or performed.'} | ConvertTo-Json | Set-Content -LiteralPath $owner -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $repository 'tools/Unity.Upm.Acceptance/Editor/UpmAcceptance.cs') -Destination (Join-Path $project 'Assets/Editor/UpmAcceptance.cs')
[ordered]@{dependencies=[ordered]@{'com.socketjack.multiplayer'=('file:'+$archive.Replace('\','/'))}} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') -Encoding utf8NoBOM
$version = (Get-Item -LiteralPath $editorPath).VersionInfo.ProductVersion
if ($version -notmatch '^(\d+\.\d+\.\d+[a-z]\d+)') { throw 'Could not read the actual Editor version.' }
"m_EditorVersion: $($Matches[1])" | Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') -Encoding utf8NoBOM
$run = Join-Path $stage ('run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run | Out-Null
function Invoke-UpmEditor([string]$Phase,[string]$Method,[string]$Marker) {
    $log = Join-Path $run ($Phase+'.log'); $receipt = Join-Path $run ($Phase+'.json')
    $arguments = @('-batchmode','-nographics','-projectPath',('"'+$project+'"'),'-executeMethod',$Method,'-socketjackPackage',('"'+$archive+'"'),'-socketjackPackageHash',$ExpectedPackageSha256.ToLowerInvariant(),'-socketjackRuntimeHash',$ExpectedRuntimeSha256.ToLowerInvariant(),'-socketjackVersion',$PackageVersion,'-socketjackReceipt',('"'+$receipt+'"'),'-logFile',('"'+$log+'"'))
    $process = Start-Process -FilePath $editorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Write-Host "UPM $Phase Editor PID $($process.Id); log $log"
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    while (!$process.WaitForExit(1000)) { if ([DateTime]::UtcNow -ge $deadline) { throw "Owned UPM $Phase Editor exceeded ten minutes; process and evidence retained." } }
    if (!(Test-Path -LiteralPath $log) -or $process.ExitCode -ne 0 -or !(Get-Content -LiteralPath $log -Raw).Contains($Marker) -or !(Test-Path -LiteralPath $receipt)) { throw "UPM $Phase failed; inspect $log" }
    $result = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($result.result -ne 'pass' -or $result.packageSha256 -ne $ExpectedPackageSha256.ToLowerInvariant() -or $result.runtimeSha256 -ne $ExpectedRuntimeSha256.ToLowerInvariant()) { throw "UPM $Phase receipt does not match reviewed inputs." }
    return $receipt
}
$importReceipt = Invoke-UpmEditor 'import-samples' 'SocketJack.Acceptance.Upm.Editor.UpmAcceptance.ImportSamples' 'SOCKETJACK_UPM_SAMPLES_IMPORTED'
$verifyReceipt = Invoke-UpmEditor 'verify-scenes' 'SocketJack.Acceptance.Upm.Editor.UpmAcceptance.VerifyAndCreateScenes' 'SOCKETJACK_UPM_ACCEPTANCE_PASS'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $ExpectedPackageSha256) { throw 'The tarball changed during acceptance.' }
[ordered]@{result='pass';scope='Actual local-tarball Package Manager resolution, both sample imports, all module assemblies, server WebGL exclusions, Physics and complete3D/2D scene creation';editor=$editorPath;package=$archive;packageVersion=$PackageVersion;packageSha256=$ExpectedPackageSha256.ToLowerInvariant();runtimeSha256=$ExpectedRuntimeSha256.ToLowerInvariant();receipts=@($importReceipt,$verifyReceipt);ownedProject=$project;checkedUtc=[DateTime]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'upm-acceptance.json') -Encoding utf8NoBOM
Write-Host "SOCKETJACK_UPM_FRESH_IMPORT_PASS $run"
