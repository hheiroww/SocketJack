param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$manifest = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'UnityPackageFamily.psd1')
if ($manifest.Format -ne 1 -or $manifest.Packages.Count -ne 6) { throw 'Expected the reviewed six-package Unity family manifest.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($spec in $manifest.Packages) {
    if (!$seen.Add($spec.Id)) { throw 'Duplicate Unity family package identity.' }
    $path = Join-Path $root $spec.Project
    [xml]$project = Get-Content -LiteralPath $path -Raw
    $versions = @($project.SelectNodes('/Project/PropertyGroup/Version'))
    $ids = @($project.SelectNodes('/Project/PropertyGroup/PackageId'))
    if ($versions.Count -ne 1 -or $ids.Count -ne 1 -or $ids[0].InnerText -cne $spec.Id -or
        $project.SelectSingleNode('/Project/PropertyGroup/TargetFramework').InnerText -cne 'netstandard2.1') { throw "Unexpected public project identity/framework: $($spec.Id)" }
    $version = $versions[0].InnerText.Trim()
    if ($version -notmatch '^\d{4}\.\d{2}\.\d{2}\.(0|[1-9]\d*)$') { throw "Invalid explicit date-code version in $($spec.Id)." }
    $numeric = [Version]$version
    $normalized = if ($numeric.Revision -eq 0) { $numeric.ToString(3) } else { $numeric.ToString(4) }
    $references = @($project.SelectNodes('/Project/ItemGroup/ProjectReference'))
    $expectedProjects = @($spec.Dependencies | ForEach-Object { "../$_/$_.csproj" })
    if ($references.Count -ne $expectedProjects.Count -or @($references | Where-Object { $_.Include.Replace('\','/') -cnotin $expectedProjects }).Count) { throw "Unreviewed project dependency in $($spec.Id)." }
    $packages = @($project.SelectNodes('/Project/ItemGroup/PackageReference'))
    if ($packages.Count -ne $spec.ThirdParty.Count) { throw "Unreviewed external dependency in $($spec.Id)." }
    foreach ($package in $packages) { if (!$spec.ThirdParty.ContainsKey($package.Include) -or $package.Version -cne $spec.ThirdParty[$package.Include]) { throw "Unreviewed external dependency/version in $($spec.Id)." } }
    foreach ($reference in $project.SelectNodes('/Project/ItemGroup/Reference')) {
        if ($reference.Include -cne 'SocketJack.Unity' -or 'SocketJack.Unity' -cnotin $spec.Dependencies -or $reference.Condition -cne "'`$(SocketJackRuntimeDll)' != ''" -or $reference.HintPath -cne '$(SocketJackRuntimeDll)') { throw "Unreviewed direct binary reference in $($spec.Id)." }
    }
    $sources = @{}
    foreach ($entry in $spec.Sources.GetEnumerator()) { $sources[$entry.Key] = Join-Path $root $entry.Value }
    if ($spec.ComponentManifest) {
        $componentManifest = Get-Content -LiteralPath (Join-Path $root $spec.ComponentManifest) -Raw | ConvertFrom-Json
        if ($componentManifest.format -ne 1) { throw 'Unsupported base Unity component manifest.' }
        $componentFiles = @($componentManifest.files)
        $componentSource = Join-Path $root 'SocketJack.Unity/Unity'
    } else { $componentFiles = @($spec.Components); $componentSource = Join-Path $root $spec.ComponentSource }
    foreach ($file in $componentFiles) {
        if ($file -notmatch '^(?:[A-Za-z][A-Za-z0-9_]*\.cs|Editor/[A-Za-z][A-Za-z0-9_]*\.cs|Plugins/[A-Za-z][A-Za-z0-9_]*\.jslib|[A-Za-z][A-Za-z0-9_]*\.jslib|link\.xml)$') { throw 'Invalid allowlisted Unity source path.' }
        $entry = "contentFiles/any/any/$($spec.ContentRoot)/$file"
        if ($sources.ContainsKey($entry)) { throw 'Duplicate Unity source allowlist entry.' }
        $sources[$entry] = Join-Path $componentSource $file
    }
    $documentation = $project.SelectSingleNode('/Project/PropertyGroup/GenerateDocumentationFile')
    [pscustomobject]@{ Id=$spec.Id; Project=$path; Version=$version; NumericVersion=$numeric; NormalizedVersion=$normalized; Dependencies=@($spec.Dependencies); ThirdParty=$spec.ThirdParty; Sources=$sources; XmlDocumentation=($documentation -and $documentation.InnerText -eq 'true'); Resources=@($spec.Resources) }
}
