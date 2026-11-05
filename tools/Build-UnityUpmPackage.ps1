param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$RuntimeDll,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedRuntimeSha256,
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$PackageVersion = '1.1.0'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$staging = [IO.Path]::GetFullPath($OutputDirectory)
$runtime = (Resolve-Path -LiteralPath $RuntimeDll).Path
if ($staging.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'UPM staging must be outside the source checkout.'
}
if ((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ne $ExpectedRuntimeSha256) { throw 'Runtime hash differs from the reviewed input.' }
$identity = [Reflection.AssemblyName]::GetAssemblyName($runtime)
if ($identity.Name -ne 'SocketJack.Unity') { throw 'The input is not the standalone SocketJack.Unity runtime.' }
$drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($staging).Substring(0, 1))
if ($drive.Free -lt 256MB) { throw 'Reserve at least 256 MiB for this bounded UPM export.' }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
$package = Join-Path $staging 'package'
if (Test-Path -LiteralPath $package) { throw 'Choose a fresh owned output directory; an existing package directory is retained.' }
New-Item -ItemType Directory -Path $package | Out-Null
@(
    'SocketJack UPM export ownership',
    "Owned package tree: $package",
    "Owned archive and verification receipts: $staging",
    "Reused reviewed runtime (preserve): $runtime",
    'No Editor, dependency cache, installer or runtime-data copy is included.',
    'No cleanup deletion is authorized or performed.'
) | Set-Content -LiteralPath (Join-Path $staging 'ownership-manifest.txt') -Encoding utf8NoBOM

