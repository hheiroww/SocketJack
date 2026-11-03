param(
    [Parameter(Mandatory = $true)][string]$StagingDirectory,
    [string]$Editor = 'C:\Program Files\Unity\Hub\Editor\2022.3.17f1\Editor\Unity.exe',
    [switch]$Build,
    [switch]$Open
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repository 'Examples/UnityPhysics'
$staging = [IO.Path]::GetFullPath($StagingDirectory)
New-Item -ItemType Directory -Force -Path $staging | Out-Null
$drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($staging).Substring(0, 1))
if ($drive.Free -lt 2GB) { throw 'Reserve at least 2 GiB for Unity import/build staging.' }
@(
    'SocketJack.Unity physics sample build ownership',
    "Source project (preserve): $project",
    "Owned build/artifacts/log directory: $staging",
    "Generated source-project dependency copies: $project\Assets\Plugins; $project\Assets\SocketJack.Unity",
    "Unity generated cache (retain pending cleanup approval): $project\Library; $project\Temp; $project\Logs",
    'Dependencies are reused from the installed Editor and global NuGet cache.',
    'No cleanup deletion is authorized or performed by this script.'
) | Set-Content -LiteralPath (Join-Path $staging 'ownership-manifest.txt')
dotnet build (Join-Path $repository 'SocketJack.Unity/SocketJack.Unity.csproj') -c Release --artifacts-path (Join-Path $staging 'artifacts')
if ($LASTEXITCODE -ne 0) { throw 'Portable Unity runtime build failed.' }
$plugins = Join-Path $project 'Assets/Plugins'
$adapters = Join-Path $project 'Assets/SocketJack.Unity'
New-Item -ItemType Directory -Force -Path $plugins, $adapters | Out-Null
Copy-Item -LiteralPath (Join-Path $staging 'artifacts/bin/SocketJack.Unity/release/SocketJack.Unity.dll') -Destination (Join-Path $plugins 'SocketJack.Unity.dll')
Get-ChildItem -LiteralPath (Join-Path $repository 'SocketJack.Unity/Unity') -File | Copy-Item -Destination $adapters
if (!$Build -and !$Open) { Write-Host "Prepared Unity source project: $project"; return }
if (!(Test-Path -LiteralPath $Editor -PathType Leaf)) { throw "Unity Editor executable not found: $Editor" }
# A second Editor must never claim the same project. Do not stop an existing Editor.
$activeEditors = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -like "*$project*" })
if ($activeEditors.Count -gt 0) { throw 'This Unity project is already open. Use its SocketJack menu to prepare/build.' }
if ($Build) {
    $player = Join-Path $staging 'Player/SocketJackPhysics.exe'
    $buildLog = Join-Path $staging 'unity-build.log'
    $arguments = @('-batchmode', '-nographics', '-quit', '-projectPath', ('"' + $project + '"'),
        '-executeMethod', 'PhysicsSampleBuild.BuildWindows', '-socketjackOutput', ('"' + $player + '"'), '-logFile', ('"' + $buildLog + '"'))
    $process = Start-Process -FilePath $Editor -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Write-Host "Unity build started: PID $($process.Id); player $player; log $buildLog"
    $process.WaitForExit()
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $player)) {
        throw "Unity player build failed (exit $($process.ExitCode)). Inspect $buildLog."
    }
    Write-Host "Built real Unity player: $player"
}
if ($Open) {
    Start-Process -FilePath $Editor -ArgumentList @('-projectPath', ('"' + $project + '"')) -WindowStyle Normal | Out-Null
    Write-Host "Opened Unity source project: $project"
}
