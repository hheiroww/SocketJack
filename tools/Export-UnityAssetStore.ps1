#requires -Version 7.4
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Editor = 'C:\Program Files\Unity\Hub\Editor\2022.3.17f1\Editor\Unity.exe',
    [string]$RuntimeDll,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedRuntimeSha256,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,40}$')][string]$ImportVerificationTag,
    [switch]$DocumentationOnly
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$projectXml = Get-Content -LiteralPath (Join-Path $repository 'SocketJack.Unity/SocketJack.Unity.csproj') -Raw
$version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
$staging = [IO.Path]::GetFullPath($OutputDirectory)
$exportProject = Join-Path $staging 'export-project'
$importProject = Join-Path $staging "fresh-import-project-$version"
if ($ImportVerificationTag) { $importProject += "-$ImportVerificationTag" }
$packageRoot = 'Assets/SocketJack.Unity'
if (!(Test-Path -LiteralPath $Editor -PathType Leaf)) { throw 'An installed, activated Unity Editor is required.' }
$drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($staging).Substring(0, 1))
if ($drive.Free -lt 3GB) { throw 'Reserve at least 3 GiB for the export and fresh-import projects.' }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
@(
    'SocketJack.Unity Asset Store artifact ownership',
    "Source checkout (preserve): $repository",
    "Owned package/build/import/cache/log outputs: $staging",
    "Export project (no source checkout copy): $exportProject",
    "Fresh import project: $importProject",
    'Only the built portable DLL, explicit public component/sample files and documentation are copied.',
    "Explicit verified runtime input (empty means source build): $RuntimeDll",
    'Installed Unity and global NuGet dependencies are reused. No cleanup deletion is authorized or performed.',
    'Retain the verified .unitypackage and receipts for review; caches/logs can be proposed for cleanup after delivery.'
) | Set-Content -LiteralPath (Join-Path $staging 'ownership-manifest.txt')

function Assert-ProjectIdle([string]$Project) {
    $running = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -like "*$Project*" })
    if ($running.Count -gt 0) { throw "An Editor already owns the staging project: $Project" }
}
function Set-StableMeta([string]$Path, [string]$AssetPath, [bool]$Folder = $false) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("SocketJack.Unity.AssetStore/$AssetPath"))
    $guid = [Convert]::ToHexString($hash).Substring(0, 32).ToLowerInvariant()
    $text = "fileFormatVersion: 2`nguid: $guid`n"
    if ($Folder) { $text += "folderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n" }
    [IO.File]::WriteAllText("$Path.meta", $text, [Text.UTF8Encoding]::new($false))
}
function Initialize-Project([string]$Project) {
    Assert-ProjectIdle $Project
    New-Item -ItemType Directory -Force -Path (Join-Path $Project 'Assets'), (Join-Path $Project 'ProjectSettings'), (Join-Path $Project 'Packages') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'Examples/UnityPhysics/ProjectSettings/ProjectVersion.txt') -Destination (Join-Path $Project 'ProjectSettings/ProjectVersion.txt')
    Copy-Item -LiteralPath (Join-Path $repository 'Examples/UnityPhysics/Packages/manifest.json') -Destination (Join-Path $Project 'Packages/manifest.json')
    $bootstrap = Join-Path $Project 'Assets/SocketJackPackagingBootstrap/Editor'
    New-Item -ItemType Directory -Force -Path $bootstrap | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'Examples/UnityPhysics/Assets/Editor/PhysicsSampleAssetStore.cs') -Destination (Join-Path $bootstrap 'PhysicsSampleAssetStore.cs')
}
function Assert-PublicRuntimeMetadata([string]$Path) {
    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::OpenRead($Path)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        foreach ($entry in $pe.ReadDebugDirectory()) {
            if ($entry.Type -eq [Reflection.PortableExecutable.DebugDirectoryEntryType]::EmbeddedPortablePdb) {
                throw 'Embedded debug symbols require a separate source-path audit and are excluded from this public package.'
            }
            if ($entry.Type -eq [Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) {
                $path = $pe.ReadCodeViewDebugDirectoryData($entry).Path
                if ([IO.Path]::IsPathFullyQualified($path) -or $path -match '(?i)(\.codex|[/\\]Users[/\\]|[/\\]tmp[/\\])') {
                    throw 'The runtime contains a machine/task debug path. Rebuild with DebugType=none and DebugSymbols=false before public export.'
                }
            }
        }
    } finally { $pe.Dispose(); $stream.Dispose() }
}
function Invoke-Editor([string]$Project, [string]$Method, [string]$Log, [string[]]$Extra, [string]$Marker, [bool]$OmitQuit = $false) {
    Assert-ProjectIdle $Project
    $arguments = @('-batchmode', '-nographics', '-projectPath', ('"' + $Project + '"'),
        '-executeMethod', $Method, '-logFile', ('"' + $Log + '"')) + $Extra
    if (!$OmitQuit) { $arguments += '-quit' }
    $process = Start-Process -FilePath $Editor -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Write-Host "Unity $Method started (PID $($process.Id)); log $Log"
    if (!$process.WaitForExit(600000)) {
        throw "The owned batch Editor exceeded ten minutes; retained PID $($process.Id). Inspect $Log; no process was stopped."
    }
    if (!(Test-Path -LiteralPath $Log)) { throw 'Unity did not write an Editor log.' }
    $logText = [IO.File]::ReadAllText($Log)
    if ($process.ExitCode -ne 0 -or $logText -match 'error CS\d+|Scripts have compiler errors|Aborting batchmode due to failure' -or !$logText.Contains($Marker)) {
        throw "Unity export/import verification failed (exit $($process.ExitCode)); inspect $Log."
    }
}