$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
function Write-Asset([string]$Relative, [string]$Source, [string]$Text) {
    if ($Relative.Contains('..') -or $Relative.Contains('\') -or $Relative.StartsWith('/')) { throw 'Noncanonical UPM asset path.' }
    $target = Join-Path $package $Relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    if ($Source) { Copy-Item -LiteralPath $Source -Destination $target }
    else { [IO.File]::WriteAllText($target, $Text, [Text.UTF8Encoding]::new($false)) }
    [void]$allowed.Add($Relative)
}
function Write-JsonAsset([string]$Relative, [object]$Data) {
    Write-Asset $Relative '' ($Data | ConvertTo-Json -Depth 12)
}

$runtimeVersion = $identity.Version.ToString()
Write-JsonAsset 'package.json' ([ordered]@{
    name = 'com.socketjack.multiplayer'; displayName = 'SocketJack Multiplayer'; version = $PackageVersion
    unity = '2022.3'; unityRelease = '17f1'
    description = "Multiplayer integration for Unity 6 and Unity 2022.3. UDP/TCP, predictive 3D/2D physics, member synchronization and setup tools. Runtime $runtimeVersion."
    author = @{ name = 'SocketJack AS Publisher'; url = 'https://socketjack.com' }
    documentationUrl = 'https://github.com/hheiroww/SocketJack'
    repository = @{ type = 'git'; url = 'https://github.com/hheiroww/SocketJack.git' }
    license = 'MIT'; keywords = @('multiplayer', 'networking', 'udp', 'tcp', 'physics')
    samples = @(@{ displayName = 'Physics Arena'; description = 'Sliding-cube host/client physics example. Use its explicit preparation/build menu.'; path = 'Samples~/Physics' })
    socketjackRuntimeVersion = $runtimeVersion; socketjackRuntimeSha256 = $ExpectedRuntimeSha256.ToLowerInvariant()
})
Write-Asset 'Runtime/Plugins/SocketJack.Unity.dll' $runtime ''
$unityRoot = Join-Path $repository 'SocketJack.Unity/Unity'
foreach ($file in Get-ChildItem -LiteralPath $unityRoot -Recurse -File | Where-Object { $_.Extension -eq '.cs' -or $_.Name -eq 'link.xml' }) {
    $relative = [IO.Path]::GetRelativePath($unityRoot, $file.FullName).Replace('\', '/')
    $destination = if ($relative.StartsWith('Editor/', [StringComparison]::Ordinal)) { $relative } else { 'Runtime/Components/' + $relative }
    Write-Asset $destination $file.FullName ''
}
Write-JsonAsset 'Runtime/SocketJack.Multiplayer.Components.asmdef' ([ordered]@{
    name = 'SocketJack.Multiplayer.Components'; rootNamespace = 'SocketJack.Multiplayer.Components'; references = @()
    includePlatforms = @(); excludePlatforms = @(); allowUnsafeCode = $false; overrideReferences = $false
    precompiledReferences = @(); autoReferenced = $true; defineConstraints = @(); versionDefines = @(); noEngineReferences = $false
})
Write-JsonAsset 'Editor/SocketJack.Multiplayer.Editor.asmdef' ([ordered]@{
    name = 'SocketJack.Multiplayer.Editor'; rootNamespace = 'SocketJack.Multiplayer.Components.Editor'
    references = @('SocketJack.Multiplayer.Components'); includePlatforms = @('Editor'); excludePlatforms = @()
    allowUnsafeCode = $false; overrideReferences = $false; precompiledReferences = @(); autoReferenced = $true
    defineConstraints = @(); versionDefines = @(); noEngineReferences = $false
})
Write-Asset 'README.md' (Join-Path $repository 'SocketJack.Unity/README.md') ''
Write-Asset 'Documentation~/QuickStart.md' (Join-Path $repository 'docs/Unity-AssetStore-QuickStart.md') ''
Write-Asset 'LICENSE.md' (Join-Path $repository 'LICENSE') ''
Write-Asset 'Third Party Notices.md' (Join-Path $repository 'docs/Unity-Third-Party-Notices.txt') ''
Write-Asset 'Samples~/Physics/Scripts/PhysicsGame.cs' (Join-Path $repository 'Examples/UnityPhysics/Assets/Scripts/PhysicsGame.cs') ''
Write-Asset 'Samples~/Physics/Editor/PhysicsSampleBuild.cs' (Join-Path $repository 'Examples/UnityPhysics/Assets/Editor/PhysicsSampleBuild.cs') ''
Write-Asset 'Samples~/Physics/README.md' (Join-Path $repository 'Examples/UnityPhysics/README.md') ''
Write-JsonAsset 'Samples~/Physics/Scripts/SocketJack.Samples.Physics.asmdef' ([ordered]@{
    name = 'SocketJack.Samples.Physics'; references = @('SocketJack.Multiplayer.Components'); autoReferenced = $true
})
Write-JsonAsset 'Samples~/Physics/Editor/SocketJack.Samples.Physics.Editor.asmdef' ([ordered]@{
    name = 'SocketJack.Samples.Physics.Editor'; references = @('SocketJack.Multiplayer.Components', 'SocketJack.Samples.Physics')
    includePlatforms = @('Editor'); autoReferenced = $true
})

# Stable GUIDs across upgrades and Asset Store/UPM source layouts. No imported machine metadata is copied.
$paths = @(Get-ChildItem -LiteralPath $package -Recurse -Force | Sort-Object FullName)
foreach ($item in $paths) {
    $relative = [IO.Path]::GetRelativePath($package, $item.FullName).Replace('\', '/')
    if ($relative -match '(^|/)(Documentation~|Samples~)(/|$)' -or $relative -eq 'package.json') { continue }
    $assetStoreRelative = if ($relative.StartsWith('Editor/', [StringComparison]::Ordinal)) { 'Runtime/Components/' + $relative } else { $relative }
    $bytes = [Text.Encoding]::UTF8.GetBytes('SocketJack.Unity.AssetStore/Assets/SocketJack.Unity/' + $assetStoreRelative)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $meta = "fileFormatVersion: 2`nguid: $($hash.Substring(0, 32))`n"
    if ($item.PSIsContainer) { $meta += "folderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n" }
    [IO.File]::WriteAllText($item.FullName + '.meta', $meta, [Text.UTF8Encoding]::new($false))
    [void]$allowed.Add($relative + '.meta')
}
$files = @(Get-ChildItem -LiteralPath $package -Recurse -File)
if (@($files | Where-Object { $_.Extension -in @('.exe', '.pdb', '.sqlite', '.db', '.pfx', '.key', '.pem') }).Count -ne 0) { throw 'Excluded material appeared in UPM staging.' }
$records = @($files | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/')
    if (!$allowed.Contains($relative)) { throw "Unlisted UPM file: $relative" }
    [ordered]@{ path = $relative; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$total = ($files | Measure-Object -Property Length -Sum).Sum
if ($total -gt 64MB) { throw 'The UPM content exceeds its bounded 64 MiB export budget.' }

Add-Type -AssemblyName System.Formats.Tar
$archive = Join-Path $staging "com.socketjack.multiplayer-$PackageVersion.tgz"
$stream = [IO.File]::Create($archive)
$gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::Optimal, $true)
try {
    $tar = [System.Formats.Tar.TarWriter]::new($gzip, $true)
    try { foreach ($file in $files) { $relative = [IO.Path]::GetRelativePath($staging, $file.FullName).Replace('\', '/'); $tar.WriteEntry($file.FullName, $relative) } }
    finally { $tar.Dispose() }
} finally { $gzip.Dispose(); $stream.Dispose() }
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{
    result = 'upm-explicit-sources-exported'; packageName = 'com.socketjack.multiplayer'; version = $PackageVersion
    runtimeVersion = $runtimeVersion; runtimeSha256 = $ExpectedRuntimeSha256.ToLowerInvariant()
    archive = [IO.Path]::GetFileName($archive); archiveSha256 = $archiveHash; archiveBytes = (Get-Item -LiteralPath $archive).Length
    sourceBytes = $total; files = $records
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $staging 'upm-verification.json') -Encoding utf8NoBOM
"$archiveHash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath (Join-Path $staging 'SHA256SUMS') -Encoding utf8NoBOM
Write-Host "UPM archive: $archive"
Write-Host 'Install the tarball through Package Manager > Add package from tarball. Fresh Unity import acceptance is required separately.'
