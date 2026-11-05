param(
    [string]$PackageRoot,
    [string]$Package,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ExpectedCommit = $env:GITHUB_SHA
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repository = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh task-owned verification test directory; retained outputs are never deleted.' }
if ($output -eq $repository -or $output.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test artifacts must remain outside source.' }
if (!$Package) {
    if (!$PackageRoot) { throw 'Supply a verified family PackageRoot or one base Package.' }
    & (Join-Path $PSScriptRoot 'Verify-UnityPackageFamily.ps1') -PackageRoot $PackageRoot -VerifyChecksums -ExpectedCommit $ExpectedCommit
    $base = (& (Join-Path $PSScriptRoot 'Get-UnityPackageFamily.ps1')) | Where-Object Id -CEQ 'SocketJack.Unity'
    $Package = Join-Path $PackageRoot "$($base.Id).$($base.NormalizedVersion).nupkg"
}
& (Join-Path $PSScriptRoot 'Verify-UnityPackage.ps1') -Package $Package -ExpectedCommit $ExpectedCommit
New-Item -ItemType Directory -Path $output | Out-Null
[pscustomobject]@{kind='SocketJack.Unity.PackageVerification/1';sourceCheckout=$repository;input=(Resolve-Path -LiteralPath $Package).Path;retained=$true;noDeletion=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'ownership.json')
$checks = [Collections.Generic.List[string]]::new()
function Rewrite-Package([string]$Name, [scriptblock]$Mutation) {
    $path = Join-Path $output "$Name.nupkg"; Copy-Item -LiteralPath $Package -Destination $path
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Update)
    try { & $Mutation $zip } finally { $zip.Dispose() }
    return $path
}
function Add-TextEntry($Zip, [string]$Name, [string]$Text) {
    $entry = $Zip.CreateEntry($Name); $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($Text) } finally { $writer.Dispose() }
}
function Mutate-Nuspec($Zip, [scriptblock]$Mutation) {
    $entry = $Zip.GetEntry('SocketJack.Unity.nuspec'); $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
    & $Mutation $xml
    # Editing a fresh test archive replaces only its metadata entry, never source or retained artifacts.
    $entry.Delete(); Add-TextEntry $Zip 'SocketJack.Unity.nuspec' $xml.OuterXml
}
function Expect-Rejection([string]$Name, [string]$Path) {
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Verify-UnityPackage.ps1') -Package $Path -ExpectedCommit $ExpectedCommit } catch { $rejected = $true }
    if (!$rejected) { throw "Verifier accepted the invalid $Name fixture." }
    $checks.Add($Name); Write-Host "PASS rejects $Name"
}
$path = Rewrite-Package 'private-entry' { param($zip) Add-TextEntry $zip 'private/session.jsonl' 'synthetic rejection fixture' }
Expect-Rejection 'unexpected private/runtime entry' $path
$path = Rewrite-Package 'duplicate' { param($zip) Add-TextEntry $zip 'README.md' 'duplicate fixture' }
Expect-Rejection 'duplicate archive entry' $path
$path = Rewrite-Package 'dependency' {
    param($zip) Mutate-Nuspec $zip {
        param($xml) $group = $xml.SelectSingleNode('//*[local-name()="dependencies"]/*[local-name()="group"]')
        $node = $xml.CreateElement('dependency', $group.NamespaceURI); $node.SetAttribute('id', 'Unreviewed.Dependency'); $node.SetAttribute('version', '1.0.0'); $group.AppendChild($node) | Out-Null
    }
}
Expect-Rejection 'unreviewed dependency' $path
$path = Rewrite-Package 'version' { param($zip) Mutate-Nuspec $zip { param($xml) $xml.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="version"]').InnerText = '1.0.0' } }
Expect-Rejection 'wrong version' $path
$path = Rewrite-Package 'changed-source' {
    param($zip) $entry = $zip.GetEntry('contentFiles/any/any/SocketJack.Unity/NetworkManager.cs'); $entry.Delete(); Add-TextEntry $zip 'contentFiles/any/any/SocketJack.Unity/NetworkManager.cs' '// synthetic changed source'
}
Expect-Rejection 'changed allowlisted source bytes' $path
$path = Rewrite-Package 'private-dll-string' {
    param($zip) $entry = $zip.GetEntry('lib/netstandard2.1/SocketJack.Unity.dll'); $stream = $entry.Open(); $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $stream.Dispose(); $memory.Dispose() }
    $entry.Delete(); $replacement = $zip.CreateEntry('lib/netstandard2.1/SocketJack.Unity.dll'); $writer = $replacement.Open()
    try { $writer.Write($bytes); $writer.Write([Text.Encoding]::UTF8.GetBytes('/.codex/synthetic-private-path')) } finally { $writer.Dispose() }
}
Expect-Rejection 'private path embedded in a managed image' $path
$debugProject = Join-Path $output 'debug-fixture'; New-Item -ItemType Directory -Path $debugProject | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework><AssemblyName>SocketJack.Unity</AssemblyName><DebugType>portable</DebugType><GenerateAssemblyInfo>false</GenerateAssemblyInfo></PropertyGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $debugProject 'DebugFixture.csproj')
'namespace PackagingFixture { public sealed class Dummy { } }' | Set-Content -LiteralPath (Join-Path $debugProject 'Dummy.cs')
dotnet build (Join-Path $debugProject 'DebugFixture.csproj') -c Release --artifacts-path (Join-Path $output 'debug-artifacts') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Debug payload rejection fixture could not compile.' }
$debugDll = Join-Path $output 'debug-artifacts/bin/DebugFixture/release/SocketJack.Unity.dll'
$path = Rewrite-Package 'debug-dll' {
    param($zip) $entry = $zip.GetEntry('lib/netstandard2.1/SocketJack.Unity.dll'); $entry.Delete(); $replacement = $zip.CreateEntry('lib/netstandard2.1/SocketJack.Unity.dll'); $writer = $replacement.Open()
    try { $writer.Write([IO.File]::ReadAllBytes($debugDll)) } finally { $writer.Dispose() }
}
Expect-Rejection 'managed debug/source-path payload' $path
$path = Rewrite-Package 'signature-only' { param($zip) Add-TextEntry $zip '.signature.p7s' 'synthetic signature payload, not a cryptographic signature' }
& (Join-Path $PSScriptRoot 'Verify-UnityPackage.ps1') -Package $path -ExpectedPackage $Package -ExpectedCommit $ExpectedCommit
$checks.Add('payload equality excludes only repository signature'); Write-Host 'PASS signature-only payload comparison'
if ($PackageRoot) {
    $badFamily = Join-Path $output 'bad-checksums'; New-Item -ItemType Directory -Path $badFamily | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $PackageRoot -File) { Copy-Item -LiteralPath $file.FullName -Destination $badFamily }
    'synthetic wrong checksum' | Set-Content -LiteralPath (Join-Path $badFamily 'SHA256SUMS')
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Verify-UnityPackageFamily.ps1') -PackageRoot $badFamily -VerifyChecksums -ExpectedCommit $ExpectedCommit } catch { $rejected = $true }
    if (!$rejected) { throw 'Verifier accepted altered family archive checksums.' }
    $checks.Add('changed family checksum receipt')
}
[pscustomobject]@{result='PASS';checks=@($checks);retained=$true;noDeletionOfSourceOrPriorArtifacts=$true}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $output 'verification-tests.json')
Write-Host "PASS Unity package verification: $($checks.Count) cases."
