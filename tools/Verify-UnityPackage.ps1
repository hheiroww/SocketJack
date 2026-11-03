param(
    [Parameter(Mandatory = $true)][string]$Package,
    [string]$ExpectedPackage,
    [string]$Project = (Join-Path $PSScriptRoot '../SocketJack.Unity/SocketJack.Unity.csproj'),
    [string]$ExpectedCommit = $env:GITHUB_SHA
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[xml]$owningProject = Get-Content -LiteralPath $Project -Raw
$releaseVersion = [Version]$owningProject.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
$normalizedVersion = if ($releaseVersion.Revision -eq 0) { $releaseVersion.ToString(3) } else { $releaseVersion.ToString(4) }
function Verify-UnityIdentity([IO.Compression.ZipArchive]$Zip) {
    $entry = $Zip.GetEntry('SocketJack.Unity.nuspec')
    if ($null -eq $entry -or $entry.Length -gt 1048576) { throw 'Missing or oversized Unity package metadata.' }
    $stream = $entry.Open()
    try {
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try { $xml = [Xml.XmlDocument]::new(); $xml.Load($reader) }
        finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    $metadata = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    if ($metadata.id -ne 'SocketJack.Unity' -or $metadata.version -ne $normalizedVersion) { throw 'Unity package ID or version differs from the owning project.' }
    if ($metadata.icon -ne 'SocketJackUnityIcon.png') { throw 'Unity package must reference its branded SocketJack icon.' }
    if ($metadata.repository.url -ne 'https://github.com/hheiroww/SocketJack' -or $metadata.repository.type -ne 'git') { throw 'Unexpected Unity package source repository.' }
    if ($ExpectedCommit -and $metadata.repository.commit -ne $ExpectedCommit) { throw 'Unity package repository commit differs from the expected source commit.' }
    $groups = @($metadata.dependencies.group)
    if ($groups.Count -ne 1 -or $groups[0].targetFramework -ne '.NETStandard2.1' -or
        $xml.SelectNodes('//*[local-name()="dependency"]').Count -ne 0) { throw 'Unity package must target .NETStandard2.1 with no package dependencies.' }
}
function Read-PackageEntries([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $zip = [IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $entries = @{}
        $totalBytes = 0L
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if ($name -eq '.signature.p7s') { continue } # NuGet may repository-sign the published archive.
            if ($name -notmatch '^(SocketJack\.Unity\.nuspec|\[Content_Types\]\.xml|_rels/\.rels|package/services/metadata/core-properties/[^/]+\.psmdcp|lib/netstandard2\.1/SocketJack\.Unity\.(dll|xml)|README\.md|QuickStart\.md|LICENSE|SocketJackUnityIcon\.png|contentFiles/any/any/SocketJack\.Unity/(NetworkManager\.cs|NetworkIdentity\.cs|NetworkTransform\.cs|SocketJackObject\.cs|SocketJackMemberSync\.cs|ExamplePlayerController\.cs|link\.xml))$') {
                throw "Unexpected Unity package entry: $name"
            }
            if ($entries.ContainsKey($name)) { throw "Duplicate Unity package entry: $name" }
            $totalBytes += $entry.Length
            if ($entry.Length -gt 16777216 -or $totalBytes -gt 67108864) { throw 'Unity package exceeds its expected bounded size.' }
            $stream = $entry.Open()
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $entries[$name] = [Convert]::ToHexString($sha.ComputeHash($stream)) }
                finally { $sha.Dispose() }
            } finally { $stream.Dispose() }
        }
        Verify-UnityIdentity $zip
        foreach ($required in @('SocketJack.Unity.nuspec', 'lib/netstandard2.1/SocketJack.Unity.dll', 'lib/netstandard2.1/SocketJack.Unity.xml',
            'README.md', 'QuickStart.md', 'LICENSE', 'SocketJackUnityIcon.png', 'contentFiles/any/any/SocketJack.Unity/NetworkManager.cs',
            'contentFiles/any/any/SocketJack.Unity/NetworkIdentity.cs', 'contentFiles/any/any/SocketJack.Unity/NetworkTransform.cs',
            'contentFiles/any/any/SocketJack.Unity/SocketJackObject.cs',
            'contentFiles/any/any/SocketJack.Unity/SocketJackMemberSync.cs',
            'contentFiles/any/any/SocketJack.Unity/ExamplePlayerController.cs', 'contentFiles/any/any/SocketJack.Unity/link.xml')) {
            if (!$entries.ContainsKey($required)) { throw "Missing required Unity package entry: $required" }
        }
        return $entries
    } finally { $zip.Dispose() }
}
$actual = Read-PackageEntries $Package
if ($ExpectedPackage) {
    $expected = Read-PackageEntries $ExpectedPackage
    if ($actual.Count -ne $expected.Count) { throw 'Published Unity package entry count differs from the verified archive.' }
    foreach ($name in $expected.Keys) {
        if (!$actual.ContainsKey($name) -or $actual[$name] -ne $expected[$name]) { throw "Published Unity package content mismatch: $name" }
    }
}
$hash = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Verified Unity package: $([IO.Path]::GetFileName($Package)); $($actual.Count) allowlisted entries; SHA256 $hash"
