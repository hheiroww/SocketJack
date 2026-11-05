param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [string]$ExpectedCommit = $env:GITHUB_SHA,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$WriteChecksums,
    [switch]$VerifyChecksums
)
$ErrorActionPreference = 'Stop'
if ($WriteChecksums -and $VerifyChecksums) { throw 'Select either writing or verifying the build checksum receipt.' }
$family = @(& (Join-Path $PSScriptRoot 'Get-UnityPackageFamily.ps1') -RepositoryRoot $RepositoryRoot)
if (@($family.Version | Select-Object -Unique).Count -ne 1) { throw 'All six Unity-family projects must use the same explicit release date code.' }
$directory = (Resolve-Path -LiteralPath $PackageRoot).Path
$files = @(Get-ChildItem -LiteralPath $directory -File -Recurse)
$packages = @($files | Where-Object Extension -CEQ '.nupkg')
if ($packages.Count -ne $family.Count -or @($files | Where-Object { $_.Extension -cne '.nupkg' -and $_.Name -cne 'SHA256SUMS' }).Count -or
    @(Get-ChildItem -LiteralPath $directory -Directory).Count) { throw 'Expected a flat artifact containing exactly six allowlisted packages and optional SHA256SUMS.' }
$checksums = foreach ($spec in $family) {
    $expectedName = "$($spec.Id).$($spec.NormalizedVersion).nupkg"
    $matching = @($packages | Where-Object Name -CEQ $expectedName)
    if ($matching.Count -ne 1) { throw "Missing or unexpected Unity-family package: $expectedName" }
    & (Join-Path $PSScriptRoot 'Verify-UnityPackage.ps1') -Package $matching[0].FullName -ExpectedCommit $ExpectedCommit -RepositoryRoot $RepositoryRoot
    (Get-FileHash -LiteralPath $matching[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $expectedName
}
$checksumPath = Join-Path $directory 'SHA256SUMS'
if ($WriteChecksums) { $checksums | Set-Content -LiteralPath $checksumPath -Encoding utf8NoBOM }
if ($VerifyChecksums) {
    if (!(Test-Path -LiteralPath $checksumPath -PathType Leaf) -or @(Get-Content -LiteralPath $checksumPath).Count -ne $checksums.Count -or
        [string]::Join([char]10, @(Get-Content -LiteralPath $checksumPath)) -cne [string]::Join([char]10, @($checksums))) { throw 'Unity-family archive checksums differ from the verified build artifact receipt.' }
}
Write-Host "Verified complete six-package Unity family $($family[0].Version)."
