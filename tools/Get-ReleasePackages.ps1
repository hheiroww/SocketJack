param(
    [string]$PackageRoot = (Join-Path $PSScriptRoot '../nupkg')
)

$ErrorActionPreference = 'Stop'
$projects = @(
    @{ Id = 'SocketJack'; Path = '../SocketJack/SocketJack.csproj' },
    @{ Id = 'SocketJack.WPF'; Path = '../SocketJack.Windows/SocketJack.WPF.csproj' }
)
$packages = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File -Filter '*.nupkg')
if ($packages.Count -ne $projects.Count) {
    throw "Expected exactly the SocketJack and SocketJack.WPF packages, found $($packages.Count)."
}

$releaseVersion = $null
$verified = foreach ($spec in $projects) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot $spec.Path) -Raw
    $version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
    if ($releaseVersion -and $version -ne $releaseVersion) {
        throw 'SocketJack and SocketJack.WPF must use the same release version.'
    }
    $releaseVersion = $version
    $numericVersion = [Version]$version
    $normalizedVersion = if ($numericVersion.Revision -eq 0) {
        $numericVersion.ToString(3)
    } else { $numericVersion.ToString(4) }
    $expectedName = "$($spec.Id).$normalizedVersion.nupkg"
    $matchingPackages = @($packages | Where-Object { $_.Name -ceq $expectedName })
    if ($matchingPackages.Count -ne 1) { throw "Expected exactly one $expectedName." }
    $package = $matchingPackages[0]

    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $manifest = @($archive.Entries | Where-Object { $_.FullName -ceq "$($spec.Id).nuspec" })
        if ($manifest.Count -ne 1) { throw "Missing package identity in $expectedName." }
        $reader = [IO.StreamReader]::new($manifest[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        if ($metadata.id -cne $spec.Id -or $metadata.version -ne $normalizedVersion) {
            throw "Unexpected package identity or version in $expectedName."
        }
        if ($metadata.repository.url.TrimEnd('/') -cne 'https://github.com/hheiroww/SocketJack') {
            throw "Unexpected repository in $expectedName."
        }
        if ($env:GITHUB_SHA -and $metadata.repository.commit -cne $env:GITHUB_SHA) {
            throw "Unexpected source commit in $expectedName."
        }
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -match '(?i)heirowllm|workstation|\.vsix$|(^|/)(SocketJack\.Portal|SocketJack\.BrowserSignIn|Mail)(/|$)|\.(pfx|pem|key|jsonl)$') {
                throw "Disallowed release content in $expectedName."
            }
        }
    } finally { $archive.Dispose() }
    $package.FullName
}
$verified