$package = Join-Path $staging "SocketJack.Unity.$version.unitypackage"
if (!$DocumentationOnly -and (Test-Path -LiteralPath (Join-Path $importProject $packageRoot))) {
    throw 'The fresh-import project already contains this package. Choose a new owned output directory for another fresh-import verification; existing outputs are retained.'
}
Initialize-Project $exportProject
Initialize-Project $importProject
if ($RuntimeDll) {
    if (!$ExpectedRuntimeSha256) { throw 'An explicit runtime input requires its verified SHA-256.' }
    $runtimeSource = (Resolve-Path -LiteralPath $RuntimeDll).Path
    $identity = [Reflection.AssemblyName]::GetAssemblyName($runtimeSource)
    if ($identity.Name -ne 'SocketJack.Unity' -or $identity.Version -ne [Version]$version -or
        (Get-FileHash -LiteralPath $runtimeSource -Algorithm SHA256).Hash -ne $ExpectedRuntimeSha256) {
        throw 'Explicit runtime identity/version/hash differs from the reviewed input.'
    }
} else {
    dotnet build (Join-Path $repository 'SocketJack.Unity/SocketJack.Unity.csproj') -c Release -p:DebugType=none -p:DebugSymbols=false --artifacts-path (Join-Path $staging 'artifacts')
    if ($LASTEXITCODE -ne 0) { throw 'Portable networking runtime build failed.' }
    $runtimeSource = Join-Path $staging 'artifacts/bin/SocketJack.Unity/release/SocketJack.Unity.dll'
}
Assert-PublicRuntimeMetadata $runtimeSource

