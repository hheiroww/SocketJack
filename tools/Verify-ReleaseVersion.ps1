param(
    [string]$Project = (Join-Path $PSScriptRoot '../SocketJack/SocketJack.csproj'),
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$PackageId = 'SocketJack'
)

$ErrorActionPreference = 'Stop'
[xml]$projectXml = Get-Content -LiteralPath $Project -Raw
$versions = @($projectXml.SelectNodes('/Project/PropertyGroup/Version'))
if ($versions.Count -ne 1) { throw "Expected exactly one explicit $PackageId release version." }
$releaseVersion = $versions[0].InnerText.Trim()
if ($releaseVersion -notmatch '^(?<year>\d{4})\.(?<month>\d{2})\.(?<day>\d{2})\.(?<revision>0|[1-9]\d*)$') {
    throw "Release version '$releaseVersion' must use yyyy.MM.dd.revision."
}
try {
    $releaseDate = [DateTime]::new([int]$Matches.year, [int]$Matches.month, [int]$Matches.day)
    $numericVersion = [Version]$releaseVersion
} catch { throw "Release version '$releaseVersion' contains an invalid date or revision." }
try { $releaseZone = [TimeZoneInfo]::FindSystemTimeZoneById('America/Chicago') }
catch { $releaseZone = [TimeZoneInfo]::FindSystemTimeZoneById('Central Standard Time') }
$today = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $releaseZone).Date
if ($releaseDate -ne $today) {
    throw "Release version '$releaseVersion' must use today's America/Chicago date, $($today.ToString('yyyy.MM.dd'))."
}
$normalizedVersion = if ($numericVersion.Revision -eq 0) {
    $numericVersion.ToString(3)
} else { $numericVersion.ToString(4) }
$feed = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($PackageId.ToLowerInvariant())/index.json"
if (@($feed.versions) -contains $normalizedVersion) {
    throw "$PackageId $releaseVersion is already published. Increment the revision before publishing again."
}
Write-Host "Release version validated: $releaseVersion (NuGet identity $normalizedVersion)."
