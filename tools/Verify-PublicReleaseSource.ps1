param(
    [string]$Commit = 'HEAD'
)

$ErrorActionPreference = 'Stop'
$repo = Join-Path $PSScriptRoot '..'
$legacyUnityFiles = @(
    'SocketJack.Unity/.editorconfig'
    'SocketJack.Unity/Globals.cs'
    'SocketJack.Unity/MainThread.cs'
    'SocketJack.Unity/SocketJack.Unity.csproj'
    'SocketJack.Unity/SocketJack.xml'
    'SocketJack.Unity/ThreadManager.cs'
)
$tracked = @(git -C $repo ls-tree -r --name-only $Commit)
if ($LASTEXITCODE -ne 0) { throw 'Could not read the public release source tree.' }

$unityPaths = '^(\.github/workflows/unity\.ya?ml$|SocketJack\.Unity(?:[./]|$)|Examples/Unity|docs/(?:Unity|branding/(?:asset-store/|socketjack-unity))|tools/(?:Unity|[a-z]+-Unity))|\.unitypackage$'
$blocked = @($tracked | Where-Object { $_ -match $unityPaths -and $_ -cnotin $legacyUnityFiles })
if ($blocked.Count -gt 0) {
    throw 'The public release source tree contains paid SocketJack Unity paths. Keep the paid asset in its private source repository.'
}

$legacyProject = 'SocketJack.Unity/SocketJack.Unity.csproj'
if ($legacyProject -cin $tracked) {
    $projectXml = git -C $repo show "${Commit}:$legacyProject"
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the legacy Unity project.' }
    [xml]$project = $projectXml -join "`n"
    $packable = @($project.SelectNodes('/Project/PropertyGroup/IsPackable'))
    if ($packable.Count -ne 1 -or $packable[0].InnerText.Trim() -ne 'false') {
        throw 'The legacy Unity helper must remain non-packable in the public source tree.'
    }
}
