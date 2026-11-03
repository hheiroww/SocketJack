param(
    [string]$Project = (Join-Path $PSScriptRoot '../SocketJack.Unity/SocketJack.Unity.csproj')
)
$ErrorActionPreference = 'Stop'
[xml]$projectXml = Get-Content -LiteralPath $Project -Raw
$versions = @($projectXml.SelectNodes('/Project/PropertyGroup/Version'))
if ($versions.Count -ne 1) { throw 'Expected exactly one explicit Unity package release version.' }
$version = $versions[0].InnerText.Trim()
if ($version -notmatch '^(?<year>\d{4})\.(?<month>\d{2})\.(?<day>\d{2})\.(?<revision>0|[1-9]\d*)$') {
    throw 'Unity release versions must use yyyy.MM.dd.revision.'
}
$date = [DateTime]::new([int]$Matches.year, [int]$Matches.month, [int]$Matches.day)
try { $zone = [TimeZoneInfo]::FindSystemTimeZoneById('America/Chicago') }
catch { $zone = [TimeZoneInfo]::FindSystemTimeZoneById('Central Standard Time') }
$today = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $zone).Date
if ($date -ne $today) { throw "Unity version $version must use today's America/Chicago date, $($today.ToString('yyyy.MM.dd'))." }
$numericVersion = [Version]$version
$normalized = if ($numericVersion.Revision -eq 0) { $numericVersion.ToString(3) } else { $numericVersion.ToString(4) }
try {
    $feed = Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/socketjack.unity/index.json'
    if (@($feed.versions) -contains $normalized) { throw "SocketJack.Unity $version is already published." }
} catch {
    # A new package has no index until its first publication. Any other failure is fatal.
    if ([int]$_.Exception.Response.StatusCode -ne 404) { throw }
}
Write-Host "Unity release version validated: $version (NuGet identity $normalized)."