$sources = [ordered]@{
    'Runtime/Plugins/SocketJack.Unity.dll' = $runtimeSource
    'Runtime/Components/NetworkManager.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/NetworkManager.cs')
    'Runtime/Components/NetworkIdentity.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/NetworkIdentity.cs')
    'Runtime/Components/NetworkTransform.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/NetworkTransform.cs')
    'Runtime/Components/SocketJackObject.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/SocketJackObject.cs')
    'Runtime/Components/SocketJackMemberSync.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/SocketJackMemberSync.cs')
    'Runtime/Components/ExamplePlayerController.cs' = (Join-Path $repository 'SocketJack.Unity/Unity/ExamplePlayerController.cs')
    'Runtime/Components/link.xml' = (Join-Path $repository 'SocketJack.Unity/Unity/link.xml')
    'Samples/Physics/Scripts/PhysicsGame.cs' = (Join-Path $repository 'Examples/UnityPhysics/Assets/Scripts/PhysicsGame.cs')
    'Samples/Physics/Editor/PhysicsSampleBuild.cs' = (Join-Path $repository 'Examples/UnityPhysics/Assets/Editor/PhysicsSampleBuild.cs')
    'Documentation/QuickStart.md' = (Join-Path $repository 'docs/Unity-AssetStore-QuickStart.md')
    'Documentation/Runtime.md' = (Join-Path $repository 'SocketJack.Unity/README.md')
    'Documentation/PhysicsSample.md' = (Join-Path $repository 'Examples/UnityPhysics/README.md')
    'Documentation/LICENSE.txt' = (Join-Path $repository 'LICENSE')
    'Documentation/Third-Party Notices.txt' = (Join-Path $repository 'docs/Unity-Third-Party-Notices.txt')
    'Branding/SocketJackUnityIcon.png' = (Join-Path $repository 'SocketJack.Unity/SocketJackUnityIcon.png')
}
if ($DocumentationOnly) {
    if (!$RuntimeDll) { throw 'A documentation-only update must reuse the explicit verified runtime DLL.' }
    $priorVerification = Get-Content -LiteralPath (Join-Path $staging 'package-verification.json') -Raw | ConvertFrom-Json
    $priorImport = Get-Content -LiteralPath (Join-Path $staging 'fresh-import-receipt.json') -Raw | ConvertFrom-Json
    if ($priorImport.result -ne 'fresh-import-compiled' -or $priorImport.runtimeSha256 -ne $ExpectedRuntimeSha256.ToLowerInvariant() -or
        $priorImport.packageSha256 -ne $priorVerification.packageSha256 -or
        (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $priorVerification.packageSha256) {
        throw 'The documentation update has no matching verified archive/import checkpoint.'
    }
    foreach ($asset in $priorVerification.assets) {
        if ($asset.path.StartsWith("$packageRoot/Documentation/", [StringComparison]::Ordinal)) { continue }
        $stagedAsset = Join-Path $exportProject $asset.path
        if ((Get-FileHash -LiteralPath $stagedAsset -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) { throw "A non-documentation staged asset changed: $($asset.path)" }
        $relative = $asset.path.Substring($packageRoot.Length + 1)
        if ($sources.Contains($relative) -and (Get-FileHash -LiteralPath $sources[$relative] -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
            throw "A non-documentation source asset changed: $relative"
        }
    }
}
$assets = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($relative in $sources.Keys) {
    $asset = "$packageRoot/$relative"
    $target = Join-Path $exportProject $asset
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    if (!$DocumentationOnly -or $relative.StartsWith('Documentation/', [StringComparison]::Ordinal)) {
        Copy-Item -LiteralPath $sources[$relative] -Destination $target
        Set-StableMeta $target $asset
    }
    [void]$assets.Add($asset)
}
foreach ($relative in @('Samples/Physics/Scenes/SocketJackPhysics.unity', 'Samples/Physics/Resources/SocketJackPhysicsStandard.mat')) {
    $asset = "$packageRoot/$relative"; $target = Join-Path $exportProject $asset
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    if (!$DocumentationOnly) { Set-StableMeta $target $asset }
    [void]$assets.Add($asset)
}
foreach ($asset in @($assets)) {
    $parent = $asset.Substring(0, $asset.LastIndexOf('/'))
    while ($parent.StartsWith($packageRoot, [StringComparison]::Ordinal)) {
        $folder = Join-Path $exportProject $parent
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        if (!$DocumentationOnly) { Set-StableMeta $folder $parent $true }
        [void]$assets.Add($parent)
        if ($parent -eq $packageRoot) { break }
        $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
    }
}
$manifest = Join-Path $staging 'asset-allowlist.txt'
@($assets | Sort-Object) | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM
$exportReceipt = Join-Path $staging 'export-receipt.json'
$exportMethod = if ($DocumentationOnly) { 'SocketJack.Publishing.Editor.PhysicsSampleAssetStore.ReExportStaged' } else { 'SocketJack.Publishing.Editor.PhysicsSampleAssetStore.Export' }
Invoke-Editor $exportProject $exportMethod (Join-Path $staging 'unity-export.log') @(
    '-socketjackPackage', ('"' + $package + '"'), '-socketjackAssetManifest', ('"' + $manifest + '"'), '-socketjackReceipt', ('"' + $exportReceipt + '"')
) 'SOCKETJACK_ASSET_STORE_EXPORT_PASS'

# Validate the actual gzip/tar asset records without extracting attacker-controlled paths.
Add-Type -AssemblyName System.Formats.Tar
$records = @{}; $totalBytes = 0L
$stream = [IO.File]::OpenRead($package)
$gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
$tar = [System.Formats.Tar.TarReader]::new($gzip, $false)
try {
    while ($null -ne ($entry = $tar.GetNextEntry())) {
        $name = $entry.Name
        if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) {
            if ($name -notmatch '^[a-f0-9]{32}/?$') { throw "Unexpected package directory: $name" }
            continue
        }
        if ($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile) -or
            $name -notmatch '^(?<guid>[a-f0-9]{32})/(?<part>asset|asset.meta|pathname|preview.png)$') { throw "Unexpected Unity archive entry: $name" }
        $guid = $Matches.guid; $part = $Matches.part
        if (!$records.ContainsKey($guid)) { $records[$guid] = @{} }
        if ($records[$guid].ContainsKey($part)) { throw "Duplicate Unity archive record: $name" }
        $totalBytes += $entry.Length
        if ($entry.Length -gt 16MB -or $totalBytes -gt 64MB) { throw 'Asset package exceeds the bounded content budget.' }
        $memory = [IO.MemoryStream]::new()
        try { $entry.DataStream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $memory.Dispose() }
        if ($part -eq 'pathname' -or $part -eq 'asset.meta') { $records[$guid][$part] = [Text.UTF8Encoding]::new($false, $true).GetString($bytes).TrimEnd([char[]]"`0`r`n") }
        else { $records[$guid][$part] = @{ bytes = $bytes.LongLength; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() } }
    }
} finally { $tar.Dispose(); $gzip.Dispose(); $stream.Dispose() }
$verified = [Collections.Generic.List[object]]::new()
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($guid in $records.Keys) {
    $record = $records[$guid]
    if (!$record.ContainsKey('pathname') -or !$record.ContainsKey('asset.meta')) { throw 'An archive asset is missing its pathname or metadata.' }
    $path = [string]$record.pathname
    if (!$assets.Contains($path) -or !$paths.Add($path) -or $path.Contains('..') -or $path.Contains('\') -or $path.Length -ge 150) { throw "Unlisted or noncanonical asset path: $path" }
    if ($record['asset.meta'] -notmatch "(?m)^guid: $guid\r?$" ) { throw "Asset metadata GUID differs from archive GUID: $path" }
    $source = Join-Path $exportProject $path
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        if (!$record.ContainsKey('asset') -or $record.asset.sha256 -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Packaged asset bytes differ from staged source: $path" }
        $verified.Add([ordered]@{ path = $path; guid = $guid; bytes = $record.asset.bytes; sha256 = $record.asset.sha256 })
    } elseif (!(Test-Path -LiteralPath $source -PathType Container)) { throw "Missing staged asset: $path" }
}
if ($paths.Count -ne $assets.Count) { throw 'The exported package does not contain every explicitly allowed asset.' }
$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{ result = 'gzip-tar-allowlist-verified'; version = $version; packageBytes = (Get-Item -LiteralPath $package).Length;
    packageSha256 = $packageHash; totalUncompressedBytes = $totalBytes; assets = @($verified) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $staging 'package-verification.json') -Encoding utf8NoBOM
"$packageHash  $([IO.Path]::GetFileName($package))" | Set-Content -LiteralPath (Join-Path $staging 'SHA256SUMS') -Encoding utf8NoBOM

if ($DocumentationOnly) {
    [ordered]@{ result = 'documentation-update-verified-code-unchanged'; packageSha256 = $packageHash;
        importedCodePackageSha256 = $priorImport.packageSha256; runtimeSha256 = $priorImport.runtimeSha256;
        compiledTypes = $priorImport.compiledTypes; checkedUtc = [DateTime]::UtcNow.ToString('O') } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'documentation-update-receipt.json') -Encoding utf8NoBOM
    Write-Host "Verified documentation update with unchanged imported code: $package"
    Write-Host "SHA-256: $packageHash"
    return
}

Invoke-Editor $importProject 'SocketJack.Publishing.Editor.PhysicsSampleAssetStore.Import' (Join-Path $staging 'unity-fresh-import.log') @(
    '-socketjackPackage', ('"' + $package + '"')
) 'SOCKETJACK_ASSET_STORE_IMPORT_COMPLETED' $true
$importReceipt = Join-Path $staging 'fresh-import-receipt.json'
Invoke-Editor $importProject 'SocketJack.Publishing.Editor.PhysicsSampleAssetStore.VerifyImport' (Join-Path $staging 'unity-fresh-compile.log') @(
    '-socketjackPackage', ('"' + $package + '"'), '-socketjackReceipt', ('"' + $importReceipt + '"')
) 'SOCKETJACK_ASSET_STORE_FRESH_IMPORT_PASS'
$imported = Get-Content -LiteralPath $importReceipt -Raw | ConvertFrom-Json
$original = Get-Content -LiteralPath $exportReceipt -Raw | ConvertFrom-Json
if ($imported.packageSha256 -ne $packageHash -or $imported.runtimeSha256 -ne $original.runtimeSha256) { throw 'Fresh import differs from the verified package/runtime.' }
$expectedTypes = @('SocketJack.Multiplayer.Components.NetworkManager', 'SocketJack.Multiplayer.Components.NetworkIdentity',
    'SocketJack.Multiplayer.Components.NetworkTransform', 'SocketJack.Multiplayer.Components.SocketJackObject',
    'SocketJack.Multiplayer.Components.SocketJackMemberSync',
    'SocketJack.Multiplayer.Components.ExamplePlayerController', 'SocketJack.Samples.Physics.PhysicsGame')
if (@($imported.compiledTypes).Count -ne $expectedTypes.Count -or @(Compare-Object $expectedTypes @($imported.compiledTypes)).Count -ne 0) {
    throw 'Fresh import component/sample namespaces differ from the published API contract.'
}
Write-Host "Verified Unity Asset Store review artifact: $package"
Write-Host "SHA-256: $packageHash"
Write-Host 'No external upload, listing, price change or cleanup deletion was performed.'
