param(
    [Parameter(Mandatory = $true)][string]$Package,
    [string]$ExpectedPackage,
    [string]$Project,
    [string]$ExpectedCommit = $env:GITHUB_SHA,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Reflection.Metadata
$family = @(& (Join-Path $PSScriptRoot 'Get-UnityPackageFamily.ps1') -RepositoryRoot $RepositoryRoot)
function Read-BoundedEntry($Entry, [long]$Maximum) {
    if ($Entry.Length -gt $Maximum) { throw 'Oversized package entry.' }
    $stream = $Entry.Open(); $memory = [IO.MemoryStream]::new(); $buffer = [byte[]]::new(8192)
    try {
        while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($count -gt $Entry.Length - $memory.Length -or $count -gt $Maximum - $memory.Length) { throw 'Archive data exceeds its declared bounded length.' }
            $memory.Write($buffer, 0, $count)
        }
        if ($memory.Length -ne $Entry.Length) { throw 'Truncated archive entry.' }
        return ,($memory.ToArray())
    } finally { $stream.Dispose(); $memory.Dispose() }
}
function Read-SafeXml([IO.Stream]$Stream) {
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Stream, $settings)
    try { $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($reader); return $xml } finally { $reader.Dispose() }
}
function Read-Identity([IO.Compression.ZipArchive]$Zip) {
    $entries = @($Zip.Entries | Where-Object { $_.FullName -cmatch '^SocketJack\.Unity(?:\.[A-Za-z]+)*\.nuspec$' })
    if ($entries.Count -ne 1 -or $entries[0].Length -gt 1048576) { throw 'Expected one bounded Unity-family nuspec.' }
    $stream = [IO.MemoryStream]::new((Read-BoundedEntry $entries[0] 1048576), $false)
    try { $xml = Read-SafeXml $stream } finally { $stream.Dispose() }
    $metadata = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $specs = @($family | Where-Object Id -CEQ $metadata.id)
    if ($specs.Count -ne 1) { throw 'Package identity is outside the reviewed Unity family.' }
    $spec = $specs[0]
    if ($Project -and (Resolve-Path -LiteralPath $Project).Path -cne $spec.Project) { throw 'The package belongs to a different project.' }
    if ($entries[0].FullName -cne "$($spec.Id).nuspec" -or $metadata.version -cne $spec.NormalizedVersion) { throw 'Package ID or version differs from its source project.' }
    if ($metadata.repository.url -cne 'https://github.com/hheiroww/SocketJack' -or $metadata.repository.type -cne 'git') { throw 'Unexpected package source repository.' }
    if ($ExpectedCommit -and $metadata.repository.commit -cne $ExpectedCommit) { throw 'Package source commit differs from the audited commit.' }
    if ($metadata.license.type -cne 'expression' -or $metadata.license.InnerText -cne 'MIT') { throw 'Package must declare the reviewed MIT license expression.' }
    if ($spec.Sources.ContainsKey('SocketJackUnityIcon.png') -and $metadata.icon -cne 'SocketJackUnityIcon.png') { throw 'Package must reference its branded SocketJack icon.' }
    if ($spec.Sources.ContainsKey('README.md') -and $metadata.readme -cne 'README.md') { throw 'Package must declare its shipped README.' }
    $groups = @($xml.SelectNodes('//*[local-name()="dependencies"]/*[local-name()="group"]'))
    if ($groups.Count -ne 1 -or $groups[0].targetFramework -cne '.NETStandard2.1') { throw 'Package must have exactly the .NETStandard2.1 dependency group.' }
    $expectedDependencies = @{}
    foreach ($id in $spec.Dependencies) { $expectedDependencies[$id] = ($family | Where-Object Id -CEQ $id).NormalizedVersion }
    foreach ($item in $spec.ThirdParty.GetEnumerator()) { $expectedDependencies[$item.Key] = $item.Value }
    $dependencies = @($xml.SelectNodes('//*[local-name()="dependency"]'))
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($dependencies.Count -ne $expectedDependencies.Count) { throw "Unexpected dependency count in $($spec.Id)." }
    foreach ($dependency in $dependencies) {
        $id = $dependency.GetAttribute('id'); $version = $dependency.GetAttribute('version')
        if (!$seen.Add($id) -or !$expectedDependencies.ContainsKey($id) -or $version -cne $expectedDependencies[$id] -or $dependency.ParentNode -ne $groups[0]) { throw "Unreviewed dependency or version in $($spec.Id): $id" }
    }
    return $spec
}
function Assert-NoPrivateMaterial([byte[]]$Bytes, [string]$Name) {
    $texts = @([Text.Encoding]::UTF8.GetString($Bytes))
    if ($Name.EndsWith('.dll', [StringComparison]::Ordinal)) {
        $texts += [Text.Encoding]::Unicode.GetString($Bytes)
        if ($Bytes.Length -gt 1) { $texts += [Text.Encoding]::Unicode.GetString($Bytes, 1, $Bytes.Length - 1) }
    }
    foreach ($text in $texts) {
        if ($text -match '(?i)\.codex[/\\]|[A-Z]:[/\\]Users[/\\][^/\\\s<>]+[/\\]|SocketJack\.Portal|BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY|chatgpt[_-]?transcript|response_item.*turn_context') { throw "Private machine/runtime material in package entry: $Name" }
    }
}
function Assert-ManagedBoundary([byte[]]$Bytes, $Spec) {
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if (!$pe.HasMetadata) { throw 'Expected a portable managed assembly.' }
        foreach ($debug in $pe.ReadDebugDirectory()) { if ($debug.Type.ToString() -in @('CodeView','EmbeddedPortablePdb')) { throw 'Published managed assembly contains a source-path/debug payload.' } }
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $identity = $metadata.GetAssemblyDefinition()
        if ($metadata.GetString($identity.Name) -cne $Spec.Id -or $identity.Version -ne $Spec.NumericVersion) { throw 'Managed assembly identity/version differs from the package.' }
        $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $work = [Collections.Generic.Stack[string]]::new(); $work.Push($Spec.Id)
        while ($work.Count -gt 0) {
            $id = $work.Pop(); if (!$visited.Add($id)) { continue }
            $dependencySpec = $family | Where-Object Id -CEQ $id
            foreach ($dependency in $dependencySpec.Dependencies) { [void]$allowed.Add($dependency); $work.Push($dependency) }
            foreach ($thirdParty in $dependencySpec.ThirdParty.Keys) { [void]$allowed.Add($thirdParty) }
        }
        foreach ($handle in $metadata.AssemblyReferences) {
            $name = $metadata.GetString($metadata.GetAssemblyReference($handle).Name)
            if ($name -notmatch '^(netstandard|mscorlib|System(?:\..+)?|Microsoft\.CSharp)$' -and !$allowed.Contains($name)) { throw "Unreviewed managed assembly reference: $name" }
        }
        $resources = @()
        foreach ($handle in $metadata.ManifestResources) {
            $resource = $metadata.GetManifestResource($handle)
            if (!$resource.Implementation.IsNil) { throw 'External assembly resource references are not allowed.' }
            $resources += $metadata.GetString($resource.Name)
        }
        if ($resources.Count -ne $Spec.Resources.Count -or @($resources | Where-Object { $_ -cnotin $Spec.Resources }).Count) { throw 'Unreviewed embedded resources in the managed assembly.' }
    } finally { $pe.Dispose(); $stream.Dispose() }
}
function Read-PackageEntries([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        if ($zip.Entries.Count -gt 1024) { throw 'Package archive has excessive entries.' }
        $spec = Read-Identity $zip
        $entries = @{}; $allNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); $total = 0L
        $dllName = "lib/netstandard2.1/$($spec.Id).dll"; $xmlName = "lib/netstandard2.1/$($spec.Id).xml"
        $required = @("$($spec.Id).nuspec", $dllName, '[Content_Types].xml', '_rels/.rels') + @($spec.Sources.Keys)
        if ($spec.XmlDocumentation) { $required += $xmlName }
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if (!$allNames.Add($name)) { throw "Duplicate or case-conflicting archive entry: $name" }
            $total += $entry.Length
            if ($entry.Length -gt 16777216 -or $total -gt 67108864) { throw 'Package exceeds its bounded publication size.' }
            if ($name -ceq '.signature.p7s') { if ($entry.Length -gt 1048576) { throw 'Oversized repository signature.' }; continue }
            if ($name -cnotin $required -and $name -cnotmatch '^package/services/metadata/core-properties/[A-Za-z0-9_-]+\.psmdcp$') { throw "Unexpected package entry: $name" }
            $bytes = Read-BoundedEntry $entry 16777216
            $entries[$name] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            if ($name -eq $dllName) { Assert-ManagedBoundary $bytes $spec }
            if ($name -notmatch '\.(png|p7s)$') { Assert-NoPrivateMaterial $bytes $name }
            if ($spec.Sources.ContainsKey($name)) {
                if (!(Test-Path -LiteralPath $spec.Sources[$name] -PathType Leaf) -or (Get-FileHash -LiteralPath $spec.Sources[$name] -Algorithm SHA256).Hash -cne $entries[$name]) { throw "Packaged source/content differs from the audited source: $name" }
            }
        }
        foreach ($name in $required) { if (!$entries.ContainsKey($name)) { throw "Missing required package entry: $name" } }
        return [pscustomobject]@{ Spec=$spec; Entries=$entries }
    } finally { $zip.Dispose() }
}
$actual = Read-PackageEntries $Package
if ($ExpectedPackage) {
    $expected = Read-PackageEntries $ExpectedPackage
    if ($actual.Spec.Id -cne $expected.Spec.Id -or $actual.Entries.Count -ne $expected.Entries.Count) { throw 'Published package identity/entry count differs from the verified archive.' }
    foreach ($name in $expected.Entries.Keys) { if (!$actual.Entries.ContainsKey($name) -or $actual.Entries[$name] -cne $expected.Entries[$name]) { throw "Published package payload mismatch: $name" } }
}
$hash = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Verified Unity family package: $($actual.Spec.Id) $($actual.Spec.NormalizedVersion); $($actual.Entries.Count) allowlisted entries; SHA256 $hash"
