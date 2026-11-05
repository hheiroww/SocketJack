#requires -Version 7.4
param(
    [Parameter(Mandatory = $true)][string]$StagingDirectory,
    [Parameter(Mandatory = $true)][string]$RuntimeDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedRuntimeSha256,
    [string]$Editor = 'C:\Program Files\Unity\Hub\Editor\2022.3.17f1\Editor\Unity.exe',
    [int]$GamePort = 18777,
    [int]$WebPort = 18778,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = [IO.Path]::GetFullPath($StagingDirectory)
$runtime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
if ($stage.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Player acceptance staging must be outside source control.' }
if ($GamePort -lt 1 -or $GamePort -gt 65535 -or $WebPort -lt 1 -or $WebPort -gt 65534 -or $GamePort -eq $WebPort -or $GamePort -eq $WebPort + 1) { throw 'Choose separate available game, web and relay ports.' }
$project = Join-Path $stage 'project'; $player = Join-Path $stage 'Player/SocketJackWeb.exe'
if ((Get-PSDrive -Name ([IO.Path]::GetPathRoot($stage).Substring(0,1))).Free -lt 2GB) { throw 'Reserve at least 2 GiB for this reusable Unity project/player.' }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$existing = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -like "*$project*" })
if ($existing.Count -gt 0) { throw 'An Editor already owns this project; no process was stopped.' }
$dlls = @('SocketJack.Unity.dll','SocketJack.Unity.Web.Client.dll','SocketJack.Unity.Web.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.IO.Pipelines.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Text.Encodings.Web.dll','System.Text.Json.dll')
foreach ($name in $dlls) { if (!(Test-Path -LiteralPath (Join-Path $runtime $name) -PathType Leaf)) { throw "Missing explicitly allowed runtime: $name" } }
if ((Get-FileHash -LiteralPath (Join-Path $runtime 'SocketJack.Unity.dll') -Algorithm SHA256).Hash -ne $ExpectedRuntimeSha256) { throw 'Core runtime hash differs from the reviewed input.' }
@(
    'SocketJack actual Unity web/account/physics acceptance ownership',
    "Owned reusable Editor project: $project",
    "Owned player, private synthetic accounts, logs and receipts: $stage",
    "Reused managed inputs (preserve): $runtime",
    'No models, installers, shared cache or entire checkout is copied.',
    'No deletion or cleanup is authorized or performed. Failed evidence is retained.'
) | Set-Content -LiteralPath (Join-Path $stage 'ownership-manifest.txt') -Encoding utf8NoBOM
if (!$SkipBuild) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project 'Assets/Plugins'),(Join-Path $project 'Assets/SocketJack'),(Join-Path $project 'Assets/Web'),(Join-Path $project 'Assets/Editor'),(Join-Path $project 'Packages'),(Join-Path $project 'ProjectSettings') | Out-Null
    foreach ($name in $dlls) { Copy-Item -LiteralPath (Join-Path $runtime $name) -Destination (Join-Path $project "Assets/Plugins/$name") }
    foreach ($folder in @(@{ Source='SocketJack.Unity/Unity'; Destination='Assets/SocketJack' },@{ Source='SocketJack.Unity.Web/Unity'; Destination='Assets/Web' })) {
        $source = Join-Path $repository $folder.Source
        foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.Extension -in @('.cs','.jslib') -or $_.Name -eq 'link.xml' }) {
            $relative = [IO.Path]::GetRelativePath($source,$file.FullName); $target = Join-Path (Join-Path $project $folder.Destination) $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null; Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
    Copy-Item -LiteralPath (Join-Path $repository 'tools/Unity.Web.Acceptance/NativeWebGame.cs') -Destination (Join-Path $project 'Assets/NativeWebGame.cs')
    Copy-Item -LiteralPath (Join-Path $repository 'tools/Unity.Web.Acceptance/Editor/NativeWebBuild.cs') -Destination (Join-Path $project 'Assets/Editor/NativeWebBuild.cs')
    Copy-Item -LiteralPath (Join-Path $repository 'Examples/UnityPhysics/Packages/manifest.json') -Destination (Join-Path $project 'Packages/manifest.json')
    $version = (Get-Item -LiteralPath $Editor).VersionInfo.ProductVersion
    $versionLine = if ($version -match '^(\d+\.\d+\.\d+[a-z]\d+)') { "m_EditorVersion: $($Matches[1])" } else { 'm_EditorVersion: 2022.3.17f1' }
    $versionLine | Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') -Encoding utf8NoBOM
    $log = Join-Path $stage ('unity-build-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.log')
    $process = Start-Process -FilePath $Editor -ArgumentList @('-batchmode','-nographics','-projectPath',('"'+$project+'"'),'-executeMethod','SocketJack.Acceptance.Web.Editor.NativeWebBuild.Build','-socketjackOutput',('"'+$player+'"'),'-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
    Write-Host "Native web Editor PID $($process.Id); log $log"
    if (!$process.WaitForExit(600000)) { throw 'Owned Editor exceeded ten minutes and was retained; no process was killed.' }
    $text = Get-Content -LiteralPath $log -Raw
    if ($process.ExitCode -ne 0 -or !$text.Contains('SOCKETJACK_WEB_NATIVE_BUILD_PASS') -or !(Test-Path -LiteralPath $player)) { throw "Unity web build failed; inspect $log" }
}
if (!(Test-Path -LiteralPath $player)) { throw 'The reusable player does not exist.' }
$run = Join-Path $stage ('run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run | Out-Null
$inputHashes = @($dlls | ForEach-Object { $file=Join-Path $runtime $_; [ordered]@{ name=$_;bytes=(Get-Item -LiteralPath $file).Length;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() } })
$inputHashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'managed-inputs.json') -Encoding utf8NoBOM
$credentials = Join-Path $run 'credentials.private.json'
@{ passwordA = 'Sj!'+[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)); passwordB='Sj!'+[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)) } | ConvertTo-Json | Set-Content -LiteralPath $credentials -Encoding utf8NoBOM
$ready = Join-Path $run 'server.ready'
function Start-Player([string]$Role,[int]$Seconds) {
    $receipt = Join-Path $run "$Role.json"; $log = Join-Path $run "$Role.log"
    $args = @('-batchmode','-nographics','-webRole',$Role,'-gamePort',"$GamePort",'-webPort',"$WebPort",'-webSeconds',"$Seconds",'-webCredentials',('"'+$credentials+'"'),'-webReceipt',('"'+$receipt+'"'),'-webReady',('"'+$ready+'"'),'-webData',('"'+(Join-Path $run 'data')+'"'),'-logFile',('"'+$log+'"'))
    $process = Start-Process -FilePath $player -ArgumentList $args -WindowStyle Hidden -PassThru
    Write-Host "Native $Role PID $($process.Id); log $log"
    return @{ Role=$Role; Process=$process; Receipt=$receipt; Log=$log }
}
$server = Start-Player 'server' 50
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while (!(Test-Path -LiteralPath $ready) -and !$server.Process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
if (!(Test-Path -LiteralPath $ready)) { throw "Native account server did not become ready; inspect $($server.Log). Retained owned process if still running." }
$clients = @((Start-Player 'clientA' 20),(Start-Player 'clientB' 20))
foreach ($item in $clients + @($server)) {
    if (!$item.Process.WaitForExit(90000)) { throw "Native $($item.Role) exceeded its deadline; retained process and evidence." }
    if ($item.Process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $item.Receipt)) { throw "Native $($item.Role) failed; inspect $($item.Log)" }
    $result = Get-Content -LiteralPath $item.Receipt -Raw | ConvertFrom-Json
    if ($result.result -ne 'pass') { throw "Native $($item.Role) acceptance did not pass; inspect $($item.Receipt)" }
}
$hashes = @($dlls | ForEach-Object { $file=Join-Path $runtime $_; [ordered]@{ name=$_;bytes=(Get-Item -LiteralPath $file).Length;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() } })
[ordered]@{result='pass';editor=$Editor;runtimeSha256=$ExpectedRuntimeSha256.ToLowerInvariant();run=$run;managedInputs=$hashes;scope='Actual Windows Mono server and two Unity clients: account login, one-use admission tickets, main-thread callbacks, global/proximity chat, owned physics';receipts=@($clients.Receipt)+@($server.Receipt)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'native-web-acceptance.json') -Encoding utf8NoBOM
Write-Host "SOCKETJACK_WEB_NATIVE_ACCEPTANCE_PASS $run"
