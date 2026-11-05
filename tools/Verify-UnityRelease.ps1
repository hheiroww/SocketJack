param([switch]$SkipFeedCheck)
$ErrorActionPreference = 'Stop'
$family = @(& (Join-Path $PSScriptRoot 'Get-UnityPackageFamily.ps1'))
if (@($family.Version | Select-Object -Unique).Count -ne 1) { throw 'All six Unity-family projects must use the same explicit release date code.' }
$version = $family[0].Version
if ($version -notmatch '^(?<year>\d{4})\.(?<month>\d{2})\.(?<day>\d{2})\.(?<revision>0|[1-9]\d*)$') { throw 'Unity release versions must use yyyy.MM.dd.revision.' }
try { $date = [DateTime]::new([int]$Matches.year, [int]$Matches.month, [int]$Matches.day) } catch { throw 'The Unity release version contains an invalid calendar date.' }
try { $zone = [TimeZoneInfo]::FindSystemTimeZoneById('America/Chicago') } catch { $zone = [TimeZoneInfo]::FindSystemTimeZoneById('Central Standard Time') }
$today = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $zone).Date
if ($date -ne $today) { throw "Unity family version $version must use today's America/Chicago date, $($today.ToString('yyyy.MM.dd'))." }
foreach ($spec in $family) {
    if (!$SkipFeedCheck) {
        try {
            $feed = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($spec.Id.ToLowerInvariant())/index.json" -TimeoutSec 30
            if (@($feed.versions) -contains $spec.NormalizedVersion) { throw "$($spec.Id) $version is already published. Increment the whole family before publishing." }
        } catch {
            # A new ID legitimately has no index. Authentication, availability, TLS and other errors fail closed.
            if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
        }
    }
    Write-Host "Unity release version validated: $($spec.Id) $version (NuGet identity $($spec.NormalizedVersion))."
}
