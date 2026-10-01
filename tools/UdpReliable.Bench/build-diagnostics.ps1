$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$evidence = Join-Path $repository 'docs\udp-reliable-results\diagnosis'
$destination = Join-Path $repository 'artifacts\udp-reliable\diagnosis'
$manifest = Get-Content (Join-Path $evidence 'summary.json') -Raw | ConvertFrom-Json
$currentHash = (Get-FileHash (Join-Path $repository 'SocketJack\Net\UdpReliableTransport.cs')).Hash
if ($currentHash -ne $manifest.SourceSha256) {
    throw 'The transport source has changed. Regenerate and review diagnostic source variants before reusing this experiment.'
}
$project = Join-Path $PSScriptRoot 'UdpReliable.Bench.csproj'
dotnet build $project -t:Rebuild -c Release -p:BuildPackage=false -p:GeneratePackageOnBuild=false -p:DocumentationFile=bin/Release/SocketJack.udp.xml -p:NoWarn=1591 -v:q
if ($LASTEXITCODE -ne 0) { throw 'Baseline build failed.' }
foreach ($variant in @('wake-coalesced', 'wake-two-flight', 'timed')) {
    $variantRoot = Join-Path $destination $variant
    dotnet build $project -c Release --no-restore -p:BuildPackage=false -p:GeneratePackageOnBuild=false -p:DocumentationFile=bin/Release/SocketJack.udp.xml -p:NoWarn=1591 "-p:CustomAfterMicrosoftCommonTargets=$evidence\profile.targets" "-p:DiagnosticTransport=$evidence\$variant.cs" "-p:DiagnosticBuildRoot=$variantRoot" "-p:BaseOutputPath=$variantRoot\bin/" -v:q
    if ($LASTEXITCODE -ne 0) { throw "Diagnostic build failed: $variant" }
}
